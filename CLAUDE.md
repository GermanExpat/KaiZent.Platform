# KaiZent.Platform

Gemeinsames Fundament der KaiZent-Produkte (zuerst LMS, später VMS). Die Produkte binden die Pakete ein und liefern nur ihren Fachteil.

## Konventionen

- **Stack:** C# / .NET 10, Nullable an, Warnungen sind Fehler. PostgreSQL über Dapper, kein ORM.
- **Pakete:** `KaiZent.Platform.Contracts` (DTOs, Routen, Rollen, Farbschemata), `KaiZent.Platform.Data` (Migrationen, Verbindungen,
  Protokoll, SecretBox), `KaiZent.Platform.Api` (Anmeldung, Mandanten, Plattform-Administration, Einstellungen).
  Ein Produkt beschreibt sich über `PlatformProduct` (Datenbank-Präfix, Mandanten-Migrationen, Module) und ruft
  `AddKaiZentPlatform`, `InitializeKaiZentPlatformAsync` und `MapKaiZentPlatform` auf. Vorbild: `samples/KaiZent.Platform.SampleHost`.
- **Mandanten:** Fachdaten nur in der Mandanten-Datenbank, über `TenantContext`. Der Katalog enthält nie Fachdaten.
  Die Plattform-Administration hat keinen Zugriff auf Mandanteninhalte. Jede Route liegt in einer Gruppe mit `RequireAuthorization(...)`
  und `AccessGuard`; der Test `Every_endpoint_except_login_and_health_requires_authorization` prüft das.
- **Rollen im Mandanten:** `tenant_admin`, `member` (Büro), `staff` (Stallpersonal), `vet` (nur lesen), `owner` (Kundenportal).
  Fachrouten wählen ihre Stufe über `MapTenantApi(prefix, TenantAccess.…)`: `Team` (Standard), `Office`, `TeamAndVetReading`, `Owner`.
- **Migrationen:** `src/KaiZent.Platform.Data/migrations/{CATALOG,TENANT}/V####__name.sql`, Produkte bringen eigene Sätze mit.
  Eine veröffentlichte Migration nie ändern, sondern eine neue anlegen. Mandanten-Migrationen dürfen nie den Katalog ansprechen.
- **SQL:** nur parametrisiert. Einzige Ausnahme: `create/drop database` in `TenantDatabases` mit streng geprüftem Namen.
- **Protokoll:** jede Änderung an Mandantendaten schreibt `TenantContext.AuditAsync`.
- **Tests:** `dotnet test tests/KaiZent.Platform.Tests` mit `KAIZENT_TEST_POSTGRES` (Wegwerf-Datenbanken, nie gegen echte Daten).
- **Veröffentlichen:** Tag `v<Version>` auf main → CI veröffentlicht die Pakete in GitHub Packages.
- **Texte:** jede sichtbare Zeichenkette über `Texts` mit Deutsch und Englisch; der Test `Platform_texts_exist_in_both_languages` prüft die Plattform-Texte.
- **Sprache:** Oberfläche, Doku und Commit-Messages auf Deutsch, Bezeichner im Code auf Englisch. Commits kurz im Imperativ.
