-- Systemeinstellungen der Plattform-Administration: E-Mail-Versand (SMTP) und KI-Anbindung.
-- Geheimnisse (SMTP-Passwort, API-Schlüssel) nur verschlüsselt mit der SecretBox (v1:…), nie im Klartext.
-- Leerer Host bzw. Anbieter: es gelten die Umgebungsvariablen (Smtp__…, Ai__…) als Rückfall.

create table catalog.mail_settings (
    id                 smallint      primary key default 1 check (id = 1),
    host               text          not null default '',
    port               integer       not null default 587 check (port between 1 and 65535),
    user_name          text          not null default '',
    password_protected text          null,
    from_address       text          not null default '',
    from_name          text          not null default 'KaiZent',
    use_ssl            boolean       not null default true,
    version            integer       not null default 1,
    updated_at         timestamptz   null,
    updated_by         text          not null default ''
);
insert into catalog.mail_settings (id) values (1);

create table catalog.ai_settings (
    id                 smallint      primary key default 1 check (id = 1),
    provider           text          not null default '',
    api_key_protected  text          null,
    model              text          not null default '',
    version            integer       not null default 1,
    updated_at         timestamptz   null,
    updated_by         text          not null default ''
);
insert into catalog.ai_settings (id) values (1);
