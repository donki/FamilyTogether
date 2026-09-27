-- Family Together — esquema PostgreSQL (Supabase)
-- Ejecutar antes que 02_rls.sql. Idempotente: se puede relanzar.
--
-- El contrato es ../../docs/ARQUITECTURA.md §6. Todo el texto del usuario llega ya cifrado
-- (prefijo enc1:, clave del grupo) y aqui solo se guarda: la base nunca ve nombres, avatares ni
-- coordenadas en claro. En claro van identificadores, fechas, bateria, rol, pausa y el codigo de
-- invitacion.
--
-- Ninguna tabla necesita extensiones: gen_random_uuid() es del nucleo desde PostgreSQL 13.

-- ---------------------------------------------------------------------------
-- Grupos y pertenencia
-- ---------------------------------------------------------------------------

create table if not exists public.groups (
    id          uuid        primary key default gen_random_uuid(),
    name_enc    text        not null,
    -- Quien lo creo. Informativo: los permisos los da group_members.role.
    created_by  uuid        references auth.users (id) on delete set null,
    created_at  timestamptz not null default now()
);

create table if not exists public.group_members (
    group_id          uuid        not null references public.groups (id) on delete cascade,
    user_id           uuid        not null references auth.users (id) on delete cascade,
    role              text        not null default 'member' check (role in ('admin', 'member')),
    display_name_enc  text        not null default '',
    avatar_enc        text        not null default '',
    -- Pausa efectiva = paused y (sin hora de fin o con la hora de fin aun por llegar).
    -- pg_cron la levanta cada 5 min cuando pasa pause_until (04_cron.sql).
    paused            boolean     not null default false,
    pause_until       timestamptz,
    joined_at         timestamptz not null default now(),
    primary key (group_id, user_id)
);

create index if not exists group_members_user_idx on public.group_members (user_id);

-- ---------------------------------------------------------------------------
-- Invitaciones y solicitudes de entrada (ARQUITECTURA §4)
-- ---------------------------------------------------------------------------

-- Codigo de 8 caracteres sin ambiguos, valido 5 minutos. Varios vigentes a la vez.
create table if not exists public.invitations (
    code          text        primary key check (code ~ '^[ABCDEFGHJKLMNPQRSTUVWXYZ23456789]{8}$'),
    group_id      uuid        not null references public.groups (id) on delete cascade,
    created_by    uuid        references auth.users (id) on delete set null,
    created_at    timestamptz not null default now(),
    expires_at    timestamptz not null,
    -- Publica ECDH de la invitacion y su privada cifrada con la clave del grupo.
    inv_pub       text        not null,
    inv_priv_enc  text        not null
);

create index if not exists invitations_group_idx   on public.invitations (group_id);
create index if not exists invitations_expires_idx on public.invitations (expires_at);

create table if not exists public.join_requests (
    id               uuid        primary key default gen_random_uuid(),
    group_id         uuid        not null references public.groups (id) on delete cascade,
    user_id          uuid        not null references auth.users (id) on delete cascade,
    -- Sin clave foranea: la invitacion se borra una hora despues de caducar y la solicitud sigue.
    invitation_code  text        not null,
    req_pub          text        not null,
    -- Copia de la invitacion (publica y privada cifrada con la clave del grupo): pg_cron borra la
    -- invitacion una hora despues de caducar y la solicitud tiene que poder aprobarse despues.
    inv_pub          text        not null,
    inv_priv_enc     text        not null,
    -- Nombre visible del solicitante cifrado con ECDH(req_priv, inv_pub).
    name_box         text        not null,
    status           text        not null default 'pending'
                                 check (status in ('pending', 'approved', 'rejected')),
    -- Clave del grupo cifrada con el mismo secreto ECDH. Solo si status = 'approved'.
    key_box          text,
    resolved_by      uuid        references auth.users (id) on delete set null,
    created_at       timestamptz not null default now(),
    resolved_at      timestamptz
);

create index if not exists join_requests_group_idx on public.join_requests (group_id, status);
create index if not exists join_requests_user_idx  on public.join_requests (user_id);

-- Una sola solicitud pendiente por usuario y grupo (request_join devuelve la que ya hay).
create unique index if not exists join_requests_one_pending_idx
    on public.join_requests (group_id, user_id) where status = 'pending';

-- ---------------------------------------------------------------------------
-- Recuperacion de claves de grupo (ARQUITECTURA §5)
-- ---------------------------------------------------------------------------

create table if not exists public.key_shares (
    id             uuid        primary key default gen_random_uuid(),
    group_id       uuid        not null references public.groups (id) on delete cascade,
    user_id        uuid        not null references auth.users (id) on delete cascade,
    device_pub     text        not null,
    fulfiller_pub  text,
    key_box        text,
    created_at     timestamptz not null default now(),
    fulfilled_at   timestamptz
);

create index if not exists key_shares_group_idx on public.key_shares (group_id) where key_box is null;
create index if not exists key_shares_user_idx  on public.key_shares (user_id);

-- ---------------------------------------------------------------------------
-- Posiciones: una fila por grupo en el que se comparte, cifrada con la clave de ese grupo
-- ---------------------------------------------------------------------------

create table if not exists public.positions (
    id           bigint      generated always as identity primary key,
    group_id     uuid        not null references public.groups (id) on delete cascade,
    user_id      uuid        not null references auth.users (id) on delete cascade,
    -- Hora original de la lectura (la cola local puede enviarla mas tarde).
    recorded_at  timestamptz not null,
    battery      smallint    check (battery between 0 and 100),
    payload_enc  text        not null
);

create index if not exists positions_member_time_idx on public.positions (group_id, user_id, recorded_at);
-- Para el borrado diario por antiguedad.
create index if not exists positions_recorded_idx    on public.positions (recorded_at);

-- Ultima posicion de cada miembro en cada grupo: es lo que pinta el mapa.
create table if not exists public.last_positions (
    group_id     uuid        not null references public.groups (id) on delete cascade,
    user_id      uuid        not null references auth.users (id) on delete cascade,
    recorded_at  timestamptz not null,
    battery      smallint,
    payload_enc  text        not null,
    primary key (group_id, user_id)
);

-- La mantiene el disparador: solo se sustituye si la posicion que entra es mas nueva (la cola
-- local puede mandar lecturas atrasadas despues de otras mas recientes).
-- security definer: el usuario no tiene permiso de escritura sobre last_positions.
create or replace function public.positions_to_last()
returns trigger
language plpgsql
security definer
set search_path = public
as $$
begin
    insert into public.last_positions (group_id, user_id, recorded_at, battery, payload_enc)
    values (new.group_id, new.user_id, new.recorded_at, new.battery, new.payload_enc)
    on conflict (group_id, user_id) do update
        set recorded_at = excluded.recorded_at,
            battery     = excluded.battery,
            payload_enc = excluded.payload_enc
        where public.last_positions.recorded_at < excluded.recorded_at;

    return new;
end;
$$;

drop trigger if exists positions_to_last on public.positions;
create trigger positions_to_last
    after insert on public.positions
    for each row execute function public.positions_to_last();

-- ---------------------------------------------------------------------------
-- Zonas, suscripciones y eventos de zona
-- ---------------------------------------------------------------------------

create table if not exists public.zones (
    id          uuid        primary key default gen_random_uuid(),
    group_id    uuid        not null references public.groups (id) on delete cascade,
    name_enc    text        not null,
    -- {"lat":..,"lon":..,"r":..} cifrado: la base no necesita la geometria.
    geo_enc     text        not null,
    created_by  uuid        references auth.users (id) on delete set null default auth.uid(),
    created_at  timestamptz not null default now(),
    updated_at  timestamptz not null default now()
);

create index if not exists zones_group_idx on public.zones (group_id);

create or replace function public.touch_updated_at()
returns trigger
language plpgsql
as $$
begin
    new.updated_at := now();
    return new;
end;
$$;

drop trigger if exists zones_touch on public.zones;
create trigger zones_touch
    before update on public.zones
    for each row execute function public.touch_updated_at();

-- Que avisos quiere cada observador: persona (target) + zona, entrada y/o salida.
create table if not exists public.zone_subscriptions (
    observer_id  uuid    not null references auth.users (id) on delete cascade,
    group_id     uuid    not null references public.groups (id) on delete cascade,
    target_id    uuid    not null references auth.users (id) on delete cascade,
    zone_id      uuid    not null references public.zones (id) on delete cascade,
    on_enter     boolean not null default true,
    on_exit      boolean not null default true,
    primary key (observer_id, target_id, zone_id)
);

-- La consulta de notify: quien sigue a (usuario, zona).
create index if not exists zone_subscriptions_target_idx
    on public.zone_subscriptions (group_id, target_id, zone_id);

-- El id lo pone el movil: asi report_zone_event es idempotente y el aviso se deduplica.
create table if not exists public.zone_events (
    id           uuid        primary key,
    group_id     uuid        not null references public.groups (id) on delete cascade,
    user_id      uuid        not null references auth.users (id) on delete cascade,
    zone_id      uuid        not null references public.zones (id) on delete cascade,
    kind         text        not null check (kind in ('enter', 'exit')),
    occurred_at  timestamptz not null default now()
);

create index if not exists zone_events_group_idx    on public.zone_events (group_id, occurred_at);
create index if not exists zone_events_occurred_idx on public.zone_events (occurred_at);

-- ---------------------------------------------------------------------------
-- SOS: una alerta con una carga cifrada por cada grupo destino
-- ---------------------------------------------------------------------------

create table if not exists public.sos_alerts (
    id          uuid        primary key,
    user_id     uuid        not null references auth.users (id) on delete cascade,
    created_at  timestamptz not null default now()
);

create index if not exists sos_alerts_user_idx    on public.sos_alerts (user_id);
create index if not exists sos_alerts_created_idx on public.sos_alerts (created_at);

create table if not exists public.sos_targets (
    sos_id       uuid not null references public.sos_alerts (id) on delete cascade,
    group_id     uuid not null references public.groups (id) on delete cascade,
    payload_enc  text not null,
    primary key (sos_id, group_id)
);

create index if not exists sos_targets_group_idx on public.sos_targets (group_id);

-- ---------------------------------------------------------------------------
-- Avisos y cuentas vinculadas
-- ---------------------------------------------------------------------------

-- El token es del movil: register_push_token lo reasigna al usuario que lo registra.
create table if not exists public.push_tokens (
    token       text        primary key,
    user_id     uuid        not null references auth.users (id) on delete cascade,
    updated_at  timestamptz not null default now()
);

create index if not exists push_tokens_user_idx on public.push_tokens (user_id);

-- Google: subject = sub. Microsoft: subject = oid. Solo lo escriben las Edge Functions.
create table if not exists public.account_links (
    provider    text        not null check (provider in ('google', 'microsoft')),
    subject     text        not null,
    user_id     uuid        not null references auth.users (id) on delete cascade,
    created_at  timestamptz not null default now(),
    primary key (provider, subject)
);

create index if not exists account_links_user_idx on public.account_links (user_id);
