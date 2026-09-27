using System.Globalization;
using System.Text.Json;

namespace FamilyTogether.Core;

/// <summary>Tesela fija de la rejilla de <see cref="Size"/> grados en la que se pide la red.</summary>
public readonly record struct RoadTile(int X, int Y)
{
    /// <summary>Lado de la tesela, en grados (unos 2,2 km de norte a sur).</summary>
    public const double Size = 0.02;

    public double South => Y * Size;
    public double West => X * Size;
    public double North => (Y + 1) * Size;
    public double East => (X + 1) * Size;

    public static RoadTile Of(double lat, double lon) => new((int)Math.Floor(lon / Size), (int)Math.Floor(lat / Size));

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{X}_{Y}");
}

/// <summary>Lo que se pudo reunir de la red para un recorrido.</summary>
/// <param name="Ways">Vias de las teselas que se tienen.</param>
/// <param name="TilesWanted">Teselas que toca el recorrido.</param>
/// <param name="TilesMissing">De ellas, las que faltan: sin red, servidor saturado o fuera del limite.</param>
public sealed record RoadFetch(IReadOnlyList<OsmWay> Ways, int TilesWanted, int TilesMissing);

/// <summary>
/// La red de calles y caminos de OpenStreetMap por teselas fijas, pedida a Overpass y guardada en el
/// movil.
/// </summary>
/// <remarks>
/// <para><b>Privacidad (lo importante).</b> A Overpass solo se le pide el rectangulo de una tesela
/// de una rejilla fija de <see cref="RoadTile.Size"/> grados: nunca el recorrido, ni una hora, ni
/// quien es. Lo que puede saber es que alguien, desde esa IP, ha querido las calles de ese cuadrado
/// de unos 2 km. El ajuste se hace en el movil (<see cref="TrackMatcher"/>).</para>
///
/// <para><b>Cache.</b> Cada tesela se guarda como fichero en la carpeta de cache de la app y vale
/// <see cref="CacheLifetime"/>; se guardan como mucho <see cref="MaxCachedTiles"/> (se borran las
/// mas viejas). Si una caducada no se puede renovar, se usa igual. Las calles no son datos del
/// usuario; el recorrido ajustado no se guarda en ningun sitio.</para>
///
/// <para><b>Uso razonable de Overpass</b> (como Hiker): como mucho <see cref="MaxTilesPerTrack"/>
/// teselas por recorrido (las que mas posiciones tienen), una peticion cada vez, y la consulta
/// declara poco tiempo y memoria (asi Overpass la admite antes). Un 429 o un 504 se reintentan tras
/// esperar (Retry-After, como mucho 10 s); si un servidor no contesta o falla la conexion, se prueba
/// el siguiente y ese no se vuelve a usar en <see cref="Cooldown"/>.</para>
///
/// <para><b>Tiempos (2026-09-27, visto en el Xiaomi).</b> overpass.kumi.systems no contestaba desde
/// la red de casa y cada tesela esperaba 70 s por el: el historial se quedaba «ajustando» minutos.
/// Ahora cada peticion tiene <see cref="RequestTimeout"/> y todo el reparto
/// <see cref="FetchBudget"/>; lo que no llega a tiempo cuenta como que falta y va recto.</para>
/// </remarks>
public sealed class OsmRoadSource
{
    public const int MaxTilesPerTrack = 24;
    public const int MaxCachedTiles = 150;
    public static readonly TimeSpan CacheLifetime = TimeSpan.FromDays(30);
    public static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(2);

    /// <summary>Intentos por servidor y tesela (los 429 y 504 se reintentan tras esperar).</summary>
    private const int MaxAttempts = 3;

    /// <summary>Margen alrededor de cada posicion al decidir que teselas toca (unos 100 m).</summary>
    private const double PointMarginDegrees = 0.001;

    private const int MemoryTiles = 40;
    /// <summary>Tiempo maximo de cada peticion a Overpass.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(25);

    /// <summary>Espera maxima antes de reintentar un 429 o un 504.</summary>
    public TimeSpan MaxRetryWait { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Tiempo maximo de descargas para un recorrido; despues, solo la cache.</summary>
    public TimeSpan FetchBudget { get; init; } = TimeSpan.FromSeconds(40);

    /// <summary>Los mismos servidores publicos que Hiker.</summary>
    public static readonly IReadOnlyList<string> DefaultEndpoints =
    [
        "https://overpass-api.de/api/interpreter",
        "https://overpass.kumi.systems/api/interpreter",
    ];

    /// <summary>
    /// Lo que se puede recorrer andando, en bici o en coche. Fuera: en obras, en proyecto,
    /// abandonadas, andenes, pistas de carreras y pasillos interiores.
    /// </summary>
    internal const string HighwayFilter =
        "^(motorway|motorway_link|trunk|trunk_link|primary|primary_link|secondary|secondary_link|tertiary|tertiary_link|" +
        "unclassified|residential|living_street|service|road|busway|pedestrian|track|footway|path|cycleway|bridleway|steps)$";

    private readonly HttpClient _http;
    private readonly string _directory;
    private readonly IReadOnlyList<string> _endpoints;
    private readonly Func<DateTimeOffset> _now;
    private readonly Dictionary<RoadTile, List<OsmWay>> _memory = [];
    private readonly LinkedList<RoadTile> _memoryOrder = new();
    private readonly object _memoryLock = new();
    private readonly Dictionary<string, DateTimeOffset> _quietUntil = [];

    public OsmRoadSource(HttpClient http, string cacheDirectory, IReadOnlyList<string>? endpoints = null, Func<DateTimeOffset>? now = null)
    {
        _http = http;
        _directory = cacheDirectory;
        _endpoints = endpoints ?? DefaultEndpoints;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Teselas que toca el recorrido, de la que mas posiciones tiene a la que menos (con un margen
    /// alrededor de cada posicion para que las calles del borde tambien esten).
    /// </summary>
    public static IReadOnlyList<RoadTile> TilesFor(IReadOnlyList<TrackPoint> points)
    {
        var count = new Dictionary<RoadTile, int>();
        foreach (var p in points)
        {
            var a = RoadTile.Of(p.Lat - PointMarginDegrees, p.Lon - PointMarginDegrees);
            var b = RoadTile.Of(p.Lat + PointMarginDegrees, p.Lon + PointMarginDegrees);
            for (var x = a.X; x <= b.X; x++)
            {
                for (var y = a.Y; y <= b.Y; y++)
                {
                    var tile = new RoadTile(x, y);
                    count[tile] = count.GetValueOrDefault(tile) + 1;
                }
            }
        }

        return [.. count.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key.X).ThenBy(kv => kv.Key.Y).Select(kv => kv.Key)];
    }

    /// <summary>Reune la red que toca el recorrido: de la memoria, del disco o de Overpass.</summary>
    public async Task<RoadFetch> GetAsync(IReadOnlyList<TrackPoint> points, CancellationToken cancellationToken = default)
    {
        var tiles = TilesFor(points);
        var wanted = tiles.Take(MaxTilesPerTrack).ToList();
        var missing = tiles.Count - wanted.Count;
        var ways = new List<OsmWay>();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var results = new List<List<OsmWay>?>();
        foreach (var tile in wanted)
        {
            // Pasado el tiempo total, solo lo que ya esta en el movil.
            var download = watch.Elapsed < FetchBudget;
            results.Add(await TileAsync(tile, download, cancellationToken).ConfigureAwait(false));
        }

        foreach (var result in results)
        {
            if (result is null)
                missing++;
            else
                ways.AddRange(result);
        }

        if (missing > 0)
            CoreLog.Write($"Red de calles: faltan {missing} de {tiles.Count} teselas ({watch.Elapsed.TotalSeconds:F1} s)");

        return new RoadFetch(ways, tiles.Count, missing);
    }

    private async Task<List<OsmWay>?> TileAsync(RoadTile tile, bool download, CancellationToken cancellationToken)
    {
        lock (_memoryLock)
        {
            if (_memory.TryGetValue(tile, out var cached))
                return cached;
        }

        var path = Path.Combine(_directory, $"roads_{tile}.json");
        List<OsmWay>? stale = null;
        try
        {
            if (File.Exists(path))
            {
                var ways = ReadCache(await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false));
                if (_now() - new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero) < CacheLifetime)
                    return Remember(tile, ways);
                stale = ways;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or FormatException)
        {
            CoreLog.Write($"Cache de calles {tile} ilegible: {ex.Message}");
        }

        var fresh = download ? await DownloadAsync(tile, cancellationToken).ConfigureAwait(false) : null;
        if (fresh is null)
            return stale is null ? null : Remember(tile, stale);

        try
        {
            Directory.CreateDirectory(_directory);
            await File.WriteAllBytesAsync(path, WriteCache(fresh), cancellationToken).ConfigureAwait(false);
            Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CoreLog.Write($"No se pudo guardar la cache de calles {tile}: {ex.Message}");
        }

        return Remember(tile, fresh);
    }

    private List<OsmWay> Remember(RoadTile tile, List<OsmWay> ways)
    {
        lock (_memoryLock)
        {
            if (_memory.TryAdd(tile, ways))
            {
                _memoryOrder.AddLast(tile);
                while (_memoryOrder.Count > MemoryTiles)
                {
                    _memory.Remove(_memoryOrder.First!.Value);
                    _memoryOrder.RemoveFirst();
                }
            }
        }
        return ways;
    }

    /// <summary>Se quedan las <see cref="MaxCachedTiles"/> teselas mas recientes.</summary>
    private void Trim()
    {
        var files = new DirectoryInfo(_directory).GetFiles("roads_*.json");
        if (files.Length <= MaxCachedTiles)
            return;

        foreach (var file in files.OrderByDescending(f => f.LastWriteTimeUtc).Skip(MaxCachedTiles))
        {
            try
            {
                file.Delete();
            }
            catch (IOException)
            {
                // Se intentara la proxima vez.
            }
        }
    }

    // -----------------------------------------------------------------------
    // Overpass
    // -----------------------------------------------------------------------

    internal static string Query(RoadTile tile) => string.Create(CultureInfo.InvariantCulture,
        $"[out:json][timeout:20][maxsize:33554432];way[\"highway\"~\"{HighwayFilter}\"]({tile.South:F2},{tile.West:F2},{tile.North:F2},{tile.East:F2});out skel geom qt;");

    private async Task<List<OsmWay>?> DownloadAsync(RoadTile tile, CancellationToken cancellationToken)
    {
        foreach (var endpoint in _endpoints)
        {
            if (_quietUntil.TryGetValue(endpoint, out var until) && _now() < until)
                continue;

            var dead = false;
            for (var attempt = 0; attempt < MaxAttempts; attempt++)
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(RequestTimeout);
                    using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
                    {
                        Content = new FormUrlEncodedContent([new KeyValuePair<string, string>("data", Query(tile))]),
                    };
                    request.Headers.TryAddWithoutValidation("User-Agent", "FamilyTogether/2026 (Android; Socratic)");

                    using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                        .ConfigureAwait(false);
                    var status = (int)response.StatusCode;
                    if ((status == 429 || status == 504) && attempt < MaxAttempts - 1)
                    {
                        // 429: esta IP ya tiene sus huecos ocupados; 504: el servidor va cargado.
                        // Las dos se pasan esperando un poco (Retry-After, como mucho 10 s).
                        var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(status == 429 ? 5 : 3);
                        wait = wait > MaxRetryWait ? MaxRetryWait : wait;
                        CoreLog.Write($"Overpass {endpoint} tesela {tile}: {status} en {watch.Elapsed.TotalSeconds:F1} s, reintento en {wait.TotalSeconds:F0} s");
                        await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                    if (!response.IsSuccessStatusCode)
                    {
                        CoreLog.Write($"Overpass {endpoint} tesela {tile}: {status} en {watch.Elapsed.TotalSeconds:F1} s");
                        break;
                    }

                    var bytes = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
                    using var document = JsonDocument.Parse(bytes);
                    if (ParseOverpass(document) is { } ways)
                    {
                        CoreLog.Write($"Overpass {endpoint} tesela {tile}: {ways.Count} vias, {bytes.Length / 1024} KB en {watch.Elapsed.TotalSeconds:F1} s");
                        return ways;
                    }

                    CoreLog.Write($"Overpass {endpoint} tesela {tile}: respuesta con error de ejecucion");
                    break;
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    // Sin respuesta a tiempo, sin red, o lo que sea: en Android el tiempo agotado llega
                    // como WebException «Socket closed», no como cancelacion. Este servidor descansa.
                    CoreLog.Write($"Overpass {endpoint} tesela {tile}: {ex.GetType().Name} {ex.Message} en {watch.Elapsed.TotalSeconds:F0} s");
                    dead = true;
                    break;
                }
            }

            if (dead)
                _quietUntil[endpoint] = _now() + Cooldown;
        }

        return null;
    }

    /// <summary>
    /// Vias de una respuesta de Overpass (<c>out skel geom</c>). <c>null</c> si la respuesta
    /// trae un error de ejecucion (tiempo o memoria agotados): seria una red a medias.
    /// </summary>
    internal static List<OsmWay>? ParseOverpass(JsonDocument document)
    {
        var root = document.RootElement;
        if (root.TryGetProperty("remark", out var remark) &&
            remark.GetString() is { } text && text.Contains("error", StringComparison.OrdinalIgnoreCase))
            return null;

        var ways = new List<OsmWay>();
        if (!root.TryGetProperty("elements", out var elements))
            return ways;

        foreach (var element in elements.EnumerateArray())
        {
            if (!element.TryGetProperty("type", out var type) || type.GetString() != "way" ||
                !element.TryGetProperty("nodes", out var nodes) || !element.TryGetProperty("geometry", out var geometry))
                continue;

            var ids = new List<long>();
            var points = new List<GeoPoint>();
            var ok = true;
            foreach (var node in nodes.EnumerateArray())
                ids.Add(node.GetInt64());
            foreach (var g in geometry.EnumerateArray())
            {
                if (g.ValueKind != JsonValueKind.Object || !g.TryGetProperty("lat", out var lat) || !g.TryGetProperty("lon", out var lon))
                {
                    ok = false;
                    break;
                }
                points.Add(new GeoPoint(lat.GetDouble(), lon.GetDouble()));
            }

            if (ok && ids.Count >= 2 && ids.Count == points.Count)
                ways.Add(new OsmWay(element.GetProperty("id").GetInt64(), [.. ids], [.. points]));
        }

        return ways;
    }

    // -----------------------------------------------------------------------
    // Formato de la cache: {"v":1,"w":[[id,[n1,n2,...],[lat1,lon1,lat2,lon2,...]],...]}
    // -----------------------------------------------------------------------

    internal static byte[] WriteCache(List<OsmWay> ways)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", 1);
            writer.WriteStartArray("w");
            foreach (var way in ways)
            {
                writer.WriteStartArray();
                writer.WriteNumberValue(way.Id);
                writer.WriteStartArray();
                foreach (var id in way.Nodes)
                    writer.WriteNumberValue(id);
                writer.WriteEndArray();
                writer.WriteStartArray();
                foreach (var p in way.Points)
                {
                    writer.WriteNumberValue(Math.Round(p.Lat, 7));
                    writer.WriteNumberValue(Math.Round(p.Lon, 7));
                }
                writer.WriteEndArray();
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return buffer.ToArray();
    }

    internal static List<OsmWay> ReadCache(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        var ways = new List<OsmWay>();
        foreach (var item in document.RootElement.GetProperty("w").EnumerateArray())
        {
            var id = item[0].GetInt64();
            var nodes = item[1].EnumerateArray().Select(n => n.GetInt64()).ToArray();
            var coords = item[2].EnumerateArray().Select(c => c.GetDouble()).ToArray();
            if (coords.Length != nodes.Length * 2)
                throw new FormatException("Via con coordenadas incompletas");

            var points = new GeoPoint[nodes.Length];
            for (var i = 0; i < nodes.Length; i++)
                points[i] = new GeoPoint(coords[2 * i], coords[2 * i + 1]);
            ways.Add(new OsmWay(id, nodes, points));
        }
        return ways;
    }
}

/// <summary>
/// Lo que usa la pantalla de Historial: reune la red por teselas y ajusta el recorrido en el movil.
/// </summary>
public sealed class TrackSnapper
{
    private readonly OsmRoadSource _roads;

    public TrackSnapper(OsmRoadSource roads) => _roads = roads;

    /// <param name="Track">Linea a dibujar (recta si no se pudo ajustar nada).</param>
    /// <param name="TilesMissing">Teselas que faltaron: esa parte puede ir recta.</param>
    public sealed record Result(MatchedTrack Track, int TilesWanted, int TilesMissing);

    /// <summary>Nunca lanza por la red: sin mapa, el recorrido sale recto como antes.</summary>
    public async Task<Result> SnapAsync(IReadOnlyList<TrackPoint> points, CancellationToken cancellationToken = default)
    {
        if (points.Count < 2)
            return new Result(MatchedTrack.Straight(points), 0, 0);

        var fetch = await _roads.GetAsync(points, cancellationToken).ConfigureAwait(false);
        if (fetch.Ways.Count == 0)
            return new Result(MatchedTrack.Straight(points), fetch.TilesWanted, fetch.TilesMissing);

        var track = await Task.Run(() => TrackMatcher.Match(RoadNetwork.Build(fetch.Ways), points, cancellationToken), cancellationToken)
            .ConfigureAwait(false);
        return new Result(track, fetch.TilesWanted, fetch.TilesMissing);
    }
}
