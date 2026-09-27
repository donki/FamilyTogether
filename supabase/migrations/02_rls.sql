-- FamilyLink — Row Level Security, permisos por tabla y funciones auxiliares.
-- Ejecutar despues de 01_schema.sql. Idempotente: se puede relanzar.
--
-- Modelo (ARQUITECTURA §6 y constitucion Mobile §10):
--   - RLS activada en TODAS las tablas; la pertenencia se comprueba con EXISTS sobre group_members.
--   - Las altas y cambios de pertenencia (crear grupo, unirse, aprobar, expulsar, pausa, SOS...)
--     solo por funciones SECURITY DEFINER de 03_functions.sql, que comprueban el rol.
--   - Escritura directa solo donde el contrato la permite: positions (INSERT), zones y
--     zone_subscriptions. Todo lo demas se revoca a anon y authenticated.
--   - Los usuarios anonimos de Supabase usan el rol authenticated (con is_anonymous en el JWT);
--     el rol anon es una peticion sin sesion y no tiene nada que hacer aqui.
--   - service_role (Edge Functions) se salta la RLS: nunca va en el cliente.

alter table public.groups             enable row level security;
alter table public.group_members      enable row level security;
alter table public.invitations        enable row level security;
alter table public.join_requests      enable row level security;
alter table public.key_shares         enable row level security;
alter table public.positions          enable row level security;
alter table public.last_positions     enable row level security;
alter table public.zones              enable row level security;
alter table public.zone_subscriptions enable row level security;
alter table public.zone_events        enable row level security;
alter table public.sos_alerts         enable row level security;
alter table public.sos_targets        enable row level security;
alter table public.push_tokens        enable row level security;
alter table public.account_links      enable row level security;

-- ---------------------------------------------------------------------------
-- Ayudas. security definer para consultar group_members sin recursion de politicas.
-- ---------------------------------------------------------------------------

create or replace function public.is_member(p_group uuid)
returns boolean
language sql
security definer
set search_path = public
stable
as $$
    select exists (
        select 1 from public.group_members
        where group_id = p_group and user_id = auth.uid()
    );
$$;

create or replace function public.is_admin(p_group uuid)
returns boolean
language sql
security definer
set search_path = public
stable
as $$
    select exists (
        select 1 from public.group_members
        where group_id = p_group and user_id = auth.uid() and role = 'admin'
    );
$$;

-- Miembro y sin pausa efectiva: la pausa sin hora de fin, o con la hora de fin aun por llegar,
-- corta el envio. La pausa cuya hora ya paso cuenta como levantada aunque pg_cron no haya pasado.
-- Solo responde sobre uno mismo o sobre companeros de grupo: asi no sirve para averiguar desde
-- fuera quien esta en un grupo.
create or replace function public.is_sharing(p_group uuid, p_user uuid)
returns boolean
language sql
security definer
set search_path = public
stable
as $$
    select exists (
        select 1 from public.group_members m
        where m.group_id = p_group
          and m.user_id = p_user
          and (not m.paused or (m.pause_until is not null and m.pause_until <= now()))
          and (p_user = auth.uid() or public.is_member(p_group))
    );
$$;

revoke all on function public.is_member(uuid)        from public, anon;
revoke all on function public.is_admin(uuid)         from public, anon;
revoke all on function public.is_sharing(uuid, uuid) from public, anon;
grant execute on function public.is_member(uuid)        to authenticated, service_role;
grant execute on function public.is_admin(uuid)         to authenticated, service_role;
grant execute on function public.is_sharing(uuid, uuid) to authenticated, service_role;

-- Las funciones de disparador no se exponen como RPC.
revoke all on function public.positions_to_last() from public, anon, authenticated;
revoke all on function public.touch_updated_at()  from public, anon, authenticated;

-- ---------------------------------------------------------------------------
-- Permisos de tabla. Supabase concede todo a anon y authenticated por defecto; aqui se quita
-- todo y se devuelve solo lo que el contrato permite. La RLS filtra las filas por encima.
-- ---------------------------------------------------------------------------

do $$
declare
    t text;
begin
    foreach t in array array[
        'groups', 'group_members', 'invitations', 'join_requests', 'key_shares',
        'positions', 'last_positions', 'zones', 'zone_subscriptions', 'zone_events',
        'sos_alerts', 'sos_targets', 'push_tokens', 'account_links'
    ] loop
        execute format('revoke all on public.%I from public, anon, authenticated', t);
        execute format('grant select on public.%I to authenticated', t);
        execute format('grant all on public.%I to service_role', t);
    end loop;
end;
$$;

-- positions: INSERT directo (el id lo pone la identidad).
grant insert (group_id, user_id, recorded_at, battery, payload_enc) on public.positions to authenticated;

-- zones: altas, cambios de nombre/geometria y bajas por cualquier miembro. El grupo y el autor
-- no se pueden cambiar despues.
grant insert (id, group_id, name_enc, geo_enc, created_by) on public.zones to authenticated;
grant update (name_enc, geo_enc)                           on public.zones to authenticated;
grant delete                                               on public.zones to authenticated;

-- zone_subscriptions: todo, porque el upsert de PostgREST reescribe todas las columnas enviadas.
grant insert, update, delete on public.zone_subscriptions to authenticated;

-- ---------------------------------------------------------------------------
-- Lectura: miembros del grupo
-- ---------------------------------------------------------------------------

drop policy if exists groups_select on public.groups;
create policy groups_select on public.groups
    for select to authenticated using (public.is_member(id));

drop policy if exists group_members_select on public.group_members;
create policy group_members_select on public.group_members
    for select to authenticated using (public.is_member(group_id));

drop policy if exists invitations_select on public.invitations;
create policy invitations_select on public.invitations
    for select to authenticated using (public.is_member(group_id));

-- El solicitante ve las suyas (asi sabe si le aprobaron y recoge key_box); los admins, las del grupo.
drop policy if exists join_requests_select on public.join_requests;
create policy join_requests_select on public.join_requests
    for select to authenticated using (user_id = auth.uid() or public.is_admin(group_id));

drop policy if exists key_shares_select on public.key_shares;
create policy key_shares_select on public.key_shares
    for select to authenticated using (user_id = auth.uid() or public.is_member(group_id));

-- ---------------------------------------------------------------------------
-- positions / last_positions
-- ---------------------------------------------------------------------------

drop policy if exists positions_select on public.positions;
create policy positions_select on public.positions
    for select to authenticated using (public.is_member(group_id));

-- Solo las propias y solo en los grupos donde se comparte ahora mismo: la pausa se cumple
-- tambien en el servidor, no solo en el movil.
drop policy if exists positions_insert on public.positions;
create policy positions_insert on public.positions
    for insert to authenticated
    with check (user_id = auth.uid() and public.is_sharing(group_id, auth.uid()));

drop policy if exists last_positions_select on public.last_positions;
create policy last_positions_select on public.last_positions
    for select to authenticated using (public.is_member(group_id));

-- ---------------------------------------------------------------------------
-- zones: cualquier miembro las crea, edita y borra
-- ---------------------------------------------------------------------------

drop policy if exists zones_select on public.zones;
create policy zones_select on public.zones
    for select to authenticated using (public.is_member(group_id));

drop policy if exists zones_insert on public.zones;
create policy zones_insert on public.zones
    for insert to authenticated
    with check (public.is_member(group_id) and created_by = auth.uid());

drop policy if exists zones_update on public.zones;
create policy zones_update on public.zones
    for update to authenticated
    using (public.is_member(group_id)) with check (public.is_member(group_id));

drop policy if exists zones_delete on public.zones;
create policy zones_delete on public.zones
    for delete to authenticated using (public.is_member(group_id));

-- ---------------------------------------------------------------------------
-- zone_subscriptions: cada observador las suyas, en grupos donde es miembro, sobre una zona de
-- ese mismo grupo y una persona de ese mismo grupo.
-- ---------------------------------------------------------------------------

drop policy if exists zone_subscriptions_select on public.zone_subscriptions;
create policy zone_subscriptions_select on public.zone_subscriptions
    for select to authenticated using (observer_id = auth.uid());

drop policy if exists zone_subscriptions_insert on public.zone_subscriptions;
create policy zone_subscriptions_insert on public.zone_subscriptions
    for insert to authenticated
    with check (
        observer_id = auth.uid()
        and public.is_member(group_id)
        and exists (select 1 from public.zones z where z.id = zone_id and z.group_id = zone_subscriptions.group_id)
        and exists (select 1 from public.group_members m
                    where m.group_id = zone_subscriptions.group_id and m.user_id = target_id)
    );

drop policy if exists zone_subscriptions_update on public.zone_subscriptions;
create policy zone_subscriptions_update on public.zone_subscriptions
    for update to authenticated
    using (observer_id = auth.uid())
    with check (
        observer_id = auth.uid()
        and public.is_member(group_id)
        and exists (select 1 from public.zones z where z.id = zone_id and z.group_id = zone_subscriptions.group_id)
        and exists (select 1 from public.group_members m
                    where m.group_id = zone_subscriptions.group_id and m.user_id = target_id)
    );

drop policy if exists zone_subscriptions_delete on public.zone_subscriptions;
create policy zone_subscriptions_delete on public.zone_subscriptions
    for delete to authenticated using (observer_id = auth.uid());

-- ---------------------------------------------------------------------------
-- zone_events / SOS
-- ---------------------------------------------------------------------------

drop policy if exists zone_events_select on public.zone_events;
create policy zone_events_select on public.zone_events
    for select to authenticated using (public.is_member(group_id));

-- El propio y los miembros de alguno de los grupos destino.
drop policy if exists sos_alerts_select on public.sos_alerts;
create policy sos_alerts_select on public.sos_alerts
    for select to authenticated using (
        user_id = auth.uid()
        or exists (select 1 from public.sos_targets t
                   where t.sos_id = sos_alerts.id and public.is_member(t.group_id))
    );

drop policy if exists sos_targets_select on public.sos_targets;
create policy sos_targets_select on public.sos_targets
    for select to authenticated using (public.is_member(group_id));

-- ---------------------------------------------------------------------------
-- push_tokens / account_links: solo los propios, solo lectura
-- ---------------------------------------------------------------------------

drop policy if exists push_tokens_select on public.push_tokens;
create policy push_tokens_select on public.push_tokens
    for select to authenticated using (user_id = auth.uid());

drop policy if exists account_links_select on public.account_links;
create policy account_links_select on public.account_links
    for select to authenticated using (user_id = auth.uid());
