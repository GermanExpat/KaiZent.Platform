using System.Net.Mail;

namespace KaiZent.Platform.Contracts.Common;

/// <summary>Eingaberegeln, die App und API gleich prüfen.</summary>
public static class Validation
{
    public const int MinPasswordLength = 12;
    public const int MaxNameLength = 200;

    /// <summary>Mindestens 12 Zeichen, mit Buchstaben und Ziffern.</summary>
    public static bool IsStrongPassword(string? password) =>
        password is { Length: >= MinPasswordLength and <= 256 } && password.Any(char.IsLetter) && password.Any(char.IsDigit);

    public const string PasswordRule = "Das Passwort braucht mindestens 12 Zeichen mit Buchstaben und Ziffern.";

    public static bool IsEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email) || email.Length > 254 || email.Trim() != email) return false;
        return MailAddress.TryCreate(email, out var parsed) && parsed.Address == email && email.Contains('.', StringComparison.Ordinal);
    }

    public static bool IsName(string? name) => !string.IsNullOrWhiteSpace(name) && name.Trim().Length <= MaxNameLength;
}
