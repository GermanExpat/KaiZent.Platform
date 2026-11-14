-- Rechnungen und Serienbriefe der Plattform an die Mandanten (Betriebe). Alles im Katalog; Inhalte der Mandanten bleiben unberührt.

-- Firmendaten des Plattform-Betreibers (genau eine Zeile), erscheinen als Absender und im Fuß der Dokumente
create table catalog.company_profile (
    id                 smallint      primary key default 1 check (id = 1),
    legal_name         text          not null default '',
    street             text          not null default '',
    postal_code        text          not null default '',
    city               text          not null default '',
    country            text          not null default 'Deutschland',
    email              text          not null default '',
    phone              text          not null default '',
    website            text          not null default '',
    vat_id             text          not null default '',
    tax_number         text          not null default '',
    managing_director  text          not null default '',
    register_court     text          not null default '',
    register_number    text          not null default '',
    bank_name          text          not null default '',
    iban               text          not null default '',
    bic                text          not null default '',
    invoice_prefix     text          not null default 'RE',
    payment_term_days  integer       not null default 14 check (payment_term_days between 0 and 365),
    vat_rate           numeric(5,2)  not null default 19 check (vat_rate between 0 and 100),
    -- Kleinunternehmer nach § 19 UStG: keine Umsatzsteuer, Hinweis auf der Rechnung
    small_business     boolean       not null default false,
    footer_note        text          not null default '',
    version            integer       not null default 1,
    updated_at         timestamptz   not null default now(),
    updated_by         text          not null default ''
);
insert into catalog.company_profile (id) values (1);

-- Rechnungsanschrift je Mandant (Stammdaten für die Abrechnung, keine Inhalte)
create table catalog.tenant_billing (
    tenant_id           uuid          primary key references catalog.tenant(id),
    customer_number     integer       generated always as identity (start with 10001) unique,
    recipient_name      text          not null,
    street              text          not null default '',
    postal_code         text          not null default '',
    city                text          not null default '',
    country             text          not null default 'Deutschland',
    billing_email       text          null,
    vat_id              text          not null default '',
    -- Beginn des Abos; ab hier zählt die Einführungsphase des damals gültigen Preisstands
    subscription_start  date          not null,
    version             integer       not null default 1,
    updated_at          timestamptz   not null default now()
);

-- Vorlagen aus dem Dokumentdesigner (Word-Format mit Platzhaltern {{…}})
create table catalog.document_template (
    id           uuid         primary key,
    kind         text         not null check (kind in ('invoice', 'letter')),
    name         text         not null,
    description  text         null,
    content      bytea        not null,
    version      integer      not null default 1,
    created_at   timestamptz  not null default now(),
    updated_at   timestamptz  not null default now(),
    updated_by   text         not null
);
create unique index document_template_name_uq on catalog.document_template (lower(name));

-- Fortlaufende Nummern je Art und Jahr, lückenlos (vergeben erst beim Ausstellen)
create table catalog.document_counter (
    kind  text     not null,
    year  integer  not null,
    last  integer  not null,
    primary key (kind, year)
);

-- Erzeugte Dokumente (Rechnung, Stornorechnung, Serienbrief) je Mandant. Ab dem Ausstellen unveränderlich; nur Status-Felder ändern sich.
create table catalog.issued_document (
    id              uuid           primary key,
    run_id          uuid           not null,
    kind            text           not null check (kind in ('invoice', 'cancellation', 'letter')),
    tenant_id       uuid           not null references catalog.tenant(id),
    template_id     uuid           not null references catalog.document_template(id),
    title           text           not null,
    number          text           null unique,
    status          text           not null default 'draft' check (status in ('draft', 'issued', 'sent', 'paid', 'cancelled')),
    issue_date      date           not null,
    service_from    date           null,
    service_to      date           null,
    due_date        date           null,
    lines           jsonb          not null default '[]',
    net             numeric(12,2)  not null default 0,
    vat_rate        numeric(5,2)   not null default 0,
    vat             numeric(12,2)  not null default 0,
    gross           numeric(12,2)  not null default 0,
    -- Bezug einer Stornorechnung auf die Rechnung, die sie aufhebt
    cancels_id      uuid           null references catalog.issued_document(id),
    -- Platzhalterwerte und Datei zum Zeitpunkt des Ausstellens
    snapshot        jsonb          null,
    file            bytea          null,
    file_type       text           null check (file_type in ('pdf', 'docx')),
    issued_at       timestamptz    null,
    sent_at         timestamptz    null,
    sent_to         text           null,
    paid_on         date           null,
    created_at      timestamptz    not null default now(),
    created_by      text           not null,
    version         integer        not null default 1,
    -- Rechnungen bekommen ihre Nummer beim Ausstellen, Serienbriefe haben keine
    constraint issued_document_number check (case when kind = 'letter' then number is null else (status = 'draft') = (number is null) end),
    constraint issued_document_file check ((status = 'draft') = (file is null))
);
create index issued_document_tenant_ix on catalog.issued_document (tenant_id, issue_date desc);
create index issued_document_run_ix on catalog.issued_document (run_id);

-- Ausgestellte Dokumente bleiben unverändert (GoBD); nur Status, Versand und Zahlung dürfen sich ändern, Entwürfe dürfen weg
create function catalog.issued_document_guard() returns trigger language plpgsql as $$
begin
    if tg_op = 'DELETE' then
        if old.status <> 'draft' then raise exception 'Ausgestellte Dokumente können nicht gelöscht werden.'; end if;
        return old;
    end if;
    if old.status <> 'draft' and (new.number is distinct from old.number or new.file is distinct from old.file
        or new.lines is distinct from old.lines or new.gross is distinct from old.gross or new.tenant_id is distinct from old.tenant_id
        or new.issue_date is distinct from old.issue_date or new.snapshot is distinct from old.snapshot or new.status = 'draft') then
        raise exception 'Ausgestellte Dokumente sind unveränderlich.';
    end if;
    return new;
end $$;
create trigger issued_document_guard before update or delete on catalog.issued_document
    for each row execute function catalog.issued_document_guard();
