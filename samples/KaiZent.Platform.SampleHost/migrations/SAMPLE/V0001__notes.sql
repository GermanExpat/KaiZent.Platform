-- Beispiel-Fachtabelle je Mandant
create schema if not exists sample;

create table sample.note (
    id          uuid        primary key,
    text        text        not null,
    created_at  timestamptz not null default now()
);
