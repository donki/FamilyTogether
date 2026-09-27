using System.Net;
using System.Text;
using FamilyTogether.Core;

namespace FamilyTogether.Core.Tests;

public class OsmRoadSourceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"ft-roads-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
            // No existe o sigue abierto: da igual, es temporal.
        }
    }

    /// <summary>Servidor de pruebas: responde por orden y apunta lo que se le pide.</summary>
    private sealed class FakeHandler(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(string Url, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (Requests)   // la fuente pide dos a la vez
                Requests.Add((request.RequestUri!.ToString(), body));
            return respond(request, body);
        }
    }

    private const string OneWay = """
        {"version":0.6,"elements":[
          {"type":"way","id":7,"bounds":{},"nodes":[1,2],"geometry":[{"lat":40.001,"lon":-3.001},{"lat":40.002,"lon":-3.001}]}
        ]}
        """;

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static readonly TrackPoint[] Track = [new(40.0015, -3.0012, 10), new(40.0018, -3.0011, 10)];

    [Fact]
    public async Task Si_el_primer_servidor_da_429_prueba_el_segundo_y_despues_usa_la_cache()
    {
        var handler = new FakeHandler((request, _) => request.RequestUri!.Host == "uno"
            ? new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            : Json(OneWay));
        var source = new OsmRoadSource(new HttpClient(handler), _dir, ["https://uno/api", "https://dos/api"]);

        var first = await source.GetAsync(Track);
        Assert.Single(first.Ways);
        Assert.Equal(0, first.TilesMissing);
        Assert.Equal(2, handler.Requests.Count);

        // Lo pedido es el rectangulo de la tesela fija, sin nada del recorrido.
        var body = Uri.UnescapeDataString(handler.Requests[1].Body.Replace('+', ' '));
        Assert.Contains("(40.00,-3.02,40.02,-3.00)", body);
        Assert.DoesNotContain("40.0015", body);

        // Otra instancia (la app reiniciada) la lee del disco sin preguntar.
        var again = new OsmRoadSource(new HttpClient(handler), _dir, ["https://uno/api", "https://dos/api"]);
        Assert.Single((await again.GetAsync(Track)).Ways);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Cache_caducada_se_renueva_y_si_no_se_puede_se_usa_igual()
    {
        var now = DateTimeOffset.UtcNow;
        var fail = false;
        var handler = new FakeHandler((_, _) => fail ? new HttpResponseMessage(HttpStatusCode.GatewayTimeout) : Json(OneWay));
        var source = new OsmRoadSource(new HttpClient(handler), _dir, ["https://uno/api"], () => now);
        await source.GetAsync(Track);

        now += OsmRoadSource.CacheLifetime + TimeSpan.FromDays(1);
        fail = true;
        var later = new OsmRoadSource(new HttpClient(handler), _dir, ["https://uno/api"], () => now);
        var result = await later.GetAsync(Track);

        Assert.Equal(2, handler.Requests.Count);   // intento renovarla
        Assert.Single(result.Ways);                 // y uso la vieja
        Assert.Equal(0, result.TilesMissing);
    }

    [Fact]
    public async Task Sin_servidores_el_recorrido_sale_recto_sin_error_y_no_insiste()
    {
        var handler = new FakeHandler((_, _) => throw new HttpRequestException("sin red"));
        var source = new OsmRoadSource(new HttpClient(handler), _dir, ["https://uno/api", "https://dos/api"]);
        var snapper = new TrackSnapper(source);

        var result = await snapper.SnapAsync(Track);

        Assert.Equal(0, result.Track.MatchedPoints);
        Assert.Equal(Track.Length, result.Track.Line.Count);
        Assert.Equal(result.TilesWanted, result.TilesMissing);

        // Durante la pausa no se vuelve a preguntar.
        var asked = handler.Requests.Count;
        await snapper.SnapAsync(Track);
        Assert.Equal(asked, handler.Requests.Count);
    }

    [Fact]
    public async Task Respuesta_con_error_de_ejecucion_no_se_guarda()
    {
        var handler = new FakeHandler((_, _) => Json("""{"elements":[],"remark":"runtime error: Query timed out"}"""));
        var source = new OsmRoadSource(new HttpClient(handler), _dir, ["https://uno/api"]);

        var result = await source.GetAsync(Track);

        Assert.Empty(result.Ways);
        Assert.Equal(1, result.TilesMissing);
        Assert.False(Directory.Exists(_dir) && Directory.GetFiles(_dir).Length > 0);
    }

    [Fact]
    public void Limita_las_teselas_y_dice_cuantas_faltan()
    {
        // Una linea recta de 60 teselas hacia el este.
        var track = Enumerable.Range(0, 60).Select(i => new TrackPoint(40.01, -3.0 + i * RoadTile.Size + 0.01, 10)).ToList();
        var tiles = OsmRoadSource.TilesFor(track);
        Assert.Equal(60, tiles.Count);
        Assert.True(tiles.Count > OsmRoadSource.MaxTilesPerTrack);
    }

    [Fact]
    public async Task Mas_teselas_que_el_limite_cuentan_como_que_faltan()
    {
        var handler = new FakeHandler((_, _) => Json("""{"elements":[]}"""));
        var source = new OsmRoadSource(new HttpClient(handler), _dir, ["https://uno/api"]);
        var track = Enumerable.Range(0, 30).Select(i => new TrackPoint(40.01, -3.0 + i * RoadTile.Size + 0.01, 10)).ToList();

        var result = await source.GetAsync(track);

        Assert.Equal(30, result.TilesWanted);
        Assert.Equal(30 - OsmRoadSource.MaxTilesPerTrack, result.TilesMissing);
        Assert.Equal(OsmRoadSource.MaxTilesPerTrack, handler.Requests.Count);
    }

    [Fact]
    public void La_cache_conserva_nodos_y_coordenadas()
    {
        var ways = new List<OsmWay> { new(5, [1, 2, 3], [new(40.1, -3.1), new(40.2, -3.2), new(40.3, -3.3)]) };
        var back = OsmRoadSource.ReadCache(OsmRoadSource.WriteCache(ways));

        var way = Assert.Single(back);
        Assert.Equal(5, way.Id);
        Assert.Equal(ways[0].Nodes, way.Nodes);
        Assert.Equal(ways[0].Points, way.Points);
    }
}
