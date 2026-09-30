using System.Net;
using System.Security.Cryptography;
using FamilyTogether.Core;

namespace FamilyTogether.Core.Tests;

/// <summary>El envio de la cola: tandas por grupo, sin red se guarda lo avanzado, rechazos se descartan.</summary>
public class LocationOutboxFlushTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"fl-flush-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SQLite.SQLiteAsyncConnection.ResetPool();
        try { File.Delete(_db); } catch (IOException) { }
    }

    private static readonly DateTimeOffset T0 = new(2026, 9, 27, 10, 0, 0, TimeSpan.Zero);

    /// <summary>Lecturas separadas 100 m para que pasen el filtro de distancia.</summary>
    private static async Task FillAsync(LocationOutbox outbox, int count, params Guid[] groups)
    {
        for (var i = 0; i < count; i++)
            await outbox.EnqueueAsync(40.0 + i * 0.001, -3.0, 8, 50, T0.AddMinutes(i), groups);
    }

    [Fact]
    public async Task EnviaCadaGrupoConSuClaveYVaciaLaCola()
    {
        var phone = new Phone();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var ka = await phone.WithKeyAsync(a);
        var kb = await phone.WithKeyAsync(b);
        var outbox = new LocationOutbox(_db);
        await FillAsync(outbox, 3, a, b);

        Assert.Equal(6, await outbox.FlushAsync(phone.Service));

        Assert.Equal(0, await outbox.PendingCountAsync());
        var posts = phone.Server.To("POST", "/rest/v1/positions").ToList();
        Assert.Equal(2, posts.Count);
        foreach (var post in posts)
        {
            var rows = post.Json.EnumerateArray().ToList();
            Assert.Equal(3, rows.Count);
            var group = rows[0].GetProperty("group_id").GetGuid();
            Assert.NotNull(Payloads.Open<Payloads.PositionData>(rows[0].GetProperty("payload_enc").GetString(), group == a ? ka : kb));
        }
    }

    [Fact]
    public async Task MasDeUnaTandaSeEnviaEnVariasPeticiones()
    {
        var phone = new Phone();
        var a = Guid.NewGuid();
        await phone.WithKeyAsync(a);
        var outbox = new LocationOutbox(_db);
        await FillAsync(outbox, 205, a);

        Assert.Equal(205, await outbox.FlushAsync(phone.Service));

        Assert.Equal([200, 5], phone.Server.To("POST", "/rest/v1/positions").Select(p => p.Json.GetArrayLength()));
    }

    [Fact]
    public async Task SinRedSeGuardaLoAvanzadoYSeSigueLaProximaVez()
    {
        var phone = new Phone();
        var a = Guid.Parse("00000000-0000-0000-0000-000000000001");   // A sale antes que B
        var b = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
        await phone.WithKeyAsync(a);
        await phone.WithKeyAsync(b);
        var offline = false;
        phone.Server.On("POST", "/rest/v1/positions", r =>
        {
            if (offline || r.Json[0].GetProperty("group_id").GetGuid() == b)
            {
                offline = true;
                throw new HttpRequestException("sin red");
            }
            return FakeSupabase.Json("");
        });
        var outbox = new LocationOutbox(_db);
        await FillAsync(outbox, 2, a, b);

        var sent = await outbox.FlushAsync(phone.Service);

        // A salio; a las filas solo les queda B.
        Assert.Equal(2, sent);
        var rows = await outbox.PeekAsync();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal([b], r.GroupList));

        offline = false;
        phone.Server.On("POST", "/rest/v1/positions", "");
        Assert.Equal(4 - sent, await outbox.FlushAsync(phone.Service));
        Assert.Equal(0, await outbox.PendingCountAsync());
    }

    [Fact]
    public async Task ErrorDelServidorTambienEsperaALaSiguienteVuelta()
    {
        var phone = new Phone();
        var a = Guid.NewGuid();
        await phone.WithKeyAsync(a);
        phone.Server.On("POST", "/rest/v1/positions", _ => FakeSupabase.Status(HttpStatusCode.ServiceUnavailable));
        var outbox = new LocationOutbox(_db);
        await FillAsync(outbox, 2, a);

        Assert.Equal(0, await outbox.FlushAsync(phone.Service));
        Assert.Equal(2, await outbox.PendingCountAsync());
    }

    [Fact]
    public async Task RechazoDefinitivoOSinClaveDescartaSoloEseGrupo()
    {
        var phone = new Phone();
        var ok = Guid.NewGuid();
        var sinClave = Guid.NewGuid();
        var expulsado = Guid.NewGuid();
        await phone.WithKeyAsync(ok);
        await phone.WithKeyAsync(expulsado);
        phone.Server.On("POST", "/rest/v1/positions", r => r.Json[0].GetProperty("group_id").GetGuid() == expulsado
            ? FakeSupabase.Json("""{"code":"P0001","message":"not_member"}""", HttpStatusCode.BadRequest)
            : FakeSupabase.Json(""));
        var outbox = new LocationOutbox(_db);
        await FillAsync(outbox, 2, ok, sinClave, expulsado);

        Assert.Equal(2, await outbox.FlushAsync(phone.Service));
        Assert.Equal(0, await outbox.PendingCountAsync());
    }

    [Fact]
    public async Task ColaVaciaNoLlama()
    {
        var phone = new Phone();
        Assert.Equal(0, await new LocationOutbox(_db).FlushAsync(phone.Service));
        Assert.Empty(phone.Server.Requests);
    }

    [Fact]
    public async Task BorrarHistorialConLaColaVacia()
    {
        var phone = new Phone();
        phone.Server.Rpc("clear_my_history", "0");
        Assert.Equal(0, await new LocationOutbox(_db).ClearHistoryAsync(phone.Service));
    }

    [Fact]
    public async Task LecturasQueNoSeEncolan()
    {
        var outbox = new LocationOutbox(_db);
        await outbox.EnqueueAsync(40, -3, 8, 50, T0, []);
        await outbox.EnqueueAsync(40, -3, 101, 50, T0, [Guid.NewGuid()]);
        await outbox.EnqueueAsync(double.NaN, -3, 8, 50, T0, [Guid.NewGuid()]);
        await outbox.EnqueueAsync(40, double.NaN, 8, 50, T0, [Guid.NewGuid()]);
        Assert.Equal(0, await outbox.PendingCountAsync());
    }

    // --- Piezas pequeñas que quedaban sin probar -------------------------------------------

    [Fact]
    public async Task ClaveGuardadaMalFormadaODeOtraLongitudNoSeUsa()
    {
        var store = new TestStore();
        var keys = new GroupKeyStore(store);
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        store.Values[GroupKeyStore.StorageKey(a)] = "esto no es base64!";
        store.Values[GroupKeyStore.StorageKey(b)] = Convert.ToBase64String(new byte[16]);

        Assert.Null(await keys.GetAsync(a));
        Assert.Null(await keys.GetAsync(b));
        await Assert.ThrowsAsync<ArgumentException>(() => keys.SetAsync(a, new byte[31]));
    }

    [Fact]
    public void SharedKeyConClaveMalFormadaEsCryptographicException()
    {
        var (pub, priv) = Crypto.NewEcdhKeyPair();
        Assert.Throws<CryptographicException>(() => Crypto.SharedKey("no-base64!", pub));
        Assert.Throws<CryptographicException>(() => Crypto.SharedKey(priv, "no-base64!"));
        Assert.Null(Crypto.TrySharedKey(priv, "no-base64!"));
        Assert.Null(Crypto.TrySharedKey(null, pub));
    }

    [Fact]
    public void UnRegistroQueFallaNoRompeNada()
    {
        Action<string> bad = _ => throw new InvalidOperationException("roto");
        CoreLog.Written += bad;
        try
        {
            CoreLog.Write("hola");   // no lanza
        }
        finally
        {
            CoreLog.Written -= bad;
        }
    }

    [Fact]
    public void PausaEfectivaComoEnElServidor()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.False(new MemberRow { Paused = false }.EffectivelyPaused(now));
        Assert.True(new MemberRow { Paused = true }.EffectivelyPaused(now));
        Assert.True(new MemberRow { Paused = true, PauseUntil = now.AddMinutes(1) }.EffectivelyPaused(now));
        Assert.False(new MemberRow { Paused = true, PauseUntil = now }.EffectivelyPaused(now));
        Assert.True(new MemberRow { Role = "admin" }.IsAdmin);
        Assert.False(new MemberRow().IsAdmin);
    }

    [Fact]
    public void ConfiguracionCoherenteConSusConstantes()
    {
        Assert.Equal(FamilyTogetherConfig.SupabaseUrl.Length > 0 && FamilyTogetherConfig.PublishableKey.Length > 0,
            FamilyTogetherConfig.IsServerConfigured);
        Assert.Equal(FamilyTogetherConfig.FcmProjectId.Length > 0 && FamilyTogetherConfig.FcmApplicationId.Length > 0
                     && FamilyTogetherConfig.FcmApiKey.Length > 0 && FamilyTogetherConfig.FcmSenderId.Length > 0,
            FamilyTogetherConfig.IsPushConfigured);
    }
}
