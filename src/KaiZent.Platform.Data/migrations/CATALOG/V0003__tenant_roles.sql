-- Fachrollen im Mandanten: Stallpersonal (staff), Tierarzt nur lesend (vet), Einsteller/Kunde mit eigenem Portal (owner)
alter table catalog.app_user drop constraint app_user_role_check;
alter table catalog.app_user add constraint app_user_role_check
    check (role in ('platform_admin', 'tenant_admin', 'member', 'staff', 'vet', 'owner'));
