using FamilyTogether.Core;

namespace FamilyTogether.Core.Tests;

/// <summary>De evento del servidor a aviso: resolucion, repetidos y consulta sin FCM.</summary>
public class EventFeedTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"fl-feed-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SQLite.SQLiteAsyncConnection.ResetPool();
        try { File.Delete(_db); } catch (IOException) { }
    }

    private static string Rows(object rows) => FakeSupabase.Serialize(rows);

    private sealed class World
    {
        public Phone Phone { get; }
        public Guid Group { get; } = Guid.NewGuid();
        public Guid Other { get; } = Guid.NewGuid();
        public byte[] Key { get; private set; } = [];

        private World(Phone phone) => Phone = phone;

        public static async Task<World> CreateAsync(bool admin = false)
        {
            var w = new World(new Phone());
            w.Phone.MemberOf((w.Group, admin));
            w.Key = await w.Phone.WithKeyAsync(w.Group);
            w.Phone.Server.Table("groups", Rows(new[] { new { id = w.Group, name_enc = Crypto.Encrypt("Casa", w.Key) } }));
            var members = Rows(new[] { new { group_id = w.Group, user_id = w.Other, display_name_enc = Crypto.Encrypt("Ana", w.Key) } });
            w.Phone.Server.Table("group_members", r => FakeSupabase.Json(
                r.Query.Contains("group_id=eq.") ? members
                : Rows(new[] { new { group_id = w.Group, user_id = w.Phone.Me, role = admin ? "admin" : "member" } })));
            return w;
        }
    }

    [Fact]
    public async Task SosDeOtroConSuPosicionYSoloUnaVez()
    {
        var w = await World.CreateAsync();
        var sos = Guid.NewGuid();
        var at = new DateTimeOffset(2026, 9, 27, 10, 0, 0, TimeSpan.Zero);
        w.Phone.Server.Table("sos_alerts", Rows(new[] { new { id = sos, user_id = w.Other, created_at = at } }));
        w.Phone.Server.Table("sos_targets", Rows(new object[]
        {
            new { sos_id = sos, group_id = Guid.NewGuid(), payload_enc = "enc1:de otro grupo" },
            new { sos_id = sos, group_id = w.Group, payload_enc = Crypto.Encrypt(Payloads.Sos(40.5, -3.5, 10, at, true), w.Key) },
        }));
        var feed = new EventFeed(_db, w.Phone.Service);

        var content = await feed.ResolveAsync(EventTypes.Sos, Guid.NewGuid(), sos);

        Assert.Equal(new NotificationContent(Channels.Sos, EventTypes.Sos, w.Group, sos, "Casa", "Ana", null, "stale", 40.5, -3.5), content);
        Assert.Null(await feed.ResolveAsync(EventTypes.Sos, w.Group, sos));   // repetido
        Assert.Null(await new EventFeed(_db, w.Phone.Service).ResolveAsync(EventTypes.Sos, w.Group, sos));   // tambien tras reiniciar
    }

    [Fact]
    public async Task SosSinPoderAbrirLaCargaAvisaSinPosicion()
    {
        var w = await World.CreateAsync();
        var sos = Guid.NewGuid();
        w.Phone.Server.Table("sos_alerts", Rows(new[] { new { id = sos, user_id = w.Other, created_at = DateTimeOffset.UtcNow } }));
        w.Phone.Server.Table("sos_targets", Rows(new[] { new { sos_id = sos, group_id = w.Group, payload_enc = "enc1:roto" } }));

        var content = await new EventFeed(_db, w.Phone.Service).ResolveAsync(EventTypes.Sos, w.Group, sos);

        Assert.NotNull(content);
        Assert.Null(content.Lat);
        Assert.Null(content.Kind);
        Assert.Equal("Ana", content.ActorName);
    }

    [Fact]
    public async Task MiPropioSosOUnoQueNoVeoNoAvisan()
    {
        var w = await World.CreateAsync();
        var mine = Guid.NewGuid();
        var sinDestino = Guid.NewGuid();
        w.Phone.Server.Table("sos_alerts", r => FakeSupabase.Json(Rows(new[]
        {
            new { id = r.Query.Contains(mine.ToString()) ? mine : sinDestino, user_id = r.Query.Contains(mine.ToString()) ? w.Phone.Me : w.Other, created_at = DateTimeOffset.UtcNow },
        })));
        var feed = new EventFeed(_db, w.Phone.Service);

        Assert.Null(await feed.ResolveAsync(EventTypes.Sos, w.Group, mine));
        Assert.Null(await feed.ResolveAsync(EventTypes.Sos, w.Group, sinDestino));   // sin sos_targets
        Assert.Null(await feed.ResolveAsync(EventTypes.Sos, w.Group, Guid.NewGuid()));
    }

    [Fact]
    public async Task EventoDeZonaConNombreDeZonaYSentido()
    {
        var w = await World.CreateAsync();
        var e = Guid.NewGuid();
        var zone = Guid.NewGuid();
        w.Phone.Server.Table("zone_events", Rows(new[] { new { id = e, group_id = w.Group, user_id = w.Other, zone_id = zone, kind = "enter", occurred_at = DateTimeOffset.UtcNow } }));
        w.Phone.Server.Table("zones", Rows(new[] { new { id = zone, group_id = w.Group, name_enc = Crypto.Encrypt("Colegio", w.Key), geo_enc = Crypto.Encrypt(Payloads.Zone(40, -3, 100), w.Key) } }));

        var content = await new EventFeed(_db, w.Phone.Service).ResolveAsync(EventTypes.ZoneEvent, w.Group, e);

        Assert.Equal(new NotificationContent(Channels.Zones, EventTypes.ZoneEvent, w.Group, e, "Casa", "Ana", "Colegio", "enter", 40, -3), content);
    }

    [Fact]
    public async Task EventoDeZonaBorradaOPropio()
    {
        var w = await World.CreateAsync();
        var e = Guid.NewGuid();
        var mio = Guid.NewGuid();
        w.Phone.Server.Table("zone_events", r => FakeSupabase.Json(Rows(new[]
        {
            new { id = e, group_id = w.Group, user_id = r.Query.Contains(mio.ToString()) ? w.Phone.Me : w.Other, zone_id = Guid.NewGuid(), kind = "exit", occurred_at = DateTimeOffset.UtcNow },
        })));
        var feed = new EventFeed(_db, w.Phone.Service);

        var content = await feed.ResolveAsync(EventTypes.ZoneEvent, w.Group, e);
        Assert.Equal(("?", "exit", (double?)null), (content!.ZoneName, content.Kind, content.Lat));
        Assert.Null(await feed.ResolveAsync(EventTypes.ZoneEvent, w.Group, mio));
    }

    [Fact]
    public async Task SolicitudPendienteAvisaYResueltaNo()
    {
        var w = await World.CreateAsync(admin: true);
        var pending = Guid.NewGuid();
        var done = Guid.NewGuid();
        w.Phone.Server.Table("join_requests", r => FakeSupabase.Json(Rows(new[]
        {
            new { id = pending, group_id = w.Group, status = r.Query.Contains(done.ToString()) ? "approved" : "pending", created_at = DateTimeOffset.UtcNow },
        })));
        var feed = new EventFeed(_db, w.Phone.Service);

        var content = await feed.ResolveAsync(EventTypes.JoinRequest, w.Group, pending);
        Assert.Equal((Channels.Requests, "Casa", "?"), (content!.Channel, content.GroupName, content.ActorName));
        Assert.Null(await feed.ResolveAsync(EventTypes.JoinRequest, w.Group, done));
    }

    [Fact]
    public async Task MiSolicitudResueltaSoloSiEsMia()
    {
        var w = await World.CreateAsync();
        var mine = Guid.NewGuid();
        var ajena = Guid.NewGuid();
        w.Phone.Store.Values["pending_join_requests"] = mine.ToString();
        w.Phone.Server.Table("join_requests", r => FakeSupabase.Json(Rows(new[] { new { id = mine, group_id = w.Group, status = "rejected" } })));
        var feed = new EventFeed(_db, w.Phone.Service);

        Assert.Null(await feed.ResolveAsync(EventTypes.RequestResolved, w.Group, ajena));
        var content = await feed.ResolveAsync(EventTypes.RequestResolved, w.Group, mine);
        Assert.Equal(("rejected", "?"), (content!.Kind, content.GroupName));
        Assert.Empty(await w.Phone.Service.GetMyPendingRequestIdsAsync());
    }

    [Fact]
    public async Task MiSolicitudAprobadaDaElNombreDelGrupo()
    {
        var w = await World.CreateAsync();
        var mine = Guid.NewGuid();
        w.Phone.Store.Values["pending_join_requests"] = mine.ToString();
        w.Phone.Server.Table("join_requests", Rows(new[] { new { id = mine, group_id = w.Group, status = "approved" } }));

        var content = await new EventFeed(_db, w.Phone.Service).ResolveAsync(EventTypes.RequestResolved, w.Group, mine);

        Assert.Equal(("approved", "Casa"), (content!.Kind, content.GroupName));
    }

    [Fact]
    public async Task KeyShareEsSilenciosoYEntregaLaClave()
    {
        var w = await World.CreateAsync(admin: true);
        var (pub, _) = Crypto.NewEcdhKeyPair();
        w.Phone.Server.Table("key_shares", Rows(new[] { new { id = Guid.NewGuid(), group_id = w.Group, user_id = w.Other, device_pub = pub } }));

        Assert.Null(await new EventFeed(_db, w.Phone.Service).ResolveAsync(EventTypes.KeyShare, w.Group, Guid.NewGuid()));
        Assert.Single(w.Phone.Server.To("POST", "/rest/v1/rpc/fulfill_key_share"));
    }

    [Fact]
    public async Task TipoDesconocidoNoAvisaPeroSeMarcaVisto()
    {
        var w = await World.CreateAsync();
        var feed = new EventFeed(_db, w.Phone.Service);
        Assert.Null(await feed.ResolveAsync("otro", w.Group, Guid.NewGuid()));
    }

    [Fact]
    public async Task SiFallaLaRedNoSeMarcaVistoYSeReintenta()
    {
        var w = await World.CreateAsync();
        var sos = Guid.NewGuid();
        var fail = true;
        w.Phone.Server.Table("sos_alerts", _ => fail
            ? throw new HttpRequestException("sin red")
            : FakeSupabase.Json(Rows(new[] { new { id = sos, user_id = w.Other, created_at = DateTimeOffset.UtcNow } })));
        w.Phone.Server.Table("sos_targets", Rows(new[] { new { sos_id = sos, group_id = w.Group, payload_enc = "enc1:x" } }));
        var feed = new EventFeed(_db, w.Phone.Service);

        await Assert.ThrowsAsync<FamilyTogetherException>(() => feed.ResolveAsync(EventTypes.Sos, w.Group, sos));
        fail = false;
        Assert.NotNull(await feed.ResolveAsync(EventTypes.Sos, w.Group, sos));
    }

    [Fact]
    public async Task ConsultaJuntaSosZonasSolicitudesYLasMiasYRecuerdaDesdeCuando()
    {
        var w = await World.CreateAsync(admin: true);
        var sos = Guid.NewGuid();
        var zone = Guid.NewGuid();
        var otraZona = Guid.NewGuid();
        var eEnter = Guid.NewGuid();
        var eExit = Guid.NewGuid();
        var eOtraZona = Guid.NewGuid();
        var request = Guid.NewGuid();
        var mine = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        w.Phone.Store.Values["pending_join_requests"] = mine.ToString();

        w.Phone.Server.Table("sos_alerts", r => FakeSupabase.Json(Rows(new[] { new { id = sos, user_id = w.Other, created_at = now } })));
        w.Phone.Server.Table("sos_targets", Rows(new[] { new { sos_id = sos, group_id = w.Group, payload_enc = Crypto.Encrypt(Payloads.Sos(1, 2, 3, now, false), w.Key) } }));
        w.Phone.Server.Table("zone_subscriptions", Rows(new[] { new { group_id = w.Group, target_id = w.Other, zone_id = zone, on_enter = true, on_exit = false } }));
        w.Phone.Server.Table("zone_events", r => FakeSupabase.Json(Rows(
            r.Query.Contains("occurred_at=gt.")
                ? new object[]
                {
                    new { id = eEnter, group_id = w.Group, user_id = w.Other, zone_id = zone, kind = "enter", occurred_at = now },
                    new { id = eExit, group_id = w.Group, user_id = w.Other, zone_id = zone, kind = "exit", occurred_at = now },        // no suscrito a salir
                    new { id = eOtraZona, group_id = w.Group, user_id = w.Other, zone_id = otraZona, kind = "enter", occurred_at = now },
                }
                : new object[] { new { id = eEnter, group_id = w.Group, user_id = w.Other, zone_id = zone, kind = "enter", occurred_at = now } })));
        w.Phone.Server.Table("join_requests", r => FakeSupabase.Json(Rows(
            r.Query.Contains("status=eq.pending")
                ? new object[] { new { id = request, group_id = w.Group, status = "pending", created_at = now } }
                : r.Query.Contains($"id=in.({mine:D})")
                    ? new object[] { new { id = mine, group_id = w.Group, status = "rejected" } }
                    : r.Query.Contains(request.ToString())
                        ? new object[] { new { id = request, group_id = w.Group, status = "pending", created_at = now } }
                        : new object[] { new { id = mine, group_id = w.Group, status = "rejected" } })));
        var feed = new EventFeed(_db, w.Phone.Service);

        var first = await feed.PollAsync();

        Assert.Equal([EventTypes.Sos, EventTypes.ZoneEvent, EventTypes.JoinRequest, EventTypes.RequestResolved], first.Select(c => c.EventType));
        Assert.Equal(eEnter, first[1].EventId);
        var sinceFirst = w.Phone.Server.To("GET", "/rest/v1/sos_alerts").First().Query;
        Assert.Contains($"user_id=neq.{w.Phone.Me:D}", sinceFirst);

        // La segunda consulta ya no repite nada y pregunta desde la anterior (menos el solape).
        Assert.Empty(await feed.PollAsync());
        var second = w.Phone.Server.To("GET", "/rest/v1/sos_alerts").Last().Query;
        Assert.NotEqual(sinceFirst, second);
    }

    [Fact]
    public async Task ConsultaSinGruposSoloMiraMisSolicitudes()
    {
        var phone = new Phone().MemberOf();
        var feed = new EventFeed(_db, phone.Service);

        Assert.Empty(await feed.PollAsync());

        Assert.Empty(phone.Server.To("GET", "/rest/v1/sos_alerts"));
        Assert.Empty(phone.Server.To("GET", "/rest/v1/join_requests"));
    }

    [Fact]
    public async Task ConsultaSinSuscripcionesNiSerAdminNoPideEventosNiSolicitudes()
    {
        var w = await World.CreateAsync(admin: false);
        var feed = new EventFeed(_db, w.Phone.Service);

        Assert.Empty(await feed.PollAsync());

        Assert.Single(w.Phone.Server.To("GET", "/rest/v1/sos_alerts"));
        Assert.Empty(w.Phone.Server.To("GET", "/rest/v1/zone_events"));
        Assert.Empty(w.Phone.Server.To("GET", "/rest/v1/join_requests"));
    }

    [Fact]
    public async Task SoloSeRecuerdanLosUltimosVistos()
    {
        var w = await World.CreateAsync();
        var feed = new EventFeed(_db, w.Phone.Service);
        var first = Guid.NewGuid();
        await feed.ResolveAsync("otro", w.Group, first);
        await Task.Delay(20);
        for (var i = 0; i < EventFeed.SeenLimit; i++)
            await feed.ResolveAsync("otro", w.Group, Guid.NewGuid());

        var db = new SQLite.SQLiteAsyncConnection(_db);
        Assert.Equal(EventFeed.SeenLimit, await db.Table<EventFeed.SeenEvent>().CountAsync());
        Assert.Null(await db.FindAsync<EventFeed.SeenEvent>(first.ToString("D")));
    }
}
