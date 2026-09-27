-- Family Together — funciones RPC (ARQUITECTURA §6, «Funciones RPC»).
-- Ejecutar despues de 02_rls.sql. Idempotente: se puede relanzar.
--
-- Todas son SECURITY DEFINER con search_path fijo y comprueban auth.uid() antes de tocar nada.
-- Los errores de negocio salen como
--     raise exception using errcode = 'P0001', message = '<clave>'
-- y la app traduce la clave: not_member, not_admin, expired, not_found, already_member,
-- last_admin, paused, not_pending. Sin sesion: errcode 28000, 'auth_required'.
--
-- El servidor no escribe nunca texto del usuario: todo lo que es texto llega ya cifrado (enc1:).

-- ---------------------------------------------------------------------------
-- Ayudas internas (no se exponen como RPC)
-- ---------------------------------------------------------------------------

create or replace function public._require_uid()
returns uuid
language plpgsql
stable
set search_path = public
as $$
declare
    v_uid uuid := auth.uid();
begin
    if v_uid is null then
        raise exception using errcode = '28000', message = 'auth_required';
    end if;
    return v_uid;
end;
$$;

create or replace function public._fail(p_key text)
returns void
language plpgsql
as $$
begin
    raise exception using errcode = 'P0001', message = p_key;
end;
$$;

-- Codigo de invitacion: 8 caracteres de ABCDEFGHJKLMNPQRSTUVWXYZ23456789 (32 simbolos).
-- La aleatoriedad sale de gen_random_uuid() (generador criptografico del nucleo, sin pgcrypto):
-- de los 16 bytes del uuid v4 se usan 8 que son enteramente aleatorios (0-5, 7 y 9; el 6 y el 8
-- llevan la version y la variante). 256 es multiplo de 32, asi que el modulo no sesga.
create or replace function public._new_invitation_code()
returns text
language plpgsql
volatile
set search_path = public
as $$
declare
    v_alphabet constant text := 'ABCDEFGHJKLMNPQRSTUVWXYZ23456789';
    v_bytes    bytea := decode(replace(gen_random_uuid()::text, '-', ''), 'hex');
    v_idx      int[] := array[0, 1, 2, 3, 4, 5, 7, 9];
    v_code     text  := '';
    i          int;
begin
    foreach i in array v_idx loop
        v_code := v_code || substr(v_alphabet, 1 + (get_byte(v_bytes, i) % 32), 1);
    end loop;
    return v_code;
end;
$$;

-- Lo que se borra de un miembro al salir o ser expulsado: sus posiciones en ese grupo (el grupo
-- deja de verle) y las suscripciones de zona en las que aparece como observador o como seguido.
create or replace function public._purge_member_data(p_group uuid, p_user uuid)
returns void
language sql
set search_path = public
as $$
    delete from public.positions          where group_id = p_group and user_id = p_user;
    delete from public.last_positions     where group_id = p_group and user_id = p_user;
    delete from public.zone_subscriptions where group_id = p_group and (observer_id = p_user or target_id = p_user);
    delete from public.key_shares         where group_id = p_group and user_id = p_user;
$$;

revoke all on function public._require_uid()                from public, anon, authenticated;
revoke all on function public._fail(text)                   from public, anon, authenticated;
revoke all on function public._new_invitation_code()        from public, anon, authenticated;
revoke all on function public._purge_member_data(uuid, uuid) from public, anon, authenticated;

-- ---------------------------------------------------------------------------
-- Grupos
-- ---------------------------------------------------------------------------

create or replace function public.create_group(p_name_enc text, p_display_name_enc text, p_avatar_enc text)
returns uuid
language plpgsql
security definer
set search_path = public
as $$
declare
    v_uid uuid := public._require_uid();
    v_id  uuid;
begin
    insert into public.groups (name_enc, created_by)
    values (coalesce(p_name_enc, ''), v_uid)
    returning id into v_id;

    insert into public.group_members (group_id, user_id, role, display_name_enc, avatar_enc)
    values (v_id, v_uid, 'admin', coalesce(p_display_name_enc, ''), coalesce(p_avatar_enc, ''));

    return v_id;
end;
$$;

-- ---------------------------------------------------------------------------
-- Invitaciones y solicitudes (ARQUITECTURA §4)
-- ---------------------------------------------------------------------------

create or replace function public.create_invitation(p_group uuid, p_inv_pub text, p_inv_priv_enc text)
returns table (code text, expires_at timestamptz)
language plpgsql
security definer
set search_path = public
as $$
declare
    v_uid     uuid := public._require_uid();
    v_code    text;
    v_expires timestamptz := now() + interval '5 minutes';
begin
    if not public.is_member(p_group) then
        perform public._fail('not_member');
    end if;
    if not public.is_admin(p_group) then
        perform public._fail('not_admin');
    end if;

    -- Reintento ante colision con un codigo que aun no se haya borrado (vigente o no).
    loop
        v_code := public._new_invitation_code();
        begin
            insert into public.invitations (code, group_id, created_by, expires_at, inv_pub, inv_priv_enc)
            values (v_code, p_group, v_uid, v_expires, p_inv_pub, p_inv_priv_enc);
            exit;
        exception when unique_violation then
            -- otro intento
        end;
    end loop;

    return query select v_code, v_expires;
end;
$$;

-- Para quien se une: la publica de la invitacion. No hace falta ser miembro (aun no lo es).
create or replace function public.invitation_info(p_code text)
returns table (group_id uuid, inv_pub text, expires_at timestamptz)
language plpgsql
security definer
set search_path = public
as $$
declare
    v_inv public.invitations%rowtype;
begin
    perform public._require_uid();

    select * into v_inv from public.invitations i where i.code = upper(trim(p_code));
    if not found then
        perform public._fail('not_found');
    end if;
    if v_inv.expires_at <= now() then
        perform public._fail('expired');
    end if;

    return query select v_inv.group_id, v_inv.inv_pub, v_inv.expires_at;
end;
$$;

create or replace function public.request_join(p_code text, p_req_pub text, p_name_box text)
returns uuid
language plpgsql
security definer
set search_path = public
as $$
declare
    v_uid uuid := public._require_uid();
    v_inv public.invitations%rowtype;
    v_id  uuid;
begin
    select * into v_inv from public.invitations i where i.code = upper(trim(p_code));
    if not found then
        perform public._fail('not_found');
    end if;
    if v_inv.expires_at <= now() then
        perform public._fail('expired');
    end if;

    if exists (select 1 from public.group_members m where m.group_id = v_inv.group_id and m.user_id = v_uid) then
        perform public._fail('already_member');
    end if;

    -- Si ya tiene una pendiente en este grupo (con este codigo o con otro), no se crea otra: se
    -- actualiza esa con los datos nuevos, porque el movil acaba de guardar una privada nueva y con
    -- la anterior no podria abrir key_box.
    update public.join_requests r
       set invitation_code = v_inv.code,
           req_pub         = p_req_pub,
           inv_pub         = v_inv.inv_pub,
           inv_priv_enc    = v_inv.inv_priv_enc,
           name_box        = p_name_box,
           created_at      = now()
     where r.group_id = v_inv.group_id and r.user_id = v_uid and r.status = 'pending'
    returning r.id into v_id;
    if found then
        return v_id;
    end if;

    -- Una invitacion creada antes de la ultima vez que se resolvio una solicitud suya en este grupo
    -- (aprobada o rechazada) no le vale: un expulsado o un rechazado no vuelve con la invitacion de
    -- antes; necesita una nueva.
    if exists (select 1 from public.join_requests r
               where r.group_id = v_inv.group_id and r.user_id = v_uid
                 and r.status <> 'pending' and r.resolved_at >= v_inv.created_at) then
        perform public._fail('expired');
    end if;

    begin
        insert into public.join_requests (group_id, user_id, invitation_code, req_pub, inv_pub, inv_priv_enc, name_box)
        values (v_inv.group_id, v_uid, v_inv.code, p_req_pub, v_inv.inv_pub, v_inv.inv_priv_enc, p_name_box)
        returning id into v_id;
    exception when unique_violation then
        -- Dos llamadas a la vez: la primera crea la fila y la segunda la actualiza con lo suyo.
        update public.join_requests r
           set invitation_code = v_inv.code,
               req_pub         = p_req_pub,
               inv_pub         = v_inv.inv_pub,
               inv_priv_enc    = v_inv.inv_priv_enc,
               name_box        = p_name_box,
               created_at      = now()
         where r.group_id = v_inv.group_id and r.user_id = v_uid and r.status = 'pending'
        returning r.id into v_id;
    end;

    return v_id;
end;
$$;

create or replace function public.approve_request(p_request uuid, p_key_box text)
returns void
language plpgsql
security definer
set search_path = public
as $$
declare
    v_uid uuid := public._require_uid();
    v_req public.join_requests%rowtype;
begin
    select * into v_req from public.join_requests where id = p_request for update;
    if not found then
        perform public._fail('not_found');
    end if;
    if not public.is_admin(v_req.group_id) then
        perform public._fail('not_admin');
    end if;
    if v_req.status <> 'pending' then
        perform public._fail('not_pending');
    end if;

    insert into public.group_members (group_id, user_id, role)
    values (v_req.group_id, v_req.user_id, 'member')
    on conflict (group_id, user_id) do nothing;

    update public.join_requests
       set status = 'approved', key_box = p_key_box, resolved_by = v_uid, resolved_at = now()
     where id = p_request;
end;
$$;

create or replace function public.reject_request(p_request uuid)
returns void
language plpgsql
security definer
set search_path = public
as $$
declare
    v_uid uuid := public._require_uid();
    v_req public.join_requests%rowtype;
begin
    select * into v_req from public.join_requests where id = p_request for update;
    if not found then
        perform public._fail('not_found');
    end if;
    if not public.is_admin(v_req.group_id) then
        perform public._fail('not_admin');
    end if;
    if v_req.status <> 'pending' then
        perform public._fail('not_pending');
    end if;

    update public.join_requests
       set status = 'rejected', resolved_by = v_uid, resolved_at = now()
     where id = p_request;
end;
$$;

-- ---------------------------------------------------------------------------
-- Miembros
-- ---------------------------------------------------------------------------

create or replace function public.update_my_member(p_group uuid, p_display_name_enc text, p_avatar_enc text)
returns void
language plpgsql
security definer
set search_path = public
as $$
declare
    v_uid uuid := public._require_uid();
begin
    update public.group_members
       set display_name_enc = coalesce(p_display_name_enc, ''),
           avatar_enc       = coalesce(p_avatar_enc, '')
     where group_id = p_group and user_id = v_uid;
    if not found then
        perform public._fail('not_member');
    end if;
end;
$$;

create or replace function public.set_role(p_group uuid, p_user uuid, p_role text)
returns void
language plpgsql
security definer
set search_path = public
as $$
declare
    v_old text;
begin
    perform public._require_uid();
    if p_role is null or p_role not in ('admin', 'member') then
        raise exception using errcode = '22023', message = 'invalid_role';
    end if;
    if not public.is_admin(p_group) then
        perform public._fail('not_admin');
    end if;

    -- Bloquea las filas del grupo: dos admins quitandose el rol a la vez no dejan el grupo sin admin.
    perform 1 from public.group_members where group_id = p_group for update;

    select role into v_old from public.group_members where group_id = p_group and user_id = p_user;
    if not found then
        perform public._fail('not_member');
    end if;

    if v_old = 'admin' and p_role = 'member'
       and (select count(*) from public.group_members where group_id = p_group and role = 'admin') <= 1 then
        perform public._fail('last_admin');
    end if;

    update public.group_members set role = p_role where group_id = p_group and user_id = p_user;
end;
$$;

create or replace function public.remove_member(p_group uuid, p_user uuid)
returns void
language plpgsql
security definer
set search_path = public
as $$
declare
    v_role text;
begin
    perform public._require_uid();
    if not public.is_admin(p_group) then
        perform public._fail('not_admin');
    end if;

    perform 1 from public.group_members where group_id = p_group for update;

    select role into v_role from public.group_members where group_id = p_group and user_id = p_user;
    if not found then
        perform public._fail('not_member');
    end if;

    if v_role = 'admin'
       and (select count(*) from public.group_members where group_id = p_group and role = 'admin') <= 1 then
        perform public._fail('last_admin');
    end if;

    delete from public.group_members where group_id = p_group and user_id = p_user;
    perform public._purge_member_data(p_group, p_user);
end;
$$;

create or replace function public.leave_group(p_group uuid)
returns void
language plpgsql
security definer
set search_path = public
as $$
declare
    v_uid     uuid := public._require_uid();
    v_role    text;
    v_members int;
    v_admins  int;
begin
    perform 1 from public.group_members where group_id = p_group for update;

    select role into v_role from public.group_members where group_id = p_group and user_id = v_uid;
    if not found then
        perform public._fail('not_member');
    end if;

    select count(*), count(*) filter (where role = 'admin')
      into v_members, v_admins
      from public.group_members where group_id = p_group;

    -- Unico miembro: el grupo se borra entero (cascada a todo lo del grupo).
    if v_members = 1 then
        delete from public.groups where id = p_group;
        return;
    end if;

    if v_role = 'admin' and v_admins <= 1 then
        perform public._fail('last_admin');
    end if;

    delete from public.group_members where group_id = p_group and user_id = v_uid;
    perform public._purge_member_data(p_group, v_uid);
end;
$$;

-- p_paused = false reanuda (y limpia pause_until). Al pausar se borra la ultima posicion de ese
-- grupo: el mapa pasa a mostrar «En pausa» y no la ultima posicion conocida.
create or replace function public.set_pause(p_group uuid, p_paused boolean, p_until timestamptz)
returns void
language plpgsql
security definer
set search_path = public
as $$
declare
    v_uid uuid := public._require_uid();
    v_paused boolean := coalesce(p_paused, false);
begin
    update public.group_members
       set paused      = v_paused,
           pause_until = case when v_paused then p_until else null end
     where group_id = p_group and user_id = v_uid;
    if not found then
        perform public._fail('not_member');
    end if;

    if v_paused then
        delete from public.last_positions where group_id = p_group and user_id = v_uid;
    end if;
end;
$$;

-- ---------------------------------------------------------------------------
-- Recuperacion de claves (ARQUITECTURA §5)
-- ---------------------------------------------------------------------------

create or replace function public.request_key_share(p_group uuid, p_device_pub text)
returns uuid
language plpgsql
security definer
set search_path = public
as $$
declare
    v_uid uuid := public._require_uid();
    v_id  uuid;
begin
    if not public.is_member(p_group) then
        perform public._fail('not_member');
    end if;

    insert into public.key_shares (group_id, user_id, device_pub)
    values (p_group, v_uid, p_device_pub)
    returning id into v_id;

    return v_id;
end;
$$;

create or replace function public.fulfill_key_share(p_id uuid, p_fulfiller_pub text, p_key_box text)
returns void
language plpgsql
security definer
set search_path = public
as $$
declare
    v_ks public.key_shares%rowtype;
begin
    perform public._require_uid();

    select * into v_ks from public.key_shares where id = p_id for update;
    if not found then
        perform public._fail('not_found');
    end if;
    if not public.is_member(v_ks.group_id) then
        perform public._fail('not_member');
    end if;
    -- La primera entrega gana; las demas no la pisan.
    if v_ks.key_box is not null then
        perform public._fail('not_pending');
    end if;

    update public.key_shares
       set fulfiller_pub = p_fulfiller_pub, key_box = p_key_box, fulfilled_at = now()
     where id = p_id;
end;
$$;

-- ---------------------------------------------------------------------------
-- Avisos
-- ---------------------------------------------------------------------------

-- El token es del movil: pasa a este usuario y deja de ser de cualquier otro.
create or replace function public.register_push_token(p_token text)
returns void
language plpgsql
security definer
set search_path = public
as $$
declare
    v_uid uuid := public._require_uid();
begin
    if p_token is null or length(trim(p_token)) = 0 then
        raise exception using errcode = '22023', message = 'invalid_token';
    end if;

    insert into public.push_tokens (token, user_id, updated_at)
    values (p_token, v_uid, now())
    on conflict (token) do update set user_id = excluded.user_id, updated_at = now();
end;
$$;

-- ---------------------------------------------------------------------------
-- SOS y eventos de zona
-- ---------------------------------------------------------------------------

-- p_targets = [{"group_id":"...","payload_enc":"enc1:..."}]. Vale aunque este en pausa.
-- Idempotente por p_id: el movil reintenta hasta que entra, y un reintento no duplica nada.
-- Los grupos de los que ya no es miembro (SOS encolado sin conexion y enviado despues de salir)
-- se ignoran; si no queda ninguno, not_member.
create or replace function public.create_sos(p_id uuid, p_targets jsonb)
returns void
language plpgsql
security definer
set search_path = public
as $$
declare
    v_uid   uuid := public._require_uid();
    v_owner uuid;
    v_valid int;
begin
    if p_id is null or p_targets is null or jsonb_typeof(p_targets) <> 'array' then
        raise exception using errcode = '22023', message = 'invalid_targets';
    end if;

    select user_id into v_owner from public.sos_alerts where id = p_id;
    if found and v_owner <> v_uid then
        perform public._fail('not_found');
    end if;

    select count(*) into v_valid
      from jsonb_array_elements(p_targets) t
     where public.is_member((t ->> 'group_id')::uuid);
    if v_valid = 0 then
        perform public._fail('not_member');
    end if;

    insert into public.sos_alerts (id, user_id) values (p_id, v_uid)
    on conflict (id) do nothing;

    insert into public.sos_targets (sos_id, group_id, payload_enc)
    select p_id, (t ->> 'group_id')::uuid, coalesce(t ->> 'payload_enc', '')
      from jsonb_array_elements(p_targets) t
     where public.is_member((t ->> 'group_id')::uuid)
    on conflict (sos_id, group_id) do nothing;
end;
$$;

-- Solo el miembro que comparte (en pausa no se informa de zonas). Idempotente por p_id.
create or replace function public.report_zone_event(p_id uuid, p_group uuid, p_zone uuid, p_kind text)
returns void
language plpgsql
security definer
set search_path = public
as $$
declare
    v_uid uuid := public._require_uid();
begin
    if p_kind is null or p_kind not in ('enter', 'exit') then
        raise exception using errcode = '22023', message = 'invalid_kind';
    end if;
    if not public.is_member(p_group) then
        perform public._fail('not_member');
    end if;
    if not public.is_sharing(p_group, v_uid) then
        perform public._fail('paused');
    end if;
    if not exists (select 1 from public.zones where id = p_zone and group_id = p_group) then
        perform public._fail('not_found');
    end if;

    -- Un reintento con el mismo id no hace nada. Un id ajeno tampoco: no se pisa.
    insert into public.zone_events (id, group_id, user_id, zone_id, kind)
    values (p_id, p_group, v_uid, p_zone, p_kind)
    on conflict (id) do nothing;
end;
$$;

-- ---------------------------------------------------------------------------
-- Recuperar la cuenta en un movil nuevo (solo service_role, desde recover-account)
-- ---------------------------------------------------------------------------

-- Pasa todo lo del usuario viejo al nuevo. No borra el viejo de auth.users: lo hace la Edge
-- Function despues, con auth.admin.deleteUser (asi GoTrue invalida tambien sus sesiones).
-- Nunca fusiona: si el nuevo ya pertenece a algun grupo, not_empty.
create or replace function public.transfer_user(p_old uuid, p_new uuid)
returns void
language plpgsql
security definer
set search_path = public
as $$
begin
    if p_old is null or p_new is null or p_old = p_new then
        raise exception using errcode = '22023', message = 'invalid_users';
    end if;
    if exists (select 1 from public.group_members where user_id = p_new) then
        perform public._fail('not_empty');
    end if;

    -- Restos del usuario nuevo sin grupo que podrian chocar con las claves primarias.
    delete from public.last_positions     where user_id = p_new;
    delete from public.zone_subscriptions where observer_id = p_new or target_id = p_new;
    delete from public.join_requests      where user_id = p_new and status = 'pending'
                                            and group_id in (select group_id from public.join_requests
                                                             where user_id = p_old and status = 'pending');

    update public.group_members      set user_id     = p_new where user_id     = p_old;
    update public.positions          set user_id     = p_new where user_id     = p_old;
    update public.last_positions     set user_id     = p_new where user_id     = p_old;
    update public.zone_subscriptions set observer_id = p_new where observer_id = p_old;
    update public.zone_subscriptions set target_id   = p_new where target_id   = p_old;
    update public.zone_events        set user_id     = p_new where user_id     = p_old;
    update public.sos_alerts         set user_id     = p_new where user_id     = p_old;
    update public.join_requests      set user_id     = p_new where user_id     = p_old;
    update public.join_requests      set resolved_by = p_new where resolved_by = p_old;
    update public.account_links      set user_id     = p_new where user_id     = p_old;
    update public.groups             set created_by  = p_new where created_by  = p_old;
    update public.invitations        set created_by  = p_new where created_by  = p_old;
    update public.zones              set created_by  = p_new where created_by  = p_old;

    -- Las peticiones de clave eran del movil viejo (su device_pub) y sus tokens de aviso tambien:
    -- no se pasan. El movil nuevo pide las claves con request_key_share y registra su token.
    delete from public.key_shares  where user_id = p_old;
    delete from public.push_tokens where user_id = p_old;
end;
$$;

-- ---------------------------------------------------------------------------
-- Permisos: las RPC, solo para usuarios con sesion; transfer_user, solo service_role.
-- ---------------------------------------------------------------------------

revoke all on function public.create_group(text, text, text)              from public, anon;
revoke all on function public.create_invitation(uuid, text, text)         from public, anon;
revoke all on function public.invitation_info(text)                       from public, anon;
revoke all on function public.request_join(text, text, text)              from public, anon;
revoke all on function public.approve_request(uuid, text)                 from public, anon;
revoke all on function public.reject_request(uuid)                        from public, anon;
revoke all on function public.update_my_member(uuid, text, text)          from public, anon;
revoke all on function public.set_role(uuid, uuid, text)                  from public, anon;
revoke all on function public.remove_member(uuid, uuid)                   from public, anon;
revoke all on function public.leave_group(uuid)                           from public, anon;
revoke all on function public.set_pause(uuid, boolean, timestamptz)       from public, anon;
revoke all on function public.request_key_share(uuid, text)               from public, anon;
revoke all on function public.fulfill_key_share(uuid, text, text)         from public, anon;
revoke all on function public.register_push_token(text)                   from public, anon;
revoke all on function public.create_sos(uuid, jsonb)                     from public, anon;
revoke all on function public.report_zone_event(uuid, uuid, uuid, text)   from public, anon;
revoke all on function public.transfer_user(uuid, uuid)                   from public, anon, authenticated;

grant execute on function public.create_group(text, text, text)            to authenticated;
grant execute on function public.create_invitation(uuid, text, text)       to authenticated;
grant execute on function public.invitation_info(text)                     to authenticated;
grant execute on function public.request_join(text, text, text)            to authenticated;
grant execute on function public.approve_request(uuid, text)               to authenticated;
grant execute on function public.reject_request(uuid)                      to authenticated;
grant execute on function public.update_my_member(uuid, text, text)        to authenticated;
grant execute on function public.set_role(uuid, uuid, text)                to authenticated;
grant execute on function public.remove_member(uuid, uuid)                 to authenticated;
grant execute on function public.leave_group(uuid)                         to authenticated;
grant execute on function public.set_pause(uuid, boolean, timestamptz)     to authenticated;
grant execute on function public.request_key_share(uuid, text)             to authenticated;
grant execute on function public.fulfill_key_share(uuid, text, text)       to authenticated;
grant execute on function public.register_push_token(text)                 to authenticated;
grant execute on function public.create_sos(uuid, jsonb)                   to authenticated;
grant execute on function public.report_zone_event(uuid, uuid, uuid, text) to authenticated;
grant execute on function public.transfer_user(uuid, uuid)                 to service_role;
