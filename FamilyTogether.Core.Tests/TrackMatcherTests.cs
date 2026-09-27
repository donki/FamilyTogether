using FamilyTogether.Core;

namespace FamilyTogether.Core.Tests;

/// <summary>
/// Ajuste de recorridos a la red con grafos sinteticos. Las coordenadas se escriben en metros
/// (x al este, y al norte) alrededor de un origen y se pasan a grados.
/// </summary>
public class TrackMatcherTests
{
    private const double Lat0 = 40.0;
    private const double Lon0 = -3.0;
    private const double MetersPerDegree = 111_195.0;

    private static GeoPoint G(double x, double y) =>
        new(Lat0 + y / MetersPerDegree, Lon0 + x / (MetersPerDegree * Math.Cos(Lat0 * Math.PI / 180)));

    private static (double X, double Y) M(GeoPoint p) =>
        ((p.Lon - Lon0) * MetersPerDegree * Math.Cos(Lat0 * Math.PI / 180), (p.Lat - Lat0) * MetersPerDegree);

    private static TrackPoint P(double x, double y, double accuracy = 10)
    {
        var g = G(x, y);
        return new TrackPoint(g.Lat, g.Lon, accuracy);
    }

    private static long _nextId = 1;

    /// <summary>Via con nodos en esos puntos; <paramref name="ids"/> para compartir nodos con otras.</summary>
    private static OsmWay Way(long[] ids, params (double X, double Y)[] points) =>
        new(_nextId++, ids, [.. points.Select(p => G(p.X, p.Y))]);

    private static bool Near((double X, double Y) a, double x, double y, double tolerance = 1.5) =>
        Math.Abs(a.X - x) < tolerance && Math.Abs(a.Y - y) < tolerance;

    [Fact]
    public void Esquina_en_L_pasa_por_la_esquina()
    {
        // Calle horizontal (0,0)-(200,0) y vertical (200,0)-(200,200); comparten el nodo 2.
        var network = RoadNetwork.Build(
        [
            Way([1, 2], (0, 0), (200, 0)),
            Way([2, 3], (200, 0), (200, 200)),
        ]);

        var result = TrackMatcher.Match(network, [P(120, 6), P(206, 90)]);

        Assert.Equal(2, result.MatchedPoints);
        var line = result.Line.Select(M).ToList();
        Assert.Contains(line, p => Near(p, 200, 0));
        Assert.True(Near(line[0], 120, 0));
        Assert.True(Near(line[^1], 200, 90));
    }

    [Fact]
    public void Calles_paralelas_no_salta_a_la_otra_por_un_punto_ruidoso()
    {
        // Dos calles paralelas a 40 m que solo se unen muy lejos (x = 0 y x = 1000).
        var network = RoadNetwork.Build(
        [
            Way([10, 11, 12, 13], (0, 0), (300, 0), (600, 0), (1000, 0)),
            Way([20, 21, 22, 23], (0, 40), (300, 40), (600, 40), (1000, 40)),
            Way([10, 20], (0, 0), (0, 40)),
            Way([13, 23], (1000, 0), (1000, 40)),
        ]);

        var track = new[] { P(300, 5), P(340, 8), P(380, 24), P(420, 6), P(460, 3) };
        var result = TrackMatcher.Match(network, track);

        Assert.Equal(track.Length, result.MatchedPoints);
        Assert.All(result.Line.Select(M), p => Assert.True(Math.Abs(p.Y) < 1, $"punto fuera de la calle: {p}"));
    }

    [Fact]
    public void Punto_ruidoso_se_pega_a_la_calle()
    {
        var network = RoadNetwork.Build([Way([1, 2, 3], (0, 0), (250, 0), (500, 0))]);

        var result = TrackMatcher.Match(network, [P(50, 4), P(100, -6), P(150, 30, 25), P(200, 2), P(250, -3)]);

        Assert.Equal(5, result.MatchedPoints);
        Assert.All(result.Line.Select(M), p => Assert.True(Math.Abs(p.Y) < 1));
        Assert.Contains(result.Line.Select(M), p => Near(p, 150, 0));
    }

    [Fact]
    public void Hueco_sin_calles_se_deja_tal_cual()
    {
        var network = RoadNetwork.Build([Way([1, 2], (0, 0), (200, 0))]);

        var result = TrackMatcher.Match(network, [P(50, 3), P(150, -3), P(400, 300), P(500, 320)]);

        Assert.Equal(2, result.MatchedPoints);
        var line = result.Line.Select(M).ToList();
        Assert.True(Near(line[^2], 400, 300, 0.01));
        Assert.True(Near(line[^1], 500, 320, 0.01));
        Assert.True(Near(line[0], 50, 0));
    }

    [Fact]
    public void Nunca_inventa_un_rodeo_absurdo()
    {
        // Dos calles a 50 m que solo se unen a 2 km: pasar de una a otra por la red seria absurdo.
        var network = RoadNetwork.Build(
        [
            Way([1, 2, 3], (0, 0), (100, 0), (2000, 0)),
            Way([4, 5, 6], (0, 50), (100, 50), (2000, 50)),
            Way([3, 6], (2000, 0), (2000, 50)),
        ]);

        var result = TrackMatcher.Match(network, [P(100, 3), P(100, 47)]);

        Assert.Equal(2, result.MatchedPoints);
        Assert.All(result.Line.Select(M), p => Assert.True(p.X < 150, $"rodeo: {p}"));
        Assert.Equal(2, result.Line.Count);
    }

    [Fact]
    public void Sin_red_sale_recto()
    {
        var track = new[] { P(0, 0), P(100, 100) };
        var result = TrackMatcher.Match(RoadNetwork.Build([]), track);

        Assert.Equal(0, result.MatchedPoints);
        Assert.Equal(track.Select(t => new GeoPoint(t.Lat, t.Lon)), result.Line);
    }

    [Fact]
    public void Via_repetida_en_dos_teselas_cuenta_una_vez()
    {
        var way = Way([1, 2, 3], (0, 0), (100, 0), (200, 0));
        var network = RoadNetwork.Build([way, way]);

        Assert.Equal(3, network.NodeCount);
        Assert.Equal(2, network.EdgeCount);
    }

    [Fact]
    public void Cuadricula_urbana_de_un_dia_sale_rapido()
    {
        // 60 × 60 manzanas de 100 m (6 km de lado) y un recorrido de 2000 posiciones en zigzag.
        const int size = 60;
        var ways = new List<OsmWay>();
        long Id(int i, int j) => 1_000_000 + i * 1000 + j;
        for (var i = 0; i <= size; i++)
        {
            ways.Add(new OsmWay(_nextId++, [.. Enumerable.Range(0, size + 1).Select(j => Id(i, j))], [.. Enumerable.Range(0, size + 1).Select(j => G(i * 100, j * 100))]));
            ways.Add(new OsmWay(_nextId++, [.. Enumerable.Range(0, size + 1).Select(j => Id(j, i))], [.. Enumerable.Range(0, size + 1).Select(j => G(j * 100, i * 100))]));
        }
        var network = RoadNetwork.Build(ways);

        var random = new Random(7);
        var track = new List<TrackPoint>();
        double x = 0, y = 0;
        for (var k = 0; k < 2000; k++)
        {
            if ((k / 40) % 2 == 0) x = Math.Min(x + 30, size * 100); else y = Math.Min(y + 30, size * 100);
            var onStreetX = (k / 40) % 2 == 0 ? x : Math.Round(x / 100) * 100;
            var onStreetY = (k / 40) % 2 == 0 ? Math.Round(y / 100) * 100 : y;
            track.Add(P(onStreetX + random.NextDouble() * 20 - 10, onStreetY + random.NextDouble() * 20 - 10, 15));
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = TrackMatcher.Match(network, track);
        watch.Stop();

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"tardo {watch.Elapsed}");
        Assert.True(result.MatchedPoints > track.Count * 9 / 10);
    }
}
