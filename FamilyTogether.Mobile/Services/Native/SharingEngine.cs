using FamilyTogether.Core;

namespace FamilyTogether.Mobile.Services.Native;

/// <summary>Una lectura de ubicación, tal como la entrega el sistema (sin tipos de Android).</summary>
/// <param name="Provider">«gps», «network»…</param>
/// <param name="Accuracy">Precisión en metros, o <c>null</c> si la lectura no la trae.</param>
/// <param name="Speed">Velocidad en m/s, o <c>null</c> si la lectura no la trae.</param>
public sealed record LocationReading(string? Provider, double Lat, double Lon, double? Accuracy, double? Speed, DateTimeOffset At);

/// <summary>
/// Lo que el motor de compartición necesita del dispositivo. En Android lo da
/// <c>LocationSharingForegroundService</c>; en las pruebas, un doble.
/// </summary>
public interface ISharingDevice
{
    /// <summary>Hay red con salida a internet (si no se puede saber, verdadero: se intenta y a la cola).</summary>
    bool IsOnline();

    /// <summary>Batería en %, o -1 si no se sabe.</summary>
    int ReadBattery();

    /// <summary>Distancia en metros entre dos puntos (la del sistema, sobre el elipsoide).</summary>
    double DistanceMeters(double lat1, double lon1, double lat2, double lon2);

    /// <summary>El sensor de movimiento significativo está armado.</summary>
    bool MotionAvailable { get; }

    /// <summary>Último disparo del sensor de movimiento.</summary>
    DateTimeOffset? LastMotion { get; }

    /// <summary>FCM está configurado: los avisos llegan por push.</summary>
    bool PushConfigured { get; }

    /// <summary>Un servicio del contenedor de MAUI, o <c>null</c> si no está.</summary>
    T? Get<T>() where T : class;

    /// <summary>Los avisos del sistema si el contenedor no los da.</summary>
    INotifier FallbackNotifier();

    /// <summary>Coge la CPU (con tiempo máximo) mientras se procesa.</summary>
    void AcquireWakeLock();

    void ReleaseWakeLock();
}

/// <summary>
/// La lógica del servicio de ubicación en primer plano (ARQUITECTURA §8), sin Android: qué se hace
/// con cada lectura, el principio del recorrido al echar a andar y el ciclo de 60 s. El servicio
/// nativo solo escucha al sistema y le pasa las lecturas.
/// </summary>
/// <remarks>
/// <para><b>Qué entra en el historial</b> (2026-09-28, <see cref="ReadingPolicy"/>): solo el GPS de
/// 25 m o mejor con el móvil moviéndose (sensor de movimiento significativo o velocidad del GPS de ir
/// andando). La red (wifi y antenas) y el GPS con el móvil quieto van como aproximadas: solo la
/// última posición del mapa.</para>
/// <para><b>Por cada lectura válida</b> (reciente y, si es buena, a ≥ 25 m de la última enviada):
/// se calculan los grupos donde comparto en ese momento, se encola en <see cref="LocationOutbox"/>
/// con su hora original, se vacía la cola si hay red, y se evalúan las zonas
/// (<see cref="ZoneWatcher"/>) informando de cada entrada o salida.</para>
/// <para><b>Cada 60 s</b>: se reintenta lo que quedó pendiente (cola, zonas, SOS), se entregan las
/// claves que alguien haya pedido y, si no hay FCM, se consultan los eventos nuevos y se avisa.</para>
/// <para><b>Nunca tumba el proceso</b>: cada paso va con su try/catch y se registra.</para>
/// </remarks>
public sealed class SharingEngine(ISharingDevice device)
{
    /// <summary>Umbral de desplazamiento y de precisión (FR-009, FR-010).</summary>
    public const float ThresholdMeters = 25f;

    /// <summary>Proveedor GPS del sistema (<c>LocationManager.GPS_PROVIDER</c>).</summary>
    public const string GpsProvider = "gps";

    /// <summary>Una lectura más vieja que esto (una «última conocida» recalentada) no se envía.</summary>
    public static readonly TimeSpan MaxReadingAge = TimeSpan.FromMinutes(2);

    /// <summary>Con FCM las claves pedidas llegan por push; aun así se mira de vez en cuando.</summary>
    public static readonly TimeSpan KeySharesEveryWithPush = TimeSpan.FromMinutes(5);

    /// <summary>Antigüedad máxima de la caché de grupos al procesar una lectura (pausas recientes).</summary>
    public static readonly TimeSpan GroupsMaxAgeOnReading = TimeSpan.FromSeconds(60);

    /// <summary>Antigüedad máxima de la caché de grupos en el ciclo periódico.</summary>
    public static readonly TimeSpan GroupsMaxAgeOnCycle = TimeSpan.FromMinutes(5);

    /// <summary>Tope de entradas y salidas de zona pendientes de informar.</summary>
    public const int MaxPendingZoneEvents = 50;

    /// <summary>Última lectura válida recibida (para el SOS si no hay GPS en ese momento).</summary>
    public static LocationReading? LastFix { get; internal set; }

    private readonly SemaphoreSlim _work = new(1, 1);
    private readonly List<(Guid Group, Guid Zone, string Kind)> _pendingZoneEvents = [];
    private readonly MotionStartBuffer _stillReadings = new();
    private bool _sentInThisRun;
    private DateTimeOffset _lastKeyShares = DateTimeOffset.MinValue;
    private DateTimeOffset _lastStillLog = DateTimeOffset.MinValue;

    /// <summary>El servicio está vivo: si no, lo que llega se ignora.</summary>
    public bool Running { get; set; }

    /// <summary>Entradas y salidas de zona aún sin informar (se reintentan en el ciclo).</summary>
    public int PendingZoneEvents
    {
        get { lock (_pendingZoneEvents) return _pendingZoneEvents.Count; }
    }

    /// <summary>Lecturas GPS buenas «por quieto» guardadas por si son el principio de un recorrido.</summary>
    public int HeldStillReadings => _stillReadings.Count;

    /// <summary>Al dejar de escuchar: lo guardado por si echaba a andar ya no vale.</summary>
    public void ClearStillReadings() => _stillReadings.Clear();

    /// <summary>
    /// ¿Qué se hace con esta lectura? (<see cref="ReadingPolicy"/>, FR-010 del 2026-09-28): solo el
    /// GPS de 25 m o mejor, con el móvil moviéndose, entra en el historial; la red y el GPS con el
    /// móvil quieto solo actualizan la última posición (aproximada). Además: antigüedad y distancia
    /// a la última enviada.
    /// </summary>
    public ReadingKind Classify(LocationReading location, DateTimeOffset now)
    {
        if (location.Accuracy is not { } accuracy)
            return ReadingKind.Discard;

        if (now - location.At > MaxReadingAge)
            return ReadingKind.Discard;

        var firstOfRun = !_sentInThisRun || SharingState.LastSent is null;
        var kind = ReadingPolicy.Classify(location.Provider, accuracy, location.Speed, device.MotionAvailable, device.LastMotion, now, firstOfRun);

        if (kind == ReadingKind.Coarse && location.Provider == GpsProvider &&
            accuracy <= ThresholdMeters && now - _lastStillLog > TimeSpan.FromMinutes(10))
        {
            _lastStillLog = now;
            NativeLog.Info($"GPS de {accuracy:F0} m con el móvil quieto: aproximada, fuera del historial.");
        }

        if (kind != ReadingKind.Fine || firstOfRun || SharingState.LastSent is not { } last)
            return kind;

        // La cola decide si una aproximada vale (solo si en 10 min no ha habido otra); una buena
        // tiene que estar a 25 m o más de la última enviada.
        return device.DistanceMeters(last.Lat, last.Lon, location.Lat, location.Lon) >= ThresholdMeters
            ? ReadingKind.Fine
            : ReadingKind.Discard;
    }

    public async Task ProcessReadingAsync(LocationReading location)
    {
        try
        {
            if (!Running)
                return;

            // Filtro rápido fuera del cerrojo; se repite dentro por si entró otra mientras tanto.
            if (location.Accuracy is not { } accuracy || accuracy > LocationOutbox.CoarseMaxAccuracyMeters)
                return;

            await _work.WaitAsync().ConfigureAwait(false);
            try
            {
                device.AcquireWakeLock();
                var kind = Classify(location, DateTimeOffset.UtcNow);
                if (kind == ReadingKind.Discard)
                    return;

                // GPS bueno con el móvil quieto: se guarda por si es el arranque de un recorrido
                // (el sensor de movimiento llega tarde; MotionStartBuffer). Los demás, ni caso.
                if (kind == ReadingKind.Coarse)
                    _stillReadings.Hold(location.Provider, new TrackPoint(location.Lat, location.Lon, accuracy, location.At));

                LastFix = location;
                await HandleReadingAsync(location.Lat, location.Lon, accuracy, location.At, kind == ReadingKind.Coarse).ConfigureAwait(false);
            }
            finally
            {
                device.ReleaseWakeLock();
                _work.Release();
            }
        }
        catch (Exception ex)
        {
            NativeLog.Error("Error al procesar una posición.", ex);
        }
    }

    /// <summary>
    /// El sensor ha saltado: las lecturas GPS buenas tomadas «por quieto» en los minutos de antes
    /// eran el principio del recorrido y entran en el historial con su hora (MotionStartBuffer).
    /// </summary>
    public async Task PromoteStillReadingsAsync(DateTimeOffset motionAt)
    {
        try
        {
            if (!Running)
                return;

            await _work.WaitAsync().ConfigureAwait(false);
            try
            {
                device.AcquireWakeLock();
                var held = _stillReadings.Release(motionAt);
                if (held.Count == 0)
                    return;

                var promoted = 0;
                foreach (var p in held)
                {
                    // La misma regla de los 25 m que una lectura buena normal.
                    if (SharingState.LastSent is { } last &&
                        device.DistanceMeters(last.Lat, last.Lon, p.Lat, p.Lon) < ThresholdMeters)
                        continue;

                    await HandleReadingAsync(p.Lat, p.Lon, p.Accuracy, p.At, coarse: false).ConfigureAwait(false);
                    promoted++;
                }

                NativeLog.Info($"Al echar a andar: {promoted} de {held.Count} lecturas de antes del sensor entran en el historial.");
            }
            finally
            {
                device.ReleaseWakeLock();
                _work.Release();
            }
        }
        catch (Exception ex)
        {
            NativeLog.Error("Error al recuperar el principio del recorrido.", ex);
        }
    }

    private async Task HandleReadingAsync(double lat, double lon, double accuracy, DateTimeOffset at, bool coarse)
    {
        var family = device.Get<FamilyService>();
        var outbox = device.Get<LocationOutbox>();
        if (family is null || outbox is null)
        {
            NativeLog.Warn("Núcleo no disponible en el servicio: lectura descartada.");
            return;
        }

        var online = device.IsOnline();

        // Los grupos se fijan AHORA y viajan con la fila: si luego se pausa uno, lo registrado
        // mientras compartía se envía, y lo registrado en pausa no llega nunca (FR-018).
        var groups = await SharingState.GetSharingGroupsAsync(family, online, GroupsMaxAgeOnReading).ConfigureAwait(false);

        if (groups.Count > 0)
        {
            try
            {
                var queued = await outbox.EnqueueAsync(lat, lon, accuracy, device.ReadBattery(), at, groups, coarse).ConfigureAwait(false);
                // Una aproximada no cuenta como referencia: la siguiente buena tiene que salir.
                if (!coarse)
                {
                    SharingState.LastSent = (lat, lon);
                    _sentInThisRun = true;
                }

                // Mi recorrido de las últimas 24 h, también en el móvil: se pinta aunque el
                // servidor falle o la cola no se haya vaciado.
                if (queued && !coarse && device.Get<LocalTrack>() is { } track)
                    await track.AddAsync(lat, lon, accuracy, at, groups).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                NativeLog.Error("No se pudo encolar la posición.", ex);
            }

            if (online)
                await FlushAsync(outbox, family).ConfigureAwait(false);
        }

        // Zonas: solo en los grupos donde comparto (en pausa el grupo no debe saber dónde estoy).
        // Con una lectura aproximada (red, GPS peor de 25 m o con el móvil quieto) no se evalúan
        // zonas: daría entradas y salidas falsas.
        var zones = device.Get<ZoneWatcher>();
        if (zones is not null && groups.Count > 0 && !coarse)
        {
            try
            {
                var transitions = await zones.EvaluateAsync(lat, lon, accuracy, groups).ConfigureAwait(false);
                lock (_pendingZoneEvents)
                    _pendingZoneEvents.AddRange(transitions);
            }
            catch (Exception ex)
            {
                NativeLog.Error("No se pudieron evaluar las zonas.", ex);
            }
        }

        if (online)
            await ReportZoneEventsAsync(family).ConfigureAwait(false);
    }

    private static async Task FlushAsync(LocationOutbox outbox, FamilyService family)
    {
        try
        {
            await outbox.FlushAsync(family).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Se queda en la cola con su hora original; el ciclo lo reintenta.
            NativeLog.Warn("No se pudo vaciar la cola de posiciones; se reintentará.", ex);
        }
    }

    /// <summary>
    /// Informa de las entradas y salidas de zona. Lo que falla (sin red) se queda en memoria y se
    /// reintenta en el ciclo; si el proceso muere antes, ese aviso se pierde, pero el estado de la
    /// zona sí está guardado y no se repite.
    /// </summary>
    private async Task ReportZoneEventsAsync(FamilyService family)
    {
        List<(Guid Group, Guid Zone, string Kind)> batch;
        lock (_pendingZoneEvents)
        {
            if (_pendingZoneEvents.Count == 0)
                return;
            batch = [.. _pendingZoneEvents];
            _pendingZoneEvents.Clear();
        }

        var failed = new List<(Guid Group, Guid Zone, string Kind)>();
        foreach (var t in batch)
        {
            try
            {
                await family.ReportZoneEventAsync(t.Group, t.Zone, t.Kind).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                NativeLog.Warn($"No se pudo informar de la zona ({t.Kind}); se reintentará.", ex);
                failed.Add(t);
            }
        }

        if (failed.Count > 0)
        {
            lock (_pendingZoneEvents)
            {
                // Tope para no crecer sin fin si el servidor rechaza siempre (expulsado, etc.).
                _pendingZoneEvents.InsertRange(0, failed);
                if (_pendingZoneEvents.Count > MaxPendingZoneEvents)
                    _pendingZoneEvents.RemoveRange(0, _pendingZoneEvents.Count - MaxPendingZoneEvents);
            }
        }
    }

    // ==================================================================================
    //  Ciclo de 60 s
    // ==================================================================================

    public async Task RunCycleAsync()
    {
        try
        {
            if (!Running)
                return;

            // Si todavía se está procesando una lectura, esta vuelta se salta: habrá otra en 60 s.
            if (!await _work.WaitAsync(0).ConfigureAwait(false))
                return;

            try
            {
                device.AcquireWakeLock();
                await CycleAsync().ConfigureAwait(false);
            }
            finally
            {
                device.ReleaseWakeLock();
                _work.Release();
            }
        }
        catch (Exception ex)
        {
            NativeLog.Error("Error en el ciclo periódico del servicio.", ex);
        }
    }

    private async Task CycleAsync()
    {
        // Lo de más de 24 h del recorrido local se borra también sin red.
        try
        {
            if (device.Get<LocalTrack>() is { } track)
                await track.PruneAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            NativeLog.Warn("No se pudo podar el recorrido local.", ex);
        }

        if (!device.IsOnline())
            return;

        var family = device.Get<FamilyService>();
        if (family is null)
            return;

        // Mantiene al día la caché de grupos (fin de pausas, grupos nuevos, expulsiones).
        await SharingState.GetSharingGroupsAsync(family, online: true, GroupsMaxAgeOnCycle).ConfigureAwait(false);

        // 1. Posiciones que se quedaron sin enviar.
        var outbox = device.Get<LocationOutbox>();
        if (outbox is not null)
        {
            try
            {
                if (await outbox.PendingCountAsync().ConfigureAwait(false) > 0)
                    await FlushAsync(outbox, family).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                NativeLog.Warn("No se pudo consultar la cola de posiciones.", ex);
            }
        }

        // 2. Zonas pendientes de informar.
        await ReportZoneEventsAsync(family).ConfigureAwait(false);

        // 3. SOS que no salió (sin conexión en su momento): se reintenta solo.
        var sos = device.Get<SosService>();
        if (sos is not null)
        {
            try
            {
                if (sos.HasPending)
                    await sos.RetryPendingAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                NativeLog.Warn("No se pudo reintentar el SOS pendiente.", ex);
            }
        }

        var push = device.PushConfigured;

        // 4. Claves pedidas por quien ha recuperado su cuenta (ARQUITECTURA §5).
        if (!push || DateTimeOffset.UtcNow - _lastKeyShares > KeySharesEveryWithPush)
        {
            try
            {
                await family.FulfillPendingKeySharesAsync().ConfigureAwait(false);
                _lastKeyShares = DateTimeOffset.UtcNow;
            }
            catch (Exception ex)
            {
                NativeLog.Warn("No se pudieron entregar las claves pedidas.", ex);
            }
        }

        // 5. Sin FCM, los avisos salen de aquí (ARQUITECTURA §7).
        if (!push && device.Get<EventFeed>() is { } feed)
        {
            try
            {
                var events = await feed.PollAsync().ConfigureAwait(false);
                if (events.Count > 0)
                {
                    var notifier = device.Get<INotifier>() ?? device.FallbackNotifier();
                    foreach (var content in events)
                        notifier.Show(content);
                }
            }
            catch (Exception ex)
            {
                NativeLog.Warn("No se pudieron consultar los eventos nuevos.", ex);
            }
        }
    }
}
