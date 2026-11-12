-- Zwei-Faktor-Anmeldung (TOTP nach RFC 6238) und Pflicht zur Zwei-Faktor-Anmeldung je Mandant.
-- Das Geheimnis liegt verschlüsselt (SecretBox), Wiederherstellungscodes nur als Hash.
alter table catalog.app_user
    add column totp_secret          text     null,
    add column totp_enabled         boolean  not null default false,
    add column totp_last_step       bigint   null,
    add column recovery_code_hashes text[]   null;

alter table catalog.tenant
    add column require_two_factor   boolean  not null default false;
