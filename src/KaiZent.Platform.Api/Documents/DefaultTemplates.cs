using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using KaiZent.Platform.Contracts.Billing;

namespace KaiZent.Platform.Api.Documents;

/// <summary>
/// Standardvorlagen (A4, DIN-5008-nah) als Ausgangspunkt für den Dokumentdesigner: Absenderzeile, Anschriftfeld, Angaben rechts,
/// Positionstabelle mit Summen und ein Fuß mit Firmen- und Bankdaten. Alles danach passt die Administration im Designer an.
/// </summary>
public static class DefaultTemplates
{
    public static byte[] For(string kind) => kind == DocumentKinds.Letter ? Letter() : Invoice();

    public static byte[] Invoice() => Build(body =>
    {
        AddAddressBlock(body, [("Rechnungsnummer", "{{Rechnung.Nummer}}"), ("Rechnungsdatum", "{{Rechnung.Datum}}"),
            ("Kundennummer", "{{Betrieb.Kundennummer}}"), ("Leistungszeitraum", "{{Rechnung.Leistungszeitraum}}")]);
        body.Append(Para("{{Dokument.Titel}}", bold: true, size: 28, spaceBefore: 480, spaceAfter: 240));
        body.Append(Para("Sehr geehrte Damen und Herren,", spaceAfter: 120));
        body.Append(Para("für die Nutzung von KaiZent stellen wir Ihnen folgende Leistungen in Rechnung:", spaceAfter: 240));

        var table = Table([600, 4900, 900, 1400, 1400]);
        table.Append(Row(true, "Pos.", "Beschreibung", "Menge", "Einzelpreis", "Betrag"));
        table.Append(Row(false, "{{Position.Nr}}", "{{Position.Beschreibung}}", "{{Position.Menge}}", "{{Position.Einzelpreis}}", "{{Position.Betrag}}"));
        body.Append(table);

        var totals = Table([6400, 1400, 1400], borders: false);
        totals.Append(Row(false, "", "Summe netto", "{{Rechnung.Netto}}"));
        totals.Append(Row(false, "", "USt. {{Rechnung.UStSatz}}", "{{Rechnung.USt}}"));
        totals.Append(Row(true, "", "Gesamtbetrag", "{{Rechnung.Brutto}}"));
        body.Append(totals);

        body.Append(Para("{{Rechnung.Steuerhinweis}}", spaceBefore: 240));
        body.Append(Para("{{Rechnung.Zahlungshinweis}}", spaceBefore: 120));
        body.Append(Para("Vielen Dank für Ihr Vertrauen.", spaceBefore: 240));
        body.Append(Para("{{Firma.Name}}"));
    });

    public static byte[] Letter() => Build(body =>
    {
        AddAddressBlock(body, [("Kundennummer", "{{Betrieb.Kundennummer}}"), ("Datum", "{{Dokument.Datum}}")]);
        body.Append(Para("{{Dokument.Titel}}", bold: true, size: 28, spaceBefore: 480, spaceAfter: 240));
        body.Append(Para("Sehr geehrte Damen und Herren,", spaceAfter: 120));
        body.Append(Para("hier steht Ihr Text.", spaceAfter: 240));
        body.Append(Para("Mit freundlichen Grüßen"));
        body.Append(Para("{{Firma.Name}}"));
    });

    private static byte[] Build(Action<Body> content)
    {
        using var stream = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            AddStyles(main);
            var footer = main.AddNewPart<FooterPart>();
            footer.Footer = new Footer(
                Para("{{Firma.Name}} · {{Firma.Anschrift}} · {{Firma.EMail}} · {{Firma.Telefon}}", size: 14, align: JustificationValues.Center),
                Para("{{Firma.Bank}} · IBAN {{Firma.IBAN}} · BIC {{Firma.BIC}} · USt-IdNr. {{Firma.UStIdNr}} · St.-Nr. {{Firma.Steuernummer}}",
                    size: 14, align: JustificationValues.Center),
                Para("{{Firma.Geschaeftsfuehrung}} · {{Firma.Registergericht}} {{Firma.Registernummer}} · {{Firma.Fusszeile}}", size: 14,
                    align: JustificationValues.Center));

            var body = new Body();
            content(body);
            body.Append(new SectionProperties(
                new FooterReference { Type = HeaderFooterValues.Default, Id = main.GetIdOfPart(footer) },
                new PageSize { Width = 11906, Height = 16838 },
                new PageMargin { Top = 1134, Right = 1134, Bottom = 1418, Left = 1418, Header = 567, Footer = 567, Gutter = 0 }));
            main.Document = new Document(body);
            main.Document.Save();
        }
        return stream.ToArray();
    }

    private static void AddStyles(MainDocumentPart main)
    {
        var styles = main.AddNewPart<StyleDefinitionsPart>();
        styles.Styles = new Styles(
            new DocDefaults(
                new RunPropertiesDefault(new RunPropertiesBaseStyle(
                    new RunFonts { Ascii = "Arial", HighAnsi = "Arial", ComplexScript = "Arial", EastAsia = "Arial" },
                    new FontSize { Val = "20" }, new FontSizeComplexScript { Val = "20" }, new Languages { Val = "de-DE" })),
                new ParagraphPropertiesDefault(new ParagraphPropertiesBaseStyle(new SpacingBetweenLines { After = "0", Line = "264", LineRule = LineSpacingRuleValues.Auto }))),
            new Style(new StyleName { Val = "Normal" }, new PrimaryStyle()) { Type = StyleValues.Paragraph, StyleId = "Normal", Default = true },
            new Style(new StyleName { Val = "Table Grid" }) { Type = StyleValues.Table, StyleId = "TableGrid" });
        styles.Styles.Save();
    }

    /// <summary>Links Rücksendezeile und Anschrift, rechts die Angaben zum Dokument.</summary>
    private static void AddAddressBlock(Body body, (string Label, string Value)[] facts)
    {
        var layout = Table([5300, 4100], borders: false);
        var left = new TableCell(
            new TableCellProperties(new TableCellWidth { Width = "5300", Type = TableWidthUnitValues.Dxa }),
            Para("{{Firma.Name}} · {{Firma.Anschrift}}", size: 14, underline: true, spaceAfter: 120, spaceBefore: 1200),
            Para("{{Betrieb.Anschrift}}"));
        var right = new TableCell(new TableCellProperties(new TableCellWidth { Width = "4100", Type = TableWidthUnitValues.Dxa }));
        right.Append(Para("{{Firma.Name}}", bold: true, size: 24, align: JustificationValues.Right, spaceAfter: 60));
        right.Append(Para("{{Firma.Strasse}}", align: JustificationValues.Right));
        right.Append(Para("{{Firma.PLZ}} {{Firma.Ort}}", align: JustificationValues.Right));
        right.Append(Para("{{Firma.Telefon}}", align: JustificationValues.Right));
        right.Append(Para("{{Firma.EMail}}", align: JustificationValues.Right));
        right.Append(Para("{{Firma.Web}}", align: JustificationValues.Right, spaceAfter: 240));
        foreach (var (label, value) in facts) right.Append(Para($"{label}: {value}", align: JustificationValues.Right));
        layout.Append(new TableRow(left, right));
        body.Append(layout);
    }

    private static Table Table(int[] widths, bool borders = true)
    {
        var props = new TableProperties(
            new TableStyle { Val = "TableGrid" },
            new TableWidth { Width = widths.Sum().ToString(System.Globalization.CultureInfo.InvariantCulture), Type = TableWidthUnitValues.Dxa },
            new TableLayout { Type = TableLayoutValues.Fixed });
        props.Append(borders
            ? new TableBorders(
                new TopBorder { Val = BorderValues.Single, Size = 4, Color = "999999" },
                new BottomBorder { Val = BorderValues.Single, Size = 4, Color = "999999" },
                new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4, Color = "DDDDDD" })
            : new TableBorders(new TopBorder { Val = BorderValues.None }, new BottomBorder { Val = BorderValues.None },
                new LeftBorder { Val = BorderValues.None }, new RightBorder { Val = BorderValues.None },
                new InsideHorizontalBorder { Val = BorderValues.None }, new InsideVerticalBorder { Val = BorderValues.None }));
        var grid = new TableGrid(widths.Select(w => new GridColumn { Width = w.ToString(System.Globalization.CultureInfo.InvariantCulture) }));
        return new Table(props, grid);
    }

    private static TableRow Row(bool bold, params string[] cells)
    {
        var row = new TableRow();
        for (int i = 0; i < cells.Length; i++)
        {
            // Zahlen (ab der dritten Spalte) rechtsbündig
            var align = i >= cells.Length - 3 && i > 1 ? JustificationValues.Right : JustificationValues.Left;
            row.Append(new TableCell(Para(cells[i], bold: bold, align: align, spaceBefore: 40, spaceAfter: 40)));
        }
        return row;
    }

    private static Paragraph Para(string text, bool bold = false, int size = 0, JustificationValues? align = null, int spaceBefore = 0,
        int spaceAfter = 0, bool underline = false)
    {
        var runProps = new RunProperties();
        if (bold) runProps.Append(new Bold());
        if (underline) runProps.Append(new Underline { Val = UnderlineValues.Single });
        if (size > 0) runProps.Append(new FontSize { Val = size.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        var paraProps = new ParagraphProperties();
        if (spaceBefore > 0 || spaceAfter > 0)
            paraProps.Append(new SpacingBetweenLines { Before = spaceBefore.ToString(System.Globalization.CultureInfo.InvariantCulture),
                After = spaceAfter.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        if (align is { } a) paraProps.Append(new Justification { Val = a });
        return new Paragraph(paraProps, new Run(runProps, new Text(text) { Space = SpaceProcessingModeValues.Preserve }));
    }
}
