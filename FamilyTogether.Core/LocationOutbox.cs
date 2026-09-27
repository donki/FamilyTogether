using SQLite;

namespace FamilyTogether.Core;

/// <summary>
/// Cola local de posiciones (SQLite): lo que se lee se guarda aqui y se envia cuando hay red, con
/// su hora original (§8).
/// </summary>
/// <remarks>
/// <para><b>Cada lectura lleva la lista de grupos que comparten en ese momento.</b> Asi la pausa se
/// cumple aunque el envio sea horas despues: lo registrado en pausa en A nunca llega a A, aunque si
/// a B. Al enviar se cifra una fila por grupo con la clave de cada uno.</para>
///
/// <para><b>Filtros de origen.</b> Se descartan lecturas con precision peor que
/// <see cref="MaxAccuracyMeters"/> y las que no se han movido <see cref="MinDistanceMeters"/> desde
/// la ultima encolada con los mismos grupos (si cambian los grupos —fin de una pausa— la lectura
/// entra aunque no se haya movido, para que el grupo que vuelve tenga posicion).</para>
/// </remarks>
public sealed class LocationOutbox
{
    public const double MaxAccuracyMeters = 25;

    /// <summary>Peor precision admitida como aproximada, cuando no hay otra (ver EnqueueAsync).</summary>
    public const double CoarseMaxAccuracyMeters = 100;

    /// <summary>Tiempo sin lecturas buenas tras el que se admite una aproximada, y entre aproximadas.</summary>
    public static readonly TimeSpan CoarseAfter = TimeSpan.FromMinutes(10);
    public const double MinDistanceMeters = 25;

    /// <summary>Filas por peticion al enviar.</summary>
    private const int Batch = 200;

    private readonly SQLiteAsyncConnection _db;
    private readonly SemaphoreSlim _flushLock = new(1, 1);
    private bool _ready;

    /// <param name="databasePath">Fichero SQLite; lo elige la app (puede ser el mismo para todo el nucleo).</param>
    public LocationOutbox(string databasePath)
    {
        _db = new SQLiteAsyncConnection(databasePath);
    }

    [Table("outbox_positions")]
    internal sealed class OutboxRow
    {
        [PrimaryKey, AutoIncrement]
        public long Id { get; set; }

        public double Lat { get; set; }
        public double Lon { get; set; }
        public double Accuracy { get; set; }
        public int Battery { get; set; }

        /// <summary>Hora de la lectura, en ticks UTC.</summary>
        public long AtUtcTicks { get; set; }

        /// <summary>Lectura peor de 25 m aceptada porque no habia otra (ver <see cref="EnqueueAsync"/>).</summary>
        public bool Coarse { get; set; }

        /// <summary>Grupos a los que aun falta enviarla, separados por comas.</summary>
        public string Groups { get; set; } = "";

        [Ignore]
        public List<Guid> GroupList
        {
            get => [.. Groups.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(Guid.Parse)];
            set => Groups = string.Join(',', value.Select(g => g.ToString("D")));
        }

        [Ignore]
        public DateTimeOffset At => new(AtUtcTicks, TimeSpan.Zero);
    }

    [Table("outbox_last")]
    internal sealed class LastRow
    {
        [PrimaryKey]
        public int Id { get; set; }
        public double Lat { get; set; }
        public double Lon { get; set; }
        public string Groups { get; set; } = "";

        /// <summary>Ultima lectura buena (25 m o mejor) y ultima aproximada, en ticks UTC.</summary>
        public long FineAtUtcTicks { get; set; }
        public long CoarseAtUtcTicks { get; set; }
    }

    private async Task InitAsync()
    {
        if (_ready)
            return;

        await _db.CreateTableAsync<OutboxRow>().ConfigureAwait(false);
        await _db.CreateTableAsync<LastRow>().ConfigureAwait(false);
        _ready = true;
    }

    /// <summary>
    /// Encola una lectura para los grupos que comparten ahora. Sin grupos, o si no pasa los
    /// filtros de precision y distancia, no se guarda nada.
    /// </summary>
    public async Task EnqueueAsync(
        double lat, double lon, double accuracy, int battery, DateTimeOffset at,
        IReadOnlyList<Guid> sharingGroups, CancellationToken cancellationToken = default)
    {
        if (sharingGroups.Count == 0 || accuracy > CoarseMaxAccuracyMeters || double.IsNaN(lat) || double.IsNaN(lon))
            return;

        await InitAsync().ConfigureAwait(false);

        var groups = string.Join(',', sharingGroups.Distinct().Order().Select(g => g.ToString("D")));
        var last = await _db.FindAsync<LastRow>(1).ConfigureAwait(false);
        var coarse = accuracy > MaxAccuracyMeters;
        if (coarse)
        {
            // Aproximada (decision de Josep, 2026-09-27): solo si en 10 minutos no ha habido ninguna
            // buena ni otra aproximada. En interiores la red da unos 100 m y sin esto el grupo no te
            // veria nunca. No entra en el historial ni en las zonas.
            if (last is not null && last.Groups == groups &&
                (at.UtcTicks - last.FineAtUtcTicks < CoarseAfter.Ticks || at.UtcTicks - last.CoarseAtUtcTicks < CoarseAfter.Ticks))
                return;
        }
        else if (last is not null && last.Groups == groups && last.FineAtUtcTicks > 0 &&
                 Geo.DistanceMeters(last.Lat, last.Lon, lat, lon) < MinDistanceMeters)
        {
            return;
        }

        await _db.InsertAsync(new OutboxRow
        {
            Lat = lat,
            Lon = lon,
            Accuracy = accuracy,
            Battery = battery,
            AtUtcTicks = at.UtcTicks,
            Groups = groups,
            Coarse = coarse,
        }).ConfigureAwait(false);

        // La referencia para la distancia minima es la ultima lectura buena: una aproximada no debe
        // impedir que la siguiente buena salga aunque este cerca.
        var next = last ?? new LastRow { Id = 1 };
        next.Groups = groups;
        if (coarse)
        {
            next.CoarseAtUtcTicks = at.UtcTicks;
        }
        else
        {
            next.Lat = lat;
            next.Lon = lon;
            next.FineAtUtcTicks = at.UtcTicks;
        }
        await _db.InsertOrReplaceAsync(next).ConfigureAwait(false);
    }

    /// <summary>
    /// Envia lo pendiente, de lo mas antiguo a lo mas nuevo, en tandas por grupo. Devuelve cuantas
    /// filas (posicion × grupo) envio.
    /// </summary>
    /// <remarks>
    /// Sin red se para y se deja todo para la siguiente vez. Si el servidor rechaza un grupo por
    /// algo que no se arregla reintentando —ya no soy miembro, estoy en pausa ahora, falta la
    /// clave— esas filas se descartan para ese grupo: no se van a poder enviar nunca.
    /// </remarks>
    public async Task<int> FlushAsync(FamilyService service, CancellationToken cancellationToken = default)
    {
        await InitAsync().ConfigureAwait(false);
        await _flushLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var sent = 0;
            while (true)
            {
                var rows = await _db.Table<OutboxRow>().OrderBy(r => r.Id).Take(Batch).ToListAsync().ConfigureAwait(false);
                if (rows.Count == 0)
                    return sent;

                var remaining = rows.ToDictionary(r => r.Id, r => r.GroupList);

                foreach (var group in rows.SelectMany(r => r.GroupList).Distinct().ToList())
                {
                    var forGroup = rows.Where(r => remaining[r.Id].Contains(group)).ToList();
                    try
                    {
                        sent += await service.InsertPositionsAsync(
                            group,
                            [.. forGroup.Select(r => (r.Lat, r.Lon, r.Accuracy, r.Battery, r.At, r.Coarse))],
                            cancellationToken).ConfigureAwait(false);
                    }
                    catch (FamilyTogetherException ex) when (ex.IsNetwork || ex.Code == FamilyTogetherException.Server)
                    {
                        // Se guarda lo avanzado y se sale: se reintenta en la proxima vuelta.
                        await SaveProgressAsync(rows, remaining).ConfigureAwait(false);
                        return sent;
                    }
                    catch (FamilyTogetherException ex)
                    {
                        CoreLog.Write($"Posiciones descartadas para {group}: {ex.Code}");
                    }

                    foreach (var row in forGroup)
                        remaining[row.Id].Remove(group);
                }

                await SaveProgressAsync(rows, remaining).ConfigureAwait(false);
            }
        }
        finally
        {
            _flushLock.Release();
        }
    }

    private async Task SaveProgressAsync(List<OutboxRow> rows, Dictionary<long, List<Guid>> remaining)
    {
        await _db.RunInTransactionAsync(connection =>
        {
            foreach (var row in rows)
            {
                var left = remaining[row.Id];
                if (left.Count == 0)
                {
                    connection.Delete<OutboxRow>(row.Id);
                }
                else if (left.Count != row.GroupList.Count)
                {
                    row.GroupList = left;
                    connection.Update(row);
                }
            }
        }).ConfigureAwait(false);
    }

    /// <summary>Lecturas en cola (cada una puede ir a varios grupos).</summary>
    public async Task<int> PendingCountAsync(CancellationToken cancellationToken = default)
    {
        await InitAsync().ConfigureAwait(false);
        return await _db.Table<OutboxRow>().CountAsync().ConfigureAwait(false);
    }

    /// <summary>Lo que hay en cola, para las pruebas.</summary>
    internal async Task<List<OutboxRow>> PeekAsync()
    {
        await InitAsync().ConfigureAwait(false);
        return await _db.Table<OutboxRow>().OrderBy(r => r.Id).ToListAsync().ConfigureAwait(false);
    }
}

/// <summary>Cuentas de geografia que no merecen una biblioteca.</summary>
public static class Geo
{
    private const double EarthRadius = 6_371_008.8;   // radio medio, en metros

    /// <summary>Distancia por haversine, en metros.</summary>
    public static double DistanceMeters(double lat1, double lon1, double lat2, double lon2)
    {
        static double Rad(double deg) => deg * Math.PI / 180;

        var dLat = Rad(lat2 - lat1);
        var dLon = Rad(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
              + Math.Cos(Rad(lat1)) * Math.Cos(Rad(lat2)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * EarthRadius * Math.Asin(Math.Min(1, Math.Sqrt(a)));
    }
}
