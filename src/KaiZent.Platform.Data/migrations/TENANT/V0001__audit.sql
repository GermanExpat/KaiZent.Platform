-- Protokoll des Mandanten: jede Änderung mit Benutzer und Zeitpunkt. Darf nie den Katalog ansprechen.
create schema if not exists audit;

create table audit.audit_log (
    id         bigserial   primary key,
    at         timestamptz not null default now(),
    user_id    uuid        not null,
    user_name  text        not null,
    action     text        not null,
    entity     text        not null,
    entity_id  text        null,
    details    jsonb       null
);

create index audit_log_entity_ix on audit.audit_log (entity, entity_id);
create index audit_log_at_ix on audit.audit_log (at);
