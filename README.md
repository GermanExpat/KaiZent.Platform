# KaiZent.Platform

Gemeinsames Fundament der KaiZent-Produkte (LMS, später VMS): Anmeldung, Mandanten mit eigener Datenbank, Plattform-Administration,
Benutzereinstellungen (Farbschema, Sprache), Protokoll, Verschlüsselung von Zugangsdaten. Die Produkte liefern nur ihren Fachteil.

## Pakete

| Paket | Inhalt |
|---|---|
| `KaiZent.Platform.Contracts` | Routen, DTOs, Rollen, Validierung, Farbschemata (DevExpress-Standardthemes), Sprachen DE/EN |
| `KaiZent.Platform.Data` | Migrationen für Katalog und Mandanten, Verbindungen je Mandant, Protokoll, Verschlüsselung |
| `KaiZent.Platform.Api` | Anmeldung (JWT, Sperre nach Fehlversuchen, Zwei-Faktor), Mandanten anlegen/sperren, Paketpreise mit Gültig-ab und Einführungsphase, Firmendaten, Rechnungen und Serienbriefe an Mandanten, Systemeinstellungen (E-Mail, KI), Benutzer, Einstellungen |
| `KaiZent.Platform.Web` | Für die Blazor-Apps: Anmeldeseiten mit Zwei-Faktor, „Mein Konto“ (Farbschema, Sprache, Passwort, Zwei-Faktor), API-Client, Texte DE/EN |

## Einbinden

```csharp
var product = new PlatformProduct("KaiZent LMS", "lms_farm_", [lmsMigrations], ["equine", "livestock"]);
builder.AddKaiZentPlatform(product);
var app = builder.Build();
await app.InitializeKaiZentPlatformAsync();
var api = app.MapKaiZentPlatform();
var horses = api.MapTenantApi("/api/horses");
horses.MapGet("", async (TenantContext ctx) => { ctx.RequireModule("equine"); ... });
```

Vollständiges Beispiel: `samples/KaiZent.Platform.SampleHost`.

## Rechnungen und Serienbriefe an Mandanten

Die Plattform-Administration pflegt ihre Firmendaten (`/api/platform/company`) und je Mandant eine Rechnungsanschrift
(`/api/platform/billing`). Vorlagen sind Word-Dokumente mit Platzhaltern wie `{{Firma.Name}}` oder `{{Betrieb.Anschrift}}`
(Liste: `DocumentPlaceholders`); eine Tabellenzeile mit `{{Position.…}}` wird je Rechnungsposition wiederholt. Ein Serienlauf erzeugt
je ausgewähltem Mandanten einen Entwurf; beim Ausstellen vergibt die Plattform die Rechnungsnummer (`<Kürzel>-<Jahr>-00001`, lückenlos),
erzeugt die Datei und speichert sie unveränderlich. Korrekturen laufen über eine Stornorechnung.

- **PDF:** `Platform:Documents:LibreOfficePath` (Pfad zu `soffice`) schaltet die PDF-Erzeugung mit LibreOffice ein. Ein Produkt kann
  stattdessen eine eigene `IDocumentConverter`-Umsetzung registrieren (vor `AddKaiZentPlatform`). Ohne Umwandler bleiben Dokumente `.docx`.
- **E-Mail:** siehe Systemeinstellungen.

## Systemeinstellungen (E-Mail-Versand, KI)

Die Plattform-Administration pflegt den SMTP-Versand (`/api/platform/settings/mail`) und die KI-Anbindung (`/api/platform/settings/ai`,
Anbieter aus `AiProviders`: Mistral, OpenAI, Anthropic). SMTP-Passwort und API-Schlüssel liegen im Katalog nur mit der `SecretBox`
verschlüsselt und kommen nie im Klartext zurück (nur „hinterlegt“ bzw. die letzten vier Zeichen des Schlüssels). Testfunktionen:
`…/mail/test` schickt eine Test-E-Mail, `…/ai/test` ruft kostenlos die Modellliste des Anbieters ab.

Produkte lesen die gültigen Werte über `PlatformSettings` (`Mail`, `Ai`, `GetMailAsync`, `GetAiAsync`) und versenden über `IPlatformMailer`.
Solange in der Administration kein Host bzw. kein Anbieter gepflegt ist, gelten die Abschnitte `Smtp` (`Host`, `Port`, `User`, `Password`,
`From`, `FromName`, `UseSsl`) und `Ai` (`Provider`, `ApiKey`, `Model`, `TimeoutSeconds`) aus der Konfiguration als Rückfall.

## Tests

```bash
KAIZENT_TEST_POSTGRES="Host=localhost;Username=postgres;Password=postgres" dotnet test tests/KaiZent.Platform.Tests
```
