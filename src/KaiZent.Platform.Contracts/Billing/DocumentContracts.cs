using KaiZent.Platform.Contracts.Common;

namespace KaiZent.Platform.Contracts.Billing;

/// <summary>Firmendaten des Plattform-Betreibers. Erscheinen als Absender, im Fuß und in den Zahlungsangaben jeder Rechnung.</summary>
public sealed record CompanyProfile(string LegalName, string Street, string PostalCode, string City, string Country, string Email, string Phone,
    string Website, string VatId, string TaxNumber, string ManagingDirector, string RegisterCourt, string RegisterNumber, string BankName,
    string Iban, string Bic, string InvoicePrefix, int PaymentTermDays, decimal VatRate, bool SmallBusiness, string FooterNote, int Version,
    DateTimeOffset UpdatedAt, string UpdatedBy)
{
    /// <summary>Genug Angaben, um eine Rechnung auszustellen (§ 14 Abs. 4 UStG: Name, Anschrift, Steuernummer oder USt-IdNr.).</summary>
    public bool IsCompleteForInvoices => !string.IsNullOrWhiteSpace(LegalName) && !string.IsNullOrWhiteSpace(Street)
        && !string.IsNullOrWhiteSpace(PostalCode) && !string.IsNullOrWhiteSpace(City)
        && (!string.IsNullOrWhiteSpace(VatId) || !string.IsNullOrWhiteSpace(TaxNumber));
}

/// <summary>Neue Firmendaten; <see cref="Version"/> ist der zuletzt gelesene Stand.</summary>
public sealed record UpdateCompanyProfileRequest(string LegalName, string Street, string PostalCode, string City, string Country, string Email,
    string Phone, string Website, string VatId, string TaxNumber, string ManagingDirector, string RegisterCourt, string RegisterNumber,
    string BankName, string Iban, string Bic, string InvoicePrefix, int PaymentTermDays, decimal VatRate, bool SmallBusiness, string FooterNote,
    int Version);

/// <summary>Rechnungsanschrift eines Mandanten. <see cref="CustomerNumber"/> vergibt die Plattform beim ersten Speichern.</summary>
public sealed record TenantBilling(Guid TenantId, string TenantName, bool TenantActive, int? CustomerNumber, string RecipientName, string Street,
    string PostalCode, string City, string Country, string? BillingEmail, string VatId, DateOnly SubscriptionStart, int Version)
{
    /// <summary>Noch nie gespeichert: Werte sind Vorschläge aus dem Mandanten.</summary>
    public bool IsNew => Version == 0;

    public bool IsComplete => !string.IsNullOrWhiteSpace(RecipientName) && !string.IsNullOrWhiteSpace(Street)
        && !string.IsNullOrWhiteSpace(PostalCode) && !string.IsNullOrWhiteSpace(City);
}

/// <summary>Rechnungsanschrift speichern; <see cref="Version"/> 0 legt sie an.</summary>
public sealed record UpdateTenantBillingRequest(string RecipientName, string Street, string PostalCode, string City, string Country,
    string? BillingEmail, string VatId, DateOnly SubscriptionStart, int Version);

/// <summary>Arten von Vorlagen und Dokumenten.</summary>
public static class DocumentKinds
{
    public const string Invoice = "invoice";
    public const string Letter = "letter";
    /// <summary>Stornorechnung: entsteht nur beim Stornieren einer Rechnung, nutzt deren Vorlage.</summary>
    public const string Cancellation = "cancellation";

    public static readonly IReadOnlyList<string> TemplateKinds = [Invoice, Letter];
}

/// <summary>Status eines erzeugten Dokuments.</summary>
public static class DocumentStatus
{
    public const string Draft = "draft";
    public const string Issued = "issued";
    public const string Sent = "sent";
    public const string Paid = "paid";
    public const string Cancelled = "cancelled";
}

/// <summary>Vorlage aus dem Dokumentdesigner (ohne Inhalt).</summary>
public sealed record DocumentTemplateSummary(Guid Id, string Kind, string Name, string? Description, IReadOnlyList<string> Placeholders,
    DateTimeOffset UpdatedAt, string UpdatedBy, int Version);

/// <summary>Vorlage mit Inhalt (Word-Dokument, Base64).</summary>
public sealed record DocumentTemplate(Guid Id, string Kind, string Name, string? Description, string ContentBase64, int Version);

/// <summary>Neue Vorlage. Ohne Inhalt entsteht die Standardvorlage der Art (Rechnung oder Brief).</summary>
public sealed record CreateDocumentTemplateRequest(string Kind, string Name, string? Description = null, string? ContentBase64 = null);

/// <summary>Vorlage speichern (aus dem Designer). <see cref="ContentBase64"/> leer lässt den Inhalt unverändert.</summary>
public sealed record UpdateDocumentTemplateRequest(string Name, string? Description, string? ContentBase64, int Version);

/// <summary>Ein Platzhalter, den der Designer anbietet.</summary>
public sealed record DocumentPlaceholder(string Key, string Group, string Description, string DescriptionEn, bool IsTableRow = false)
{
    public string Token => "{{" + Key + "}}";
}

/// <summary>Position einer Rechnung. <see cref="UsePackagePrice"/>: Einzelpreis aus dem Paketpreis des Mandanten im Leistungsmonat.</summary>
public sealed record DocumentLine(string Description, decimal Quantity, decimal UnitNet, bool UsePackagePrice = false)
{
    public decimal Net => decimal.Round(Quantity * UnitNet, 2, MidpointRounding.AwayFromZero);
}

/// <summary>
/// Serienlauf: dieselbe Vorlage für mehrere Mandanten. Je Mandant entsteht ein Entwurf; ausgestellt und versendet wird danach.
/// Bei Rechnungen sind Positionen und Leistungszeitraum Pflicht.
/// </summary>
public sealed record CreateDocumentSeriesRequest(Guid TemplateId, IReadOnlyList<Guid> TenantIds, string Title, DateOnly IssueDate,
    DateOnly? ServiceFrom = null, DateOnly? ServiceTo = null, IReadOnlyList<DocumentLine>? Lines = null);

/// <summary>Erzeugtes Dokument (ohne Datei).</summary>
public sealed record IssuedDocument(Guid Id, Guid RunId, string Kind, Guid TenantId, string TenantName, Guid TemplateId, string Title, string? Number,
    string Status, DateOnly IssueDate, DateOnly? ServiceFrom, DateOnly? ServiceTo, DateOnly? DueDate, IReadOnlyList<DocumentLine> Lines,
    decimal Net, decimal VatRate, decimal Vat, decimal Gross, Guid? CancelsId, string? FileType, DateTimeOffset? SentAt, string? SentTo,
    DateOnly? PaidOn, DateTimeOffset CreatedAt, string CreatedBy, int Version);

/// <summary>Aktion für mehrere Dokumente (Ausstellen, Versenden).</summary>
public sealed record DocumentBatchRequest(IReadOnlyList<Guid> Ids);

/// <summary>Ergebnis je Dokument; <see cref="Error"/> ist gesetzt, wenn es bei diesem nicht geklappt hat.</summary>
public sealed record DocumentBatchItem(Guid Id, bool Ok, string? Error, IssuedDocument? Document);

public sealed record MarkPaidRequest(DateOnly PaidOn, int Version);

/// <summary>Datei zum Herunterladen.</summary>
public sealed record DocumentFile(string FileName, string ContentType, string ContentBase64);

/// <summary>Regeln für Firmendaten, Anschriften und Serienläufe; gleich in App und API.</summary>
public static class DocumentRules
{
    public const int MaxTemplateBytes = 10 * 1024 * 1024;
    public const int MaxTextLength = 500;
    public const int MaxLines = 50;
    public const int MaxTenantsPerSeries = 500;

    public static IEnumerable<(string Field, string Message)> Check(UpdateCompanyProfileRequest r)
    {
        foreach (var (field, value) in TextFields(r))
            if (value is null || value.Length > MaxTextLength) yield return (field, $"Höchstens {MaxTextLength} Zeichen.");
        if (!string.IsNullOrWhiteSpace(r.Email) && !Validation.IsEmail(r.Email.Trim())) yield return (nameof(r.Email), "Die E-Mail-Adresse ist ungültig.");
        if (!string.IsNullOrWhiteSpace(r.Iban) && !IsIban(r.Iban)) yield return (nameof(r.Iban), "Die IBAN ist ungültig.");
        if (string.IsNullOrWhiteSpace(r.InvoicePrefix) || r.InvoicePrefix.Trim().Length > 10 || !r.InvoicePrefix.Trim().All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
            yield return (nameof(r.InvoicePrefix), "Kürzel für Rechnungsnummern: 1 bis 10 Buchstaben, Ziffern oder Bindestriche.");
        if (r.PaymentTermDays is < 0 or > 365) yield return (nameof(r.PaymentTermDays), "Zahlungsziel: 0 bis 365 Tage.");
        if (r.VatRate is < 0 or > 100 || decimal.Round(r.VatRate, 2) != r.VatRate) yield return (nameof(r.VatRate), "Steuersatz: 0 bis 100 %.");
    }

    private static IEnumerable<(string, string?)> TextFields(UpdateCompanyProfileRequest r) =>
    [
        (nameof(r.LegalName), r.LegalName), (nameof(r.Street), r.Street), (nameof(r.PostalCode), r.PostalCode), (nameof(r.City), r.City),
        (nameof(r.Country), r.Country), (nameof(r.Email), r.Email), (nameof(r.Phone), r.Phone), (nameof(r.Website), r.Website),
        (nameof(r.VatId), r.VatId), (nameof(r.TaxNumber), r.TaxNumber), (nameof(r.ManagingDirector), r.ManagingDirector),
        (nameof(r.RegisterCourt), r.RegisterCourt), (nameof(r.RegisterNumber), r.RegisterNumber), (nameof(r.BankName), r.BankName),
        (nameof(r.Iban), r.Iban), (nameof(r.Bic), r.Bic), (nameof(r.FooterNote), r.FooterNote),
    ];

    public static IEnumerable<(string Field, string Message)> Check(UpdateTenantBillingRequest r)
    {
        if (!Validation.IsName(r.RecipientName)) yield return (nameof(r.RecipientName), "Bitte den Rechnungsempfänger angeben.");
        if (string.IsNullOrWhiteSpace(r.Street) || r.Street.Length > MaxTextLength) yield return (nameof(r.Street), "Bitte Straße und Hausnummer angeben.");
        if (string.IsNullOrWhiteSpace(r.PostalCode) || r.PostalCode.Length > 20) yield return (nameof(r.PostalCode), "Bitte die Postleitzahl angeben.");
        if (string.IsNullOrWhiteSpace(r.City) || r.City.Length > MaxTextLength) yield return (nameof(r.City), "Bitte den Ort angeben.");
        if (r.Country is null || r.Country.Length > MaxTextLength) yield return (nameof(r.Country), $"Höchstens {MaxTextLength} Zeichen.");
        if (!string.IsNullOrWhiteSpace(r.BillingEmail) && !Validation.IsEmail(r.BillingEmail.Trim()))
            yield return (nameof(r.BillingEmail), "Die E-Mail-Adresse ist ungültig.");
        if (r.VatId is null || r.VatId.Length > 30) yield return (nameof(r.VatId), "Höchstens 30 Zeichen.");
    }

    public static IEnumerable<(string Field, string Message)> Check(CreateDocumentSeriesRequest r, string templateKind)
    {
        if (r.TenantIds is null || r.TenantIds.Count == 0) yield return (nameof(r.TenantIds), "Bitte mindestens einen Betrieb auswählen.");
        else if (r.TenantIds.Count > MaxTenantsPerSeries) yield return (nameof(r.TenantIds), $"Höchstens {MaxTenantsPerSeries} Betriebe je Serienlauf.");
        if (!Validation.IsName(r.Title)) yield return (nameof(r.Title), "Bitte einen Titel angeben (höchstens 200 Zeichen).");
        if (templateKind != DocumentKinds.Invoice) yield break;

        if (r.ServiceFrom is not { } from || r.ServiceTo is not { } to || to < from)
            yield return (nameof(r.ServiceFrom), "Bitte den Leistungszeitraum angeben (bis nicht vor von).");
        if (r.Lines is null || r.Lines.Count == 0) yield return (nameof(r.Lines), "Bitte mindestens eine Position angeben.");
        else if (r.Lines.Count > MaxLines) yield return (nameof(r.Lines), $"Höchstens {MaxLines} Positionen.");
        else if (r.Lines.Any(l => !Validation.IsName(l.Description) || l.Quantity <= 0 || l.Quantity > 100_000 || decimal.Round(l.Quantity, 3) != l.Quantity
                     || (!l.UsePackagePrice && (l.UnitNet < 0 || l.UnitNet > PackagePriceRules.MaxAmount || decimal.Round(l.UnitNet, 2) != l.UnitNet))))
            yield return (nameof(r.Lines), "Jede Position braucht eine Beschreibung, eine Menge über 0 und einen Preis ab 0 € mit höchstens zwei Nachkommastellen.");
    }

    /// <summary>Prüfsumme nach ISO 13616 (Modulo 97).</summary>
    public static bool IsIban(string value)
    {
        var iban = new string(value.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToUpperInvariant();
        if (iban.Length is < 15 or > 34 || !iban.All(char.IsAsciiLetterOrDigit) || !char.IsAsciiLetter(iban[0]) || !char.IsAsciiLetter(iban[1])) return false;
        var rearranged = iban[4..] + iban[..4];
        var remainder = 0;
        foreach (var c in rearranged)
        {
            var digits = char.IsAsciiDigit(c) ? (c - '0').ToString() : (c - 'A' + 10).ToString();
            foreach (var d in digits) remainder = (remainder * 10 + (d - '0')) % 97;
        }
        return remainder == 1;
    }

    /// <summary>Summen einer Rechnung: Positionen kaufmännisch gerundet, Steuer auf die Nettosumme.</summary>
    public static (decimal Net, decimal Vat, decimal Gross) Totals(IEnumerable<DocumentLine> lines, decimal vatRate)
    {
        var net = lines.Sum(l => l.Net);
        var vat = decimal.Round(net * vatRate / 100m, 2, MidpointRounding.AwayFromZero);
        return (net, vat, net + vat);
    }

    /// <summary>
    /// Monatlicher Paketpreis eines Mandanten für den Monat, in dem <paramref name="serviceFrom"/> liegt. Die Einführungsphase (kostenlose und
    /// reduzierte Monate) folgt dem Preisstand am Abo-Beginn, danach gilt der reguläre Monatspreis des Stands im Leistungsmonat.
    /// Null, wenn es für einen der beiden Tage keinen Preisstand gibt.
    /// </summary>
    public static decimal? PackageMonthlyNet(IReadOnlyCollection<PackagePrice> prices, DateOnly subscriptionStart, DateOnly serviceFrom)
    {
        var atStart = PackagePriceRules.ActiveOn(prices, subscriptionStart);
        var atService = PackagePriceRules.ActiveOn(prices, serviceFrom);
        if (atStart is null || atService is null) return null;
        var month = (serviceFrom.Year - subscriptionStart.Year) * 12 + serviceFrom.Month - subscriptionStart.Month + 1;
        if (month < 1) return 0m;
        if (month <= atStart.FreeMonths) return 0m;
        if (month <= atStart.FreeMonths + atStart.ReducedMonths) return atStart.ReducedMonthlyNet ?? atService.MonthlyNet;
        return atService.MonthlyNet;
    }
}

/// <summary>Platzhalter, die der Designer anbietet und die beim Erzeugen ersetzt werden.</summary>
public static class DocumentPlaceholders
{
    public const string LinePrefix = "Position.";

    public static readonly IReadOnlyList<DocumentPlaceholder> Company =
    [
        new("Firma.Name", "Firma", "Name des Unternehmens", "Company name"),
        new("Firma.Anschrift", "Firma", "Anschrift in einer Zeile", "Address in one line"),
        new("Firma.Strasse", "Firma", "Straße und Hausnummer", "Street"),
        new("Firma.PLZ", "Firma", "Postleitzahl", "Postal code"),
        new("Firma.Ort", "Firma", "Ort", "City"),
        new("Firma.Land", "Firma", "Land", "Country"),
        new("Firma.EMail", "Firma", "E-Mail-Adresse", "Email"),
        new("Firma.Telefon", "Firma", "Telefon", "Phone"),
        new("Firma.Web", "Firma", "Website", "Website"),
        new("Firma.UStIdNr", "Firma", "USt-IdNr.", "VAT ID"),
        new("Firma.Steuernummer", "Firma", "Steuernummer", "Tax number"),
        new("Firma.Geschaeftsfuehrung", "Firma", "Geschäftsführung", "Managing director"),
        new("Firma.Registergericht", "Firma", "Registergericht", "Register court"),
        new("Firma.Registernummer", "Firma", "Handelsregisternummer", "Register number"),
        new("Firma.Bank", "Firma", "Bank", "Bank"),
        new("Firma.IBAN", "Firma", "IBAN", "IBAN"),
        new("Firma.BIC", "Firma", "BIC", "BIC"),
        new("Firma.Fusszeile", "Firma", "Freier Text für den Fuß", "Footer note"),
    ];

    public static readonly IReadOnlyList<DocumentPlaceholder> Recipient =
    [
        new("Betrieb.Name", "Betrieb", "Name des Betriebs", "Farm name"),
        new("Betrieb.Empfaenger", "Betrieb", "Rechnungsempfänger", "Bill-to name"),
        new("Betrieb.Anschrift", "Betrieb", "Anschrift mehrzeilig (Empfänger, Straße, Ort)", "Address block (multi-line)"),
        new("Betrieb.Strasse", "Betrieb", "Straße und Hausnummer", "Street"),
        new("Betrieb.PLZ", "Betrieb", "Postleitzahl", "Postal code"),
        new("Betrieb.Ort", "Betrieb", "Ort", "City"),
        new("Betrieb.Land", "Betrieb", "Land", "Country"),
        new("Betrieb.EMail", "Betrieb", "E-Mail für Rechnungen", "Billing email"),
        new("Betrieb.UStIdNr", "Betrieb", "USt-IdNr. des Betriebs", "Farm VAT ID"),
        new("Betrieb.Kundennummer", "Betrieb", "Kundennummer", "Customer number"),
    ];

    public static readonly IReadOnlyList<DocumentPlaceholder> Document =
    [
        new("Dokument.Titel", "Dokument", "Titel des Serienlaufs", "Title of the series"),
        new("Dokument.Datum", "Dokument", "Datum des Dokuments", "Document date"),
    ];

    public static readonly IReadOnlyList<DocumentPlaceholder> Invoice =
    [
        new("Rechnung.Nummer", "Rechnung", "Rechnungsnummer", "Invoice number"),
        new("Rechnung.Datum", "Rechnung", "Rechnungsdatum", "Invoice date"),
        new("Rechnung.Leistungszeitraum", "Rechnung", "Leistungszeitraum", "Service period"),
        new("Rechnung.Faellig", "Rechnung", "Fällig am", "Due date"),
        new("Rechnung.Netto", "Rechnung", "Summe netto", "Net total"),
        new("Rechnung.UStSatz", "Rechnung", "Umsatzsteuersatz", "VAT rate"),
        new("Rechnung.USt", "Rechnung", "Umsatzsteuer", "VAT amount"),
        new("Rechnung.Brutto", "Rechnung", "Gesamtbetrag", "Gross total"),
        new("Rechnung.Steuerhinweis", "Rechnung", "Hinweis Kleinunternehmer (§ 19 UStG), sonst leer", "Small business note, else empty"),
        new("Rechnung.Zahlungshinweis", "Rechnung", "Zahlungsaufforderung mit Ziel und IBAN", "Payment instruction"),
        new("Rechnung.Bezug", "Rechnung", "Bei Storno: aufgehobene Rechnung", "Cancellation: cancelled invoice"),
        new("Position.Nr", "Position", "Positionsnummer (Tabellenzeile wird je Position wiederholt)", "Line number (table row repeats)", true),
        new("Position.Beschreibung", "Position", "Beschreibung", "Description", true),
        new("Position.Menge", "Position", "Menge", "Quantity", true),
        new("Position.Einzelpreis", "Position", "Einzelpreis netto", "Unit price", true),
        new("Position.Betrag", "Position", "Betrag netto", "Line amount", true),
    ];

    /// <summary>Alle Platzhalter einer Vorlagenart.</summary>
    public static IReadOnlyList<DocumentPlaceholder> For(string kind) =>
        kind == DocumentKinds.Letter ? [.. Company, .. Recipient, .. Document] : [.. Company, .. Recipient, .. Document, .. Invoice];
}
