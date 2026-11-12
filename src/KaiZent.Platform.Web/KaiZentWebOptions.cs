namespace KaiZent.Platform.Web;

/// <summary>Angaben des Produkts für die gemeinsamen Seiten (Marke, Cookies, Adressen).</summary>
public sealed class KaiZentWebOptions
{
    /// <summary>Vollständiger Name, z. B. "KaiZent LMS".</summary>
    public string ProductName { get; set; } = "KaiZent";

    /// <summary>Kurzname neben der Marke, z. B. "LMS".</summary>
    public string ProductShort { get; set; } = "";

    /// <summary>Unterzeile auf der Anmeldeseite (Deutsch, Englisch).</summary>
    public string TaglineDe { get; set; } = "";
    public string TaglineEn { get; set; } = "";

    /// <summary>Präfix der Cookies, damit Produkte auf derselben Domain sich nicht stören (z. B. "lms" → "lms-auth").</summary>
    public string CookiePrefix { get; set; } = "kz";

    /// <summary>Basisadresse der API (Konfiguration <c>Api:BaseUrl</c>).</summary>
    public string ApiBaseUrl { get; set; } = "http://localhost:5310/";

    /// <summary>Eigene Adresse der Plattform-Administration (Konfiguration <c>Web:AdminHost</c>); null = alles auf einer Adresse.</summary>
    public string? AdminHost { get; set; }

    /// <summary>Ordner für die Schlüssel von Cookie und Antiforgery (Konfiguration <c>DataProtection:KeysPath</c>).</summary>
    public string? KeysPath { get; set; }

    public string AuthCookie => CookiePrefix + "-auth";
    public string ChallengeCookie => CookiePrefix + "-2fa";
    public string RecoveryCodesCookie => CookiePrefix + "-2fa-codes";
    public string LanguageCookie => CookiePrefix + "-lang";
}
