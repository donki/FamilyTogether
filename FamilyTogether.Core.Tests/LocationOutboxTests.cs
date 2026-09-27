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
}
