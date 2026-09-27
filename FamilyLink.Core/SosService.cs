using SQLite;

namespace FamilyLink.Core;

/// <summary>
/// SOS: se guarda al pulsar, se envia y, si no hay red, se reintenta hasta que sale (HU6).
/// </summary>
/// <remarks>
/// <para><b>El id se genera en el movil al pulsar</b> y se guarda con el SOS. <c>create_sos</c> es
/// idempotente por ese id, asi que reintentar nunca duplica la alerta, y los demas moviles
/// descartan el aviso repetido por el mismo <c>event_id</c>.</para>
///
/// <para><b>El contenido va cifrado por grupo</b> (posicion, hora y si es la ultima conocida): una
/// fila de <c>sos_targets</c> por grupo destino, cada una con su clave. Se envia aunque este en
/// pausa en ese grupo (FR-020).</para>
///
/// <para><b>Cuando se da por enviado.</b> Cuando el servidor acepta <c>create_sos</c> y la llamada a
/// <c>notify</c> llega (aunque responda error: eso no se arregla reintentando). Si lo que falla es
/// la red al avisar, el SOS sigue pendiente y se vuelve a mandar entero.</para>
/// </remarks>
public sealed class SosService
{
    private readonly SQLiteAsyncConnection _db;
    private readonly FamilyService _service;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private int _pending;

    public SosService(string databasePath, FamilyService service)
    {
        _db = new SQLiteAsyncConnection(databasePath);
        _service = service;

        // HasPending tiene que ser cierto desde el primer momento (p. ej. tras reiniciar el movil
        // con un SOS sin enviar), asi que la tabla se prepara aqui y no en la primera llamada.
        var connection = _db.GetConnection();
        using (connection.Lock())
        {
            connection.CreateTable<PendingSos>();
            _pending = connection.Table<PendingSos>().Count();
        }
    }

    [Table("pending_sos")]
    internal sealed class PendingSos
    {
        [PrimaryKey]
        public string Id { get; set; } = "";

        public string Groups { get; set; } = "";
        public double Lat { get; set; }
        public double Lon { get; set; }
        public double Accuracy { get; set; }
        public long AtUtcTicks { get; set; }
        public bool Stale { get; set; }
        public long CreatedUtcTicks { get; set; }
        public int Attempts { get; set; }
    }

    /// <summary>Hay algun SOS guardado que aun no ha salido. La app lo muestra.</summary>
    public bool HasPending => Volatile.Read(ref _pending) > 0;

    /// <summary>Cambia <see cref="HasPending"/>.</summary>
    public event EventHandler? PendingChanged;

    /// <summary>
    /// Guarda el SOS, intenta enviarlo y devuelve su id. Sin red no lanza: queda pendiente y lo
    /// reenvia <see cref="RetryPendingAsync"/>.
    /// </summary>
    public async Task<Guid> SendAsync(
        IReadOnlyList<Guid> groups,
        (double Lat, double Lon, double Acc, DateTimeOffset At, bool Stale) position,
        CancellationToken cancellationToken = default)
    {
        if (groups.Count == 0)
            throw new ArgumentException("El SOS necesita al menos un grupo.", nameof(groups));

        var id = Guid.NewGuid();
        var row = new PendingSos
        {
            Id = id.ToString("D"),
            Groups = string.Join(',', groups.Distinct().Select(g => g.ToString("D"))),
            Lat = position.Lat,
            Lon = position.Lon,
            Accuracy = position.Acc,
            AtUtcTicks = position.At.UtcTicks,
            Stale = position.Stale,
            CreatedUtcTicks = DateTimeOffset.UtcNow.UtcTicks,
        };

        await _db.InsertAsync(row).ConfigureAwait(false);
        await RecountAsync().ConfigureAwait(false);

        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await TrySendAsync(row, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }

        return id;
    }

    /// <summary>Reenvia lo pendiente. Lo llaman la app al volver la red y el servicio en cada vuelta.</summary>
    public async Task RetryPendingAsync(CancellationToken cancellationToken = default)
    {
        if (!HasPending)
            return;

        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var rows = await _db.Table<PendingSos>().OrderBy(r => r.CreatedUtcTicks).ToListAsync().ConfigureAwait(false);
            foreach (var row in rows)
            {
                if (!await TrySendAsync(row, cancellationToken).ConfigureAwait(false))
                    break;   // sin red: no tiene sentido probar los siguientes
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>Devuelve false si fallo la red (queda pendiente).</summary>
    private async Task<bool> TrySendAsync(PendingSos row, CancellationToken cancellationToken)
    {
        var id = Guid.Parse(row.Id);
        try
        {
            var at = new DateTimeOffset(row.AtUtcTicks, TimeSpan.Zero);
            var payload = Payloads.Sos(row.Lat, row.Lon, row.Accuracy, at, row.Stale);

            var targets = new List<object>();
            foreach (var group in row.Groups.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(Guid.Parse))
            {
                if (await _service.Keys.GetAsync(group).ConfigureAwait(false) is not { } key)
                {
                    CoreLog.Write($"SOS {id}: sin la clave de {group}, no se le envia.");
                    continue;
                }

                targets.Add(new { group_id = group, payload_enc = Crypto.Encrypt(payload, key) });
            }

            if (targets.Count > 0)
            {
                await _service.Client.RpcAsync("create_sos", new { p_id = id, p_targets = targets }, cancellationToken)
                    .ConfigureAwait(false);

                await NotifyOrThrowOnNetworkAsync(id, cancellationToken).ConfigureAwait(false);
            }

            await _db.DeleteAsync<PendingSos>(row.Id).ConfigureAwait(false);
            await RecountAsync().ConfigureAwait(false);
            return true;
        }
        catch (FamilyLinkException ex) when (ex.IsNetwork || ex.Code == FamilyLinkException.Server)
        {
            row.Attempts++;
            await _db.UpdateAsync(row).ConfigureAwait(false);
            CoreLog.Write($"SOS {id} pendiente (intento {row.Attempts}): {ex.Code}");
            return false;
        }
        catch (FamilyLinkException ex)
        {
            // Rechazo que no cambia reintentando (no_member, sesion invalida…): se abandona.
            CoreLog.Write($"SOS {id} rechazado: {ex.Code} {ex.Message}");
            await _db.DeleteAsync<PendingSos>(row.Id).ConfigureAwait(false);
            await RecountAsync().ConfigureAwait(false);
            return true;
        }
    }

    private async Task NotifyOrThrowOnNetworkAsync(Guid id, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await _service.Client.InvokeFunctionAsync("notify", new { type = EventTypes.Sos, id }, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (FamilyLinkException ex) when (!ex.IsNetwork)
        {
            CoreLog.Write($"notify sos {id}: {ex.Code}");
            return;
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                CoreLog.Write($"notify sos {id}: {(int)response.StatusCode}");
        }
    }

    private async Task RecountAsync()
    {
        var before = Volatile.Read(ref _pending);
        var now = await _db.Table<PendingSos>().CountAsync().ConfigureAwait(false);
        Volatile.Write(ref _pending, now);
        if ((before > 0) != (now > 0))
            PendingChanged?.Invoke(this, EventArgs.Empty);
    }
}
