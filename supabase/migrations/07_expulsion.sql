-- Family Together — al expulsado no le vale una invitación de antes de la expulsión (2026-09-29).
--
-- Visto en la prueba de dos móviles: el administrador crea un código, expulsa a alguien y ese
-- alguien pide entrar con el código de antes. request_join solo comparaba con la última solicitud
-- resuelta (la aprobación, anterior al código), así que la solicitud entraba. La expulsión no
-- dejaba rastro de cuándo pasó.
--
-- group_removals guarda cuándo se expulsó a cada persona de cada grupo (sin texto ni coordenadas:
-- nada que cifrar). Solo la tocan funciones SECURITY DEFINER: RLS activada y sin políticas ni
-- permisos para anon/authenticated. Un disparador en join_requests rechaza con 'expired' cualquier
-- solicitud (nueva o reenviada con otro código) hecha con una invitación creada antes de la
-- expulsión, sin tocar request_join.
-- Relanzable.

create table if not exists public.group_removals (
    group_id   uuid not null references public.groups(id) on delete cascade,
    user_id    uuid not null references auth.users(id) on delete cascade,
    removed_at timestamptz not null default now(),
    primary key (group_id, user_id)
);

alter table public.group_removals enable row level security;
revoke all on table public.group_removals from public, anon, authenticated;

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

    insert into public.group_removals (group_id, user_id, removed_at)
    values (p_group, p_user, now())
    on conflict (group_id, user_id) do update set removed_at = excluded.removed_at;
end;
$$;

create or replace function public._join_request_not_after_removal()
returns trigger
language plpgsql
security definer
set search_path = public
as $$
begin
    if exists (select 1
                 from public.group_removals g
                 join public.invitations i on i.code = new.invitation_code
                where g.group_id = new.group_id and g.user_id = new.user_id
                  and g.removed_at >= i.created_at) then
        perform public._fail('expired');
    end if;
    return new;
end;
$$;

revoke all on function public._join_request_not_after_removal() from public, anon, authenticated;

drop trigger if exists join_requests_not_after_removal on public.join_requests;
create trigger join_requests_not_after_removal
    before insert or update of invitation_code on public.join_requests
    for each row execute function public._join_request_not_after_removal();
