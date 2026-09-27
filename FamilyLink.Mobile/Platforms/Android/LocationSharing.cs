using Android;
using Android.Content;
using Android.Content.PM;
using Android.Locations;
using Android.OS;
using Android.Provider;
using AndroidX.Core.Content;
using FamilyLink.Mobile.Services;
using AndroidLocation = Android.Locations.Location;
using AndroidUri = Android.Net.Uri;

namespace FamilyLink.Mobile.Platforms.Android;

/// <inheritdoc cref="ILocationSharing"/>
/// <remarks>
/// <para><b>Permisos en dos pasos</b> (la única forma que Android admite desde la 11): primero la
/// ubicación precisa «mientras se usa»; después, por separado, «todo el tiempo». En Android 11+
/// el segundo paso no es un diálogo: el sistema abre la página de permisos de ubicación de la app
/// en Ajustes y devuelve el resultado al volver.</para>
/// <para>Solo con la ubicación aproximada no se puede compartir (todas las lecturas pasan de
/// 25 m): se trata como <see cref="LocationPermissionState.Denied"/> y la interfaz guía a Ajustes.</para>
/// </remarks>
public sealed class LocationSharing : ILocationSharing
{
    /// <summary>Tiempo máximo para una lectura fresca en el SOS.</summary>
    private static readonly TimeSpan FreshFixTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Una lectura más vieja que esto se marca como «última conocida» (stale).</summary>
    private static readonly TimeSpan FreshMaxAge = TimeSpan.FromMinutes(2);

    private static Context AppContext => global::Android.App.Application.Context;

    public bool IsRunning => LocationSharingForegroundService.IsRunning;

    // ==================================================================================
    //  Permisos
    // ==================================================================================

    public Task<LocationPermissionState> CheckPermissionAsync() => Task.FromResult(CurrentState(AppContext));

    public async Task<LocationPermissionState> RequestPermissionAsync()
    {
        try
        {
            // Paso 1: precisa (Android muestra el diálogo con «precisa / aproximada»).
            if (!HasFinePermission(AppContext))
            {
                await MainThread.InvokeOnMainThreadAsync(Permissions.RequestAsync<Permissions.LocationWhenInUse>);
                if (!HasFinePermission(AppContext))
                    return LocationPermissionState.Denied;
            }

            // Paso 2: en segundo plano (Android 10+). En 11+ abre la página de Ajustes.
            if (OperatingSystem.IsAndroidVersionAtLeast(29) && !HasBackgroundPermission(AppContext))
                await MainThread.InvokeOnMainThreadAsync(Permissions.RequestAsync<BackgroundLocationPermission>);
        }
        catch (Exception ex)
        {
            NativeLog.Warn("No se pudo pedir el permiso de ubicación.", ex);
        }

        return CurrentState(AppContext);
    }

    internal static LocationPermissionState CurrentState(Context context)
    {
        if (!HasFinePermission(context))
            return LocationPermissionState.Denied;
        return HasBackgroundPermission(context) ? LocationPermissionState.Always : LocationPermissionState.WhileInUse;
    }

    internal static bool HasFinePermission(Context context) =>
        ContextCompat.CheckSelfPermission(context, Manifest.Permission.AccessFineLocation) == Permission.Granted;

    /// <summary>Antes de Android 10 no existe el permiso aparte: la precisa ya vale en segundo plano.</summary>
    internal static bool HasBackgroundPermission(Context context) =>
        HasFinePermission(context) &&
        (!OperatingSystem.IsAndroidVersionAtLeast(29) ||
         ContextCompat.CheckSelfPermission(context, Manifest.Permission.AccessBackgroundLocation) == Permission.Granted);

    /// <summary>
    /// «Permitir todo el tiempo». MAUI lo pide con <c>LocationAlways</c> junto con la precisa, y
    /// Android 11+ ignora la petición si van juntas: por eso va sola, después del paso 1.
    /// </summary>
    private sealed class BackgroundLocationPermission : Permissions.BasePlatformPermission
    {
        public override (string androidPermission, bool isRuntime)[] RequiredPermissions =>
            OperatingSystem.IsAndroidVersionAtLeast(29)
                ? [(Manifest.Permission.AccessBackgroundLocation, true)]
                : [];
    }

    // ==================================================================================
    //  Servicio
    // ==================================================================================

    public void Start()
    {
        SharingState.Enabled = true;
        SharingState.InvalidateGroups();

        if (!HasFinePermission(AppContext))
        {
            // Queda anotado: en cuanto haya permiso y se abra la app, arranca.
            NativeLog.Warn("Start sin permiso de ubicación: el servicio no se arranca todavía.");
            return;
        }

        LocationSharingForegroundService.StartService(AppContext);
    }

    public void Stop()
    {
        SharingState.Enabled = false;
        LocationSharingForegroundService.StopService(AppContext);
    }

    /// <summary>
    /// Si el usuario compartía y el servicio no está vivo, lo arranca. Lo llama
    /// <see cref="MainActivity"/> al abrirse (app en pantalla: Android lo permite siempre).
    /// </summary>
    internal static void ResumeIfEnabled()
    {
        try
        {
            if (SharingState.Enabled && !LocationSharingForegroundService.IsRunning && HasFinePermission(AppContext))
                LocationSharingForegroundService.StartService(AppContext);
        }
        catch (Exception ex)
        {
            NativeLog.Warn("No se pudo reanudar la compartición al abrir la app.", ex);
        }
    }

    /// <summary>
    /// Avisa de que ha cambiado dónde se comparte (pausa, reanudar, entrar o salir de un grupo):
    /// la siguiente posición vuelve a preguntar al servidor en vez de usar la caché. Conviene que
    /// la interfaz lo llame tras <c>SetPauseAsync</c>, <c>LeaveGroupAsync</c> y al unirse.
    /// </summary>
    public static void NotifySharingChanged() => SharingState.InvalidateGroups();

    // ==================================================================================
    //  Posición para el SOS
    // ==================================================================================

    public async Task<(double Lat, double Lon, double Accuracy, DateTimeOffset At, bool Stale)?> GetCurrentOrLastAsync()
    {
        if (!HasFinePermission(AppContext))
            return LastKnown();

        try
        {
            var request = new GeolocationRequest(GeolocationAccuracy.Best, FreshFixTimeout);
            var fix = await MainThread.InvokeOnMainThreadAsync(() => Geolocation.Default.GetLocationAsync(request))
                .WaitAsync(FreshFixTimeout + TimeSpan.FromSeconds(2))
                .ConfigureAwait(false);

            if (fix is not null)
            {
                var stale = DateTimeOffset.UtcNow - fix.Timestamp > FreshMaxAge;
                return (fix.Latitude, fix.Longitude, fix.Accuracy ?? 0, fix.Timestamp, stale);
            }
        }
        catch (Exception ex)
        {
            // Sin GPS, en interior, tiempo agotado: se usa la última conocida con su hora.
            NativeLog.Info($"SOS sin lectura fresca: {ex.GetType().Name}: {ex.Message}");
        }

        return LastKnown();
    }

    /// <summary>La más reciente entre la del servicio y las últimas conocidas de cada proveedor.</summary>
    private static (double Lat, double Lon, double Accuracy, DateTimeOffset At, bool Stale)? LastKnown()
    {
        AndroidLocation? best = LocationSharingForegroundService.LastFix;

        try
        {
            if (HasFinePermission(AppContext) &&
                AppContext.GetSystemService(Context.LocationService) is LocationManager manager)
            {
                foreach (var provider in manager.GetProviders(false) ?? [])
                {
                    var candidate = manager.GetLastKnownLocation(provider);
                    if (candidate is not null && (best is null || candidate.Time > best.Time))
                        best = candidate;
                }
            }
        }
        catch (Exception ex)
        {
            NativeLog.Warn("No se pudo leer la última ubicación conocida.", ex);
        }

        if (best is null)
            return null;

        return (best.Latitude, best.Longitude, best.HasAccuracy ? best.Accuracy : 0,
            DateTimeOffset.FromUnixTimeMilliseconds(best.Time), true);
    }

    // ==================================================================================
    //  Batería y ajustes
    // ==================================================================================

    public bool IsIgnoringBatteryOptimizations
    {
        get
        {
            try
            {
                if (!OperatingSystem.IsAndroidVersionAtLeast(23))
                    return true;
                var power = (PowerManager?)AppContext.GetSystemService(Context.PowerService);
                return power?.IsIgnoringBatteryOptimizations(AppContext.PackageName) ?? false;
            }
            catch
            {
                return false;
            }
        }
    }

    public void OpenBatteryOptimizationSettings()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(23))
            return;

        // Diálogo directo del sistema «¿Permitir que se ejecute siempre en segundo plano?».
        if (TryStart(new Intent(Settings.ActionRequestIgnoreBatteryOptimizations,
                AndroidUri.Parse("package:" + AppContext.PackageName))))
            return;

        // Algunos fabricantes lo quitan: la lista general de optimización.
        if (TryStart(new Intent(Settings.ActionIgnoreBatteryOptimizationSettings)))
            return;

        OpenAppSettings();
    }

    public void OpenAppSettings()
    {
        if (!TryStart(new Intent(Settings.ActionApplicationDetailsSettings,
                AndroidUri.Parse("package:" + AppContext.PackageName))))
            TryStart(new Intent(Settings.ActionSettings));
    }

    // ==================================================================================
    //  Autoinicio del fabricante
    // ==================================================================================

    private enum Vendor { Other, Xiaomi, Huawei, Oppo, Vivo, Samsung, Asus, Meizu }

    private static Vendor CurrentVendor
    {
        get
        {
            var text = $"{Build.Manufacturer} {Build.Brand}".ToLowerInvariant();
            if (text.Contains("xiaomi") || text.Contains("redmi") || text.Contains("poco")) return Vendor.Xiaomi;
            if (text.Contains("huawei") || text.Contains("honor")) return Vendor.Huawei;
            if (text.Contains("oppo") || text.Contains("realme") || text.Contains("oneplus")) return Vendor.Oppo;
            if (text.Contains("vivo") || text.Contains("iqoo")) return Vendor.Vivo;
            if (text.Contains("samsung")) return Vendor.Samsung;
            if (text.Contains("asus")) return Vendor.Asus;
            if (text.Contains("meizu")) return Vendor.Meizu;
            return Vendor.Other;
        }
    }

    public string? ManufacturerAutostartHint => CurrentVendor switch
    {
        Vendor.Xiaomi => AndroidTexts.Get("AutostartXiaomi"),
        Vendor.Huawei => AndroidTexts.Get("AutostartHuawei"),
        Vendor.Oppo => AndroidTexts.Get("AutostartOppo"),
        Vendor.Vivo => AndroidTexts.Get("AutostartVivo"),
        Vendor.Samsung => AndroidTexts.Get("AutostartSamsung"),
        Vendor.Asus => AndroidTexts.Get("AutostartAsus"),
        Vendor.Meizu => AndroidTexts.Get("AutostartMeizu"),
        _ => null,
    };

    /// <summary>
    /// Pantallas conocidas de autoinicio / ahorro de batería de cada capa. Cambian entre versiones,
    /// así que se prueban en orden; si ninguna existe (o no es accesible), se abren los ajustes de la
    /// app. Se prueba lanzando y capturando el error, sin consultar el gestor de paquetes: así no
    /// hace falta declarar esos paquetes en <c>&lt;queries&gt;</c>.
    /// </summary>
    private static readonly Dictionary<Vendor, (string Package, string Activity)[]> AutostartScreens = new()
    {
        [Vendor.Xiaomi] =
        [
            ("com.miui.securitycenter", "com.miui.permcenter.autostart.AutoStartManagementActivity"),
            ("com.miui.securitycenter", "com.miui.powercenter.PowerSettings"),
        ],
        [Vendor.Huawei] =
        [
            ("com.huawei.systemmanager", "com.huawei.systemmanager.startupmgr.ui.StartupNormalAppListActivity"),
            ("com.huawei.systemmanager", "com.huawei.systemmanager.appcontrol.activity.StartupAppControlActivity"),
            ("com.huawei.systemmanager", "com.huawei.systemmanager.optimize.process.ProtectActivity"),
            ("com.hihonor.systemmanager", "com.hihonor.systemmanager.startupmgr.ui.StartupNormalAppListActivity"),
        ],
        [Vendor.Oppo] =
        [
            ("com.coloros.safecenter", "com.coloros.safecenter.permission.startup.StartupAppListActivity"),
            ("com.coloros.safecenter", "com.coloros.safecenter.startupapp.StartupAppListActivity"),
            ("com.oppo.safe", "com.oppo.safe.permission.startup.StartupAppListActivity"),
            ("com.oneplus.security", "com.oneplus.security.chainlaunch.view.ChainLaunchAppListActivity"),
        ],
        [Vendor.Vivo] =
        [
            ("com.vivo.permissionmanager", "com.vivo.permissionmanager.activity.BgStartUpManagerActivity"),
            ("com.iqoo.secure", "com.iqoo.secure.ui.phoneoptimize.AddWhiteListActivity"),
            ("com.iqoo.secure", "com.iqoo.secure.ui.phoneoptimize.BgStartUpManager"),
        ],
        [Vendor.Samsung] =
        [
            ("com.samsung.android.lool", "com.samsung.android.sm.battery.ui.BatteryActivity"),
            ("com.samsung.android.sm", "com.samsung.android.sm.ui.battery.BatteryActivity"),
        ],
        [Vendor.Asus] =
        [
            ("com.asus.mobilemanager", "com.asus.mobilemanager.autostart.AutoStartActivity"),
            ("com.asus.mobilemanager", "com.asus.mobilemanager.entry.FunctionActivity"),
        ],
        [Vendor.Meizu] =
        [
            ("com.meizu.safe", "com.meizu.safe.permission.SmartBGActivity"),
            ("com.meizu.safe", "com.meizu.safe.permission.PermissionMainActivity"),
        ],
    };

    public void OpenManufacturerAutostartSettings()
    {
        if (AutostartScreens.TryGetValue(CurrentVendor, out var screens))
        {
            foreach (var (package, activity) in screens)
            {
                var intent = new Intent();
                intent.SetComponent(new ComponentName(package, activity));
                if (TryStart(intent))
                    return;
            }
        }

        OpenAppSettings();
    }

    // ==================================================================================

    /// <summary>
    /// Abre una pantalla desde la actividad visible o, si no la hay, desde el contexto de la app en
    /// una tarea nueva. Devuelve falso si no existe o no es accesible.
    /// </summary>
    private static bool TryStart(Intent intent)
    {
        try
        {
            var activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity;
            if (activity is not null)
            {
                activity.StartActivity(intent);
            }
            else
            {
                intent.AddFlags(ActivityFlags.NewTask);
                AppContext.StartActivity(intent);
            }
            return true;
        }
        catch (ActivityNotFoundException)
        {
            return false;
        }
        catch (Java.Lang.SecurityException)
        {
            // Pantalla del fabricante no exportada en esta versión.
            return false;
        }
        catch (Exception ex)
        {
            NativeLog.Warn($"No se pudo abrir {intent.Component?.ClassName ?? intent.Action}.", ex);
            return false;
        }
    }
}
