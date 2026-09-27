using System.Globalization;

namespace FamilyLink.Core;

/// <summary>
/// Lo que llama la interfaz: grupos, invitaciones, miembros, posiciones, zonas y claves.
/// </summary>
/// <remarks>
/// <para><b>Todo lo que se ve pasa por aqui descifrado.</b> El servidor solo guarda sobres
/// <c>enc1:</c>; la clave de cada grupo vive en <see cref="GroupKeyStore"/>. Si falta, los nombres
/// salen como «?» y el grupo con <see cref="Group.KeyMissing"/>: nunca una excepcion por no poder
/// leer algo.</para>
///
/// <para><b>Avisos.</b> Cada operacion que genera un evento (solicitud, resolucion, peticion de
/// clave, SOS, zona) llama despues a la Edge Function <c>notify</c> con <c>{type, id}</c>. Si falla
/// se registra y ya: la operacion ya esta hecha y los demas moviles la veran al consultar.</para>
/// </remarks>
public sealed class FamilyService
{
    /// <summary>Lo que se ve cuando no se puede descifrar un nombre.</summary>
    public const string Unreadable = "?";

    private const string KeyProfileName = "profile_name";
    private const string KeyProfileAvatar = "profile_avatar";
    private const string KeyPendingRequests = "pending_join_requests";

    private readonly SupabaseClient _client;
    private readonly GroupKeyStore _keys;
    private readonly ISecureStore _secure;

    public FamilyService(SupabaseClient client, GroupKeyStore keys, ISecureStore secure)
    {
        _client = client;
        _keys = keys;
        _secure = secure;
    }

    public SupabaseClient Client => _client;

    public GroupKeyStore Keys => _keys;

    // -----------------------------------------------------------------------
    // Grupos
    // -----------------------------------------------------------------------

    public async Task<IReadOnlyList<Group>> GetGroupsAsync(CancellationToken cancellationToken = default)
    {
        var mine = await MyMembershipsAsync(cancellationToken).ConfigureAwait(false);
        if (mine.Count == 0)
            return [];

        var groups = await _client.SelectAsync<GroupRow>(
            "groups", $"select=id,name_enc&id={In(mine.Select(m => m.GroupId))}", cancellationToken).ConfigureAwait(false);

        var result = new List<Group>();
        foreach (var membership in mine)
        {
            var row = groups.FirstOrDefault(g => g.Id == membership.GroupId);
            var key = await _keys.GetAsync(membership.GroupId).ConfigureAwait(false);
            var readable = Crypto.TryDecrypt(row?.NameEnc, key, out var name) && key is not null;

            result.Add(new Group(
                membership.GroupId,
                readable ? name : Unreadable,
                membership.IsAdmin,
                KeyMissing: key is null || !readable));
        }

        return [.. result.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    public async Task<Group> CreateGroupAsync(string name, string myDisplayName, string? avatarBase64, CancellationToken cancellationToken = default)
    {
        await SaveProfileAsync(myDisplayName, avatarBase64).ConfigureAwait(false);

        var key = Crypto.NewGroupKey();
        var id = await _client.RpcAsync<Guid>("create_group", new
        {
            p_name_enc = Crypto.Encrypt(name, key),
            p_display_name_enc = Crypto.Encrypt(myDisplayName, key),
            p_avatar_enc = EncryptOrNull(avatarBase64, key),
        }, cancellationToken).ConfigureAwait(false);

        await _keys.SetAsync(id, key).ConfigureAwait(false);
        return new Group(id, name, IAmAdmin: true, KeyMissing: false);
    }

    // -----------------------------------------------------------------------
    // Invitaciones y solicitudes (§4)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Invitacion nueva: par ECDH cuya privada viaja cifrada con la clave del grupo, para que
    /// cualquier administrador —no solo este movil— pueda abrir las solicitudes que lleguen.
    /// </summary>
    public async Task<Invitation> CreateInvitationAsync(Guid group, CancellationToken cancellationToken = default)
    {
        var key = await RequireKeyAsync(group).ConfigureAwait(false);
        var (pub, priv) = Crypto.NewEcdhKeyPair();

        var rows = await _client.RpcAsync<List<InvitationRow>>("create_invitation", new
        {
            p_group = group,
            p_inv_pub = pub,
            p_inv_priv_enc = Crypto.Encrypt(priv, key),
        }, cancellationToken).ConfigureAwait(false);

        var row = rows?.FirstOrDefault()
            ?? throw new FamilyLinkException(FamilyLinkException.Server, "create_invitation no devolvio el codigo.");

        return new Invitation(row.Code, row.ExpiresAt, InviteCodes.ToQrPayload(row.Code));
    }

    /// <summary>
    /// Pide entrar con un codigo o con el contenido del QR. Devuelve el id de la solicitud; la
    /// privada ECDH se queda en el almacen seguro hasta que se resuelva.
    /// </summary>
    public async Task<Guid> RequestJoinAsync(string codeOrQr, string myDisplayName, CancellationToken cancellationToken = default)
    {
        var code = InviteCodes.Parse(codeOrQr);
        await SaveProfileAsync(myDisplayName, null, keepAvatar: true).ConfigureAwait(false);

        var info = (await _client.RpcAsync<List<InvitationRow>>("invitation_info", new { p_code = code }, cancellationToken)
            .ConfigureAwait(false))?.FirstOrDefault();
        if (info is null || string.IsNullOrEmpty(info.InvPub))
            throw new FamilyLinkException("not_found", "not_found");

        var (reqPub, reqPriv) = Crypto.NewEcdhKeyPair();
        var shared = Crypto.SharedKey(reqPriv, info.InvPub);

        var id = await _client.RpcAsync<Guid>("request_join", new
        {
            p_code = code,
            p_req_pub = reqPub,
            p_name_box = Crypto.Encrypt(myDisplayName, shared),
        }, cancellationToken).ConfigureAwait(false);

        // Si ya habia una pendiente, el servidor la actualiza con este req_pub y este name_box y
        // devuelve su id: la privada nueva sustituye a la anterior.
        await _secure.SetAsync(ReqPrivKey(id), reqPriv).ConfigureAwait(false);
        await _secure.SetAsync(ReqGroupKey(id), info.GroupId.ToString("D")).ConfigureAwait(false);
        await AddPendingRequestAsync(id).ConfigureAwait(false);

        await NotifyAsync(EventTypes.JoinRequest, id, cancellationToken).ConfigureAwait(false);
        return id;
    }

    /// <summary>
    /// Mira como va mi solicitud. Si esta aprobada, abre la clave del grupo con ECDH, la guarda y
    /// publica mi nombre (y avatar) cifrados ya con ella.
    /// </summary>
    public async Task<JoinState> CheckMyRequestAsync(Guid requestId, CancellationToken cancellationToken = default)
    {
        var row = (await _client.SelectAsync<JoinRequestRow>(
            "join_requests",
            $"select=id,group_id,status,key_box,inv_pub,req_pub&id=eq.{requestId:D}",
            cancellationToken).ConfigureAwait(false)).FirstOrDefault();

        if (row is null || row.Status == "rejected")
        {
            // Sin fila: el grupo se borro o la solicitud ya no es visible. A efectos de la app, no entra.
            await ForgetRequestAsync(requestId).ConfigureAwait(false);
            return JoinState.Rejected;
        }

        if (row.Status != "approved")
            return JoinState.Pending;

        if (await _keys.GetAsync(row.GroupId).ConfigureAwait(false) is null)
        {
            var priv = await _secure.GetAsync(ReqPrivKey(requestId)).ConfigureAwait(false);
            if (OpenKeyBox(priv, row.InvPub, row.KeyBox) is { } groupKey)
            {
                await _keys.SetAsync(row.GroupId, groupKey).ConfigureAwait(false);
            }
            else
            {
                // Sin la privada (reinstalacion) o caja ilegible: es miembro pero sin clave. El
                // grupo saldra con KeyMissing y se puede pedir con RequestMissingKeysAsync.
                CoreLog.Write($"Solicitud {requestId} aprobada pero sin poder abrir la clave del grupo.");
            }
        }

        if (await _keys.GetAsync(row.GroupId).ConfigureAwait(false) is { } key)
        {
            var (name, avatar) = await ProfileAsync().ConfigureAwait(false);
            await _client.RpcAsync("update_my_member", new
            {
                p_group = row.GroupId,
                p_display_name_enc = Crypto.Encrypt(name, key),
                p_avatar_enc = EncryptOrNull(avatar, key),
            }, cancellationToken).ConfigureAwait(false);
        }

        await ForgetRequestAsync(requestId).ConfigureAwait(false);
        return JoinState.Approved;
    }

    /// <summary>Mis solicitudes aun sin resolver (las que este movil envio).</summary>
    public async Task<IReadOnlyList<Guid>> GetMyPendingRequestIdsAsync()
    {
        var stored = await _secure.GetAsync(KeyPendingRequests).ConfigureAwait(false);
        return [.. (stored ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => Guid.TryParse(s, out var g) ? g : Guid.Empty)
            .Where(g => g != Guid.Empty)];
    }

    public async Task<IReadOnlyList<JoinRequest>> GetPendingRequestsAsync(Guid group, CancellationToken cancellationToken = default)
    {
        var rows = await _client.SelectAsync<JoinRequestRow>(
            "join_requests",
            $"select={RequestColumns}&group_id=eq.{group:D}&status=eq.pending&order=created_at.asc",
            cancellationToken).ConfigureAwait(false);

        var result = new List<JoinRequest>();
        foreach (var row in rows)
        {
            var name = await OpenRequestNameAsync(row, cancellationToken).ConfigureAwait(false);
            result.Add(new JoinRequest(row.Id, row.GroupId, name, row.CreatedAt));
        }

        return result;
    }

    /// <summary>
    /// Aprueba: la clave del grupo viaja en <c>key_box</c> cifrada con el secreto ECDH de la
    /// solicitud. La fila se vuelve a leer siempre: una solicitud repetida se actualiza en el
    /// servidor con otro <c>req_pub</c>, y un secreto calculado al listar podria ser el de antes.
    /// </summary>
    public async Task ApproveAsync(JoinRequest request, CancellationToken cancellationToken = default)
    {
        var key = await RequireKeyAsync(request.GroupId).ConfigureAwait(false);

        var row = (await _client.SelectAsync<JoinRequestRow>(
            "join_requests",
            $"select={RequestColumns}&id=eq.{request.Id:D}",
            cancellationToken).ConfigureAwait(false)).FirstOrDefault()
            ?? throw new FamilyLinkException("not_found", "not_found");

        var shared = RequestSecret(key, row.InvPrivEnc, row.ReqPub)
            ?? throw new FamilyLinkException(FamilyLinkException.KeyMissing, "No se puede abrir la privada de la invitacion de esta solicitud.");

        await _client.RpcAsync("approve_request", new
        {
            p_request = request.Id,
            p_key_box = SealKeyBox(key, shared),
        }, cancellationToken).ConfigureAwait(false);

        await NotifyAsync(EventTypes.RequestResolved, request.Id, cancellationToken).ConfigureAwait(false);
    }

    public async Task RejectAsync(JoinRequest request, CancellationToken cancellationToken = default)
    {
        await _client.RpcAsync("reject_request", new { p_request = request.Id }, cancellationToken).ConfigureAwait(false);
        await NotifyAsync(EventTypes.RequestResolved, request.Id, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Columnas de <c>join_requests</c> que hacen falta para abrir una solicitud.</summary>
    internal const string RequestColumns = "id,group_id,user_id,invitation_code,req_pub,inv_pub,inv_priv_enc,name_box,status,created_at";

    /// <summary>
    /// Nombre de quien pide entrar, con lo que trae la propia fila de la solicitud (§4): su copia
    /// de <c>inv_priv_enc</c> se abre con la clave del grupo, ECDH con <c>req_pub</c> y
    /// <c>name_box</c>. No depende de que la invitacion siga existiendo. «?» si algo no cuadra.
    /// </summary>
    internal async Task<string> OpenRequestNameAsync(JoinRequestRow row, CancellationToken cancellationToken)
    {
        var key = await _keys.GetAsync(row.GroupId).ConfigureAwait(false);
        return OpenRequestName(key, row.InvPrivEnc, row.ReqPub, row.NameBox);
    }

    // Las piezas de §4 sin servidor, para poder probar el flujo entero.

    /// <summary>Lado del administrador: secreto ECDH(inv_priv, req_pub), o null si algo no se puede abrir.</summary>
    internal static byte[]? RequestSecret(byte[]? groupKey, string? invPrivEnc, string? reqPub) =>
        groupKey is not null && Crypto.TryDecrypt(invPrivEnc, groupKey, out var invPriv) && invPriv.Length > 0
            ? Crypto.TrySharedKey(invPriv, reqPub)
            : null;

    /// <summary>Lado del administrador: el nombre del solicitante, o «?».</summary>
    internal static string OpenRequestName(byte[]? groupKey, string? invPrivEnc, string? reqPub, string? nameBox) =>
        RequestSecret(groupKey, invPrivEnc, reqPub) is { } shared
            ? Crypto.DecryptOr(nameBox, shared, Unreadable)
            : Unreadable;

    /// <summary>Lado del administrador: <c>key_box</c> = la clave del grupo en base64, cifrada con el secreto.</summary>
    internal static string SealKeyBox(byte[] groupKey, byte[] shared) =>
        Crypto.Encrypt(Convert.ToBase64String(groupKey), shared);

    /// <summary>Lado del solicitante: abre <c>key_box</c> con ECDH(req_priv, inv_pub). Null si no se puede.</summary>
    internal static byte[]? OpenKeyBox(string? reqPriv, string? invPub, string? keyBox) =>
        Crypto.TrySharedKey(reqPriv, invPub) is { } shared
            && Crypto.TryDecrypt(keyBox, shared, out var keyBase64)
            && TryBase64(keyBase64) is { Length: 32 } key
                ? key
                : null;

    // -----------------------------------------------------------------------
    // Miembros y posiciones
    // -----------------------------------------------------------------------

    public async Task<IReadOnlyList<Member>> GetMembersAsync(Guid group, CancellationToken cancellationToken = default)
    {
        var me = await MeAsync(cancellationToken).ConfigureAwait(false);
        var key = await _keys.GetAsync(group).ConfigureAwait(false);
        var rows = await MemberRowsAsync(group, cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;

        return [.. rows.Select(row => ToMember(row, key, me, now))
            .OrderByDescending(m => m.IsMe)
            .ThenBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    public async Task<IReadOnlyList<MemberPosition>> GetLastPositionsAsync(Guid group, CancellationToken cancellationToken = default)
    {
        var key = await _keys.GetAsync(group).ConfigureAwait(false);
        var rows = await _client.SelectAsync<PositionRow>(
            "last_positions", $"select=user_id,recorded_at,battery,payload_enc&group_id=eq.{group:D}", cancellationToken)
            .ConfigureAwait(false);

        return [.. rows.Select(r => ToPosition(r, key)).OfType<MemberPosition>()];
    }

    /// <summary>
    /// Recorrido de un miembro en un dia. El dia es el del calendario de <paramref name="tz"/>: se
    /// pide al servidor el tramo UTC que lo cubre y se filtra otra vez al convertir cada hora.
    /// </summary>
    public async Task<IReadOnlyList<MemberPosition>> GetHistoryAsync(
        Guid group, Guid user, DateOnly day, TimeZoneInfo tz, CancellationToken cancellationToken = default)
    {
        var (from, to) = DayRange(day, tz);
        var key = await _keys.GetAsync(group).ConfigureAwait(false);

        const int page = 1000;   // el max-rows por defecto de Supabase
        var result = new List<MemberPosition>();
        for (var offset = 0; ; offset += page)
        {
            var rows = await _client.SelectAsync<PositionRow>(
                "positions",
                $"select=user_id,recorded_at,battery,payload_enc&group_id=eq.{group:D}&user_id=eq.{user:D}" +
                $"&recorded_at=gte.{Stamp(from)}&recorded_at=lt.{Stamp(to)}&order=recorded_at.asc&limit={page}&offset={offset}",
                cancellationToken).ConfigureAwait(false);

            result.AddRange(rows
                .Where(r => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(r.RecordedAt, tz).DateTime) == day)
                .Select(r => ToPosition(r, key))
                .OfType<MemberPosition>());

            if (rows.Count < page)
                break;
        }

        return result;
    }

    /// <summary>Inicio y fin (UTC) del dia <paramref name="day"/> en <paramref name="tz"/>.</summary>
    internal static (DateTimeOffset From, DateTimeOffset To) DayRange(DateOnly day, TimeZoneInfo tz) =>
        (LocalMidnightUtc(day, tz), LocalMidnightUtc(day.AddDays(1), tz));

    private static DateTimeOffset LocalMidnightUtc(DateOnly day, TimeZoneInfo tz)
    {
        var local = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);

        // Hay zonas donde la medianoche no existe el dia del cambio de hora: se toma la primera
        // hora valida.
        while (tz.IsInvalidTime(local))
            local = local.AddMinutes(30);

        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, tz), TimeSpan.Zero);
    }

    public async Task SetRoleAsync(Guid group, Guid user, bool admin, CancellationToken cancellationToken = default) =>
        await _client.RpcAsync("set_role", new { p_group = group, p_user = user, p_role = admin ? "admin" : "member" }, cancellationToken)
            .ConfigureAwait(false);

    public async Task RemoveMemberAsync(Guid group, Guid user, CancellationToken cancellationToken = default) =>
        await _client.RpcAsync("remove_member", new { p_group = group, p_user = user }, cancellationToken).ConfigureAwait(false);

    public async Task LeaveGroupAsync(Guid group, CancellationToken cancellationToken = default)
    {
        await _client.RpcAsync("leave_group", new { p_group = group }, cancellationToken).ConfigureAwait(false);
        await _keys.RemoveAsync(group).ConfigureAwait(false);
    }

    public async Task SetPauseAsync(Guid group, bool paused, DateTimeOffset? until, CancellationToken cancellationToken = default) =>
        await _client.RpcAsync("set_pause", new
        {
            p_group = group,
            p_paused = paused,
            p_until = paused ? until?.ToUniversalTime() : null,
        }, cancellationToken).ConfigureAwait(false);

    /// <summary>Nombre y avatar en todos mis grupos (en los que tengo clave; en los demas, al recibirla).</summary>
    public async Task UpdateMyProfileAsync(string displayName, string? avatarBase64, CancellationToken cancellationToken = default)
    {
        await SaveProfileAsync(displayName, avatarBase64).ConfigureAwait(false);

        foreach (var membership in await MyMembershipsAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await _keys.GetAsync(membership.GroupId).ConfigureAwait(false) is not { } key)
                continue;

            await _client.RpcAsync("update_my_member", new
            {
                p_group = membership.GroupId,
                p_display_name_enc = Crypto.Encrypt(displayName, key),
                p_avatar_enc = EncryptOrNull(avatarBase64, key),
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>El perfil guardado en este movil: el ultimo nombre y avatar que se dieron.</summary>
    public async Task<(string Name, string? AvatarBase64)> ProfileAsync()
    {
        var name = await _secure.GetAsync(KeyProfileName).ConfigureAwait(false) ?? "";
        var avatar = await _secure.GetAsync(KeyProfileAvatar).ConfigureAwait(false);
        return (name, string.IsNullOrEmpty(avatar) ? null : avatar);
    }

    // -----------------------------------------------------------------------
    // Zonas
    // -----------------------------------------------------------------------

    /// <summary>Zonas del grupo. Las que no se pueden descifrar no salen: sin centro no hay donde pintarlas.</summary>
    public async Task<IReadOnlyList<Zone>> GetZonesAsync(Guid group, CancellationToken cancellationToken = default)
    {
        var key = await _keys.GetAsync(group).ConfigureAwait(false);
        var rows = await _client.SelectAsync<ZoneRow>(
            "zones", $"select=id,group_id,name_enc,geo_enc,created_by&group_id=eq.{group:D}", cancellationToken)
            .ConfigureAwait(false);

        var result = new List<Zone>();
        foreach (var row in rows)
        {
            if (Payloads.Open<Payloads.ZoneData>(row.GeoEnc, key) is not { } geo)
                continue;

            result.Add(new Zone(row.Id, row.GroupId, Crypto.DecryptOr(row.NameEnc, key, Unreadable), geo.Lat, geo.Lon, geo.R, row.CreatedBy));
        }

        return [.. result.OrderBy(z => z.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>Crea (Id vacio o que no exista) o cambia una zona.</summary>
    public async Task SaveZoneAsync(Zone zone, CancellationToken cancellationToken = default) =>
        await SaveZoneReturningIdAsync(zone, cancellationToken).ConfigureAwait(false);

    /// <summary>Como <see cref="SaveZoneAsync"/>, devolviendo el id con el que queda la zona.</summary>
    public async Task<Guid> SaveZoneReturningIdAsync(Zone zone, CancellationToken cancellationToken = default)
    {
        var key = await RequireKeyAsync(zone.GroupId).ConfigureAwait(false);
        var nameEnc = Crypto.Encrypt(zone.Name, key);
        var geoEnc = Crypto.Encrypt(Payloads.Zone(zone.Lat, zone.Lon, zone.Radius), key);

        if (zone.Id != Guid.Empty)
        {
            var changed = await _client.UpdateAsync("zones", $"id=eq.{zone.Id:D}", new
            {
                name_enc = nameEnc,
                geo_enc = geoEnc,
                updated_at = DateTimeOffset.UtcNow,
            }, cancellationToken).ConfigureAwait(false);

            if (changed > 0)
                return zone.Id;
        }

        var id = zone.Id == Guid.Empty ? Guid.NewGuid() : zone.Id;
        var me = await MeAsync(cancellationToken).ConfigureAwait(false);
        await _client.InsertAsync("zones", new
        {
            id,
            group_id = zone.GroupId,
            name_enc = nameEnc,
            geo_enc = geoEnc,
            created_by = me,
        }, cancellationToken).ConfigureAwait(false);

        return id;
    }

    public async Task DeleteZoneAsync(Guid zone, CancellationToken cancellationToken = default) =>
        await _client.DeleteAsync("zones", $"id=eq.{zone:D}", cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<ZoneSubscription>> GetMySubscriptionsAsync(Guid group, CancellationToken cancellationToken = default)
    {
        var me = await MeAsync(cancellationToken).ConfigureAwait(false);
        var rows = await _client.SelectAsync<SubscriptionRow>(
            "zone_subscriptions",
            $"select=group_id,target_id,zone_id,on_enter,on_exit&observer_id=eq.{me:D}&group_id=eq.{group:D}",
            cancellationToken).ConfigureAwait(false);

        return [.. rows.Select(r => new ZoneSubscription(r.TargetId, r.ZoneId, r.OnEnter, r.OnExit))];
    }

    /// <summary>Activa o cambia un aviso persona + zona. Con los dos apagados, se borra la fila.</summary>
    public async Task SetSubscriptionAsync(Guid group, ZoneSubscription s, CancellationToken cancellationToken = default)
    {
        var me = await MeAsync(cancellationToken).ConfigureAwait(false);

        if (!s.OnEnter && !s.OnExit)
        {
            await _client.DeleteAsync(
                "zone_subscriptions",
                $"observer_id=eq.{me:D}&target_id=eq.{s.TargetId:D}&zone_id=eq.{s.ZoneId:D}",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        await _client.UpsertAsync("zone_subscriptions", new
        {
            observer_id = me,
            group_id = group,
            target_id = s.TargetId,
            zone_id = s.ZoneId,
            on_enter = s.OnEnter,
            on_exit = s.OnExit,
        }, "observer_id,target_id,zone_id", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Informa de una entrada o salida (lo detecta <see cref="ZoneWatcher"/>) y avisa.</summary>
    public async Task ReportZoneEventAsync(Guid group, Guid zone, string kind, CancellationToken cancellationToken = default)
    {
        if (kind is not ("enter" or "exit"))
            throw new ArgumentException("kind tiene que ser enter o exit.", nameof(kind));

        var id = Guid.NewGuid();
        await _client.RpcAsync("report_zone_event", new { p_id = id, p_group = group, p_zone = zone, p_kind = kind }, cancellationToken)
            .ConfigureAwait(false);
        await NotifyAsync(EventTypes.ZoneEvent, id, cancellationToken).ConfigureAwait(false);
    }

    // -----------------------------------------------------------------------
    // Claves (§5)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Entrega la clave a quien la haya pedido en mis grupos: por cada peticion sin <c>key_box</c>,
    /// un par efimero, la clave cifrada con ECDH(efimera, device_pub) y <c>fulfill_key_share</c>.
    /// </summary>
    public async Task FulfillPendingKeySharesAsync(CancellationToken cancellationToken = default) =>
        await FulfillPendingKeySharesCountAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>Como <see cref="FulfillPendingKeySharesAsync"/>, devolviendo cuantas entrego.</summary>
    public async Task<int> FulfillPendingKeySharesCountAsync(CancellationToken cancellationToken = default)
    {
        var me = await MeAsync(cancellationToken).ConfigureAwait(false);
        var groups = new List<Guid>();
        foreach (var membership in await MyMembershipsAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await _keys.GetAsync(membership.GroupId).ConfigureAwait(false) is not null)
                groups.Add(membership.GroupId);
        }

        if (groups.Count == 0)
            return 0;

        var shares = await _client.SelectAsync<KeyShareRow>(
            "key_shares",
            $"select=id,group_id,user_id,device_pub&key_box=is.null&user_id=neq.{me:D}&group_id={In(groups)}",
            cancellationToken).ConfigureAwait(false);

        var done = 0;
        foreach (var share in shares)
        {
            var key = await _keys.GetAsync(share.GroupId).ConfigureAwait(false);
            if (key is null || string.IsNullOrEmpty(share.DevicePub))
                continue;

            var (ephPub, ephPriv) = Crypto.NewEcdhKeyPair();
            var shared = Crypto.TrySharedKey(ephPriv, share.DevicePub);
            if (shared is null)
                continue;

            try
            {
                await _client.RpcAsync("fulfill_key_share", new
                {
                    p_id = share.Id,
                    p_fulfiller_pub = ephPub,
                    p_key_box = Crypto.Encrypt(Convert.ToBase64String(key), shared),
                }, cancellationToken).ConfigureAwait(false);
                done++;
            }
            catch (FamilyLinkException ex) when (!ex.IsNetwork)
            {
                // Otro miembro se adelanto, o la peticion ya no existe: no es asunto de este movil.
                CoreLog.Write($"fulfill_key_share {share.Id}: {ex.Code}");
            }
        }

        return done;
    }

    /// <summary>
    /// Tras recuperar la cuenta (o si algun grupo sale con KeyMissing): recoge las claves que ya
    /// hayan entregado y pide las que falten. Es idempotente: no repite una peticion abierta.
    /// </summary>
    public async Task RequestMissingKeysAsync(CancellationToken cancellationToken = default) =>
        await RequestMissingKeysCountAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>Como <see cref="RequestMissingKeysAsync"/>, devolviendo cuantas claves recupero.</summary>
    public async Task<int> RequestMissingKeysCountAsync(CancellationToken cancellationToken = default)
    {
        var me = await MeAsync(cancellationToken).ConfigureAwait(false);
        var missing = new List<Guid>();
        foreach (var membership in await MyMembershipsAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await _keys.GetAsync(membership.GroupId).ConfigureAwait(false) is null)
                missing.Add(membership.GroupId);
        }

        if (missing.Count == 0)
            return 0;

        var mine = await _client.SelectAsync<KeyShareRow>(
            "key_shares",
            $"select=id,group_id,user_id,device_pub,fulfiller_pub,key_box&user_id=eq.{me:D}&group_id={In(missing)}",
            cancellationToken).ConfigureAwait(false);

        var recovered = 0;
        var open = new HashSet<Guid>();
        foreach (var share in mine)
        {
            var priv = await _secure.GetAsync(SharePrivKey(share.Id)).ConfigureAwait(false);
            if (priv is null)
                continue;   // la pidio otro movil (o una instalacion anterior): no la puedo abrir

            if (string.IsNullOrEmpty(share.KeyBox))
            {
                open.Add(share.GroupId);
                continue;
            }

            var shared = Crypto.TrySharedKey(priv, share.FulfillerPub);
            if (shared is not null
                && Crypto.TryDecrypt(share.KeyBox, shared, out var keyBase64)
                && TryBase64(keyBase64) is { Length: 32 } key
                && await _keys.GetAsync(share.GroupId).ConfigureAwait(false) is null)
            {
                await _keys.SetAsync(share.GroupId, key).ConfigureAwait(false);
                _secure.Remove(SharePrivKey(share.Id));
                recovered++;

                // Con la clave ya puedo volver a publicar mi nombre en ese grupo.
                var (name, avatar) = await ProfileAsync().ConfigureAwait(false);
                if (name.Length > 0)
                {
                    await _client.RpcAsync("update_my_member", new
                    {
                        p_group = share.GroupId,
                        p_display_name_enc = Crypto.Encrypt(name, key),
                        p_avatar_enc = EncryptOrNull(avatar, key),
                    }, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        foreach (var group in missing)
        {
            if (open.Contains(group) || await _keys.GetAsync(group).ConfigureAwait(false) is not null)
                continue;

            var (pub, priv) = Crypto.NewEcdhKeyPair();
            var id = await _client.RpcAsync<Guid>("request_key_share", new { p_group = group, p_device_pub = pub }, cancellationToken)
                .ConfigureAwait(false);
            await _secure.SetAsync(SharePrivKey(id), priv).ConfigureAwait(false);
            await NotifyAsync(EventTypes.KeyShare, id, cancellationToken).ConfigureAwait(false);
        }

        return recovered;
    }

    public async Task RegisterPushTokenAsync(string token, CancellationToken cancellationToken = default) =>
        await _client.RpcAsync("register_push_token", new { p_token = token }, cancellationToken).ConfigureAwait(false);

    // -----------------------------------------------------------------------
    // Para LocationOutbox, SosService y EventFeed
    // -----------------------------------------------------------------------

    /// <summary>Mi id de usuario (crea el anonimo si hace falta).</summary>
    internal async Task<Guid> MeAsync(CancellationToken cancellationToken)
    {
        await _client.EnsureSignedInAsync(cancellationToken).ConfigureAwait(false);
        return _client.UserGuid;
    }

    internal async Task<List<MemberRow>> MyMembershipsAsync(CancellationToken cancellationToken)
    {
        var me = await MeAsync(cancellationToken).ConfigureAwait(false);
        return await _client.SelectAsync<MemberRow>(
            "group_members", $"select=group_id,user_id,role,paused,pause_until&user_id=eq.{me:D}", cancellationToken)
            .ConfigureAwait(false);
    }

    internal Task<List<MemberRow>> MemberRowsAsync(Guid group, CancellationToken cancellationToken) =>
        _client.SelectAsync<MemberRow>(
            "group_members",
            $"select=group_id,user_id,role,display_name_enc,avatar_enc,paused,pause_until&group_id=eq.{group:D}",
            cancellationToken);

    /// <summary>Nombre visible de un miembro, descifrado; «?» si no se puede.</summary>
    internal async Task<string> MemberNameAsync(Guid group, Guid user, CancellationToken cancellationToken)
    {
        var key = await _keys.GetAsync(group).ConfigureAwait(false);
        var row = (await _client.SelectAsync<MemberRow>(
            "group_members",
            $"select=group_id,user_id,display_name_enc&group_id=eq.{group:D}&user_id=eq.{user:D}",
            cancellationToken).ConfigureAwait(false)).FirstOrDefault();

        return row is null ? Unreadable : Crypto.DecryptOr(row.DisplayNameEnc, key, Unreadable);
    }

    internal async Task<string> GroupNameAsync(Guid group, CancellationToken cancellationToken)
    {
        var key = await _keys.GetAsync(group).ConfigureAwait(false);
        var row = (await _client.SelectAsync<GroupRow>("groups", $"select=id,name_enc&id=eq.{group:D}", cancellationToken)
            .ConfigureAwait(false)).FirstOrDefault();

        return row is null || key is null ? Unreadable : Crypto.DecryptOr(row.NameEnc, key, Unreadable);
    }

    /// <summary>Inserta una tanda de posiciones de un grupo, ya cifradas con su clave.</summary>
    internal async Task<int> InsertPositionsAsync(
        Guid group, IReadOnlyList<(double Lat, double Lon, double Acc, int Battery, DateTimeOffset At)> points,
        CancellationToken cancellationToken)
    {
        var key = await RequireKeyAsync(group).ConfigureAwait(false);
        var me = await MeAsync(cancellationToken).ConfigureAwait(false);

        var rows = points.Select(p => (object)new
        {
            group_id = group,
            user_id = me,
            recorded_at = p.At.ToUniversalTime(),
            battery = (short)Math.Clamp(p.Battery, 0, 100),
            payload_enc = Crypto.Encrypt(Payloads.Position(p.Lat, p.Lon, p.Acc), key),
        }).ToList();

        await _client.InsertAsync("positions", rows, cancellationToken).ConfigureAwait(false);
        return rows.Count;
    }

    /// <summary><c>notify</c> de un evento. Si falla, se registra y no rompe nada.</summary>
    internal async Task<bool> NotifyAsync(string type, Guid id, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _client.InvokeFunctionAsync("notify", new { type, id }, cancellationToken)
                .ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
                return true;

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            CoreLog.Write($"notify {type} {id}: {(int)response.StatusCode} {body}");
            return false;
        }
        catch (Exception ex) when (ex is FamilyLinkException or HttpRequestException)
        {
            CoreLog.Write($"notify {type} {id}: {ex.Message}");
            return false;
        }
    }

    // -----------------------------------------------------------------------

    private async Task<byte[]> RequireKeyAsync(Guid group) =>
        await _keys.GetAsync(group).ConfigureAwait(false)
        ?? throw new FamilyLinkException(FamilyLinkException.KeyMissing, $"Sin la clave del grupo {group}.");

    private static Member ToMember(MemberRow row, byte[]? key, Guid me, DateTimeOffset now)
    {
        string? avatar = null;
        if (!string.IsNullOrEmpty(row.AvatarEnc) && Crypto.TryDecrypt(row.AvatarEnc, key, out var a) && a.Length > 0)
            avatar = a;

        var paused = row.EffectivelyPaused(now);
        return new Member(
            row.GroupId,
            row.UserId,
            Crypto.DecryptOr(row.DisplayNameEnc, key, Unreadable) is { Length: > 0 } name ? name : Unreadable,
            avatar,
            row.IsAdmin,
            paused,
            paused ? row.PauseUntil : null,
            row.UserId == me);
    }

    private static MemberPosition? ToPosition(PositionRow row, byte[]? key) =>
        Payloads.Open<Payloads.PositionData>(row.PayloadEnc, key) is { } p
            ? new MemberPosition(row.UserId, p.Lat, p.Lon, p.Acc, row.Battery ?? -1, row.RecordedAt)
            : null;

    private async Task SaveProfileAsync(string name, string? avatar, bool keepAvatar = false)
    {
        await _secure.SetAsync(KeyProfileName, name).ConfigureAwait(false);
        if (keepAvatar)
            return;

        if (string.IsNullOrEmpty(avatar))
            _secure.Remove(KeyProfileAvatar);
        else
            await _secure.SetAsync(KeyProfileAvatar, avatar).ConfigureAwait(false);
    }

    private async Task AddPendingRequestAsync(Guid id)
    {
        var ids = (await GetMyPendingRequestIdsAsync().ConfigureAwait(false)).ToList();
        if (!ids.Contains(id))
            ids.Add(id);
        await _secure.SetAsync(KeyPendingRequests, string.Join(',', ids)).ConfigureAwait(false);
    }

    private async Task ForgetRequestAsync(Guid id)
    {
        var ids = (await GetMyPendingRequestIdsAsync().ConfigureAwait(false)).Where(g => g != id).ToList();
        if (ids.Count == 0)
            _secure.Remove(KeyPendingRequests);
        else
            await _secure.SetAsync(KeyPendingRequests, string.Join(',', ids)).ConfigureAwait(false);

        _secure.Remove(ReqPrivKey(id));
        _secure.Remove(ReqGroupKey(id));
    }

    private static string? EncryptOrNull(string? plain, byte[] key) =>
        string.IsNullOrEmpty(plain) ? null : Crypto.Encrypt(plain, key);

    private static byte[]? TryBase64(string value)
    {
        try
        {
            return Convert.FromBase64String(value);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    internal static string ReqPrivKey(Guid request) => $"req_priv_{request:D}";

    private static string ReqGroupKey(Guid request) => $"req_group_{request:D}";

    private static string SharePrivKey(Guid share) => $"share_priv_{share:D}";

    /// <summary><c>in.(a,b,c)</c> de PostgREST.</summary>
    internal static string In(IEnumerable<Guid> ids) => $"in.({string.Join(',', ids.Select(i => i.ToString("D")))})";

    /// <summary>Marca de tiempo para un filtro de PostgREST: UTC con <c>Z</c>, sin <c>+</c> que escapar.</summary>
    internal static string Stamp(DateTimeOffset at) =>
        at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}
