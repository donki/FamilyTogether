-- Family Together — tareas periodicas con pg_cron (ARQUITECTURA §6, «pg_cron»).
-- Ejecutar despues de 03_functions.sql. Idempotente: cada tarea se desprograma (si existe) antes de
-- volver a programarla.
--
-- En Supabase, pg_cron se activa en Database > Extensions (o con el create extension de abajo).
-- La retencion es de 30 dias (constitucion Mobile §10).

do $$
begin
    if exists (select 1 from pg_available_extensions where name = 'pg_cron') then
        create extension if not exists pg_cron;
    end if;
end;
$$;

-- ---------------------------------------------------------------------------
-- Lo que hace cada tarea, en funciones: se prueban igual con o sin pg_cron.
-- ---------------------------------------------------------------------------

-- Diaria: posiciones, eventos de zona y SOS de mas de 30 dias (sos_targets va en cascada).
create or replace function public.purge_old_data()
returns void
language sql
security definer
set search_path = public
as $$
    delete from public.positions   where recorded_at < now() - interval '30 days';
    delete from public.zone_events where occurred_at < now() - interval '30 days';
    delete from public.sos_alerts  where created_at  < now() - interval '30 days';
$$;

-- Cada 5 minutos: invitaciones caducadas hace mas de 1 h y pausas cuya hora de fin ya paso.
create or replace function public.expire_invitations_and_pauses()
returns void
language sql
security definer
set search_path = public
as $$
    delete from public.invitations where expires_at < now() - interval '1 hour';
    update public.group_members
       set paused = false, pause_until = null
     where pause_until is not null and pause_until <= now();
$$;

revoke all on function public.purge_old_data()                from public, anon, authenticated;
revoke all on function public.expire_invitations_and_pauses() from public, anon, authenticated;

-- ---------------------------------------------------------------------------
-- Programacion
-- ---------------------------------------------------------------------------

do $$
begin
    if not exists (select 1 from pg_namespace where nspname = 'cron') then
        raise notice 'pg_cron no esta instalado: activalo (Database > Extensions) y relanza este fichero.';
        return;
    end if;

    if exists (select 1 from cron.job where jobname = 'familytogether_purge_old_data') then
        perform cron.unschedule('familytogether_purge_old_data');
    end if;
    perform cron.schedule('familytogether_purge_old_data', '0 3 * * *',
                          'select public.purge_old_data()');

    if exists (select 1 from cron.job where jobname = 'familytogether_expire') then
        perform cron.unschedule('familytogether_expire');
    end if;
    perform cron.schedule('familytogether_expire', '*/5 * * * *',
                          'select public.expire_invitations_and_pauses()');
end;
$$;
