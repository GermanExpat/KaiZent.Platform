using System.Globalization;
using KaiZent.Platform.Contracts.Billing;

namespace KaiZent.Platform.Api.Documents;

/// <summary>Empfänger eines Dokuments zum Zeitpunkt des Erzeugens.</summary>
public sealed record DocumentRecipient(string TenantName, int? CustomerNumber, string RecipientName, string Street, string PostalCode, string City,
    string Country, string? BillingEmail, string VatId);

/// <summary>Daten eines Dokuments, die in die Platzhalter fließen.</summary>
public sealed record DocumentFacts(string Kind, string Title, string? Number, DateOnly IssueDate, DateOnly? ServiceFrom, DateOnly? ServiceTo,
    DateOnly? DueDate, IReadOnlyList<DocumentLine> Lines, decimal Net, decimal VatRate, decimal Vat, decimal Gross, string? CancelsNumber);

/// <summary>Werte für die Platzhalter aus <see cref="DocumentPlaceholders"/>; Zahlen und Daten in deutscher Schreibweise.</summary>
public static class DocumentValues
{
    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");

    public static string Money(decimal value) => value.ToString("N2", De) + " €";
    public static string Date(DateOnly value) => value.ToString("dd.MM.yyyy", De);

    public static Dictionary<string, string> Build(CompanyProfile c, DocumentRecipient r, DocumentFacts d)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Firma.Name"] = c.LegalName,
            ["Firma.Anschrift"] = Join(", ", c.Street, Join(" ", c.PostalCode, c.City)),
            ["Firma.Strasse"] = c.Street,
            ["Firma.PLZ"] = c.PostalCode,
            ["Firma.Ort"] = c.City,
            ["Firma.Land"] = c.Country,
            ["Firma.EMail"] = c.Email,
            ["Firma.Telefon"] = c.Phone,
            ["Firma.Web"] = c.Website,
            ["Firma.UStIdNr"] = c.VatId,
            ["Firma.Steuernummer"] = c.TaxNumber,
            ["Firma.Geschaeftsfuehrung"] = c.ManagingDirector,
            ["Firma.Registergericht"] = c.RegisterCourt,
            ["Firma.Registernummer"] = c.RegisterNumber,
            ["Firma.Bank"] = c.BankName,
            ["Firma.IBAN"] = FormatIban(c.Iban),
            ["Firma.BIC"] = c.Bic,
            ["Firma.Fusszeile"] = c.FooterNote,

            ["Betrieb.Name"] = r.TenantName,
            ["Betrieb.Empfaenger"] = r.RecipientName,
            ["Betrieb.Anschrift"] = string.Join("\n", new[] { r.RecipientName, r.Street, Join(" ", r.PostalCode, r.City),
                IsHomeCountry(r.Country) ? "" : r.Country }.Where(s => !string.IsNullOrWhiteSpace(s))),
            ["Betrieb.Strasse"] = r.Street,
            ["Betrieb.PLZ"] = r.PostalCode,
            ["Betrieb.Ort"] = r.City,
            ["Betrieb.Land"] = r.Country,
            ["Betrieb.EMail"] = r.BillingEmail ?? "",
            ["Betrieb.UStIdNr"] = r.VatId,
            ["Betrieb.Kundennummer"] = r.CustomerNumber?.ToString(CultureInfo.InvariantCulture) ?? "",

            ["Dokument.Titel"] = d.Title,
            ["Dokument.Datum"] = Date(d.IssueDate),
        };
        if (d.Kind == DocumentKinds.Letter) return values;

        var number = d.Number ?? "ENTWURF";
        values["Rechnung.Nummer"] = number;
        values["Rechnung.Datum"] = Date(d.IssueDate);
        values["Rechnung.Leistungszeitraum"] = d.ServiceFrom is { } from && d.ServiceTo is { } to
            ? (from == to ? Date(from) : $"{Date(from)} – {Date(to)}") : "";
        values["Rechnung.Faellig"] = d.DueDate is { } due ? Date(due) : "";
        values["Rechnung.Netto"] = Money(d.Net);
        values["Rechnung.UStSatz"] = d.VatRate.ToString("0.##", De) + " %";
        values["Rechnung.USt"] = Money(d.Vat);
        values["Rechnung.Brutto"] = Money(d.Gross);
        values["Rechnung.Steuerhinweis"] = c.SmallBusiness ? "Gemäß § 19 UStG wird keine Umsatzsteuer berechnet." : "";
        values["Rechnung.Bezug"] = d.CancelsNumber is null ? "" : $"Diese Stornorechnung hebt die Rechnung {d.CancelsNumber} auf.";
        values["Rechnung.Zahlungshinweis"] = d.Kind == DocumentKinds.Cancellation
            ? values["Rechnung.Bezug"] + " Ein bereits gezahlter Betrag wird erstattet oder verrechnet."
            : d.Gross <= 0 ? "Es ist keine Zahlung fällig."
            : $"Bitte überweisen Sie den Gesamtbetrag von {Money(d.Gross)}" + (d.DueDate is { } dd ? $" bis zum {Date(dd)}" : "")
              + $" unter Angabe der Rechnungsnummer {number}" + (string.IsNullOrWhiteSpace(c.Iban) ? "." : $" auf das Konto IBAN {FormatIban(c.Iban)}"
              + (string.IsNullOrWhiteSpace(c.BankName) ? "" : $" ({c.BankName})") + ".");
        return values;
    }

    /// <summary>Werte je Position für die wiederholte Tabellenzeile.</summary>
    public static IReadOnlyList<IReadOnlyDictionary<string, string>> Lines(IReadOnlyList<DocumentLine> lines) =>
        lines.Select((l, i) => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Position.Nr"] = (i + 1).ToString(CultureInfo.InvariantCulture),
            ["Position.Beschreibung"] = l.Description,
            ["Position.Menge"] = l.Quantity.ToString("0.###", De),
            ["Position.Einzelpreis"] = Money(l.UnitNet),
            ["Position.Betrag"] = Money(l.Net),
        }).ToList();

    /// <summary>Beispielwerte für die Vorschau im Designer.</summary>
    public static (DocumentRecipient Recipient, DocumentFacts Facts) Sample(string kind, DateOnly today)
    {
        var recipient = new DocumentRecipient("Reiterhof Musterwiese", 10001, "Reiterhof Musterwiese GbR", "Feldweg 3", "12345", "Musterdorf",
            "Deutschland", "buero@musterwiese.example", "");
        DocumentLine[] lines = [new("KaiZent LMS – Abo " + today.ToString("MMMM yyyy", De), 1, 99m), new("AI-Credits 250", 1, 25m)];
        var (net, vat, gross) = DocumentRules.Totals(lines, 19m);
        var first = new DateOnly(today.Year, today.Month, 1);
        return (recipient, new DocumentFacts(kind, kind == DocumentKinds.Letter ? "Information zu Ihrem Abo" : "Rechnung", "RE-" + today.Year + "-00001",
            today, first, first.AddMonths(1).AddDays(-1), today.AddDays(14), lines, net, 19m, vat, gross, null));
    }

    public static string FormatIban(string iban)
    {
        var compact = new string(iban.Where(ch => !char.IsWhiteSpace(ch)).ToArray()).ToUpperInvariant();
        return string.Join(' ', compact.Chunk(4).Select(ch => new string(ch)));
    }

    private static bool IsHomeCountry(string country) =>
        string.IsNullOrWhiteSpace(country) || country.Trim() is "Deutschland" or "DE" or "Germany";

    private static string Join(string separator, params string[] parts) =>
        string.Join(separator, parts.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()));
}
