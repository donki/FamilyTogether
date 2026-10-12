using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Locations;
using Android.Net;
using Android.OS;
using AndroidX.Core.App;
using FamilyTogether.Core;
using FamilyTogether.Mobile.Services;
using FamilyTogether.Mobile.Services.Native;
using AndroidLocation = Android.Locations.Location;

namespace FamilyTogether.Mobile.Platforms.Android;

/// <summary>
/// Servicio en primer plano de tipo <c>location</c> que comparte la posición con los grupos
/// (ARQUITECTURA §8, Mobile §10). Es el de Hiker, pero con un umbral de 25 m en vez de grabación
/// continua. Lo que se hace con cada lectura y el ciclo de 60 s están en <see cref="SharingEngine"/>
/// (sin Android, probado); aquí solo queda escuchar al sistema y darle lo que pide.
/// </summary>
/// <remarks>
/// <para><b>Sin Google Play Services.</b> <see cref="LocationManager"/> del sistema, siempre con
/// <c>minDistance = 25 m</c>. Qué proveedores y cada cuánto lo decide <see cref="LocationPlan"/>
/// (2026-10-06, batería, SC-005): moviéndose, <c>GPS_PROVIDER</c> y <c>NETWORK_PROVIDER</c> cada
/// 30 s; quieto (el sensor de movimiento significativo no ha saltado en 5 min), el GPS apagado y
/// solo la red cada 5 min y la pasiva. El <c>minDistance</c> no apaga el GPS: solo filtra lo que se
/// entrega; por eso hay que quitar la petición del GPS cuando no hace falta.</para>
/// <para>El paso a quieto lo comprueba una alarma de <see cref="AlarmManager"/> (que despierta
/// aunque el móvil duerma, a diferencia del temporizador del ciclo, que se para con la CPU): si no,
/// con el móvil dormido en un cajón, el GPS se quedaría buscando satélites.</para>
/// <para><b>Latido</b> (2026-10-12, <see cref="SendPolicy"/>): la posición sale al moverse o, como
/// mucho, cada hora. Con el móvil quieto del todo el sistema no entrega lecturas (el
/// <c>minDistance</c>), así que una alarma <c>setAndAllowWhileIdle</c> (<see cref="HeartbeatReceiver"/>,
/// que también salta en Doze, sin permiso de alarmas exactas) despierta al servicio a la hora de la
/// última enviada; si toca, pide una lectura suelta (<see cref="RequestFreshFix"/>: la red, o el GPS
/// con tiempo máximo) y el motor la envía como latido.</para>
/// <para>Solo coge la CPU (bloqueo parcial con tiempo máximo) mientras procesa, no de forma
/// continua como Hiker, que solo graba durante una ruta.</para>
/// </remarks>
[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeLocation)]
public class LocationSharingForegroundService : Service, ILocationListener, ISharingDevice, AlarmManager.IOnAlarmListener
{
    /// <summary>Cada cuánto se reintenta lo pendiente y se consultan los eventos (ARQUITECTURA §7).</summary>
    private static readonly TimeSpan CycleEvery = TimeSpan.FromSeconds(60);

    /// <summary>Tiempo máximo que se retiene la CPU por cada trabajo.</summary>
    private const long WakeLockTimeoutMs = 60_000;

    private static volatile bool _running;

    /// <summary>El servicio vivo, para la alarma del latido (<see cref="HeartbeatReceiver"/>).</summary>
    private static LocationSharingForegroundService? _instance;

    /// <summary>Código del <see cref="PendingIntent"/> de la alarma del latido.</summary>
    private const int HeartbeatRequestCode = 7301;

    /// <summary>Verdadero mientras el servicio está vivo y en primer plano.</summary>
    public static bool IsRunning => _running;

    private readonly SharingEngine _engine;
    private LocationManager? _locationManager;
    private PowerManager.WakeLock? _wakeLock;
    private Timer? _timer;
    private bool _listening;
    private readonly SignificantMotion _motion = new();
    private readonly object _requestLock = new();
    private IList<string> _available = [];

    public LocationSharingForegroundService() => _engine = new SharingEngine(this);

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
        _engine.Running = true;
        _instance = this;
        StartListening();
        StartCycle();
        ScheduleHeartbeat();

        // Sticky: si Android mata el proceso por memoria, recrea el servicio y se sigue compartiendo.
        return StartCommandResult.Sticky;
    }

    public override void OnDestroy()
    {
        _running = false;
        _engine.Running = false;
        if (ReferenceEquals(_instance, this))
            _instance = null;

        try
        {
            _timer?.Dispose();
            _timer = null;
            CancelStillCheck();
            CancelHeartbeat();
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
            var builder = new NotificationCompat.Builder(this, NotificationRules.ChannelService);
            builder.SetContentTitle(NotificationRules.Text("ServiceNotifTitle", "Sharing your location"));
            builder.SetContentText(NotificationRules.Text("ServiceNotifText", "Your groups can see where you are."));
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
                StartForeground(NotificationRules.ServiceNotificationId, notification, ForegroundService.TypeLocation);
            else
                StartForeground(NotificationRules.ServiceNotificationId, notification);

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

            _available = _locationManager.GetProviders(false) ?? [];

            // Se arranca moviéndose: la primera posición buena sale con el GPS (SC-002); si en 5 min
            // nada dice que se mueve, LocationPlan lo pasa a quieto y se apaga el GPS.
            _engine.BeginTracking(DateTimeOffset.UtcNow);
            Register(TrackingMode.Moving);

            if (!_listening)
                NativeLog.Warn("Ni GPS ni red disponibles en este dispositivo.");
            else
            {
                _motion.Moved -= OnMoved;
                _motion.Moved += OnMoved;
                _motion.Start(this);
                ScheduleStillCheck();
            }
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

        _motion.Moved -= OnMoved;
        _motion.Stop();
        _engine.ClearStillReadings();
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

    /// <summary>
    /// Pide al sistema lo del modo (<see cref="LocationPlan.Requests"/>), quitando antes lo de antes.
    /// Se registra aunque el proveedor esté apagado: si el usuario lo enciende después, Android
    /// empieza a entregar sin tener que reiniciar el servicio.
    /// </summary>
    private void Register(TrackingMode mode)
    {
        lock (_requestLock)
        {
            if (_locationManager is null)
                return;

            _locationManager.RemoveUpdates(this);
            var any = false;
            foreach (var request in LocationPlan.Requests(mode, [.. _available]))
            {
                _locationManager.RequestLocationUpdates(request.Provider, (long)request.MinTime.TotalMilliseconds,
                    request.MinDistanceMeters, this, Looper.MainLooper);
                any = true;
            }

            _listening = any;
        }
    }

    public void ApplyTracking(TrackingMode mode)
    {
        if (!_running)
            return;

        try
        {
            Register(mode);
        }
        catch (Java.Lang.SecurityException ex)
        {
            NativeLog.Warn("Servicio sin permiso de ubicación; se para.", ex);
            SharingState.Enabled = false;
            StopSelf();
            return;
        }

        if (mode == TrackingMode.Moving)
            ScheduleStillCheck();
        else
            CancelStillCheck();
    }

    // ==================================================================================
    //  Comprobación de «quieto» (alarma que despierta)
    // ==================================================================================

    /// <summary>
    /// Programa una alarma para cuando el móvil pasaría a quieto. Con el mismo oyente, la nueva
    /// sustituye a la anterior. Sin sensor de movimiento no hay paso a quieto: no se programa.
    /// Inexacta y sin «AllowWhileIdle»: en Doze espera a la ventana de mantenimiento, que basta.
    /// </summary>
    private void ScheduleStillCheck()
    {
        try
        {
            if (!_motion.Available || GetSystemService(AlarmService) is not AlarmManager alarms)
                return;

            var wait = _engine.StillAt - DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
            if (wait < TimeSpan.FromSeconds(5))
                wait = TimeSpan.FromSeconds(5);

            alarms.Set(AlarmType.ElapsedRealtimeWakeup, SystemClock.ElapsedRealtime() + (long)wait.TotalMilliseconds,
                "FamilyTogether:still", this, null);
        }
        catch (Exception ex)
        {
            NativeLog.Warn("No se pudo programar la comprobación de móvil quieto.", ex);
        }
    }

    private void CancelStillCheck()
    {
        try
        {
            if (GetSystemService(AlarmService) is AlarmManager alarms)
                alarms.Cancel(this);
        }
        catch (Exception)
        {
            // Nada que cancelar.
        }
    }

    /// <summary>La alarma: si sigue moviéndose (otro aviso o velocidad), se vuelve a programar.</summary>
    public void OnAlarm()
    {
        if (!_running)
            return;

        if (_engine.UpdateTracking(DateTimeOffset.UtcNow) == TrackingMode.Moving)
            ScheduleStillCheck();
    }

    // ==================================================================================
    //  Latido de cada hora (alarma que despierta también en Doze)
    // ==================================================================================

    /// <summary>
    /// Programa la alarma del latido para <see cref="SharingEngine.NextHeartbeatCheck"/>. Inexacta y
    /// con «AllowWhileIdle»: en Doze salta igual (como mucho una cada 9 min por app, de sobra) y no
    /// necesita el permiso de alarmas exactas. La nueva sustituye a la anterior (mismo código).
    /// </summary>
    private void ScheduleHeartbeat()
    {
        try
        {
            if (GetSystemService(AlarmService) is not AlarmManager alarms || HeartbeatIntent() is not { } pending)
                return;

            var now = DateTimeOffset.UtcNow;
            var wait = _engine.NextHeartbeatCheck(now) - now;
            if (wait < TimeSpan.FromSeconds(30))
                wait = TimeSpan.FromSeconds(30);

            alarms.SetAndAllowWhileIdle(AlarmType.ElapsedRealtimeWakeup,
                SystemClock.ElapsedRealtime() + (long)wait.TotalMilliseconds, pending);
        }
        catch (Exception ex)
        {
            NativeLog.Warn("No se pudo programar el latido de la posición.", ex);
        }
    }

    private void CancelHeartbeat()
    {
        try
        {
            if (GetSystemService(AlarmService) is AlarmManager alarms && HeartbeatIntent() is { } pending)
                alarms.Cancel(pending);
        }
        catch (Exception)
        {
            // Nada que cancelar.
        }
    }

    private PendingIntent? HeartbeatIntent() =>
        PendingIntent.GetBroadcast(this, HeartbeatRequestCode, new Intent(this, typeof(HeartbeatReceiver)),
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

    /// <summary>La alarma del latido (<see cref="HeartbeatReceiver"/>): mira si toca y la reprograma.</summary>
    internal static void OnHeartbeatAlarm()
    {
        var service = _instance;
        if (service is null || !_running)
            return;

        service._engine.CheckHeartbeat(DateTimeOffset.UtcNow);
        service.ScheduleHeartbeat();
    }

    /// <summary>
    /// Pide una lectura suelta (<see cref="LocationPlan.FreshFixProvider"/>), con tiempo máximo
    /// <see cref="LocationPlan.FreshFixTimeout"/>. Antes mira la última conocida de cada proveedor:
    /// si es reciente (de otra app, por ejemplo), vale y no se enciende nada.
    /// </summary>
    public void RequestFreshFix()
    {
        var manager = _locationManager;
        if (manager is null || !_running)
            return;

        try
        {
            AcquireWakeLock();

            // Una reciente ya calculada por el sistema: gratis.
            var now = DateTimeOffset.UtcNow;
            AndroidLocation? recent = null;
            foreach (var provider in _available)
            {
                var known = manager.GetLastKnownLocation(provider);
                if (known is null || now - DateTimeOffset.FromUnixTimeMilliseconds(known.Time) > SharingEngine.MaxReadingAge)
                    continue;
                if (recent is null || known.Time > recent.Time)
                    recent = known;
            }
            if (recent is not null)
            {
                Deliver(recent);
                return;
            }

            var enabled = _available.Where(p =>
            {
                try { return manager.IsProviderEnabled(p); }
                catch { return false; }
            }).ToList();
            var chosen = LocationPlan.FreshFixProvider(enabled);
            if (chosen is null)
            {
                NativeLog.Warn("Latido: ni la red ni el GPS están encendidos; no se puede pedir una lectura.");
                ReleaseWakeLock();
                return;
            }

            var handler = new Handler(Looper.MainLooper!);
            var timeout = (long)LocationPlan.FreshFixTimeout.TotalMilliseconds;
            if (OperatingSystem.IsAndroidVersionAtLeast(30))
            {
                var cancel = new CancellationSignal();
                manager.GetCurrentLocation(chosen, cancel, MainExecutor!, new FreshFixConsumer(this));
                handler.PostDelayed(() => cancel.Cancel(), timeout);
            }
            else
            {
                var once = new FreshFixListener(this);
#pragma warning disable CS0618, CA1422 // requestSingleUpdate: obsoleto en Android 11, aquí solo para 7-10.
                manager.RequestSingleUpdate(chosen, once, Looper.MainLooper);
#pragma warning restore CS0618, CA1422
                handler.PostDelayed(() =>
                {
                    try { manager.RemoveUpdates(once); }
                    catch (Exception) { /* Ya entregada o sin permiso: nada que quitar. */ }
                }, timeout);
            }
        }
        catch (Java.Lang.SecurityException ex)
        {
            NativeLog.Warn("Latido: sin permiso de ubicación.", ex);
            ReleaseWakeLock();
        }
        catch (Exception ex)
        {
            NativeLog.Error("Latido: no se pudo pedir una lectura.", ex);
            ReleaseWakeLock();
        }
    }

    /// <summary>La lectura suelta del latido (Android 11+).</summary>
    private sealed class FreshFixConsumer(LocationSharingForegroundService service) : Java.Lang.Object, Java.Util.Functions.IConsumer
    {
        public void Accept(Java.Lang.Object? value)
        {
            if (value is AndroidLocation location)
                service.Deliver(location);
            else
            {
                NativeLog.Info("Latido: no ha llegado ninguna lectura a tiempo; se reintentará.");
                service.ReleaseWakeLock();
            }
        }
    }

    /// <summary>La lectura suelta del latido (Android 7-10).</summary>
    private sealed class FreshFixListener(LocationSharingForegroundService service) : Java.Lang.Object, ILocationListener
    {
        public void OnLocationChanged(AndroidLocation location) => service.Deliver(location);

        public void OnProviderDisabled(string provider) { }

        public void OnProviderEnabled(string provider) { }

        public void OnStatusChanged(string? provider, [global::Android.Runtime.GeneratedEnum] Availability status, Bundle? extras) { }
    }

    public void OnLocationChanged(AndroidLocation location) => Deliver(location);

    private void Deliver(AndroidLocation location)
    {
        // Llega en el hilo principal: el trabajo (red, SQLite, cifrado) se hace fuera.
        var reading = new LocationReading(location.Provider, location.Latitude, location.Longitude,
            location.HasAccuracy ? location.Accuracy : null, location.HasSpeed ? location.Speed : null,
            DateTimeOffset.FromUnixTimeMilliseconds(location.Time));
        _ = Task.Run(() => _engine.ProcessReadingAsync(reading));
    }

    public void OnProviderDisabled(string provider) => NativeLog.Info($"Proveedor de ubicación apagado: {provider}");

    public void OnProviderEnabled(string provider) => NativeLog.Info($"Proveedor de ubicación encendido: {provider}");

    public void OnStatusChanged(string? provider, [global::Android.Runtime.GeneratedEnum] Availability status, Bundle? extras) { }

    /// <summary>El sensor ha saltado: lo de antes puede ser el principio del recorrido.</summary>
    private void OnMoved(DateTimeOffset at)
    {
        // Lo primero, encender el GPS si estaba quieto (el aviso del sensor ya llega con retraso).
        _engine.UpdateTracking(at);
        _ = Task.Run(() => _engine.PromoteStillReadingsAsync(at));
    }

    // ==================================================================================
    //  Ciclo de 60 s
    // ==================================================================================

    private void StartCycle()
    {
        if (_timer is not null)
            return;

        // Primera pasada a los pocos segundos: recoge lo que quedara pendiente de antes.
        _timer = new Timer(_ => _ = Task.Run(_engine.RunCycleAsync), null, TimeSpan.FromSeconds(5), CycleEvery);
    }

    // ==================================================================================
    //  Lo que pide el motor (ISharingDevice)
    // ==================================================================================

    public bool MotionAvailable => _motion.Available;

    public DateTimeOffset? LastMotion => _motion.LastMotion;

    public bool PushConfigured => new PushService().IsConfigured;

    public T? Get<T>() where T : class => PlatformServiceLocator.Get<T>();

    public INotifier FallbackNotifier() => new Notifier();

    public double DistanceMeters(double lat1, double lon1, double lat2, double lon2)
    {
        var results = new float[1];
        AndroidLocation.DistanceBetween(lat1, lon1, lat2, lon2, results);
        return results[0];
    }

    public int ReadBattery()
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

    public bool IsOnline()
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

    public void AcquireWakeLock()
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

    public void ReleaseWakeLock()
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
