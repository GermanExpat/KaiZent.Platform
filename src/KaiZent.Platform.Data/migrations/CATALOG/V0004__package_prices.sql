-- Preisstände des einen Abo-Pakets. Ein Stand gilt ab valid_from bis zum nächsten; frühere Stände bleiben als Verlauf erhalten.
-- Einführungsphase je Mandant ab Abo-Beginn: free_months kostenlos, danach reduced_months zum reduced_monthly_net.
create table catalog.package_price (
    id                   uuid          primary key,
    valid_from           date          not null unique,
    monthly_net          numeric(10,2) not null check (monthly_net > 0),
    yearly_net           numeric(10,2) not null check (yearly_net > 0),
    free_months          integer       not null default 0 check (free_months >= 0),
    reduced_months       integer       not null default 0 check (reduced_months >= 0),
    reduced_monthly_net  numeric(10,2) null check (reduced_monthly_net >= 0),
    note                 text          null,
    created_at           timestamptz   not null default now(),
    created_by           text          not null,
    constraint package_price_intro check (free_months + reduced_months <= 36),
    constraint package_price_reduced check ((reduced_months = 0) = (reduced_monthly_net is null))
);
