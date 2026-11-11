using System.Security.Cryptography;
using System.Text;

namespace KaiZent.Platform.Data;

/// <summary>
/// Verschlüsselt Zugangsdaten der Mandanten (Postfach-Kennwörter, eigene KI-Schlüssel …) mit AES-256-GCM. Der Schlüssel liegt als Datei neben den übrigen Schlüsseln der API
/// (<c>Platform:DataProtection:KeysPath</c>, im Betrieb das Volume <c>apikeys</c>, das auch gesichert wird). Die API legt ihn beim ersten Start an,
/// der Worker liest ihn nur. Ohne Schlüssel sind gespeicherte Kennwörter wertlos: Datenbank allein verrät sie nicht.
/// </summary>
public sealed class SecretBox
{
    public const string FileName = "kaizent-secrets.key";
    private const string Prefix = "v1:";
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly byte[] _key;

    private SecretBox(byte[] key) => _key = key;

    /// <summary>
    /// Kurzer Fingerabdruck des Schlüssels (erste 16 Hex-Zeichen von SHA-256), um eine hinterlegte Kopie zu prüfen, ohne den Schlüssel zu zeigen.
    ///
    /// </summary>
    public string Fingerprint => Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(Convert.ToBase64String(_key))))[..16];

    public static SecretBox FromKey(byte[] key) =>
        key.Length == 32 ? new SecretBox(key) : throw new ArgumentException("Der Schlüssel muss 32 Bytes lang sein.", nameof(key));

    /// <summary>Liest den Schlüssel oder legt ihn an (nur Eigentümer darf lesen). Zwei gleichzeitige Starts einigen sich auf die zuerst geschriebene Datei.</summary>
    public static SecretBox LoadOrCreate(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, FileName);
        if (!File.Exists(path))
        {
            try
            {
                var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                using var file = new FileStream(path, options);
                file.Write(Encoding.ASCII.GetBytes(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))));
            }
            catch (IOException) when (File.Exists(path))
            {
                // ein anderer Prozess war schneller
            }
        }
        return TryLoad(directory) ?? throw new InvalidOperationException($"Schlüsseldatei {path} ist unlesbar.");
    }

    /// <summary>Schlüssel lesen, ohne ihn anzulegen (Worker). null, solange die API ihn noch nicht angelegt hat.</summary>
    public static SecretBox? TryLoad(string directory)
    {
        var path = Path.Combine(directory, FileName);
        if (!File.Exists(path)) return null;
        try
        {
            var key = Convert.FromBase64String(File.ReadAllText(path).Trim());
            return key.Length == 32 ? new SecretBox(key) : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>Eigener Schlüssel für einen Zweck (HKDF-SHA256), z. B. je Mandant für die Dokumentablage. Der Hauptschlüssel verlässt die Klasse nie.</summary>
    public byte[] DeriveKey(string purpose) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, _key, 32, salt: null, info: Encoding.UTF8.GetBytes("kaizent-platform:" + purpose));

    public string Protect(string plain)
    {
        var data = Encoding.UTF8.GetBytes(plain);
        var output = new byte[NonceSize + data.Length + TagSize];
        var nonce = output.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, data, output.AsSpan(NonceSize, data.Length), output.AsSpan(NonceSize + data.Length, TagSize));
        return Prefix + Convert.ToBase64String(output);
    }

    /// <summary>Entschlüsselt; bei falschem Schlüssel oder manipuliertem Wert eine <see cref="CryptographicException"/>.</summary>
    public string Unprotect(string protectedValue)
    {
        if (!protectedValue.StartsWith(Prefix, StringComparison.Ordinal)) throw new CryptographicException("Unbekanntes Format.");
        var input = Convert.FromBase64String(protectedValue[Prefix.Length..]);
        if (input.Length < NonceSize + TagSize) throw new CryptographicException("Wert zu kurz.");
        var length = input.Length - NonceSize - TagSize;
        var plain = new byte[length];
        using var aes = new AesGcm(_key, TagSize);
        aes.Decrypt(input.AsSpan(0, NonceSize), input.AsSpan(NonceSize, length), input.AsSpan(NonceSize + length, TagSize), plain);
        return Encoding.UTF8.GetString(plain);
    }
}
