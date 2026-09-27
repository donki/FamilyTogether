-- Family Together — borrar mi historial (2026-09-27, petición de Josep).
--
-- Cada usuario puede borrar SU historial de posiciones, en todos sus grupos, y nunca el de otro.
-- Va por RPC SECURITY DEFINER sin parámetros: el usuario sale de auth.uid(), así que no hay forma
-- de apuntar a otra persona. La tabla positions sigue sin permiso de DELETE para authenticated
-- (02_rls.sql): un DELETE directo por REST lo rechaza PostgreSQL antes de mirar la RLS.
--
-- Qué se conserva: last_positions, la última posición de cada grupo, para que el mapa te siga
-- viendo. No es historial (una fila por grupo, se sustituye con cada posición nueva y se borra al
-- pausar, salir o ser expulsado).
--
-- No recibe ni guarda texto ni coordenadas: nada que cifrar.
-- Relanzable.

create or replace function public.clear_my_history()
returns integer
language plpgsql
security definer
set search_path = public
as $$
declare
    v_uid   uuid := public._require_uid();
    v_count integer;
begin
    delete from public.positions where user_id = v_uid;
    get diagnostics v_count = row_count;
    return v_count;
end;
$$;

revoke all on function public.clear_my_history() from public, anon;
grant execute on function public.clear_my_history() to authenticated;
