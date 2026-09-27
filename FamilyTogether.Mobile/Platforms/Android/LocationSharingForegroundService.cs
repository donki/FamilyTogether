using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Locations;
using Android.Net;
using Android.OS;
using AndroidX.Core.App;
using FamilyTogether.Core;
using FamilyTogether.Mobile.Services;
using AndroidLocation = Android.Locations.Location;

namespace FamilyTogether.Mobile.Platforms.Android;

/// <summary>
/// Servicio en primer plano de tipo <c>location</c> que comparte la posición con los grupos
/// (ARQUITECTURA §8, Mobile §10). Es el de Hiker, pero con un umbral de 25 m en vez de grabación
/// continua.
/// </summary>
/// <remarks>
/// <para><b>Sin Google Play Services.</b> <see cref="LocationManager"/> del sistema con
/// <c>GPS_PROVIDER</c> y <c>NETWORK_PROVIDER</c>, cada uno con <c>minDistance = 25 m</c> y
/// <c>minTime = 30 s</c>: el sistema no despierta a la app mientras el móvil no se mueve, y moviéndose
/// no entrega más de una lectura cada medio minuto por proveedor (batería, SC-005).</para>
/// <para><b>Por cada lectura válida</b> (precisión ≤ 25 m, reciente y a ≥ 25 m de la última
/// enviada): se calculan los grupos donde comparto en ese momento, se encola en
/// <see cref="LocationOutbox"/> con su hora original, se vacía la cola si hay red, y se evalúan las
/// zonas (<see cref="ZoneWatcher"/>) informando de cada entrada o salida.</para>
/// <para><b>Cada 60 s</b>: se reintenta lo que quedó pendiente (cola, zonas, SOS), se entregan las
/// claves que alguien haya pedido y, si no hay FCM, se consultan los eventos nuevos y se avisa.</para>
/// <para><b>Nunca tumba el proceso</b>: cada paso va con su try/catch y se registra.</para>
/// <para>Solo coge la CPU (bloqueo parcial con tiempo máximo) mientras procesa, no de forma
/// continua como Hiker, que solo graba durante una ruta.</para>
/// </remarks>
[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeLocation)]
public class LocationSharingForegroundService : Service, ILocationListener
{
    /// <summary>Umbral de desplazamiento y de precisión (FR-009, FR-010).</summary>
    internal const float ThresholdMeters = 25f;

    /// <summary>Intervalo mínimo entre lecturas de un mismo proveedor.</summary>
    private const long MinTimeMs = 30_000;

    /// <summary>Una lectura más vieja que esto (una «última conocida» recalentada) no se envía.</summary>
    private static readonly TimeSpan MaxReadingAge = TimeSpan.FromMinutes(2);

    /// <summary>Cada cuánto se reintenta lo pendiente y se consultan los eventos (ARQUITECTURA §7).</summary>
    private static readonly TimeSpan CycleEvery = TimeSpan.FromSeconds(60);

    /// <summary>Con FCM las claves pedidas llegan por push; aun así se mira de vez en cuando.</summary>
    private static readonly TimeSpan KeySharesEveryWithPush = TimeSpan.FromMinutes(5);

    /// <summary>Antigüedad máxima de la caché de grupos al procesar una lectura (pausas recientes).</summary>
    private static readonly TimeSpan GroupsMaxAgeOnReading = TimeSpan.FromSeconds(60);

    /// <summary>Antigüedad máxima de la caché de grupos en el ciclo periódico.</summary>
    private static readonly TimeSpan GroupsMaxAgeOnCycle = TimeSpan.FromMinutes(5);

    /// <summary>Tiempo máximo que se retiene la CPU por cada trabajo.</summary>
    private const long WakeLockTimeoutMs = 60_000;

    private static volatile bool _running;

    /// <summary>Verdadero mientras el servicio está vivo y en primer plano.</summary>
    public static bool IsRunning => _running;

    /// <summary>Última lectura válida recibida (para el SOS si no hay GPS en ese momento).</summary>
    internal static AndroidLocation? LastFix { get; private set; }

    private readonly SemaphoreSlim _work = new(1, 1);
    private readonly List<(Guid Group, Guid Zone, string Kind)> _pendingZoneEvents = [];
    private LocationManager? _locationManager;
    private PowerManager.WakeLock? _wakeLock;
    private Timer? _timer;
    private bool _listening;
    private bool _sentInThisRun;
    private DateTimeOffset _lastKeyShares = DateTimeOffset.MinValue;

    // ==================================================================================
    //  Arranque y parada (desde LocationSharing y BootReceiver)
    // ==================================================================================

    /// <summary>Arranca el servicio. Quien llama ya ha comprobado el permiso de ubicación.</summary>
    internal static bool StartService(Context context)
    {
        try
        {
            var intent = new Intent(context, typeof(LocationSharingForegroundService));
            if (OperatingSystem.IsAndroidVersionAtLeast(26))
                context.StartForegroundService(intent);
            else
                context.StartService(intent);
            return true;
        }
        catch (Exception ex)
        {
            // ForegroundServiceStartNotAllowedException (Android 12+) si la app está detrás y no
            // hay exención: se reintentará al abrir la app (MainActivity).
            NativeLog.Warn("No se pudo arrancar el servicio de ubicación.", ex);
            return false;
        }
    }

    internal static void StopService(Context context)
    {
        try
        {
            context.StopService(new Intent(context, typeof(LocationSharingForegroundService)));
        }
        catch (Exception ex)
        {
            NativeLog.Warn("No se pudo parar el servicio de ubicación.", ex);
        }
    }

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        // Lo primero, siempre: tras StartForegroundService hay pocos segundos para llamar a
        // StartForeground o Android cierra el proceso.
        if (!TryStartInForeground())
        {
            StopSelf();
            return StartCommandResult.NotSticky;
        }

        // Recreado por Android (Sticky, intent nulo) cuando el usuario ya lo había desactivado.
        if (!SharingState.Enabled)
        {
            NativeLog.Info("Servicio recreado con la compartición desactivada: se para.");
            StopSelf();
            return StartCommandResult.NotSticky;
        }

        _running = true;
        StartListening();
        StartCycle();

        // Sticky: si Android mata el proceso por memoria, recrea el servicio y se sigue compartiendo.
        return StartCommandResult.Sticky;
    }

    public override void OnDestroy()
    {
        _running = false;

        try
        {
            _timer?.Dispose();
            _timer = null;
            StopListening();
            ReleaseWakeLock();
            if (OperatingSystem.IsAndroidVersionAtLeast(24))
                StopForeground(StopForegroundFlags.Remove);
            else
                StopForeground(true);
        }
        catch (Exception ex)
        {
            NativeLog.Warn("Error al cerrar el servicio de ubicación.", ex);
        }

        base.OnDestroy();
    }

    // ==================================================================================
    //  Notificación fija
    // ==================================================================================

    private bool TryStartInForeground()
    {
        try
        {
            Notifier.EnsureChannels(this);

            var launch = new Intent(this, typeof(MainActivity));
            launch.AddFlags(ActivityFlags.NewTask | ActivityFlags.SingleTop);
            var pending = PendingIntent.GetActivity(this, 0, launch,
                PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

            // Cada Set* del binding devuelve un Builder anulable: se llama sobre la misma variable.
            var builder = new NotificationCompat.Builder(this, Notifier.ChannelService);
            builder.SetContentTitle(Notifier.Text("ServiceNotifTitle", "Sharing your location"));
            builder.SetContentText(Notifier.Text("ServiceNotifText", "Your groups can see where you are."));
            builder.SetSmallIcon(Resource.Drawable.ic_notification);
            builder.SetOngoing(true);
            builder.SetShowWhen(false);
            builder.SetContentIntent(pending);
            builder.SetCategory(NotificationCompat.CategoryService);
            builder.SetPriority((int)NotificationPriority.Low);
            builder.SetForegroundServiceBehavior(NotificationCompat.ForegroundServiceImmediate);
            var notification = builder.Build()
                ?? throw new InvalidOperationException("NotificationCompat.Builder.Build() devolvió null");

            if (OperatingSystem.IsAndroidVersionAtLeast(29))
                StartForeground(Notifier.ServiceNotificationId, notification, ForegroundService.TypeLocation);
            else
                StartForeground(Notifier.ServiceNotificationId, notification);

            return true;
        }
        catch (Exception ex)
        {
            // Android 14+: SecurityException si falta el permiso de ubicación; Android 12+:
            // ForegroundServiceStartNotAllowedException si se arrancó desde segundo plano sin exención.
            NativeLog.Error("No se pudo poner el servicio de ubicación en primer plano.", ex);
            return false;
        }
    }

    // ==================================================================================
    //  Escucha de la ubicación
    // ==================================================================================

    private void StartListening()
    {
        if (_listening)
            return;

        try
        {
            _locationManager = (LocationManager?)GetSystemService(LocationService);
            if (_locationManager is null)
            {
                NativeLog.Warn("Sin LocationManager: no se puede compartir la ubicación.");
                return;
            }

            var available = _locationManager.GetProviders(false) ?? [];

            // GPS: el que da precisión de metros en la calle. Red (wifi y antenas): la que funciona
            // dentro de casa; muchas de sus lecturas no bajan de 25 m y se descartan, pero con wifi
            // a menudo sí, y ahí el GPS no fija.
            foreach (var provider in new[] { LocationManager.GpsProvider, LocationManager.NetworkProvider })
            {
                if (!available.Contains(provider))
                    continue;

                // Se registra aunque el proveedor esté apagado: si el usuario lo enciende después,
                // Android empieza a entregar sin tener que reiniciar el servicio.
                _locationManager.RequestLocationUpdates(provider, MinTimeMs, ThresholdMeters, this, Looper.MainLooper);
                _listening = true;
            }

            if (!_listening)
                NativeLog.Warn("Ni GPS ni red disponibles en este dispositivo.");
        }
        catch (Java.Lang.SecurityException ex)
        {
            // Permiso retirado desde Ajustes: sin ubicación no hay nada que hacer.
            NativeLog.Warn("Servicio sin permiso de ubicación; se para.", ex);
            SharingState.Enabled = false;
            StopSelf();
        }
        catch (Exception ex)
        {
            NativeLog.Error("No se pudo empezar a escuchar la ubicación.", ex);
        }
    }

    private void StopListening()
    {
        if (!_listening)
            return;

        try
        {
            _locationManager?.RemoveUpdates(this);
        }
        catch (Exception)
        {
            // Permiso retirado mientras tanto: no hay nada que quitar.
        }

        _listening = false;
    }

    public void OnLocationChanged(AndroidLocation location)
    {
        // Llega en el hilo principal: el trabajo (red, SQLite, cifrado) se hace fuera.
        var copy = new AndroidLocation(location);
        _ = Task.Run(() => ProcessReadingAsync(copy));
    }

    public void OnProviderDisabled(string provider) => NativeLog.Info($"Proveedor de ubicación apagado: {provider}");

    public void OnProviderEnabled(string provider) => NativeLog.Info($"Proveedor de ubicación encendido: {provider}");

    public void OnStatusChanged(string? provider, [global::Android.Runtime.GeneratedEnum] Availability status, Bundle? extras) { }

    /// <summary>¿Vale esta lectura? Precisión, antigüedad y distancia a la última enviada.</summary>
    private bool IsWorthSending(AndroidLocation location)
    {
        if (!location.HasAccuracy || location.Accuracy > ThresholdMeters)
            return false;

        var at = DateTimeOffset.FromUnixTimeMilliseconds(location.Time);
        if (DateTimeOffset.UtcNow - at > MaxReadingAge)
            return false;

        // La primera lectura válida de cada arranque se envía siempre: tras activar la compartición
        // o reiniciar el móvil, el grupo tiene que ver una posición reciente.
        if (!_sentInThisRun || SharingState.LastSent is not { } last)
            return true;

        var results = new float[1];
        AndroidLocation.DistanceBetween(last.Lat, last.Lon, location.Latitude, location.Longitude, results);
        return results[0] >= ThresholdMeters;
    }

    private async Task ProcessReadingAsync(AndroidLocation location)
    {
        try
        {
            if (!_running)
                return;

            // Filtro rápido fuera del cerrojo; se repite dentro por si entró otra mientras tanto.
            if (!location.HasAccuracy || location.Accuracy > ThresholdMeters)
                return;

            await _work.WaitAsync().ConfigureAwait(false);
            AcquireWakeLock();
            try
            {
                if (!IsWorthSending(location))
                    return;

                LastFix = location;
                await HandleReadingAsync(location).ConfigureAwait(false);
            }
            finally
            {
                ReleaseWakeLock();
                _work.Release();
            }
        }
        catch (Exception ex)
        {
            NativeLog.Error("Error al procesar una posición.", ex);
        }
    }

    private async Task HandleReadingAsync(AndroidLocation location)
    {
        var family = PlatformServiceLocator.Get<FamilyService>();
        var outbox = PlatformServiceLocator.Get<LocationOutbox>();
        if (family is null || outbox is null)
        {
            NativeLog.Warn("Núcleo no disponible en el servicio: lectura descartada.");
            return;
        }

        var lat = location.Latitude;
        var lon = location.Longitude;
        double accuracy = location.Accuracy;
        var at = DateTimeOffset.FromUnixTimeMilliseconds(location.Time);
        var online = IsOnline();

        // Los grupos se fijan AHORA y viajan con la fila: si luego se pausa uno, lo registrado
        // mientras compartía se envía, y lo registrado en pausa no llega nunca (FR-018).
        var groups = await SharingState.GetSharingGroupsAsync(family, online, GroupsMaxAgeOnReading).ConfigureAwait(false);

        if (groups.Count > 0)
        {
            try
            {
                await outbox.EnqueueAsync(lat, lon, accuracy, ReadBattery(), at, groups).ConfigureAwait(false);
                SharingState.LastSent = (lat, lon);
                _sentInThisRun = true;
            }
            catch (Exception ex)
            {
                NativeLog.Error("No se pudo encolar la posición.", ex);
            }

            if (online)
                await FlushAsync(outbox, family).ConfigureAwait(false);
        }

        // Zonas: solo en los grupos donde comparto (en pausa el grupo no debe saber dónde estoy).
        var zones = PlatformServiceLocator.Get<ZoneWatcher>();
        if (zones is not null && groups.Count > 0)
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
                if (_pendingZoneEvents.Count > 50)
                    _pendingZoneEvents.RemoveRange(0, _pendingZoneEvents.Count - 50);
            }
        }
    }

    // ==================================================================================
    //  Ciclo de 60 s
    // ==================================================================================

    private void StartCycle()
    {
        if (_timer is not null)
            return;

        // Primera pasada a los pocos segundos: recoge lo que quedara pendiente de antes.
        _timer = new Timer(_ => _ = Task.Run(RunCycleAsync), null, TimeSpan.FromSeconds(5), CycleEvery);
    }

    private async Task RunCycleAsync()
    {
        try
        {
            if (!_running)
                return;

            // Si todavía se está procesando una lectura, esta vuelta se salta: habrá otra en 60 s.
            if (!await _work.WaitAsync(0).ConfigureAwait(false))
                return;

            AcquireWakeLock();
            try
            {
                await CycleAsync().ConfigureAwait(false);
            }
            finally
            {
                ReleaseWakeLock();
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
        if (!IsOnline())
            return;

        var family = PlatformServiceLocator.Get<FamilyService>();
        if (family is null)
            return;

        // Mantiene al día la caché de grupos (fin de pausas, grupos nuevos, expulsiones).
        await SharingState.GetSharingGroupsAsync(family, online: true, GroupsMaxAgeOnCycle).ConfigureAwait(false);

        // 1. Posiciones que se quedaron sin enviar.
        var outbox = PlatformServiceLocator.Get<LocationOutbox>();
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
        var sos = PlatformServiceLocator.Get<SosService>();
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

        var push = new PushService().IsConfigured;

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
        if (!push)
        {
            var feed = PlatformServiceLocator.Get<EventFeed>();
            if (feed is not null)
            {
                try
                {
                    var events = await feed.PollAsync().ConfigureAwait(false);
                    if (events.Count > 0)
                    {
                        var notifier = PlatformServiceLocator.Get<INotifier>() ?? new Notifier();
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

    // ==================================================================================
    //  Utilidades
    // ==================================================================================

    private int ReadBattery()
    {
        try
        {
            var battery = (BatteryManager?)GetSystemService(BatteryService);
            var level = battery?.GetIntProperty((int)BatteryProperty.Capacity) ?? -1;
            return level is >= 0 and <= 100 ? level : -1;
        }
        catch
        {
            return -1;
        }
    }

    private bool IsOnline()
    {
        try
        {
            var connectivity = (ConnectivityManager?)GetSystemService(ConnectivityService);
            if (connectivity is null)
                return true;   // Sin forma de saberlo: se intenta y, si falla, a la cola.

            if (OperatingSystem.IsAndroidVersionAtLeast(23))
            {
                var network = connectivity.ActiveNetwork;
                var caps = network is null ? null : connectivity.GetNetworkCapabilities(network);
                return caps?.HasCapability(NetCapability.Internet) == true;
            }

#pragma warning disable CA1422 // Solo para Android anterior a 6.
            return connectivity.ActiveNetworkInfo?.IsConnected == true;
#pragma warning restore CA1422
        }
        catch
        {
            return true;
        }
    }

    private void AcquireWakeLock()
    {
        try
        {
            if (_wakeLock is null)
            {
                var power = (PowerManager?)GetSystemService(PowerService);
                _wakeLock = power?.NewWakeLock(WakeLockFlags.Partial, "FamilyTogether:location");
                _wakeLock?.SetReferenceCounted(false);
            }

            _wakeLock?.Acquire(WakeLockTimeoutMs);
        }
        catch (Exception ex)
        {
            NativeLog.Warn("No se pudo coger el bloqueo de CPU.", ex);
        }
    }

    private void ReleaseWakeLock()
    {
        try
        {
            if (_wakeLock is { IsHeld: true })
                _wakeLock.Release();
        }
        catch
        {
            // Ya caducado: no hay nada que soltar.
        }
    }
}
