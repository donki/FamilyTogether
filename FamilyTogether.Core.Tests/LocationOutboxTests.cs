using System.Net;
using System.Text;
using FamilyTogether.Core;

namespace FamilyTogether.Core.Tests;

public class LocationOutboxTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"fl-outbox-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SQLite.SQLiteAsyncConnection.ResetPool();
        try { File.Delete(_db); } catch (IOException) { }
    }

    [Fact]
    public async Task EncolaConLosGruposQueCompartenEnEseMomento()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var paused = Guid.NewGuid();   // en pausa: la app no lo pasa en la lista
        var outbox = new LocationOutbox(_db);
        var at = new DateTimeOffset(2026, 9, 27, 10, 0, 0, TimeSpan.Zero);

        await outbox.EnqueueAsync(40.0, -3.0, 8, 77, at, [a, b]);

        var row = Assert.Single(await outbox.PeekAsync());
        Assert.Equal(new HashSet<Guid> { a, b }, row.GroupList.ToHashSet());
        Assert.DoesNotContain(paused, row.GroupList);
        Assert.Equal(at, row.At);
        Assert.Equal(77, row.Battery);
        Assert.Equal(1, await outbox.PendingCountAsync());
    }

    [Fact]
    public async Task CadaLecturaGuardaSuPropiaLista()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var outbox = new LocationOutbox(_db);
        var at = DateTimeOffset.UtcNow;

        await outbox.EnqueueAsync(40.0, -3.0, 8, 50, at, [a, b]);
        // B se pone en pausa y la siguiente lectura solo va a A.
        await outbox.EnqueueAsync(40.001, -3.0, 8, 50, at.AddMinutes(1), [a]);

        var rows = await outbox.PeekAsync();
        Assert.Equal(2, rows.Count);
        Assert.Contains(b, rows[0].GroupList);
        Assert.Equal([a], rows[1].GroupList);
    }

    [Fact]
    public async Task DescartaSinGruposPrecisionMalaYSinMoverse()
    {
        var a = Guid.NewGuid();
        var outbox = new LocationOutbox(_db);
        var at = DateTimeOffset.UtcNow;

        await outbox.EnqueueAsync(40.0, -3.0, 8, 50, at, []);            // todo en pausa
        await outbox.EnqueueAsync(40.0, -3.0, 150, 50, at, [a]);         // precision > 100 m: nunca
        Assert.Equal(0, await outbox.PendingCountAsync());

        await outbox.EnqueueAsync(40.0, -3.0, 8, 50, at, [a]);
        await outbox.EnqueueAsync(40.0001, -3.0, 8, 50, at, [a]);        // ~11 m: no se ha movido
        Assert.Equal(1, await outbox.PendingCountAsync());

        await outbox.EnqueueAsync(40.0003, -3.0, 8, 50, at, [a]);        // ~33 m: si
        Assert.Equal(2, await outbox.PendingCountAsync());
    }

    [Fact]
    public async Task AproximadaSoloSiEnDiezMinutosNoHaHabidoOtra()
    {
        var a = Guid.NewGuid();
        var outbox = new LocationOutbox(_db);
        var t0 = new DateTimeOffset(2026, 9, 27, 10, 0, 0, TimeSpan.Zero);

        // En casa: la primera aproximada (100 m) entra, marcada; otra enseguida, no.
        await outbox.EnqueueAsync(40.0, -3.0, 100, 50, t0, [a]);
        await outbox.EnqueueAsync(40.0, -3.0, 90, 50, t0.AddMinutes(2), [a]);
        var rows = await outbox.PeekAsync();
        Assert.True(Assert.Single(rows).Coarse);

        // Una buena entra aunque este cerca de la aproximada, y no va marcada.
        await outbox.EnqueueAsync(40.0001, -3.0, 10, 50, t0.AddMinutes(3), [a]);
        rows = await outbox.PeekAsync();
        Assert.Equal(2, rows.Count);
        Assert.False(rows[1].Coarse);

        // Con una buena hace menos de 10 minutos, la aproximada no entra; pasados 10, si.
        await outbox.EnqueueAsync(40.0, -3.0, 60, 50, t0.AddMinutes(8), [a]);
        Assert.Equal(2, await outbox.PendingCountAsync());
        await outbox.EnqueueAsync(40.0, -3.0, 60, 50, t0.AddMinutes(14), [a]);
        Assert.Equal(3, await outbox.PendingCountAsync());
    }

    [Fact]
    public async Task SiCambianLosGruposEntraAunqueNoSeMueva()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var outbox = new LocationOutbox(_db);

        await outbox.EnqueueAsync(40.0, -3.0, 8, 50, DateTimeOffset.UtcNow, [a]);
        await outbox.EnqueueAsync(40.0, -3.0, 8, 50, DateTimeOffset.UtcNow, [a, b]);   // B vuelve de la pausa

        Assert.Equal(2, await outbox.PendingCountAsync());
    }

    /// <summary>Servidor falso: la sesion anonima y la RPC clear_my_history (o un fallo).</summary>
    private sealed class FakeServer(bool fail) : HttpMessageHandler
    {
        public int Clears { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/auth/v1/signup"))
                return Task.FromResult(Json($$$"""{"access_token":"a","refresh_token":"r","expires_in":3600,"user":{"id":"{{{Guid.NewGuid()}}}"}}"""));
            if (path.EndsWith("/rpc/clear_my_history"))
            {
                Clears++;
                return Task.FromResult(fail ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : Json("3"));
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage Json(string json) =>
            new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    private sealed class MemoryStore : ISecureStore, ITokenStore
    {
        private readonly Dictionary<string, string> _d = new();
        public Task<string?> GetAsync(string key) => Task.FromResult(_d.TryGetValue(key, out var v) ? v : null);
        public Task SetAsync(string key, string value) { _d[key] = value; return Task.CompletedTask; }
        Task ITokenStore.SetAsync(string key, string? value)
        {
            if (value is null) _d.Remove(key); else _d[key] = value;
            return Task.CompletedTask;
        }
        public void Remove(string key) => _d.Remove(key);
    }

    private static FamilyService Service(FakeServer server)
    {
        var store = new MemoryStore();
        var client = new SupabaseClient(new HttpClient(server), store, "https://prueba.local", "clave");
        return new FamilyService(client, new GroupKeyStore(store), store);
    }

    [Fact]
    public async Task BorrarHistorialVaciaLaColaMenosLaMasReciente()
    {
        var a = Guid.NewGuid();
        var outbox = new LocationOutbox(_db);
        var t0 = new DateTimeOffset(2026, 9, 27, 10, 0, 0, TimeSpan.Zero);
        await outbox.EnqueueAsync(40.0, -3.0, 8, 50, t0, [a]);
        await outbox.EnqueueAsync(40.001, -3.0, 8, 50, t0.AddMinutes(1), [a]);
        await outbox.EnqueueAsync(40.002, -3.0, 8, 50, t0.AddMinutes(2), [a]);

        var server = new FakeServer(fail: false);
        Assert.Equal(3, await outbox.ClearHistoryAsync(Service(server)));

        Assert.Equal(1, server.Clears);
        var left = Assert.Single(await outbox.PeekAsync());
        Assert.Equal(t0.AddMinutes(2), left.At);
    }

    [Fact]
    public async Task SiElServidorFallaNoSeTocaLaCola()
    {
        var a = Guid.NewGuid();
        var outbox = new LocationOutbox(_db);
        await outbox.EnqueueAsync(40.0, -3.0, 8, 50, DateTimeOffset.UtcNow, [a]);
        await outbox.EnqueueAsync(40.001, -3.0, 8, 50, DateTimeOffset.UtcNow, [a]);

        await Assert.ThrowsAsync<FamilyTogetherException>(() => outbox.ClearHistoryAsync(Service(new FakeServer(fail: true))));

        Assert.Equal(2, await outbox.PendingCountAsync());
    }
}
