-- Family Together — prueba local de las migraciones contra un PostgreSQL sin Supabase.
--
-- Simula lo minimo de Supabase (esquema auth con auth.users y auth.uid(), roles anon /
-- authenticated / service_role, privilegios por defecto que concede Supabase y, si no hay
-- pg_cron, un esquema cron de pega), aplica 01-04 DOS veces (tienen que ser relanzables) y
-- ejercita los flujos con varios usuarios. Cualquier fallo corta el script con un error.
--
--   psql -p 54317 -U postgres -d familytogether_test -v ON_ERROR_STOP=1 -f supabase/tests/prueba_local.sql
--
-- OJO: borra y rehace los esquemas public, auth, cron y test de la base a la que se conecte.
-- Usar una base de pruebas, nunca la de Supabase.

\set ON_ERROR_STOP on
\set QUIET on
\pset tuples_only on
\pset format unaligned
-- Cada comprobacion que pasa sale como «WARNING:  ok  ...»; la primera que falla corta el script.
set client_min_messages = warning;

-- ===========================================================================
-- 0. Simulacion de Supabase
-- ===========================================================================

drop schema if exists public cascade;
drop schema if exists auth   cascade;
drop schema if exists test   cascade;
do $$
begin
    if not exists (select 1 from pg_extension where extname = 'pg_cron') then
        execute 'drop schema if exists cron cascade';
    end if;
end;
$$;

do $$
begin
    if not exists (select 1 from pg_roles where rolname = 'anon') then
        create role anon nologin noinherit;
    end if;
    if not exists (select 1 from pg_roles where rolname = 'authenticated') then
        create role authenticated nologin noinherit;
    end if;
    if not exists (select 1 from pg_roles where rolname = 'service_role') then
        create role service_role nologin noinherit bypassrls;
    end if;
end;
$$;

create schema public;
grant usage on schema public to anon, authenticated, service_role;

-- Lo que hace Supabase: todo lo nuevo de public queda concedido a los tres roles. Las migraciones
-- tienen que quitar lo que no toca; si no, esta prueba lo detecta.
alter default privileges in schema public grant all on tables    to anon, authenticated, service_role;
alter default privileges in schema public grant all on sequences to anon, authenticated, service_role;
alter default privileges in schema public grant all on functions to anon, authenticated, service_role;

create schema auth;
grant usage on schema auth to anon, authenticated, service_role;
create table auth.users (id uuid primary key);
create function auth.uid() returns uuid
language sql stable
as $$ select nullif(current_setting('request.jwt.claim.sub', true), '')::uuid $$;
grant execute on function auth.uid() to public;

-- cron de pega si no hay pg_cron (mismo contrato: unschedule falla si no existe la tarea).
do $$
begin
    if exists (select 1 from pg_available_extensions where name = 'pg_cron') then
        return;
    end if;
    create schema cron;
    create table cron.job (
        jobid    bigserial primary key,
        jobname  text unique,
        schedule text not null,
        command  text not null
    );
    create function cron.schedule(p_name text, p_schedule text, p_command text) returns bigint
    language plpgsql as $f$
    declare v bigint;
    begin
        insert into cron.job (jobname, schedule, command) values (p_name, p_schedule, p_command)
        returning jobid into v;
        return v;
    end;
    $f$;
    create function cron.unschedule(p_name text) returns boolean
    language plpgsql as $f$
    begin
        delete from cron.job where jobname = p_name;
        if not found then
            raise exception 'could not find valid entry for job ''%''', p_name;
        end if;
        return true;
    end;
    $f$;
end;
$$;

-- Ayudas de la prueba. No son security definer: se ejecutan con el rol de quien llama.
create schema test;
grant usage on schema test to public;

create function test.ok(p_cond boolean, p_msg text) returns void
language plpgsql as $$
begin
    if p_cond is not true then
        raise exception 'FALLO: %', p_msg;
    end if;
    raise warning 'ok  %', p_msg;
end;
$$;

-- Ejecuta p_sql y exige que falle con esa clave (mensaje) o ese SQLSTATE.
create function test.err(p_sql text, p_expected text, p_msg text) returns void
language plpgsql as $$
begin
    begin
        execute p_sql;
    exception when others then
        if sqlerrm = p_expected or sqlstate = p_expected then
            raise warning 'ok  % (%)', p_msg, p_expected;
            return;
        end if;
        raise exception 'FALLO: % -> esperaba %, llego "%" (%)', p_msg, p_expected, sqlerrm, sqlstate;
    end;
    raise exception 'FALLO: % -> esperaba el error %, y no fallo', p_msg, p_expected;
end;
$$;

-- Filas visibles para el rol actual, en TODAS las tablas de public, que mencionen alguno de esos
-- identificadores en cualquier columna. Para SC-006 tiene que dar 0.
create function test.visible_rows(p_ids uuid[]) returns bigint
language plpgsql as $$
declare
    t     text;
    n     bigint;
    total bigint := 0;
    pats  text[] := (select array_agg('%' || x::text || '%') from unnest(p_ids) x);
begin
    for t in select c.relname from pg_class c join pg_namespace s on s.oid = c.relnamespace
             where s.nspname = 'public' and c.relkind = 'r' order by 1 loop
        begin
            execute format('select count(*) from public.%I x where to_jsonb(x)::text like any ($1)', t)
               into n using pats;
        exception when insufficient_privilege then
            n := 0;   -- ni siquiera puede leer la tabla: tampoco ve nada
        end;
        if n > 0 then
            raise warning '   % ve % filas ajenas en %', current_user, n, t;
        end if;
        total := total + n;
    end loop;
    return total;
end;
$$;

-- Lo mismo sin RLS (como superusuario): cuantas tablas tienen datos de esos identificadores.
create function test.tables_with(p_ids uuid[]) returns int
language plpgsql as $$
declare
    t    text;
    n    bigint;
    k    int := 0;
    pats text[] := (select array_agg('%' || x::text || '%') from unnest(p_ids) x);
begin
    for t in select c.relname from pg_class c join pg_namespace s on s.oid = c.relnamespace
             where s.nspname = 'public' and c.relkind = 'r' loop
        execute format('select count(*) from public.%I x where to_jsonb(x)::text like any ($1)', t)
           into n using pats;
        if n > 0 then k := k + 1; end if;
    end loop;
    return k;
end;
$$;

grant execute on all functions in schema test to public;

-- Usuarios: u1 admin del grupo A, u2 se une a A, u3 admin del grupo B (el de fuera),
-- u4 movil nuevo para recuperar, u5 solicitante rechazado.
\set u1 '00000000-0000-0000-0000-000000000001'
\set u2 '00000000-0000-0000-0000-000000000002'
\set u3 '00000000-0000-0000-0000-000000000003'
\set u4 '00000000-0000-0000-0000-000000000004'
\set u5 '00000000-0000-0000-0000-000000000005'
insert into auth.users (id) values (:'u1'), (:'u2'), (:'u3'), (:'u4'), (:'u5');

\set as_u1   'reset role; set request.jwt.claim.sub to ''00000000-0000-0000-0000-000000000001''; set role authenticated;'
\set as_u2   'reset role; set request.jwt.claim.sub to ''00000000-0000-0000-0000-000000000002''; set role authenticated;'
\set as_u3   'reset role; set request.jwt.claim.sub to ''00000000-0000-0000-0000-000000000003''; set role authenticated;'
\set as_u4   'reset role; set request.jwt.claim.sub to ''00000000-0000-0000-0000-000000000004''; set role authenticated;'
\set as_u5   'reset role; set request.jwt.claim.sub to ''00000000-0000-0000-0000-000000000005''; set role authenticated;'
\set as_anon 'reset role; set request.jwt.claim.sub to ''''; set role anon;'
\set as_svc  'reset role; set request.jwt.claim.sub to ''''; set role service_role;'
\set as_root 'reset role; set request.jwt.claim.sub to '''';'

-- ===========================================================================
-- 1. Migraciones, dos veces
-- ===========================================================================

\echo '== Migraciones (primera pasada)'
\ir ../migrations/01_schema.sql
\ir ../migrations/02_rls.sql
\ir ../migrations/03_functions.sql
\ir ../migrations/04_cron.sql
\echo '== Migraciones (segunda pasada: relanzables)'
\ir ../migrations/01_schema.sql
\ir ../migrations/02_rls.sql
\ir ../migrations/03_functions.sql
\ir ../migrations/04_cron.sql

select test.ok((select count(*) from cron.job where jobname like 'familytogether_%') = 2,
               'pg_cron: dos tareas, sin duplicar al relanzar');
select test.ok((select schedule from cron.job where jobname = 'familytogether_purge_old_data') = '0 3 * * *',
               'pg_cron: purga diaria a las 03:00');
select test.ok((select schedule from cron.job where jobname = 'familytogether_expire') = '*/5 * * * *',
               'pg_cron: caducidades cada 5 min');
select test.ok((select bool_and(c.relrowsecurity) from pg_class c join pg_namespace s on s.oid = c.relnamespace
                where s.nspname = 'public' and c.relkind = 'r')
               and (select count(*) from pg_class c join pg_namespace s on s.oid = c.relnamespace
                    where s.nspname = 'public' and c.relkind = 'r') = 14,
               'RLS activada en las 14 tablas');
select test.ok(not has_function_privilege('authenticated', 'public.transfer_user(uuid,uuid)', 'execute')
               and not has_function_privilege('anon', 'public.transfer_user(uuid,uuid)', 'execute')
               and has_function_privilege('service_role', 'public.transfer_user(uuid,uuid)', 'execute'),
               'transfer_user: solo service_role');
select test.ok(not has_function_privilege('anon', 'public.create_group(text,text,text)', 'execute'),
               'anon no ejecuta RPC');

-- ===========================================================================
-- 2. Grupos
-- ===========================================================================

\echo '== Grupos'
:as_u1
select public.create_group('enc1:grupoA', 'enc1:nombre1', '') as ga \gset
select test.ok((select count(*) from public.groups where id = :'ga') = 1, 'u1 ve su grupo');
select test.ok((select role from public.group_members where group_id = :'ga' and user_id = :'u1') = 'admin',
               'el creador queda como admin');
select test.err($$insert into public.groups (name_enc) values ('x')$$, '42501', 'insert directo en groups prohibido');
select test.err($$update public.group_members set role = 'admin'$$, '42501', 'update directo en group_members prohibido');

:as_u3
select public.create_group('enc1:grupoB', 'enc1:nombre3', '') as gb \gset

-- ===========================================================================
-- 3. Invitaciones y solicitudes
-- ===========================================================================

\echo '== Invitaciones y solicitudes'
:as_u1
select code as code1, expires_at as exp1 from public.create_invitation(:'ga', 'invpubA', 'enc1:invprivA') \gset
select test.ok(:'code1' ~ '^[ABCDEFGHJKLMNPQRSTUVWXYZ23456789]{8}$', 'codigo de 8 caracteres del alfabeto');
select test.ok(:'exp1'::timestamptz between now() + interval '4 minutes 50 seconds' and now() + interval '5 minutes 10 seconds',
               'caduca en 5 minutos');
select code as code2 from public.create_invitation(:'ga', 'invpubA2', 'enc1:invprivA2') \gset
select test.ok(:'code1' <> :'code2' and (select count(*) from public.invitations where group_id = :'ga') = 2,
               'varios codigos vigentes a la vez');

-- Muchos codigos: todos validos y distintos (la funcion interna no es RPC: como superusuario).
:as_root
select test.ok((select count(distinct c) = 500 and bool_and(c ~ '^[ABCDEFGHJKLMNPQRSTUVWXYZ23456789]{8}$')
                from (select public._new_invitation_code() c from generate_series(1, 500)) s) is not false,
               '500 codigos generados, validos');

:as_u2
select test.err(format('select * from public.create_invitation(%L, ''p'', ''q'')', :'ga'), 'not_member',
                'crear invitacion sin ser miembro');
select test.ok((select group_id from public.invitation_info(lower(:'code1'))) = :'ga'::uuid,
               'invitation_info devuelve el grupo (sin distinguir mayusculas)');
select test.ok((select inv_pub from public.invitation_info(:'code1')) = 'invpubA', 'invitation_info devuelve inv_pub');
select test.err($$select * from public.invitation_info('ZZZZZZZZ')$$, 'not_found', 'codigo inexistente');

:as_root
insert into public.invitations (code, group_id, created_by, expires_at, inv_pub, inv_priv_enc)
values ('EXPRED22', :'ga', :'u1', now() - interval '10 minutes', 'x', 'y');

:as_u2
select test.err($$select * from public.invitation_info('EXPRED22')$$, 'expired', 'invitation_info de un codigo caducado');
select test.err($$select public.request_join('EXPRED22', 'r', 'n')$$, 'expired', 'request_join con codigo caducado');
select test.ok((select count(*) from public.join_requests) = 0, 'sin solicitud tras codigo caducado');

select public.request_join(:'code1', 'reqpub2', 'box2') as rid2 \gset
select test.ok(public.request_join(:'code1', 'reqpub2b', 'box2b') = :'rid2'::uuid, 'solicitud duplicada: devuelve la misma');
select test.ok(public.request_join(:'code2', 'reqpub2c', 'box2c') = :'rid2'::uuid, 'con otro codigo del mismo grupo: la misma');
select test.ok((select count(*) from public.join_requests) = 1, 'el solicitante ve solo su solicitud');
select test.ok((select status from public.join_requests where id = :'rid2') = 'pending', 'pendiente');
select test.ok((select invitation_code = :'code2' and req_pub = 'reqpub2c' and name_box = 'box2c'
                        and inv_pub = 'invpubA2' and inv_priv_enc = 'enc1:invprivA2'
                   from public.join_requests where id = :'rid2'),
               'solicitud repetida: actualiza codigo, req_pub, name_box, inv_pub e inv_priv_enc');
select test.ok((select count(*) from public.groups) = 0 and (select count(*) from public.group_members) = 0,
               'pendiente: aun no ve el grupo ni los miembros');
select test.err(format('select public.approve_request(%L, ''k'')', :'rid2'), 'not_admin', 'aprobarse a si mismo');

:as_u1
select test.err(format('select public.request_join(%L, ''r'', ''n'')', :'code1'), 'already_member', 'ya es miembro');
select test.ok((select inv_priv_enc from public.join_requests where group_id = :'ga') = 'enc1:invprivA2',
               'el admin ve la solicitud con inv_priv_enc');

:as_u3
select test.ok((select count(*) from public.join_requests where id = :'rid2') = 0, 'otro grupo no ve la solicitud');
select test.err(format('select public.approve_request(%L, ''k'')', :'rid2'), 'not_admin', 'admin de otro grupo no aprueba');

:as_root
delete from public.invitations where code = :'code2';   -- lo que hace pg_cron una hora despues
:as_u1
select public.approve_request(:'rid2', 'keybox2');
select test.ok(true, 'aprobar despues de borrarse la invitacion');
select test.err(format('select public.approve_request(%L, ''k'')', :'rid2'), 'not_pending', 'aprobar dos veces');
select test.err(format('select public.reject_request(%L)', :'rid2'), 'not_pending', 'rechazar una aprobada');
select test.err($$select public.approve_request('00000000-0000-0000-0000-00000000dead', 'k')$$, 'not_found', 'solicitud inexistente');
select test.ok((select role from public.group_members where group_id = :'ga' and user_id = :'u2') = 'member',
               'aprobado: miembro con rol member');

:as_u2
select test.ok((select status = 'approved' and key_box = 'keybox2' from public.join_requests where id = :'rid2'),
               'el solicitante ve key_box');
select public.update_my_member(:'ga', 'enc1:nombre2', 'enc1:avatar2');
select test.ok((select display_name_enc from public.group_members where group_id = :'ga' and user_id = :'u2') = 'enc1:nombre2',
               'update_my_member');
select test.err(format('select public.update_my_member(%L, ''a'', ''b'')', :'gb'), 'not_member', 'update_my_member en grupo ajeno');
select test.ok((select count(*) from public.groups where id = :'ga') = 1, 'aprobado: ve el grupo');

-- Rechazo
:as_u5
select public.request_join(:'code1', 'reqpub5', 'box5') as rid5 \gset
:as_u1
select public.reject_request(:'rid5');
:as_u5
select test.ok((select status from public.join_requests where id = :'rid5') = 'rejected', 'rechazada: el solicitante lo ve');
select test.ok((select key_box from public.join_requests where id = :'rid5') is null, 'rechazada: sin key_box');
select test.ok((select count(*) from public.groups) = 0, 'rechazado: no ve el grupo');
select test.err(format('select public.request_join(%L, ''r'', ''n'')', :'code1'), 'expired',
                'rechazado: una invitacion anterior al rechazo no le vale');

-- ===========================================================================
-- 4. Posiciones y pausa
-- ===========================================================================

\echo '== Posiciones y pausa'
:as_u1
insert into public.positions (group_id, user_id, recorded_at, battery, payload_enc)
values (:'ga', :'u1', now() - interval '1 minute', 80, 'enc1:p1');
insert into public.positions (group_id, user_id, recorded_at, battery, payload_enc)
values (:'ga', :'u1', now() - interval '10 minutes', 90, 'enc1:p0');
select test.ok((select payload_enc from public.last_positions where group_id = :'ga' and user_id = :'u1') = 'enc1:p1',
               'last_positions: una posicion atrasada no pisa la mas nueva');
insert into public.positions (group_id, user_id, recorded_at, battery, payload_enc)
values (:'ga', :'u1', now(), 79, 'enc1:p2');
select test.ok((select payload_enc from public.last_positions where group_id = :'ga' and user_id = :'u1') = 'enc1:p2',
               'last_positions: la mas nueva sustituye');
select test.err(format($$insert into public.positions (group_id, user_id, recorded_at, battery, payload_enc)
                        values (%L, %L, now(), 1, 'x')$$, :'ga', :'u2'), '42501', 'insertar posicion a nombre de otro');
select test.err(format($$insert into public.positions (group_id, user_id, recorded_at, battery, payload_enc)
                        values (%L, %L, now(), 1, 'x')$$, :'gb', :'u1'), '42501', 'insertar posicion en grupo ajeno');
select test.err($$update public.positions set payload_enc = 'x'$$, '42501', 'update de posiciones prohibido');
select test.err($$insert into public.last_positions (group_id, user_id, recorded_at, payload_enc)
                  values (gen_random_uuid(), auth.uid(), now(), 'x')$$, '42501', 'last_positions no se escribe directo');

:as_u2
insert into public.positions (group_id, user_id, recorded_at, battery, payload_enc)
values (:'ga', :'u2', now(), 50, 'enc1:q1');
select test.ok((select count(*) from public.last_positions where group_id = :'ga') = 2, 'u2 ve las dos ultimas posiciones');
select public.set_pause(:'ga', true, null);
select test.ok((select count(*) from public.last_positions where group_id = :'ga' and user_id = :'u2') = 0,
               'al pausar se borra su ultima posicion');
select test.err(format($$insert into public.positions (group_id, user_id, recorded_at, battery, payload_enc)
                        values (%L, %L, now(), 50, 'x')$$, :'ga', :'u2'), '42501', 'en pausa: insert en positions rechazado');
select public.set_pause(:'ga', true, now() + interval '1 hour');
select test.err(format($$insert into public.positions (group_id, user_id, recorded_at, battery, payload_enc)
                        values (%L, %L, now(), 50, 'x')$$, :'ga', :'u2'), '42501', 'en pausa con hora de fin futura: rechazado');
select test.err(format('select public.set_pause(%L, true, null)', :'gb'), 'not_member', 'pausa en grupo ajeno');

:as_root
update public.group_members set pause_until = now() - interval '1 minute' where group_id = :'ga' and user_id = :'u2';
:as_u2
insert into public.positions (group_id, user_id, recorded_at, battery, payload_enc)
values (:'ga', :'u2', now(), 49, 'enc1:q2');
select test.ok(true, 'pausa con la hora de fin pasada: vuelve a compartir aunque cron no haya pasado');
:as_root
select public.expire_invitations_and_pauses();
select test.ok((select not paused and pause_until is null from public.group_members where group_id = :'ga' and user_id = :'u2'),
               'cron: levanta las pausas vencidas');
:as_u2
select public.set_pause(:'ga', true, null);
select public.set_pause(:'ga', false, now() + interval '3 hours');
select test.ok((select not paused and pause_until is null from public.group_members where group_id = :'ga' and user_id = :'u2'),
               'reanudar limpia pause_until');

-- ===========================================================================
-- 5. Zonas, suscripciones y eventos de zona
-- ===========================================================================

\echo '== Zonas'
:as_u2
insert into public.zones (group_id, name_enc, geo_enc) values (:'ga', 'enc1:casa', 'enc1:geo') returning id as zone1 \gset
select test.ok((select created_by from public.zones where id = :'zone1') = :'u2'::uuid, 'created_by = autor por defecto');
select test.err(format($$insert into public.zones (group_id, name_enc, geo_enc, created_by) values (%L, 'a', 'b', %L)$$, :'ga', :'u1'),
                '42501', 'zona a nombre de otro');
:as_u1
update public.zones set name_enc = 'enc1:casa2' where id = :'zone1';
select test.ok((select name_enc from public.zones where id = :'zone1') = 'enc1:casa2', 'otro miembro edita la zona');
select test.err(format($$update public.zones set group_id = %L where id = %L$$, :'gb', :'zone1'), '42501',
                'no se puede mover la zona de grupo');
insert into public.zone_subscriptions (observer_id, group_id, target_id, zone_id, on_enter, on_exit)
values (:'u1', :'ga', :'u2', :'zone1', true, false);
insert into public.zone_subscriptions (observer_id, group_id, target_id, zone_id, on_enter, on_exit)
values (:'u1', :'ga', :'u2', :'zone1', true, true)
on conflict (observer_id, target_id, zone_id) do update set on_enter = excluded.on_enter, on_exit = excluded.on_exit;
select test.ok((select on_exit from public.zone_subscriptions where zone_id = :'zone1'), 'suscripcion por upsert');
select test.err(format($$insert into public.zone_subscriptions (observer_id, group_id, target_id, zone_id) values (%L, %L, %L, %L)$$,
                       :'u2', :'ga', :'u1', :'zone1'), '42501', 'suscripcion a nombre de otro');
select test.err(format($$insert into public.zone_subscriptions (observer_id, group_id, target_id, zone_id) values (%L, %L, %L, %L)$$,
                       :'u1', :'ga', :'u3', :'zone1'), '42501', 'suscripcion a alguien de fuera del grupo');
:as_u2
select test.ok((select count(*) from public.zone_subscriptions) = 0, 'cada uno ve solo sus suscripciones');
select gen_random_uuid() as zev1 \gset
select public.report_zone_event(:'zev1', :'ga', :'zone1', 'enter');
select public.report_zone_event(:'zev1', :'ga', :'zone1', 'enter');
select test.ok((select count(*) from public.zone_events where id = :'zev1') = 1, 'report_zone_event idempotente');
select test.err(format('select public.report_zone_event(gen_random_uuid(), %L, %L, ''enter'')', :'gb', :'zone1'),
                'not_member', 'evento de zona en grupo ajeno');
select test.err(format('select public.report_zone_event(gen_random_uuid(), %L, gen_random_uuid(), ''enter'')', :'ga'),
                'not_found', 'evento de zona con zona inexistente');
select public.set_pause(:'ga', true, null);
select test.err(format('select public.report_zone_event(gen_random_uuid(), %L, %L, ''exit'')', :'ga', :'zone1'),
                'paused', 'evento de zona en pausa');
:as_u1
select test.ok((select count(*) from public.zone_events where id = :'zev1') = 1, 'los miembros ven el evento de zona');

-- ===========================================================================
-- 6. SOS (u2 sigue en pausa en A: el SOS sale igual)
-- ===========================================================================

\echo '== SOS'
:as_u2
select gen_random_uuid() as sos1 \gset
select public.create_sos(:'sos1', jsonb_build_array(
    jsonb_build_object('group_id', :'ga', 'payload_enc', 'enc1:sosA'),
    jsonb_build_object('group_id', :'gb', 'payload_enc', 'enc1:sosB')));
select public.create_sos(:'sos1', jsonb_build_array(jsonb_build_object('group_id', :'ga', 'payload_enc', 'enc1:sosA')));
select test.ok((select count(*) from public.sos_alerts where id = :'sos1') = 1, 'SOS idempotente');
:as_root
select test.ok((select count(*) from public.sos_targets where sos_id = :'sos1') = 1,
               'SOS: solo a los grupos donde es miembro (B ignorado), aunque este en pausa');
:as_u2
select test.err(format('select public.create_sos(gen_random_uuid(), %L::jsonb)',
                       jsonb_build_array(jsonb_build_object('group_id', :'gb', 'payload_enc', 'x'))), 'not_member',
                'SOS solo a grupos ajenos');
select public.set_pause(:'ga', false, null);
:as_u1
select test.ok((select count(*) from public.sos_alerts where id = :'sos1') = 1
               and (select payload_enc from public.sos_targets where sos_id = :'sos1') = 'enc1:sosA',
               'los miembros del grupo destino ven el SOS');
:as_u3
select test.err(format('select public.create_sos(%L, ''[]''::jsonb)', :'sos1'), 'not_found', 'reutilizar el id de un SOS ajeno');

-- ===========================================================================
-- 7. Recuperacion de claves
-- ===========================================================================

\echo '== Claves'
:as_u2
select public.request_key_share(:'ga', 'devpub2') as ks1 \gset
select test.err(format('select public.request_key_share(%L, ''d'')', :'gb'), 'not_member', 'pedir clave de grupo ajeno');
:as_u3
select test.err(format('select public.fulfill_key_share(%L, ''f'', ''k'')', :'ks1'), 'not_member', 'entregar clave sin ser miembro');
:as_u1
select test.ok((select count(*) from public.key_shares where group_id = :'ga' and key_box is null) = 1, 'los miembros ven la peticion');
select public.fulfill_key_share(:'ks1', 'fulpub1', 'keybox-ks');
select test.err(format('select public.fulfill_key_share(%L, ''f'', ''k'')', :'ks1'), 'not_pending', 'entregar dos veces');
:as_u2
select test.ok((select key_box = 'keybox-ks' and fulfiller_pub = 'fulpub1' from public.key_shares where id = :'ks1'),
               'quien pidio recoge la clave');

-- ===========================================================================
-- 8. Tokens de aviso y vinculos
-- ===========================================================================

\echo '== Tokens y vinculos'
:as_u1
select public.register_push_token('tok-movil-1');
select public.register_push_token('tok-u1');
:as_u2
select public.register_push_token('tok-movil-1');
select public.register_push_token('tok-u2');
select test.ok((select count(*) from public.push_tokens) = 2, 'el token pasa al ultimo usuario que lo registra');
:as_u1
select test.ok((select count(*) from public.push_tokens) = 1, 'y deja de ser del anterior');
select test.err($$insert into public.push_tokens (token, user_id) values ('x', auth.uid())$$, '42501', 'push_tokens no se escribe directo');
select test.err($$insert into public.account_links (provider, subject, user_id) values ('google', 'x', auth.uid())$$, '42501',
                'account_links no se escribe directo');
:as_root
insert into public.account_links (provider, subject, user_id) values ('google', 'sub-u1', :'u1');
:as_u1
select test.ok((select count(*) from public.account_links) = 1, 'account_links: el propio lo ve');

-- ===========================================================================
-- 9. SC-006: nadie de fuera ve nada del grupo A
-- ===========================================================================

\echo '== SC-006'
:as_root
insert into public.positions (group_id, user_id, recorded_at, battery, payload_enc)
values (:'gb', :'u3', now(), 70, 'enc1:b1');
select test.ok(test.tables_with(array[:'ga', :'u1', :'u2']::uuid[]) = 14,
               'hay datos del grupo A en las 14 tablas (la prueba siguiente tiene sentido)');
:as_u3
select test.ok(test.visible_rows(array[:'ga', :'u1', :'u2']::uuid[]) = 0, 'SC-006: el admin del grupo B no ve nada de A');
select test.ok((select count(*) from public.groups) = 1 and (select count(*) from public.last_positions) = 1,
               'y si ve lo suyo');
update public.zones set name_enc = 'hack' where id = :'zone1';
delete from public.zones where id = :'zone1';
:as_root
select test.ok((select name_enc from public.zones where id = :'zone1') = 'enc1:casa2', 'SC-006: no puede editar ni borrar zonas de A');
:as_u3
select test.err(format($$insert into public.zones (group_id, name_enc, geo_enc) values (%L, 'a', 'b')$$, :'ga'), '42501',
                'SC-006: no puede crear zonas en A');
select test.err(format('select public.set_role(%L, %L, ''member'')', :'ga', :'u1'), 'not_admin', 'SC-006: set_role en A');
select test.err(format('select public.remove_member(%L, %L)', :'ga', :'u2'), 'not_admin', 'SC-006: expulsar en A');
select test.err(format('select public.leave_group(%L)', :'ga'), 'not_member', 'SC-006: salir de A sin ser miembro');
:as_u5
-- Su propia solicitud rechazada si la ve (menciona el grupo A y a quien la resolvio): es suya.
select test.ok(test.visible_rows(array[:'u2', :'gb', :'u3']::uuid[]) = 0
               and (select count(*) from public.groups) = 0 and (select count(*) from public.group_members) = 0
               and (select count(*) from public.join_requests) = 1,
               'SC-006: el rechazado no ve nada salvo su solicitud');
:as_u4
select test.ok(test.visible_rows(array[:'ga', :'u1', :'u2', :'gb', :'u3']::uuid[]) = 0,
               'SC-006: un usuario sin grupos no ve nada');
:as_anon
select test.ok(test.visible_rows(array[:'ga', :'u1', :'u2', :'gb', :'u3']::uuid[]) = 0, 'SC-006: anon no ve nada');
select test.err('select count(*) from public.groups', '42501', 'anon no puede ni leer las tablas');

-- ===========================================================================
-- 10. Roles, ultimo admin, expulsion
-- ===========================================================================

\echo '== Roles y expulsion'
:as_u1
select test.err(format('select public.set_role(%L, %L, ''member'')', :'ga', :'u1'), 'last_admin', 'quitarse el rol siendo el unico admin');
select test.err(format('select public.leave_group(%L)', :'ga'), 'last_admin', 'unico admin con mas miembros no puede salir');
select test.err(format('select public.remove_member(%L, %L)', :'ga', :'u1'), 'last_admin', 'expulsar al unico admin');
select test.err(format('select public.set_role(%L, %L, ''member'')', :'ga', :'u5'), 'not_member', 'set_role a quien no es miembro');
select public.set_role(:'ga', :'u2', 'admin');
:as_u2
select public.set_role(:'ga', :'u1', 'member');
:as_u1
select test.err(format('select public.set_role(%L, %L, ''admin'')', :'ga', :'u1'), 'not_admin', 'un member no cambia roles');
select test.err(format('select * from public.create_invitation(%L, ''p'', ''q'')', :'ga'), 'not_admin', 'un member no invita');
:as_u2
select public.set_role(:'ga', :'u1', 'admin');
select public.set_role(:'ga', :'u2', 'member');

-- u2 tiene posiciones, ultima posicion y suscripciones en A.
insert into public.positions (group_id, user_id, recorded_at, battery, payload_enc)
values (:'ga', :'u2', now(), 48, 'enc1:q3');
insert into public.zone_subscriptions (observer_id, group_id, target_id, zone_id) values (:'u2', :'ga', :'u1', :'zone1');
:as_u1
select public.remove_member(:'ga', :'u2');
:as_root
select test.ok((select count(*) from public.positions where group_id = :'ga' and user_id = :'u2') = 0
               and (select count(*) from public.last_positions where group_id = :'ga' and user_id = :'u2') = 0,
               'expulsion: se borran sus posiciones del grupo');
select test.ok((select count(*) from public.zone_subscriptions where group_id = :'ga'
                and (observer_id = :'u2' or target_id = :'u2')) = 0, 'expulsion: se borran sus suscripciones');
:as_u2
select test.ok((select count(*) from public.groups) = 0 and (select count(*) from public.positions) = 0
               and (select count(*) from public.zones) = 0, 'expulsado: deja de ver el grupo al instante');
select test.err(format('select public.request_join(%L, ''r'', ''n'')', :'code1'), 'expired',
                'expulsado: la invitacion de antes no le sirve');

-- ===========================================================================
-- 11. transfer_user (recuperar la cuenta)
-- ===========================================================================

\echo '== transfer_user'
:as_u4
select test.err(format('select public.transfer_user(%L, %L)', :'u1', :'u4'), '42501', 'authenticated no puede transfer_user');
:as_anon
select test.err(format('select public.transfer_user(%L, %L)', :'u1', :'u4'), '42501', 'anon no puede transfer_user');
:as_svc
select test.err(format('select public.transfer_user(%L, %L)', :'u1', :'u3'), 'not_empty', 'no se fusiona con un usuario con grupos');
select public.transfer_user(:'u1', :'u4');
:as_root
select test.ok((select role from public.group_members where group_id = :'ga' and user_id = :'u4') = 'admin'
               and (select count(*) from public.group_members where user_id = :'u1') = 0, 'pertenencia transferida');
select test.ok((select count(*) from public.positions where user_id = :'u4') = 3
               and (select count(*) from public.last_positions where user_id = :'u4') = 1, 'posiciones transferidas');
select test.ok((select user_id from public.account_links where subject = 'sub-u1') = :'u4'::uuid, 'vinculo transferido');
select test.ok((select count(*) from public.push_tokens where user_id = :'u1') = 0, 'tokens del movil viejo borrados');
delete from auth.users where id = :'u1';   -- lo que hace recover-account con auth.admin.deleteUser
select test.ok((select count(*) from public.group_members where group_id = :'ga') = 1, 'borrar el viejo no se lleva nada');
:as_u4
select test.ok((select count(*) from public.groups where id = :'ga') = 1, 'el movil nuevo ve el grupo');
select test.ok((select count(*) from public.join_requests where id = :'rid2') = 1, 'y las solicitudes que resolvio');

-- ===========================================================================
-- 12. Salir del grupo
-- ===========================================================================

\echo '== Salir'
:as_u3
select public.leave_group(:'gb');
:as_root
select test.ok((select count(*) from public.groups where id = :'gb') = 0
               and (select count(*) from public.positions where group_id = :'gb') = 0,
               'el unico miembro sale: el grupo se borra entero');
:as_u4
select code as code3 from public.create_invitation(:'ga', 'invpubA3', 'enc1:invprivA3') \gset
:as_u5
select public.request_join(:'code3', 'reqpub5b', 'box5b') as rid5b \gset
:as_u4
select public.approve_request(:'rid5b', 'kb5');
:as_u5
insert into public.positions (group_id, user_id, recorded_at, battery, payload_enc)
values (:'ga', :'u5', now(), 20, 'enc1:r1');
select public.leave_group(:'ga');
:as_root
select test.ok((select count(*) from public.positions where group_id = :'ga' and user_id = :'u5') = 0
               and (select count(*) from public.group_members where group_id = :'ga' and user_id = :'u5') = 0,
               'un miembro sale: el grupo deja de ver su posicion');
select test.ok((select count(*) from public.groups where id = :'ga') = 1, 'el grupo sigue');

-- ===========================================================================
-- 13. Tareas de pg_cron
-- ===========================================================================

\echo '== Retencion'
:as_root
insert into public.positions (group_id, user_id, recorded_at, battery, payload_enc)
values (:'ga', :'u4', now() - interval '31 days', 10, 'enc1:vieja');
insert into public.zone_events (id, group_id, user_id, zone_id, kind, occurred_at)
values (gen_random_uuid(), :'ga', :'u4', :'zone1', 'exit', now() - interval '31 days');
insert into public.sos_alerts (id, user_id, created_at) values ('00000000-0000-0000-0000-0000000005a5', :'u4', now() - interval '31 days');
insert into public.sos_targets (sos_id, group_id, payload_enc) values ('00000000-0000-0000-0000-0000000005a5', :'ga', 'x');
select public.purge_old_data();
select test.ok((select count(*) from public.positions where payload_enc = 'enc1:vieja') = 0
               and (select count(*) from public.positions where group_id = :'ga') > 0, 'purga: posiciones de mas de 30 dias');
select test.ok((select count(*) from public.zone_events where occurred_at < now() - interval '30 days') = 0
               and (select count(*) from public.zone_events) = 1, 'purga: eventos de zona de mas de 30 dias');
select test.ok((select count(*) from public.sos_alerts where id = '00000000-0000-0000-0000-0000000005a5') = 0
               and (select count(*) from public.sos_targets where sos_id = '00000000-0000-0000-0000-0000000005a5') = 0
               and (select count(*) from public.sos_alerts) = 1, 'purga: SOS de mas de 30 dias');
select public.expire_invitations_and_pauses();
select test.ok((select count(*) from public.invitations where code = 'EXPRED22') = 1, 'invitacion caducada hace 10 min: se queda');
update public.invitations set expires_at = now() - interval '61 minutes' where code = 'EXPRED22';
select public.expire_invitations_and_pauses();
select test.ok((select count(*) from public.invitations where code = 'EXPRED22') = 0
               and (select count(*) from public.invitations) = 2, 'invitacion caducada hace mas de 1 h: borrada');

\echo '== TODAS LAS PRUEBAS PASAN'
