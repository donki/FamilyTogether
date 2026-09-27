using SQLite;

namespace FamilyLink.Core;

/// <summary>
/// De un evento del servidor a lo que hace falta para el aviso: por FCM (<see cref="ResolveAsync"/>)
/// o, sin FCM, consultando (<see cref="PollAsync"/>) (§7).
/// </summary>
/// <remarks>
/// <para><b>FCM solo trae identificadores</b> (<c>type, group_id, event_id</c>…): el contenido se
/// pide por REST con la sesion del usuario —la RLS decide si lo puede ver— y se descifra con la
/// clave del grupo. Asi Google nunca ve nombres ni posiciones.</para>
///
/// <para><b>Repetidos.</b> Un mismo evento puede llegar por FCM y por consulta, o dos veces por FCM.
/// Se recuerdan los ultimos <see cref="SeenLimit"/> <c>event_id</c> en SQLite y lo ya visto
/// devuelve null. Un evento solo se marca como visto cuando se ha podido resolver: si falla la red,
/// la siguiente vez se intenta otra vez.</para>
/// </remarks>
public sealed class EventFeed
{
    public const int SeenLimit = 500;

    /// <summary>Solape entre consultas, para no perder nada por relojes desajustados. Los repetidos se descartan.</summary>
    private static readonly TimeSpan Overlap = TimeSpan.FromMinutes(5);

    /// <summary>En la primera consulta no se sacan avisos viejos: solo lo de este rato.</summary>
    private static readonly TimeSpan FirstPollWindow = TimeSpan.FromMinutes(15);

    private const string KeyLastPoll = "last_poll";

    private readonly SQLiteAsyncConnection _db;
    private readonly FamilyService _service;
    private readonly SupabaseClient _client;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _ready;

    public EventFeed(string databasePath, FamilyService service)
    {
        _db = new SQLiteAsyncConnection(databasePath);
        _service = service;
        _client = service.Client;
    }

    [Table("seen_events")]
    internal sealed class SeenEvent
    {
        [PrimaryKey]
        public string EventId { get; set; } = "";

        [Indexed]
        public long SeenUtcTicks { get; set; }
    }

    [Table("feed_state")]
    internal sealed class FeedState
    {
        [PrimaryKey]
        public string Key { get; set; } = "";
        public string Value { get; set; } = "";
    }

    private async Task InitAsync()
    {
        if (_ready)
            return;

        await _db.CreateTableAsync<SeenEvent>().ConfigureAwait(false);
        await _db.CreateTableAsync<FeedState>().ConfigureAwait(false);
        _ready = true;
    }

    /// <summary>
    /// Resuelve un evento recibido por FCM. Null si ya se vio, si no aplica (es mio, ya se resolvio,
    /// ya no soy miembro…) o si es de los silenciosos (<c>key_share</c>, que se atiende aqui mismo).
    /// </summary>
    public async Task<NotificationContent?> ResolveAsync(string type, Guid groupId, Guid eventId, CancellationToken cancellationToken = default)
    {
        await InitAsync().ConfigureAwait(false);

        if (type == EventTypes.KeyShare)
        {
            // Aviso silencioso: alguien necesita la clave de un grupo. Se entrega y no se muestra nada.
            await _service.FulfillPendingKeySharesAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        if (await IsSeenAsync(eventId).ConfigureAwait(false))
            return null;

        var content = type switch
        {
            EventTypes.Sos => await ResolveSosAsync(groupId, eventId, cancellationToken).ConfigureAwait(false),
            EventTypes.ZoneEvent => await ResolveZoneEventAsync(eventId, cancellationToken).ConfigureAwait(false),
            EventTypes.JoinRequest => await ResolveJoinRequestAsync(eventId, cancellationToken).ConfigureAwait(false),
            EventTypes.RequestResolved => await ResolveMyRequestAsync(eventId, cancellationToken).ConfigureAwait(false),
            _ => null,
        };

        // Resuelto (con aviso o sin el): no se vuelve a mirar. Si hubo excepcion no se llega aqui.
        await MarkSeenAsync(eventId).ConfigureAwait(false);
        return content;
    }

    /// <summary>
    /// Lo nuevo desde la ultima consulta, para cuando no hay FCM: SOS de mis grupos, eventos de zona
    /// a los que estoy suscrito, solicitudes pendientes donde soy administrador y la resolucion de
    /// mis propias solicitudes.
    /// </summary>
    public async Task<IReadOnlyList<NotificationContent>> PollAsync(CancellationToken cancellationToken = default)
    {
        await InitAsync().ConfigureAwait(false);
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var started = DateTimeOffset.UtcNow;
            var last = await _db.FindAsync<FeedState>(KeyLastPoll).ConfigureAwait(false);
            var since = last is not null && long.TryParse(last.Value, out var ticks)
                ? new DateTimeOffset(ticks, TimeSpan.Zero) - Overlap
                : started - FirstPollWindow;

            var me = await _service.MeAsync(cancellationToken).ConfigureAwait(false);
            var memberships = await _service.MyMembershipsAsync(cancellationToken).ConfigureAwait(false);
            var result = new List<NotificationContent>();

            async Task Add(string type, Guid group, Guid id)
            {
                if (await ResolveAsync(type, group, id, cancellationToken).ConfigureAwait(false) is { } content)
                    result.Add(content);
            }

            if (memberships.Count > 0)
            {
                // SOS: la RLS solo deja ver los que tienen como destino un grupo mio.
                var alerts = await _client.SelectAsync<SosAlertRow>(
                    "sos_alerts",
                    $"select=id,user_id,created_at&created_at=gt.{FamilyService.Stamp(since)}&user_id=neq.{me:D}&order=created_at.asc",
                    cancellationToken).ConfigureAwait(false);

                foreach (var alert in alerts)
                {
                    var target = (await _client.SelectAsync<SosTargetRow>(
                        "sos_targets", $"select=sos_id,group_id&sos_id=eq.{alert.Id:D}&limit=1", cancellationToken)
                        .ConfigureAwait(false)).FirstOrDefault();
                    if (target is not null)
                        await Add(EventTypes.Sos, target.GroupId, alert.Id).ConfigureAwait(false);
                }

                // Zonas: solo las combinaciones persona + zona + sentido a las que estoy suscrito.
                var subscriptions = await _client.SelectAsync<SubscriptionRow>(
                    "zone_subscriptions",
                    $"select=group_id,target_id,zone_id,on_enter,on_exit&observer_id=eq.{me:D}",
                    cancellationToken).ConfigureAwait(false);

                if (subscriptions.Count > 0)
                {
                    var events = await _client.SelectAsync<ZoneEventRow>(
                        "zone_events",
                        $"select=id,group_id,user_id,zone_id,kind,occurred_at&occurred_at=gt.{FamilyService.Stamp(since)}" +
                        $"&user_id={FamilyService.In(subscriptions.Select(s => s.TargetId).Distinct())}&order=occurred_at.asc",
                        cancellationToken).ConfigureAwait(false);

                    foreach (var e in events.Where(e => subscriptions.Any(s =>
                        s.TargetId == e.UserId && s.ZoneId == e.ZoneId && (e.Kind == "enter" ? s.OnEnter : s.OnExit))))
                    {
                        await Add(EventTypes.ZoneEvent, e.GroupId, e.Id).ConfigureAwait(false);
                    }
                }

                // Solicitudes pendientes en los grupos donde soy administrador.
                var adminGroups = memberships.Where(m => m.IsAdmin).Select(m => m.GroupId).ToList();
                if (adminGroups.Count > 0)
                {
                    var requests = await _client.SelectAsync<JoinRequestRow>(
                        "join_requests",
                        $"select=id,group_id,created_at&status=eq.pending&created_at=gt.{FamilyService.Stamp(since)}" +
                        $"&group_id={FamilyService.In(adminGroups)}&order=created_at.asc",
                        cancellationToken).ConfigureAwait(false);

                    foreach (var request in requests)
                        await Add(EventTypes.JoinRequest, request.GroupId, request.Id).ConfigureAwait(false);
                }
            }

            // Mis solicitudes: si ya se resolvieron.
            var mine = await _service.GetMyPendingRequestIdsAsync().ConfigureAwait(false);
            if (mine.Count > 0)
            {
                var rows = await _client.SelectAsync<JoinRequestRow>(
                    "join_requests", $"select=id,group_id,status&id={FamilyService.In(mine)}", cancellationToken).ConfigureAwait(false);

                foreach (var row in rows.Where(r => r.Status != "pending"))
                    await Add(EventTypes.RequestResolved, row.GroupId, row.Id).ConfigureAwait(false);
            }

            await _db.InsertOrReplaceAsync(new FeedState { Key = KeyLastPoll, Value = started.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture) })
                .ConfigureAwait(false);
            return result;
        }
        finally
        {
            _lock.Release();
        }
    }

    // -----------------------------------------------------------------------

    private async Task<NotificationContent?> ResolveSosAsync(Guid groupId, Guid eventId, CancellationToken cancellationToken)
    {
        var me = await _service.MeAsync(cancellationToken).ConfigureAwait(false);
        var alert = (await _client.SelectAsync<SosAlertRow>(
            "sos_alerts", $"select=id,user_id,created_at&id=eq.{eventId:D}", cancellationToken).ConfigureAwait(false)).FirstOrDefault();
        if (alert is null || alert.UserId == me)
            return null;

        var targets = await _client.SelectAsync<SosTargetRow>(
            "sos_targets", $"select=sos_id,group_id,payload_enc&sos_id=eq.{eventId:D}", cancellationToken).ConfigureAwait(false);

        // El del grupo que dice el aviso; si no, cualquiera de los mios que pueda abrir.
        SosTargetRow? chosen = null;
        Payloads.SosData? data = null;
        foreach (var target in targets.OrderByDescending(t => t.GroupId == groupId))
        {
            var key = await _service.Keys.GetAsync(target.GroupId).ConfigureAwait(false);
            chosen ??= target;
            if (Payloads.Open<Payloads.SosData>(target.PayloadEnc, key) is { } opened)
            {
                chosen = target;
                data = opened;
                break;
            }
        }

        if (chosen is null)
            return null;

        return new NotificationContent(
            Channels.Sos,
            EventTypes.Sos,
            chosen.GroupId,
            eventId,
            await _service.GroupNameAsync(chosen.GroupId, cancellationToken).ConfigureAwait(false),
            await _service.MemberNameAsync(chosen.GroupId, alert.UserId, cancellationToken).ConfigureAwait(false),
            null,
            data?.Stale == true ? "stale" : null,
            data?.Lat,
            data?.Lon);
    }

    private async Task<NotificationContent?> ResolveZoneEventAsync(Guid eventId, CancellationToken cancellationToken)
    {
        var me = await _service.MeAsync(cancellationToken).ConfigureAwait(false);
        var e = (await _client.SelectAsync<ZoneEventRow>(
            "zone_events", $"select=id,group_id,user_id,zone_id,kind,occurred_at&id=eq.{eventId:D}", cancellationToken)
            .ConfigureAwait(false)).FirstOrDefault();
        if (e is null || e.UserId == me)
            return null;

        var key = await _service.Keys.GetAsync(e.GroupId).ConfigureAwait(false);
        var zone = (await _client.SelectAsync<ZoneRow>(
            "zones", $"select=id,group_id,name_enc,geo_enc,created_by&id=eq.{e.ZoneId:D}", cancellationToken)
            .ConfigureAwait(false)).FirstOrDefault();
        var geo = zone is null ? null : Payloads.Open<Payloads.ZoneData>(zone.GeoEnc, key);

        return new NotificationContent(
            Channels.Zones,
            EventTypes.ZoneEvent,
            e.GroupId,
            eventId,
            await _service.GroupNameAsync(e.GroupId, cancellationToken).ConfigureAwait(false),
            await _service.MemberNameAsync(e.GroupId, e.UserId, cancellationToken).ConfigureAwait(false),
            zone is null ? FamilyService.Unreadable : Crypto.DecryptOr(zone.NameEnc, key, FamilyService.Unreadable),
            e.Kind,
            geo?.Lat,
            geo?.Lon);
    }

    private async Task<NotificationContent?> ResolveJoinRequestAsync(Guid eventId, CancellationToken cancellationToken)
    {
        var row = (await _client.SelectAsync<JoinRequestRow>(
            "join_requests",
            $"select={FamilyService.RequestColumns}&id=eq.{eventId:D}",
            cancellationToken).ConfigureAwait(false)).FirstOrDefault();
        if (row is null || row.Status != "pending")
            return null;

        return new NotificationContent(
            Channels.Requests,
            EventTypes.JoinRequest,
            row.GroupId,
            eventId,
            await _service.GroupNameAsync(row.GroupId, cancellationToken).ConfigureAwait(false),
            await _service.OpenRequestNameAsync(row, cancellationToken).ConfigureAwait(false),
            null, null, null, null);
    }

    /// <summary>Mi solicitud se resolvio. Si esta aprobada, de paso se guarda la clave y se publica mi nombre.</summary>
    private async Task<NotificationContent?> ResolveMyRequestAsync(Guid eventId, CancellationToken cancellationToken)
    {
        var row = (await _client.SelectAsync<JoinRequestRow>(
            "join_requests", $"select=id,group_id,status&id=eq.{eventId:D}", cancellationToken).ConfigureAwait(false)).FirstOrDefault();
        var mine = await _service.GetMyPendingRequestIdsAsync().ConfigureAwait(false);
        if (row is null || row.Status == "pending" || !mine.Contains(eventId))
            return null;

        var state = await _service.CheckMyRequestAsync(eventId, cancellationToken).ConfigureAwait(false);
        var groupName = state == JoinState.Approved
            ? await _service.GroupNameAsync(row.GroupId, cancellationToken).ConfigureAwait(false)
            : FamilyService.Unreadable;

        return new NotificationContent(
            Channels.Requests,
            EventTypes.RequestResolved,
            row.GroupId,
            eventId,
            groupName,
            string.Empty,
            null,
            state == JoinState.Approved ? "approved" : "rejected",
            null, null);
    }

    private async Task<bool> IsSeenAsync(Guid eventId) =>
        await _db.FindAsync<SeenEvent>(eventId.ToString("D")).ConfigureAwait(false) is not null;

    private async Task MarkSeenAsync(Guid eventId)
    {
        await _db.InsertOrReplaceAsync(new SeenEvent { EventId = eventId.ToString("D"), SeenUtcTicks = DateTimeOffset.UtcNow.UtcTicks })
            .ConfigureAwait(false);

        // Solo los ultimos SeenLimit: lo mas viejo ya no puede volver a llegar.
        await _db.ExecuteAsync(
            "DELETE FROM seen_events WHERE EventId NOT IN (SELECT EventId FROM seen_events ORDER BY SeenUtcTicks DESC LIMIT ?)",
            SeenLimit).ConfigureAwait(false);
    }
}
