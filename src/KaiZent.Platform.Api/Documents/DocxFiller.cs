using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using KaiZent.Platform.Contracts.Billing;

namespace KaiZent.Platform.Api.Documents;

/// <summary>
/// Befüllt Word-Vorlagen (.docx) aus dem Dokumentdesigner. Platzhalter haben die Form <c>{{Firma.Name}}</c> und dürfen beliebig formatiert sein;
/// Word und der Designer teilen Text oft in mehrere Abschnitte (Runs), deshalb wird je Absatz über alle Textstücke hinweg gesucht.
/// Eine Tabellenzeile mit <c>{{Position.…}}</c> wird je Position wiederholt. Zeilenumbrüche im Wert werden zu Zeilenumbrüchen in Word.
/// </summary>
public static partial class DocxFiller
{
    [GeneratedRegex(@"\{\{\s*([A-Za-zÄÖÜäöüß0-9_.]+)\s*\}\}")]
    private static partial Regex PlaceholderPattern();

    /// <summary>Prüft, ob die Bytes ein Word-Dokument sind, und liefert die enthaltenen Platzhalter (ohne Klammern, ohne Doppelte).</summary>
    public static IReadOnlyList<string>? ReadPlaceholders(byte[] docx)
    {
        try
        {
            using var stream = new MemoryStream(docx, writable: false);
            using var doc = WordprocessingDocument.Open(stream, false);
            if (doc.MainDocumentPart?.Document?.Body is null) return null;
            var found = new List<string>();
            foreach (var part in TextParts(doc))
                foreach (var p in part.Descendants<Paragraph>())
                    foreach (Match m in PlaceholderPattern().Matches(TextOf(p)))
                        if (!found.Contains(m.Groups[1].Value)) found.Add(m.Groups[1].Value);
            return found;
        }
        catch (Exception e) when (e is OpenXmlPackageException or InvalidDataException or FileFormatException or IOException
                                      or InvalidOperationException or System.Xml.XmlException)
        {
            return null;
        }
    }

    /// <summary>
    /// Setzt die Werte ein. <paramref name="lines"/> füllt Tabellenzeilen mit Positions-Platzhaltern (eine Zeile je Eintrag; keine Einträge:
    /// die Zeile entfällt). Unbekannte Platzhalter bleiben sichtbar stehen, damit sie beim Lesen auffallen.
    /// </summary>
    public static byte[] Fill(byte[] docx, IReadOnlyDictionary<string, string> values, IReadOnlyList<IReadOnlyDictionary<string, string>> lines)
    {
        using var stream = new MemoryStream();
        stream.Write(docx);
        stream.Position = 0;
        using (var doc = WordprocessingDocument.Open(stream, true))
        {
            foreach (var part in TextParts(doc))
            {
                foreach (var row in part.Descendants<TableRow>().Where(IsLineRow).ToList())
                {
                    foreach (var line in lines)
                    {
                        var copy = (TableRow)row.CloneNode(true);
                        foreach (var p in copy.Descendants<Paragraph>().ToList()) FillParagraph(p, line);
                        row.InsertBeforeSelf(copy);
                    }
                    row.Remove();
                }
                foreach (var p in part.Descendants<Paragraph>().ToList()) FillParagraph(p, values);
            }
            doc.Save();
        }
        return stream.ToArray();
    }

    private static bool IsLineRow(TableRow row) =>
        row.Descendants<Paragraph>().Any(p => TextOf(p).Contains("{{" + DocumentPlaceholders.LinePrefix, StringComparison.Ordinal))
        // verschachtelte Tabellen: nur die innerste Zeile wiederholen
        && !row.Descendants<TableRow>().Any(IsLineRow);

    private static string TextOf(Paragraph p) => string.Concat(DirectTexts(p).Select(t => t.Text));

    private static List<Text> DirectTexts(Paragraph p) => p.Descendants<Text>().Where(t => t.Ancestors<Paragraph>().First() == p).ToList();

    private static IEnumerable<OpenXmlPartRootElement> TextParts(WordprocessingDocument doc)
    {
        var main = doc.MainDocumentPart;
        if (main?.Document is { } body) yield return body;
        if (main is null) yield break;
        foreach (var h in main.HeaderParts) if (h.Header is { } header) yield return header;
        foreach (var f in main.FooterParts) if (f.Footer is { } footer) yield return footer;
    }

    private static void FillParagraph(Paragraph p, IReadOnlyDictionary<string, string> values)
    {
        // nur direkte Texte dieses Absatzes (verschachtelte Absätze, z. B. in Textfeldern, kommen selbst dran)
        var texts = DirectTexts(p);
        if (texts.Count == 0) return;
        var full = string.Concat(texts.Select(t => t.Text));
        if (!full.Contains("{{", StringComparison.Ordinal)) return;

        var starts = new int[texts.Count];
        for (int i = 1; i < texts.Count; i++) starts[i] = starts[i - 1] + texts[i - 1].Text.Length;

        // von hinten nach vorn, damit die Positionen davor gültig bleiben
        foreach (var m in PlaceholderPattern().Matches(full).Reverse())
        {
            if (!values.TryGetValue(m.Groups[1].Value, out var value)) continue;
            var (first, firstOffset) = Locate(starts, m.Index);
            var (last, lastOffset) = Locate(starts, m.Index + m.Length - 1);
            var prefix = texts[first].Text[..firstOffset];
            var suffix = texts[last].Text[(lastOffset + 1)..];
            for (int i = first + 1; i <= last; i++) texts[i].Text = i == last ? suffix : "";
            SetValue(texts[first], prefix, value, first == last ? suffix : "");
        }
    }

    private static (int Index, int Offset) Locate(int[] starts, int position)
    {
        var i = Array.FindLastIndex(starts, s => s <= position);
        return (i, position - starts[i]);
    }

    /// <summary>Wert in ein Textstück schreiben; mehrere Zeilen als Zeilenumbrüche im selben Run.</summary>
    private static void SetValue(Text text, string prefix, string value, string suffix)
    {
        var lines = value.Replace("\r\n", "\n").Split('\n');
        text.Text = prefix + lines[0] + (lines.Length == 1 ? suffix : "");
        text.Space = SpaceProcessingModeValues.Preserve;
        OpenXmlElement anchor = text;
        for (int i = 1; i < lines.Length; i++)
        {
            var br = new Break();
            anchor.InsertAfterSelf(br);
            var t = new Text(lines[i] + (i == lines.Length - 1 ? suffix : "")) { Space = SpaceProcessingModeValues.Preserve };
            br.InsertAfterSelf(t);
            anchor = t;
        }
    }
}
