-- Katalog: Mandanten und Benutzerkonten. Enthält nie Inhalte eines Mandanten.
create schema if not exists catalog;

create table catalog.tenant (
    id                uuid        primary key,
    name              text        not null,
    database_name     text        not null unique,
    is_active         boolean     not null default true,
    -- Fachmodule des Produkts, die für diesen Mandanten freigeschaltet sind (z. B. LMS: equine, livestock)
    modules           text[]      not null default '{}',
    contact_email     text        null,
    default_theme     text        not null default 'fluent-light-blue',
    default_language  text        not null default 'de',
    created_at        timestamptz not null default now(),
    deactivated_at    timestamptz null
);

create table catalog.app_user (
    id                    uuid        primary key,
    tenant_id             uuid        null references catalog.tenant(id),
    email                 text        not null,
    display_name          text        not null,
    password_hash         text        not null,
    role                  text        not null check (role in ('platform_admin', 'tenant_admin', 'member')),
    is_active             boolean     not null default true,
    failed_logins         integer     not null default 0,
    locked_until          timestamptz null,
    -- steigt bei Passwortwechsel, Sperre und Rollenwechsel; ältere Tokens gelten dann nicht mehr
    session_version       integer     not null default 0,
    must_change_password  boolean     not null default false,
    created_at            timestamptz not null default now(),
    last_login_at         timestamptz null,
    -- die Plattform-Administration gehört zu keinem Mandanten, alle anderen zu genau einem
    constraint app_user_tenant_by_role check ((role = 'platform_admin') = (tenant_id is null))
);

-- Eine Adresse darf je Bereich (Plattform, Mandanten) einmal vorkommen
create unique index app_user_email_uq on catalog.app_user (lower(email), (role = 'platform_admin'));
create index app_user_tenant_ix on catalog.app_user (tenant_id);

-- Abgemeldete Tokens bis zu ihrem Ablauf
create table catalog.revoked_token (
    jti         text        primary key,
    expires_at  timestamptz not null
);

-- Fehlversuche je Konto und Absender (IP des Browsers)
create table catalog.login_throttle (
    user_id       uuid        not null references catalog.app_user(id) on delete cascade,
    client        text        not null,
    failures      integer     not null default 0,
    locked_until  timestamptz null,
    updated_at    timestamptz not null default now(),
    primary key (user_id, client)
);

-- Einstellungen je Konto: Farbschema, Sprache, Dichte. Folgen dem Benutzer auf jedes Gerät.
create table catalog.user_preferences (
    user_id     uuid        primary key references catalog.app_user(id) on delete cascade,
    theme       text        not null,
    language    text        not null,
    density     text        not null default 'normal' check (density in ('normal', 'compact')),
    updated_at  timestamptz not null default now()
);

-- Protokoll der Plattform-Administration (Mandant angelegt, deaktiviert …), ohne Inhalte
create table catalog.platform_audit (
    id         bigserial   primary key,
    at         timestamptz not null default now(),
    user_id    uuid        null,
    user_name  text        not null,
    action     text        not null,
    tenant_id  uuid        null,
    details    jsonb       null
);
