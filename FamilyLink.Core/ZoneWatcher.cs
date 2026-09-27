using SQLite;

namespace FamilyLink.Core;

/// <summary>
/// Entradas y salidas de zona, calculadas en el movil con histeresis (§8).
/// </summary>
/// <remarks>
/// <para><b>Histeresis.</b> Con margen <c>m = max(15 m, precision)</c>: se entra si
/// <c>distancia &lt; r − m</c> y se sale si <c>distancia &gt; r + m</c>. Entre medias no cambia
/// nada, asi que el ruido del GPS en el borde no dispara avisos en bucle.</para>
///
/// <para><b>Estado por (grupo, zona), persistido.</b> Sobrevive a que Android mate el servicio. La
/// primera vez que se sabe donde esta uno respecto a una zona se guarda sin avisar: instalar la
/// app, crear una zona encima o volver de una pausa no son entradas ni salidas. Al dejar de
/// compartir con un grupo se olvida su estado por lo mismo.</para>
///
/// <para>Devuelve las transiciones; la app llama luego a
/// <see cref="FamilyService.ReportZoneEventAsync"/> con cada una.</para>
/// </remarks>
public sealed class ZoneWatcher
{
    public const double MinMarginMeters = 15;

    /// <summary>Cada cuanto se vuelven a pedir las zonas al servidor.</summary>
    public static readonly TimeSpan ZonesRefresh = TimeSpan.FromMinutes(5);

    private readonly SQLiteAsyncConnection _db;
    private readonly Func<Guid, CancellationToken, Task<IReadOnlyList<Zone>>> _zoneSource;
    private readonly Dictionary<Guid, (DateTimeOffset At, IReadOnlyList<Zone> Zones)> _zones = [];
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _ready;

    public ZoneWatcher(string databasePath, FamilyService service)
        : this(databasePath, (group, ct) => service.GetZonesAsync(group, ct))
    {
    }

    /// <summary>Con otra fuente de zonas (pruebas).</summary>
    public ZoneWatcher(string databasePath, Func<Guid, CancellationToken, Task<IReadOnlyList<Zone>>> zoneSource)
    {
        _db = new SQLiteAsyncConnection(databasePath);
        _zoneSource = zoneSource;
    }

    [Table("zone_state")]
    internal sealed class ZoneState
    {
        /// <summary><c>grupo|zona</c>.</summary>
        [PrimaryKey]
        public string Key { get; set; } = "";

        public string GroupId { get; set; } = "";
        public bool Inside { get; set; }
    }

    /// <summary>
    /// La regla de histeresis, sin estado: dentro, fuera o <c>null</c> si esta en la franja
    /// dudosa y no se puede decidir.
    /// </summary>
    public static bool? Classify(double distance, double radius, double accuracy)
    {
        var margin = Math.Max(MinMarginMeters, double.IsNaN(accuracy) ? 0 : accuracy);
        if (distance < radius - margin)
            return true;
        if (distance > radius + margin)
            return false;
        return null;
    }

    /// <summary>El estado siguiente: cambia solo si la lectura cae claramente al otro lado.</summary>
    public static bool? Next(bool? wasInside, double distance, double radius, double accuracy) =>
        Classify(distance, radius, accuracy) ?? wasInside;

    /// <summary>Que las zonas se vuelvan a pedir en la proxima evaluacion (p. ej. tras editar una).</summary>
    public void InvalidateZones()
    {
        lock (_zones)
            _zones.Clear();
    }

    /// <summary>
    /// Evalua una lectura contra las zonas de los grupos que comparten. Devuelve las transiciones
    /// (<c>enter</c> / <c>exit</c>).
    /// </summary>
    public async Task<IReadOnlyList<(Guid Group, Guid Zone, string Kind)>> EvaluateAsync(
        double lat, double lon, double accuracy, IReadOnlyList<Guid> sharingGroups, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_ready)
            {
                await _db.CreateTableAsync<ZoneState>().ConfigureAwait(false);
                _ready = true;
            }

            var states = (await _db.Table<ZoneState>().ToListAsync().ConfigureAwait(false)).ToDictionary(s => s.Key);
            var sharing = sharingGroups.Select(g => g.ToString("D")).ToHashSet();
            var seen = new HashSet<string>();
            var transitions = new List<(Guid, Guid, string)>();

            foreach (var group in sharingGroups.Distinct())
            {
                var zones = await ZonesAsync(group, cancellationToken).ConfigureAwait(false);
                if (zones is null)
                {
                    // Sin zonas conocidas (sin red la primera vez): se deja su estado como estaba.
                    foreach (var key in states.Keys.Where(k => k.StartsWith(group.ToString("D"), StringComparison.Ordinal)))
                        seen.Add(key);
                    continue;
                }

                foreach (var zone in zones)
                {
                    var key = $"{group:D}|{zone.Id:D}";
                    seen.Add(key);

                    var had = states.TryGetValue(key, out var state) ? state.Inside : (bool?)null;
                    var now = Next(had, Geo.DistanceMeters(lat, lon, zone.Lat, zone.Lon), zone.Radius, accuracy);

                    if (now is null || now == had)
                        continue;

                    await _db.InsertOrReplaceAsync(new ZoneState { Key = key, GroupId = group.ToString("D"), Inside = now.Value })
                        .ConfigureAwait(false);

                    if (had is not null)
                        transitions.Add((group, zone.Id, now.Value ? "enter" : "exit"));
                }
            }

            // Zonas borradas y grupos que ya no comparten (pausa, expulsion): su estado se olvida.
            foreach (var stale in states.Values.Where(s => !seen.Contains(s.Key) || !sharing.Contains(s.GroupId)))
                await _db.DeleteAsync<ZoneState>(stale.Key).ConfigureAwait(false);

            return transitions;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Zonas del grupo, de la cache si es reciente. Null si no se sabe (sin red y sin cache).</summary>
    private async Task<IReadOnlyList<Zone>?> ZonesAsync(Guid group, CancellationToken cancellationToken)
    {
        (DateTimeOffset At, IReadOnlyList<Zone> Zones) cached;
        bool hasCache;
        lock (_zones)
            hasCache = _zones.TryGetValue(group, out cached);

        if (hasCache && DateTimeOffset.UtcNow - cached.At < ZonesRefresh)
            return cached.Zones;

        try
        {
            var zones = await _zoneSource(group, cancellationToken).ConfigureAwait(false);
            lock (_zones)
                _zones[group] = (DateTimeOffset.UtcNow, zones);
            return zones;
        }
        catch (FamilyLinkException ex)
        {
            CoreLog.Write($"Zonas de {group}: {ex.Code}");
            return hasCache ? cached.Zones : null;
        }
    }
}
