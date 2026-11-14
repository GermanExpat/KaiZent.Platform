using System.Diagnostics;
using System.Net;
using System.Net.Mail;
using KaiZent.Platform.Api.Settings;
using Microsoft.Extensions.Logging;

namespace KaiZent.Platform.Api.Documents;

/// <summary>
/// Wandelt ein befülltes Word-Dokument in PDF. Ein Produkt kann eine eigene Umsetzung registrieren (z. B. mit der DevExpress Office File API),
/// <b>bevor</b> es <c>AddKaiZentPlatform</c> aufruft. Ohne Umwandlung bleiben Dokumente im Word-Format.
/// </summary>
public interface IDocumentConverter
{
    /// <summary>Kurzname für Protokoll und Anzeige (z. B. <c>libreoffice</c>), null: keine Umwandlung.</summary>
    string? Name { get; }

    /// <summary>PDF-Bytes oder null, wenn diese Umsetzung nicht umwandelt.</summary>
    Task<byte[]?> ToPdfAsync(byte[] docx, CancellationToken ct);
}

/// <summary>Keine Umwandlung: Dokumente bleiben .docx.</summary>
public sealed class NoDocumentConverter : IDocumentConverter
{
    public string? Name => null;
    public Task<byte[]?> ToPdfAsync(byte[] docx, CancellationToken ct) => Task.FromResult<byte[]?>(null);
}

/// <summary>Einstellungen aus <c>Platform:Documents</c>.</summary>
public sealed class DocumentOptions
{
    /// <summary>Pfad zu <c>soffice</c> (LibreOffice). Gesetzt: Dokumente werden als PDF erzeugt.</summary>
    public string? LibreOfficePath { get; set; }

    public int ConvertTimeoutSeconds { get; set; } = 60;
}

/// <summary>PDF über LibreOffice im Hintergrundmodus. Jede Umwandlung nutzt ein eigenes Profil, damit mehrere gleichzeitig laufen können.</summary>
public sealed class LibreOfficeConverter(DocumentOptions options, ILogger<LibreOfficeConverter> log) : IDocumentConverter
{
    public string? Name => "libreoffice";

    public async Task<byte[]?> ToPdfAsync(byte[] docx, CancellationToken ct)
    {
        var work = Path.Combine(Path.GetTempPath(), "kz_convert_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var input = Path.Combine(work, "document.docx");
            await File.WriteAllBytesAsync(input, docx, ct);
            var psi = new ProcessStartInfo(options.LibreOfficePath!)
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            };
            foreach (var arg in new[] { "-env:UserInstallation=file://" + Path.Combine(work, "profile"), "--headless", "--norestore",
                         "--convert-to", "pdf", "--outdir", work, input })
                psi.ArgumentList.Add(arg);
            using var process = Process.Start(psi) ?? throw new InvalidOperationException("LibreOffice ließ sich nicht starten.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, options.ConvertTimeoutSeconds)));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw;
            }
            var output = Path.Combine(work, "document.pdf");
            if (process.ExitCode != 0 || !File.Exists(output))
            {
                log.LogWarning("PDF-Umwandlung fehlgeschlagen (Exit {Code}): {Error}", process.ExitCode, await process.StandardError.ReadToEndAsync(ct));
                throw new InvalidOperationException("Die PDF-Umwandlung ist fehlgeschlagen.");
            }
            return await File.ReadAllBytesAsync(output, ct);
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}

/// <summary>Anhang einer E-Mail.</summary>
public sealed record MailAttachment(string FileName, string ContentType, byte[] Content);

/// <summary>E-Mail-Versand der Plattform (Rechnungen, Serienbriefe).</summary>
public interface IPlatformMailer
{
    bool IsConfigured { get; }

    /// <summary>Sendet die Nachricht; wirft bei Fehlern eine Ausnahme mit lesbarer Meldung.</summary>
    Task SendAsync(string to, string subject, string body, MailAttachment? attachment, string? replyTo, CancellationToken ct);
}

/// <summary>
/// E-Mail über SMTP mit den Einstellungen aus der Plattform-Administration (Rückfall: Abschnitt <c>Smtp</c>). Liest die Einstellungen bei jedem
/// Versand, damit Änderungen in der Administration ohne Neustart gelten.
/// </summary>
public sealed class SmtpPlatformMailer(PlatformSettings settings) : IPlatformMailer
{
    public const int TimeoutSeconds = 30;

    public bool IsConfigured => settings.Mail.IsConfigured;

    public async Task SendAsync(string to, string subject, string body, MailAttachment? attachment, string? replyTo, CancellationToken ct)
    {
        var mail = await settings.GetMailAsync(ct);
        if (!mail.IsConfigured)
            throw new InvalidOperationException("Der E-Mail-Versand ist nicht eingerichtet (Plattform-Administration, Systemeinstellungen).");
        using var message = new MailMessage(new MailAddress(mail.From, mail.FromName), new MailAddress(to))
        {
            Subject = subject, Body = body, BodyEncoding = System.Text.Encoding.UTF8, SubjectEncoding = System.Text.Encoding.UTF8,
        };
        if (!string.IsNullOrWhiteSpace(replyTo)) message.ReplyToList.Add(new MailAddress(replyTo));
        if (attachment is not null)
            message.Attachments.Add(new Attachment(new MemoryStream(attachment.Content), attachment.FileName, attachment.ContentType));
        using var client = new SmtpClient(mail.Host, mail.Port) { EnableSsl = mail.UseSsl, Timeout = TimeoutSeconds * 1000 };
        if (mail.User.Length > 0) client.Credentials = new NetworkCredential(mail.User, mail.Password);
        try
        {
            await client.SendMailAsync(message, ct);
        }
        catch (SmtpException e)
        {
            throw new InvalidOperationException("Versand fehlgeschlagen: " + e.Message, e);
        }
    }
}
