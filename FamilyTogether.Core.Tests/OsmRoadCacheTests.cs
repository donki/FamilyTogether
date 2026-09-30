using System.Net;
using System.Text;
using System.Text.Json;
using FamilyTogether.Core;

namespace FamilyTogether.Core.Tests;

/// <summary>La cache de calles (memoria y disco) y la lectura de Overpass en sus casos raros.</summary>
public class OsmRoadCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"ft-roadcache-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private sealed class Counting(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(respond());
        }
    }

    /// <summary>Una via que pasa por la tesela de (40.01, lon).</summary>
    private static HttpResponseMessage OneWay() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("""
            {"elements":[{"type":"way","id":7,"nodes":[1,2],"geometry":[{"lat":40.001,"lon":-3.019},{"lat":40.019,"lon":-3.001}]}]}
            """, Encoding.UTF8, "application/json"),
    };

    private static List<TrackPoint> Line(int tiles, double firstLon = -3.0) =>
        [.. Enumerable.Range(0, tiles).Select(i => new TrackPoint(40.01, firstLon + i * RoadTile.Size + 0.01, 10))];

    [Fact]
    public async Task LaMismaInstanciaNoVuelveAlDiscoNiALaRed()
    {
        var handler = new Counting(OneWay);
        var source = new OsmRoadSource(new HttpClient(handler), _dir, ["https://uno/api"]);
        var track = Line(1);

        await source.GetAsync(track);
        Directory.Delete(_dir, true);   // ya no hay disco: tiene que salir de memoria
        var again = await source.GetAsync(track);

        Assert.Single(again.Ways);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task EnMemoriaSoloLasUltimasCuarentaTeselas()
    {
        var handler = new Counting(OneWay);
        var source = new OsmRoadSource(new HttpClient(handler), _dir, ["https://uno/api"]);

        await source.GetAsync(Line(24, -3.0));
        await source.GetAsync(Line(24, -3.0 + 24 * RoadTile.Size));   // 48 en total: salen las 8 primeras
        Assert.Equal(48, handler.Calls);

        Directory.Delete(_dir, true);
        await source.GetAsync(Line(8, -3.0));   // las primeras ya no estan en memoria ni en disco
        Assert.Equal(56, handler.Calls);
        await source.GetAsync(Line(8, -3.0 + 40 * RoadTile.Size));   // las ultimas si
        Assert.Equal(56, handler.Calls);
    }

    [Fact]
    public async Task EnDiscoSoloLasMasRecientes()
    {
        Directory.CreateDirectory(_dir);
        var old = DateTime.UtcNow.AddDays(-1);
        for (var i = 0; i < OsmRoadSource.MaxCachedTiles; i++)
        {
            var file = Path.Combine(_dir, $"roads_{1000 + i}_0.json");
            await File.WriteAllTextAsync(file, """{"v":1,"w":[]}""");
            File.SetLastWriteTimeUtc(file, old.AddMinutes(i));
        }

        var source = new OsmRoadSource(new HttpClient(new Counting(OneWay)), _dir, ["https://uno/api"]);
        await source.GetAsync(Line(1));

        var files = Directory.GetFiles(_dir, "roads_*.json").Select(Path.GetFileName).ToList();
        Assert.Equal(OsmRoadSource.MaxCachedTiles, files.Count);
        Assert.DoesNotContain("roads_1000_0.json", files);   // la mas vieja
        Assert.Contains($"roads_{RoadTile.Of(40.01, -2.99)}.json", files);
    }

    [Fact]
    public async Task CacheIlegibleSeVuelveAPedir()
    {
        Directory.CreateDirectory(_dir);
        var tile = RoadTile.Of(40.01, -2.99);
        await File.WriteAllTextAsync(Path.Combine(_dir, $"roads_{tile}.json"), "{roto");
        var handler = new Counting(OneWay);

        var result = await new OsmRoadSource(new HttpClient(handler), _dir, ["https://uno/api"]).GetAsync(Line(1));

        Assert.Single(result.Ways);
        Assert.Equal(1, handler.Calls);
        Assert.Single(OsmRoadSource.ReadCache(await File.ReadAllBytesAsync(Path.Combine(_dir, $"roads_{tile}.json"))));
    }

    [Fact]
    public async Task SiNoSePuedeGuardarLaCacheSeUsaIgual()
    {
        Directory.CreateDirectory(_dir);
        var notADirectory = Path.Combine(_dir, "fichero");
        await File.WriteAllTextAsync(notADirectory, "x");

        var result = await new OsmRoadSource(new HttpClient(new Counting(OneWay)), notADirectory, ["https://uno/api"]).GetAsync(Line(1));

        Assert.Single(result.Ways);
    }

    [Fact]
    public void CacheConCoordenadasIncompletasEsFormatException()
    {
        Assert.Throws<FormatException>(() => OsmRoadSource.ReadCache(Encoding.UTF8.GetBytes("""{"v":1,"w":[[1,[1,2],[40,-3]]]}""")));
    }

    [Theory]
    [InlineData("""{"version":0.6}""", 0)]                                                                           // sin elements
    [InlineData("""{"elements":[{"type":"node","id":1}]}""", 0)]                                                      // no es via
    [InlineData("""{"elements":[{"type":"way","id":1,"nodes":[1,2]}]}""", 0)]                                          // sin geometria
    [InlineData("""{"elements":[{"type":"way","id":1,"nodes":[1,2],"geometry":[{"lat":1,"lon":2},null]}]}""", 0)]      // punto nulo
    [InlineData("""{"elements":[{"type":"way","id":1,"nodes":[1,2],"geometry":[{"lat":1,"lon":2},{"lat":1}]}]}""", 0)] // punto sin lon
    [InlineData("""{"elements":[{"type":"way","id":1,"nodes":[1],"geometry":[{"lat":1,"lon":2}]}]}""", 0)]            // un solo nodo
    [InlineData("""{"elements":[{"type":"way","id":1,"nodes":[1,2,3],"geometry":[{"lat":1,"lon":2},{"lat":1,"lon":3}]}]}""", 0)] // nodos y puntos no cuadran
    [InlineData("""{"remark":"aviso sin importancia","elements":[{"type":"way","id":1,"nodes":[1,2],"geometry":[{"lat":1,"lon":2},{"lat":1,"lon":3}]}]}""", 1)]
    public void OverpassSoloDaViasCompletas(string json, int expected)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Equal(expected, OsmRoadSource.ParseOverpass(document)!.Count);
    }

    [Fact]
    public async Task ElAjusteConCallesPegaElRecorridoALaVia()
    {
        var source = new OsmRoadSource(new HttpClient(new Counting(OneWay)), _dir, ["https://uno/api"]);
        // Dos puntos junto a la via diagonal de la tesela.
        var track = new List<TrackPoint> { new(40.005, -3.0148, 10), new(40.010, -3.0098, 10), new(40.015, -3.0049, 10) };

        var result = await new TrackSnapper(source).SnapAsync(track);

        Assert.True(result.Track.MatchedPoints > 0);
        Assert.Equal(0, result.TilesMissing);
    }

    [Fact]
    public async Task UnSoloPuntoNoPideNada()
    {
        var handler = new Counting(OneWay);
        var result = await new TrackSnapper(new OsmRoadSource(new HttpClient(handler), _dir, ["https://uno/api"]))
            .SnapAsync([new TrackPoint(40, -3, 5)]);

        Assert.Equal((0, 0), (result.TilesWanted, result.TilesMissing));
        Assert.Single(result.Track.Line);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public void ClienteConLaConfiguracionDeLaCompilacion()
    {
        var client = new SupabaseClient(new HttpClient(new FakeSupabase()), new TestStore());
        Assert.Equal(FamilyTogetherConfig.IsServerConfigured, client.IsConfigured);
    }
}
