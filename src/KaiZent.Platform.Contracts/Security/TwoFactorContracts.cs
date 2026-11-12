namespace KaiZent.Platform.Contracts.Security;

/// <summary>
/// Antwort auf die Anmeldung mit Passwort (Status 202), wenn noch ein zweiter Faktor fehlt: <see cref="ChallengeToken"/> (5 Minuten gültig)
/// geht mit dem Code aus der Authenticator-App an <c>PlatformRoutes.LoginTwoFactor</c>.
/// <see cref="SetupRequired"/>: die Zwei-Faktor-Anmeldung ist Pflicht, das Konto hat sie aber noch nicht eingerichtet.
/// </summary>
public sealed record TwoFactorChallenge(string ChallengeToken, DateTimeOffset ExpiresAt, bool SetupRequired);

/// <summary>Zweiter Schritt: sechsstelliger Code oder einer der Wiederherstellungscodes.</summary>
public sealed record TwoFactorLoginRequest(string ChallengeToken, string Code);

/// <summary>Stand der Zwei-Faktor-Anmeldung des eigenen Kontos.</summary>
public sealed record TwoFactorStatus(bool Enabled, int RecoveryCodesLeft, bool Required);

/// <summary>Neues Geheimnis zum Einrichten: als QR-Code (SVG) oder zum Abtippen.</summary>
public sealed record TwoFactorSetup(string Secret, string OtpAuthUri, string QrCodeSvg);

/// <summary>Einrichtung bestätigen mit dem ersten Code aus der App. Mit <see cref="ChallengeToken"/> auch während der Anmeldung möglich.</summary>
public sealed record EnableTwoFactorRequest(string Code, string? ChallengeToken = null);

/// <summary>Nach dem Einschalten: Wiederherstellungscodes (nur einmal sichtbar) und, beim Einrichten während der Anmeldung, die Anmeldung selbst.</summary>
public sealed record TwoFactorEnabled(IReadOnlyList<string> RecoveryCodes, LoginResponse? Login);

/// <summary>Abschalten mit dem Passwort und einem aktuellen Code (oder Wiederherstellungscode).</summary>
public sealed record DisableTwoFactorRequest(string Password, string? Code = null);
