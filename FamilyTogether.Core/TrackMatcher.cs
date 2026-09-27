namespace FamilyTogether.Core;

/// <summary>Una posicion del recorrido con su precision (metros) y su hora (default si no se sabe).</summary>
public readonly record struct TrackPoint(double Lat, double Lon, double Accuracy, DateTimeOffset At = default);

/// <summary>
/// Resultado del ajuste: la linea que se dibuja y cuantas posiciones se pegaron a la red.
/// </summary>
/// <param name="Line">Linea a dibujar, en orden. Sin ajuste, las mismas posiciones.</param>
/// <param name="MatchedPoints">Posiciones que se pegaron a una calle o camino.</param>
/// <param name="PointCount">Posiciones de entrada.</param>
public sealed record MatchedTrack(IReadOnlyList<GeoPoint> Line, int MatchedPoints, int PointCount)
{
    public static MatchedTrack Straight(IReadOnlyList<TrackPoint> points) =>
        new([.. points.Select(p => new GeoPoint(p.Lat, p.Lon))], 0, points.Count);
}

/// <summary>
/// Ajusta un recorrido a la red de calles y caminos, en el movil (map matching por HMM + Viterbi,
/// al estilo de Newson y Krumm, 2009).
/// </summary>
/// <remarks>
/// <para><b>Modelo.</b> Cada posicion tiene como candidatos sus proyecciones sobre las aristas a
/// menos de un radio que depende de su precision. La probabilidad de emision es gaussiana en la
/// distancia del punto al candidato; la de transicion entre dos candidatos seguidos, exponencial
/// en la diferencia entre la distancia por la red y la distancia en linea recta entre las dos
/// posiciones: lo logico es ir por el camino corto. Viterbi elige la secuencia mas probable y entre
/// cada par elegido se dibuja el camino mas corto por la red (Dijkstra acotado).</para>
///
/// <para><b>Nunca inventar un rodeo.</b> Una transicion cuyo camino por la red pasa de
/// <c>2 × recta + 150 m</c> no se admite. Si en un paso no queda ninguna, la cadena se corta y ese
/// tramo se dibuja recto. Igual si una posicion no tiene ninguna calle cerca (se dibuja tal cual)
/// o si entre dos posiciones hay mas de <see cref="MaxStepMeters"/> (tunel, GPS perdido).</para>
///
/// <para>Es solo para dibujar: el recorrido guardado no cambia.</para>
/// </remarks>
public static class TrackMatcher
{
    /// <summary>Candidatos por posicion.</summary>
    internal const int MaxCandidates = 6;

    /// <summary>Radio de busqueda minimo y maximo, en metros.</summary>
    internal const double MinRadius = 25;
    internal const double MaxRadius = 80;

    /// <summary>Mas de esto en linea recta entre dos posiciones seguidas: el tramo va recto.</summary>
    internal const double MaxStepMeters = 2000;

    /// <summary>Rodeo maximo admitido: <c>factor × recta + holgura</c>.</summary>
    internal const double DetourFactor = 2;
    internal const double DetourSlackMeters = 150;

    /// <summary>Escala (m) de la probabilidad de transicion.</summary>
    internal const double Beta = 25;

    public static MatchedTrack Match(RoadNetwork network, IReadOnlyList<TrackPoint> points, CancellationToken cancellationToken = default)
    {
        var n = points.Count;
        if (n == 0 || network.IsEmpty)
            return MatchedTrack.Straight(points);

        var px = new double[n];
        var py = new double[n];
        var candidates = new List<RoadNetwork.OnEdge>[n];
        var emission = new double[n][];

        for (var i = 0; i < n; i++)
        {
            (px[i], py[i]) = network.Project(new GeoPoint(points[i].Lat, points[i].Lon));
            var sigma = Sigma(points[i].Accuracy);
            candidates[i] = network.Candidates(px[i], py[i], Radius(sigma), MaxCandidates);
            emission[i] = [.. candidates[i].Select(c => -0.5 * (c.Distance / sigma) * (c.Distance / sigma))];
        }

        // Viterbi por cadenas: una cadena se corta donde no hay transicion admisible.
        var choice = Enumerable.Repeat(-1, n).ToArray();
        var chain = Enumerable.Repeat(-1, n).ToArray();
        var back = new int[n][];
        double[]? score = null;
        var chainStart = -1;
        var chainId = 0;

        void Finish(int last)
        {
            if (score is null)
                return;

            var best = ArgMax(score);
            for (var i = last; i >= chainStart; i--)
            {
                choice[i] = best;
                chain[i] = chainId;
                if (i > chainStart)
                    best = back[i][best];
            }
            chainId++;
            score = null;
        }

        void Start(int i)
        {
            chainStart = i;
            score = (double[])emission[i].Clone();
        }

        for (var i = 0; i < n; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (candidates[i].Count == 0)
            {
                Finish(i - 1);
                continue;
            }

            if (score is null)
            {
                Start(i);
                continue;
            }

            var straight = Math.Sqrt((px[i] - px[i - 1]) * (px[i] - px[i - 1]) + (py[i] - py[i - 1]) * (py[i] - py[i - 1]));
            if (straight > MaxStepMeters)
            {
                Finish(i - 1);
                Start(i);
                continue;
            }

            var bound = DetourFactor * straight + DetourSlackMeters;
            var next = Enumerable.Repeat(double.NegativeInfinity, candidates[i].Count).ToArray();
            var from = new int[candidates[i].Count];

            for (var a = 0; a < candidates[i - 1].Count; a++)
            {
                if (double.IsNegativeInfinity(score[a]))
                    continue;

                var start = candidates[i - 1][a];
                var reach = ShortestFrom(network, start, bound, out _);
                for (var b = 0; b < candidates[i].Count; b++)
                {
                    var along = NetworkDistance(network, start, candidates[i][b], reach);
                    if (along > bound)
                        continue;

                    var value = score[a] - Math.Abs(along - straight) / Beta + emission[i][b];
                    if (value > next[b])
                    {
                        next[b] = value;
                        from[b] = a;
                    }
                }
            }

            if (next.All(double.IsNegativeInfinity))
            {
                Finish(i - 1);
                Start(i);
                continue;
            }

            back[i] = from;
            score = next;
        }
        Finish(n - 1);

        // Geometria: por la red dentro de cada cadena; recto entre cadenas y en lo que no encaja.
        var line = new List<GeoPoint>(n * 2);
        var matched = 0;
        for (var i = 0; i < n; i++)
        {
            if (choice[i] < 0)
            {
                Append(line, new GeoPoint(points[i].Lat, points[i].Lon));
                continue;
            }

            matched++;
            var here = candidates[i][choice[i]];
            if (i > 0 && chain[i - 1] == chain[i])
            {
                var there = candidates[i - 1][choice[i - 1]];
                foreach (var p in Path(network, there, here))
                    Append(line, p);
            }
            else
            {
                Append(line, network.Unproject(here.X, here.Y));
            }
        }

        return new MatchedTrack(line, matched, n);
    }

    private static double Sigma(double accuracy) => accuracy is > 0 and not double.NaN ? Math.Clamp(accuracy, 5, 25) : 10;

    private static double Radius(double sigma) => Math.Clamp(3 * sigma + 10, MinRadius, MaxRadius);

    private static int ArgMax(double[] values)
    {
        var best = 0;
        for (var i = 1; i < values.Length; i++)
        {
            if (values[i] > values[best])
                best = i;
        }
        return best;
    }

    private static void Append(List<GeoPoint> line, GeoPoint p)
    {
        if (line.Count == 0 || Math.Abs(line[^1].Lat - p.Lat) > 1e-9 || Math.Abs(line[^1].Lon - p.Lon) > 1e-9)
            line.Add(p);
    }

    /// <summary>
    /// Dijkstra desde un sitio sobre una arista (sus dos extremos, con lo que falta hasta cada uno),
    /// hasta <paramref name="bound"/> metros. Devuelve la distancia a cada vertice alcanzado.
    /// </summary>
    private static Dictionary<int, double> ShortestFrom(RoadNetwork network, RoadNetwork.OnEdge start, double bound, out Dictionary<int, int> previous)
    {
        var distance = new Dictionary<int, double>();
        var back = new Dictionary<int, int>();
        previous = back;
        var queue = new PriorityQueue<int, double>();

        void Seed(int vertex, double d)
        {
            if (d <= bound && (!distance.TryGetValue(vertex, out var old) || d < old))
            {
                distance[vertex] = d;
                back[vertex] = -1;
                queue.Enqueue(vertex, d);
            }
        }

        var length = network.Length(start.Edge);
        Seed(network.From(start.Edge), start.T * length);
        Seed(network.To(start.Edge), (1 - start.T) * length);

        var done = new HashSet<int>();
        while (queue.TryDequeue(out var vertex, out var d))
        {
            if (!done.Add(vertex))
                continue;

            foreach (var edge in network.EdgesOf(vertex))
            {
                var other = network.Other(edge, vertex);
                var nd = d + network.Length(edge);
                if (nd <= bound && (!distance.TryGetValue(other, out var old) || nd < old))
                {
                    distance[other] = nd;
                    back[other] = vertex;
                    queue.Enqueue(other, nd);
                }
            }
        }

        return distance;
    }

    /// <summary>Distancia por la red entre dos sitios, con las distancias ya calculadas desde el primero.</summary>
    private static double NetworkDistance(RoadNetwork network, RoadNetwork.OnEdge a, RoadNetwork.OnEdge b, Dictionary<int, double> reach)
    {
        var length = network.Length(b.Edge);
        var best = double.PositiveInfinity;

        if (a.Edge == b.Edge)
            best = Math.Abs(b.T - a.T) * length;

        if (reach.TryGetValue(network.From(b.Edge), out var dFrom))
            best = Math.Min(best, dFrom + b.T * length);
        if (reach.TryGetValue(network.To(b.Edge), out var dTo))
            best = Math.Min(best, dTo + (1 - b.T) * length);

        return best;
    }

    /// <summary>
    /// El camino mas corto de <paramref name="a"/> a <paramref name="b"/> como puntos (sin el de
    /// salida, que ya esta en la linea). Si no lo hay dentro del limite, recto.
    /// </summary>
    private static IEnumerable<GeoPoint> Path(RoadNetwork network, RoadNetwork.OnEdge a, RoadNetwork.OnEdge b)
    {
        var end = network.Unproject(b.X, b.Y);
        var straight = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
        var bound = DetourFactor * straight + DetourSlackMeters + 2 * MaxRadius;
        var reach = ShortestFrom(network, a, bound, out var previous);

        var length = network.Length(b.Edge);
        var direct = a.Edge == b.Edge ? Math.Abs(b.T - a.T) * length : double.PositiveInfinity;
        var viaFrom = reach.TryGetValue(network.From(b.Edge), out var dFrom) ? dFrom + b.T * length : double.PositiveInfinity;
        var viaTo = reach.TryGetValue(network.To(b.Edge), out var dTo) ? dTo + (1 - b.T) * length : double.PositiveInfinity;

        if (direct <= viaFrom && direct <= viaTo)
            return [end];   // misma arista (o nada mejor): directo
        if (double.IsPositiveInfinity(Math.Min(viaFrom, viaTo)))
            return [end];   // sin camino: recto

        var vertex = viaFrom <= viaTo ? network.From(b.Edge) : network.To(b.Edge);
        var vertices = new List<int>();
        while (vertex >= 0)
        {
            vertices.Add(vertex);
            vertex = previous[vertex];
        }
        vertices.Reverse();

        var result = vertices.Select(v => network.Unproject(network.X(v), network.Y(v))).ToList();
        result.Add(end);
        return result;
    }
}
