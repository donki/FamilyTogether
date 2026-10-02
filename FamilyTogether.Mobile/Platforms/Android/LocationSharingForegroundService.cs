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
/// <para><b>Sin Google Play Services.</b> <see cref="LocationManager"/> del sistema con
/// <c>GPS_PROVIDER</c> y <c>NETWORK_PROVIDER</c>, cada uno con <c>minDistance = 25 m</c> y
/// <c>minTime = 30 s</c>: el sistema no despierta a la app mientras el móvil no se mueve, y moviéndose
/// no entrega más de una lectura cada medio minuto por proveedor (batería, SC-005).</para>
/// <para>Solo coge la CPU (bloqueo parcial con tiempo máximo) mientras procesa, no de forma
/// continua como Hiker, que solo graba durante una ruta.</para>
/// </remarks>
[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeLocation)]
public class LocationSharingForegroundService : Service, ILocationListener, ISharingDevice
{
    /// <summary>Intervalo mínimo entre lecturas de un mismo proveedor.</summary>
    private const long MinTimeMs = 30_000;

    /// <summary>Cada cuánto se reintenta lo pendiente y se consultan los eventos (ARQUITECTURA §7).</summary>
    private static readonly TimeSpan CycleEvery = TimeSpan.FromSeconds(60);

    /// <summary>Tiempo máximo que se retiene la CPU por cada trabajo.</summary>
    private const long WakeLockTimeoutMs = 60_000;

    private static volatile bool _running;

    /// <summary>Verdadero mientras el servicio está vivo y en primer plano.</summary>
    public static bool IsRunning => _running;

    private readonly SharingEngine _engine;
    private LocationManager? _locationManager;
    private PowerManager.WakeLock? _wakeLock;
    private Timer? _timer;
    private bool _listening;
    private readonly SignificantMotion _motion = new();

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
        StartListening();
        StartCycle();

        // Sticky: si Android mata el proceso por memoria, recrea el servicio y se sigue compartiendo.
        return StartCommandResult.Sticky;
    }

    public override void OnDestroy()
    {
        _running = false;
        _engine.Running = false;

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
                _locationManager.RequestLocationUpdates(provider, MinTimeMs, SharingEngine.ThresholdMeters, this, Looper.MainLooper);
                _listening = true;
            }

            if (!_listening)
                NativeLog.Warn("Ni GPS ni red disponibles en este dispositivo.");
            else
            {
                _motion.Moved -= OnMoved;
                _motion.Moved += OnMoved;
                _motion.Start(this);
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

    public void OnLocationChanged(AndroidLocation location)
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
    private void OnMoved(DateTimeOffset at) => _ = Task.Run(() => _engine.PromoteStillReadingsAsync(at));

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
