# KaiZent.Platform

Gemeinsames Fundament der KaiZent-Produkte (LMS, später VMS): Anmeldung, Mandanten mit eigener Datenbank, Plattform-Administration,
Benutzereinstellungen (Farbschema, Sprache), Protokoll, Verschlüsselung von Zugangsdaten. Die Produkte liefern nur ihren Fachteil.

## Pakete

| Paket | Inhalt |
|---|---|
| `KaiZent.Platform.Contracts` | Routen, DTOs, Rollen, Validierung, Farbschemata (DevExpress-Standardthemes), Sprachen DE/EN |
| `KaiZent.Platform.Data` | Migrationen für Katalog und Mandanten, Verbindungen je Mandant, Protokoll, Verschlüsselung |
| `KaiZent.Platform.Api` | Anmeldung (JWT, Sperre nach Fehlversuchen, Zwei-Faktor), Mandanten anlegen/sperren, Paketpreise mit Gültig-ab und Einführungsphase, Benutzer, Einstellungen |
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

## Tests

```bash
KAIZENT_TEST_POSTGRES="Host=localhost;Username=postgres;Password=postgres" dotnet test tests/KaiZent.Platform.Tests
```
