namespace FamilyTogether.Core;

/// <summary>Un punto en grados (WGS84).</summary>
public readonly record struct GeoPoint(double Lat, double Lon);

/// <summary>
/// Una via de OpenStreetMap tal y como sale de Overpass: los identificadores de sus nodos y sus
/// coordenadas, en el mismo orden. Dos vias se cruzan donde comparten un nodo.
/// </summary>
public sealed record OsmWay(long Id, long[] Nodes, GeoPoint[] Points);

/// <summary>
/// Red de calles y caminos para ajustar recorridos (<see cref="TrackMatcher"/>): un grafo no
/// dirigido en el que cada nodo de OSM es un vertice y cada tramo entre dos nodos seguidos de una
/// via, una arista recta.
/// </summary>
/// <remarks>
/// <para><b>Plano local.</b> Todo se calcula en metros sobre una proyeccion equirectangular
/// centrada en la red: a la escala de un dia de recorrido (decenas de kilometros) el error es
/// despreciable y las cuentas son sumas y productos.</para>
///
/// <para><b>Sin sentido de circulacion.</b> Las direcciones unicas se ignoran a proposito: el
/// ajuste es para dibujar, y quien va andando las recorre en los dos sentidos.</para>
///
/// <para><b>Indice espacial.</b> Rejilla de celdas de <see cref="CellMeters"/> con las aristas que
/// tocan cada una, para buscar candidatos sin recorrer toda la red.</para>
/// </remarks>
public sealed class RoadNetwork
{
    private const double EarthRadius = 6_371_008.8;
    private const double CellMeters = 60;

    private readonly double _lat0;
    private readonly double _lon0;
    private readonly double _cos0;

    // Vertices
    private readonly List<double> _x = [];
    private readonly List<double> _y = [];
    private readonly List<List<int>> _adjacent = [];   // aristas que salen de cada vertice

    // Aristas: extremos y longitud
    private readonly List<int> _from = [];
    private readonly List<int> _to = [];
    private readonly List<double> _length = [];

    private readonly Dictionary<(int, int), List<int>> _cells = [];

    private RoadNetwork(double lat0, double lon0)
    {
        _lat0 = lat0;
        _lon0 = lon0;
        _cos0 = Math.Cos(lat0 * Math.PI / 180);
    }

    public int NodeCount => _x.Count;

    public int EdgeCount => _from.Count;

    public bool IsEmpty => _from.Count == 0;

    /// <summary>
    /// Monta la red con las vias dadas. Una via que llega repetida (esta en dos teselas) se toma una
    /// vez.
    /// </summary>
    public static RoadNetwork Build(IEnumerable<OsmWay> ways)
    {
        var list = new List<OsmWay>();
        var seen = new HashSet<long>();
        foreach (var way in ways)
        {
            if (way.Nodes.Length >= 2 && way.Nodes.Length == way.Points.Length && seen.Add(way.Id))
                list.Add(way);
        }

        double lat0 = 0, lon0 = 0;
        if (list.Count > 0)
        {
            lat0 = list.Average(w => w.Points[0].Lat);
            lon0 = list.Average(w => w.Points[0].Lon);
        }

        var network = new RoadNetwork(lat0, lon0);
        var index = new Dictionary<long, int>();
        var edges = new HashSet<(int, int)>();

        foreach (var way in list)
        {
            var previous = -1;
            for (var i = 0; i < way.Nodes.Length; i++)
            {
                if (!index.TryGetValue(way.Nodes[i], out var vertex))
                {
                    var (x, y) = network.Project(way.Points[i]);
                    vertex = network._x.Count;
                    network._x.Add(x);
                    network._y.Add(y);
                    network._adjacent.Add([]);
                    index[way.Nodes[i]] = vertex;
                }

                if (previous >= 0 && previous != vertex && edges.Add((Math.Min(previous, vertex), Math.Max(previous, vertex))))
                    network.AddEdge(previous, vertex);

                previous = vertex;
            }
        }

        return network;
    }

    private void AddEdge(int a, int b)
    {
        var edge = _from.Count;
        _from.Add(a);
        _to.Add(b);
        _length.Add(Math.Sqrt(Sq(_x[b] - _x[a]) + Sq(_y[b] - _y[a])));
        _adjacent[a].Add(edge);
        _adjacent[b].Add(edge);

        // Celdas que toca la caja de la arista (las aristas son cortas: suelen ser una o dos).
        var (cx0, cy0) = Cell(Math.Min(_x[a], _x[b]), Math.Min(_y[a], _y[b]));
        var (cx1, cy1) = Cell(Math.Max(_x[a], _x[b]), Math.Max(_y[a], _y[b]));
        for (var cx = cx0; cx <= cx1; cx++)
        {
            for (var cy = cy0; cy <= cy1; cy++)
            {
                if (!_cells.TryGetValue((cx, cy), out var bucket))
                    _cells[(cx, cy)] = bucket = [];
                bucket.Add(edge);
            }
        }
    }

    // -----------------------------------------------------------------------
    // Proyeccion
    // -----------------------------------------------------------------------

    internal (double X, double Y) Project(GeoPoint p) =>
        ((p.Lon - _lon0) * Math.PI / 180 * EarthRadius * _cos0, (p.Lat - _lat0) * Math.PI / 180 * EarthRadius);

    internal GeoPoint Unproject(double x, double y) =>
        new(_lat0 + y / EarthRadius * 180 / Math.PI, _lon0 + x / (EarthRadius * _cos0) * 180 / Math.PI);

    private static (int, int) Cell(double x, double y) => ((int)Math.Floor(x / CellMeters), (int)Math.Floor(y / CellMeters));

    private static double Sq(double v) => v * v;

    // -----------------------------------------------------------------------
    // Consultas para el ajuste
    // -----------------------------------------------------------------------

    internal double X(int vertex) => _x[vertex];
    internal double Y(int vertex) => _y[vertex];
    internal int From(int edge) => _from[edge];
    internal int To(int edge) => _to[edge];
    internal double Length(int edge) => _length[edge];
    internal List<int> EdgesOf(int vertex) => _adjacent[vertex];

    internal int Other(int edge, int vertex) => _from[edge] == vertex ? _to[edge] : _from[edge];

    /// <summary>Un sitio sobre una arista: <paramref name="T"/> = 0 en <c>From</c>, 1 en <c>To</c>.</summary>
    internal readonly record struct OnEdge(int Edge, double T, double X, double Y, double Distance);

    /// <summary>
    /// Proyeccion del punto (en metros) sobre las aristas a menos de <paramref name="radius"/>:
    /// la mas cercana de cada arista, de la mas cercana a la mas lejana, como mucho
    /// <paramref name="max"/>.
    /// </summary>
    internal List<OnEdge> Candidates(double px, double py, double radius, int max)
    {
        var found = new List<OnEdge>();
        var (cx0, cy0) = Cell(px - radius, py - radius);
        var (cx1, cy1) = Cell(px + radius, py + radius);
        var visited = new HashSet<int>();

        for (var cx = cx0; cx <= cx1; cx++)
        {
            for (var cy = cy0; cy <= cy1; cy++)
            {
                if (!_cells.TryGetValue((cx, cy), out var bucket))
                    continue;

                foreach (var edge in bucket)
                {
                    if (!visited.Add(edge))
                        continue;

                    var on = ProjectOn(edge, px, py);
                    if (on.Distance <= radius)
                        found.Add(on);
                }
            }
        }

        found.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        if (found.Count > max)
            found.RemoveRange(max, found.Count - max);
        return found;
    }

    internal OnEdge ProjectOn(int edge, double px, double py)
    {
        var ax = _x[_from[edge]];
        var ay = _y[_from[edge]];
        var dx = _x[_to[edge]] - ax;
        var dy = _y[_to[edge]] - ay;
        var len2 = dx * dx + dy * dy;
        var t = len2 < 1e-9 ? 0 : Math.Clamp(((px - ax) * dx + (py - ay) * dy) / len2, 0, 1);
        var x = ax + t * dx;
        var y = ay + t * dy;
        return new OnEdge(edge, t, x, y, Math.Sqrt(Sq(px - x) + Sq(py - y)));
    }
}
