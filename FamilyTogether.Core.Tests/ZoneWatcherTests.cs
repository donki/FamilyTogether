using FamilyTogether.Core;

namespace FamilyTogether.Core.Tests;

public class ZoneWatcherTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"fl-zones-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SQLite.SQLiteAsyncConnection.ResetPool();
        try { File.Delete(_db); } catch (IOException) { }
    }

    [Theory]
    // r = 150, precision 10 → margen 15: dentro < 135, fuera > 165
    [InlineData(100, 10, true)]
    [InlineData(134, 10, true)]
    [InlineData(140, 10, null)]
    [InlineData(165, 10, null)]
    [InlineData(166, 10, false)]
    // precision 40 → margen 40: dentro < 110, fuera > 190
    [InlineData(120, 40, null)]
    [InlineData(109, 40, true)]
    [InlineData(191, 40, false)]
    public void Clasificacion(double distance, double accuracy, bool? expected)
    {
        Assert.Equal(expected, ZoneWatcher.Classify(distance, 150, accuracy));
    }

    [Fact]
    public void LaFranjaNoCambiaElEstado()
    {
        Assert.True(ZoneWatcher.Next(true, 160, 150, 5));
        Assert.False(ZoneWatcher.Next(false, 140, 150, 5));
        Assert.Null(ZoneWatcher.Next(null, 150, 150, 5));
        Assert.False(ZoneWatcher.Next(true, 170, 150, 5));
        Assert.True(ZoneWatcher.Next(false, 130, 150, 5));
    }

    /// <summary>Un punto a <paramref name="meters"/> al norte.</summary>
    private static (double Lat, double Lon) North(double lat, double lon, double meters) =>
        (lat + meters / 111_195.0, lon);

    [Fact]
    public async Task TransicionesConHisteresisYEstadoPersistido()
    {
        var group = Guid.NewGuid();
        var zone = new Zone(Guid.NewGuid(), group, "Casa", 40.0, -3.0, 150, Guid.NewGuid());
        Task<IReadOnlyList<Zone>> Source(Guid g, CancellationToken _) =>
            Task.FromResult<IReadOnlyList<Zone>>(g == group ? [zone] : []);

        IReadOnlyList<Guid> groups = [group];

        async Task<IReadOnlyList<(Guid Group, Guid Zone, string Kind)>> At(ZoneWatcher w, double meters)
        {
            var (lat, lon) = North(zone.Lat, zone.Lon, meters);
            return await w.EvaluateAsync(lat, lon, 10, groups);
        }

        var watcher = new ZoneWatcher(_db, Source);

        // Primera vez: se sabe que esta fuera, sin aviso.
        Assert.Empty(await At(watcher, 500));
        // Borde: nada.
        Assert.Empty(await At(watcher, 150));
        Assert.Empty(await At(watcher, 140));
        // Dentro de verdad: entrada.
        Assert.Equal((group, zone.Id, "enter"), Assert.Single(await At(watcher, 100)));
        // Ruido en el borde: nada.
        Assert.Empty(await At(watcher, 160));
        Assert.Empty(await At(watcher, 100));

        // Otro watcher sobre el mismo fichero (el servicio reiniciado) recuerda que estaba dentro.
        var restarted = new ZoneWatcher(_db, Source);
        Assert.Equal((group, zone.Id, "exit"), Assert.Single(await At(restarted, 200)));
    }

    [Fact]
    public async Task AlDejarDeCompartirSeOlvidaElEstado()
    {
        var group = Guid.NewGuid();
        var zone = new Zone(Guid.NewGuid(), group, "Cole", 41.0, 2.0, 100, Guid.NewGuid());
        var watcher = new ZoneWatcher(_db, (g, _) => Task.FromResult<IReadOnlyList<Zone>>([zone]));

        Assert.Empty(await watcher.EvaluateAsync(zone.Lat, zone.Lon, 5, [group]));   // dentro, primera vez
        Assert.Empty(await watcher.EvaluateAsync(zone.Lat, zone.Lon, 5, []));        // pausa: se olvida

        var (lat, lon) = North(zone.Lat, zone.Lon, 1000);
        Assert.Empty(await watcher.EvaluateAsync(lat, lon, 5, [group]));             // vuelve fuera: sin aviso
    }

    [Fact]
    public async Task SinRedSeUsaLaCache()
    {
        var group = Guid.NewGuid();
        var zone = new Zone(Guid.NewGuid(), group, "Parque", 41.0, 2.0, 100, Guid.NewGuid());
        var online = true;
        var watcher = new ZoneWatcher(_db, (g, _) => online
            ? Task.FromResult<IReadOnlyList<Zone>>([zone])
            : throw new FamilyTogetherException(FamilyTogetherException.Network, "sin red"));

        Assert.Empty(await watcher.EvaluateAsync(zone.Lat, zone.Lon, 5, [group]));
        online = false;
        watcher.InvalidateZones();

        // Sin cache ni red, el estado se conserva: no hay aviso falso ni se pierde lo sabido.
        var (lat, lon) = North(zone.Lat, zone.Lon, 1000);
        Assert.Empty(await watcher.EvaluateAsync(lat, lon, 5, [group]));

        online = true;
        Assert.Equal((group, zone.Id, "exit"), Assert.Single(await watcher.EvaluateAsync(lat, lon, 5, [group])));
    }
}
