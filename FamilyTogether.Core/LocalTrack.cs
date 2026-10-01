using SQLite;

namespace FamilyTogether.Core;

/// <summary>
/// Mi propio recorrido de las ultimas 24 horas, guardado en el movil (SQLite en la carpeta privada
/// de la app, como la cola). Sirve para pintar «Ultimas 24 horas» de mi historial aunque el servidor
/// no responda o la cola aun no se haya vaciado (FR-014, 2026-10-01).
/// </summary>
/// <remarks>
/// <para><b>Solo lo que entra en el historial</b>: las lecturas buenas que la cola acepto, con los
/// grupos donde compartia en ese momento (lo grabado en pausa en un grupo no se pinta en ese
/// grupo, igual que no le llega).</para>
/// <para><b>Caduca a las 24 horas</b> (<see cref="Keep"/>): al guardar y al consultar se borra lo
/// anterior. Lo que vale de verdad es lo del servidor, cifrado, con su retencion de 30 dias.</para>
/// <para>Las coordenadas no salen nunca de aqui: el fichero es privado de la app y no va en las
/// copias de seguridad (el mismo trato que la cola, <see cref="LocationOutbox"/>).</para>
/// </remarks>
public sealed class LocalTrack
{
    /// <summary>Cuanto se guarda.</summary>
    public static readonly TimeSpan Keep = TimeSpan.FromHours(24);

    private readonly SQLiteAsyncConnection _db;
    private readonly Func<DateTimeOffset> _now;
    private bool _ready;

    /// <param name="now">Reloj (para las pruebas); por defecto, el del sistema.</param>
    public LocalTrack(string databasePath, Func<DateTimeOffset>? now = null)
    {
        _db = new SQLiteAsyncConnection(databasePath);
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    [Table("local_track")]
    internal sealed class TrackRow
    {
        [PrimaryKey, AutoIncrement]
        public long Id { get; set; }

        public double Lat { get; set; }
        public double Lon { get; set; }
        public double Accuracy { get; set; }

        /// <summary>Hora de la lectura, en ticks UTC.</summary>
        [Indexed]
        public long AtUtcTicks { get; set; }

        /// <summary>Grupos donde compartia al leerla, separados por comas.</summary>
        public string Groups { get; set; } = "";
    }

    private async Task InitAsync()
    {
        if (_ready)
            return;

        await _db.CreateTableAsync<TrackRow>().ConfigureAwait(false);
        _ready = true;
    }

    /// <summary>
    /// Guarda una lectura buena. Sin grupos, con coordenadas no validas o mas vieja que
    /// <see cref="Keep"/> no guarda nada. Borra de paso lo caducado.
    /// </summary>
    public async Task AddAsync(double lat, double lon, double accuracy, DateTimeOffset at, IReadOnlyList<Guid> groups)
    {
        if (groups.Count == 0 || double.IsNaN(lat) || double.IsNaN(lon) || _now() - at > Keep)
            return;

        await InitAsync().ConfigureAwait(false);
        await _db.InsertAsync(new TrackRow
        {
            Lat = lat,
            Lon = lon,
            Accuracy = accuracy,
            AtUtcTicks = at.UtcTicks,
            Groups = string.Join(',', groups.Distinct().Order().Select(g => g.ToString("D"))),
        }).ConfigureAwait(false);
        await PruneAsync().ConfigureAwait(false);
    }

    /// <summary>Borra lo que tiene mas de <see cref="Keep"/>. Devuelve cuantas lecturas borro.</summary>
    public async Task<int> PruneAsync()
    {
        await InitAsync().ConfigureAwait(false);
        var limit = (_now() - Keep).UtcTicks;
        return await _db.ExecuteAsync("delete from local_track where AtUtcTicks < ?", limit).ConfigureAwait(false);
    }

    /// <summary>
    /// Mis lecturas para <paramref name="group"/> entre <paramref name="from"/> (incluida) y
    /// <paramref name="to"/> (excluida), de la mas antigua a la mas nueva.
    /// </summary>
    public async Task<IReadOnlyList<MemberPosition>> GetAsync(Guid me, Guid group, DateTimeOffset from, DateTimeOffset to)
    {
        await PruneAsync().ConfigureAwait(false);
        var key = group.ToString("D");
        var rows = await _db.Table<TrackRow>()
            .Where(r => r.AtUtcTicks >= from.UtcTicks && r.AtUtcTicks < to.UtcTicks)
            .OrderBy(r => r.AtUtcTicks)
            .ToListAsync().ConfigureAwait(false);

        return [.. rows
            .Where(r => r.Groups.Split(',').Contains(key))
            .Select(r => new MemberPosition(me, r.Lat, r.Lon, r.Accuracy, -1, new DateTimeOffset(r.AtUtcTicks, TimeSpan.Zero)))];
    }

    /// <summary>Borra todo (al borrar mi historial).</summary>
    public async Task ClearAsync()
    {
        await InitAsync().ConfigureAwait(false);
        await _db.DeleteAllAsync<TrackRow>().ConfigureAwait(false);
    }

    /// <summary>Lecturas guardadas (de todos los grupos).</summary>
    public async Task<int> CountAsync()
    {
        await InitAsync().ConfigureAwait(false);
        return await _db.Table<TrackRow>().CountAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Junta lo del servidor con lo guardado en el movil: ordenado por hora y sin repetir la misma
    /// lectura (misma hora, al segundo; gana la del servidor, que trae la bateria).
    /// </summary>
    public static IReadOnlyList<MemberPosition> Merge(IReadOnlyList<MemberPosition> server, IReadOnlyList<MemberPosition> local)
    {
        if (local.Count == 0)
            return [.. server.OrderBy(p => p.At)];

        var seen = server.Select(p => p.At.ToUnixTimeSeconds()).ToHashSet();
        return [.. server.Concat(local.Where(p => !seen.Contains(p.At.ToUnixTimeSeconds()))).OrderBy(p => p.At)];
    }
}
