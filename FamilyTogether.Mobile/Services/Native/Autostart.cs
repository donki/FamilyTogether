namespace FamilyTogether.Mobile.Services.Native;

/// <summary>Capa del fabricante, para la pantalla de inicio automático.</summary>
public enum Vendor { Other, Xiaomi, Huawei, Oppo, Vivo, Samsung, Asus, Meizu }

/// <summary>
/// Inicio automático de cada fabricante (lo usa <c>LocationSharing</c>): quién es el fabricante, qué
/// texto se enseña y qué pantallas se prueban para abrirlo.
/// </summary>
public static class Autostart
{
    /// <summary>
    /// El fabricante, por <c>Build.Manufacturer</c> y, si no dice nada conocido, por <c>Build.Brand</c>.
    /// Un Android sobre x86 es un emulador (MuMu, por ejemplo, se declara Samsung de arriba abajo):
    /// no se sabe qué capa lleva y se trata como desconocido.
    /// </summary>
    public static Vendor Detect(string? manufacturer, string? brand, string? firstAbi)
    {
        if ((firstAbi ?? string.Empty).StartsWith("x86", StringComparison.OrdinalIgnoreCase))
            return Vendor.Other;

        var byManufacturer = VendorOf(manufacturer);
        return byManufacturer != Vendor.Other ? byManufacturer : VendorOf(brand);
    }

    public static Vendor VendorOf(string? value)
    {
        var text = (value ?? string.Empty).ToLowerInvariant();
        if (text.Contains("xiaomi") || text.Contains("redmi") || text.Contains("poco")) return Vendor.Xiaomi;
        if (text.Contains("huawei") || text.Contains("honor")) return Vendor.Huawei;
        if (text.Contains("oppo") || text.Contains("realme") || text.Contains("oneplus")) return Vendor.Oppo;
        if (text.Contains("vivo") || text.Contains("iqoo")) return Vendor.Vivo;
        if (text.Contains("samsung")) return Vendor.Samsung;
        if (text.Contains("asus")) return Vendor.Asus;
        if (text.Contains("meizu")) return Vendor.Meizu;
        return Vendor.Other;
    }

    /// <summary>
    /// Solo Xiaomi, Redmi y POCO tienen texto propio (constitución General §6.13: no se nombran otros
    /// fabricantes). Los demás, conocidos o no, reciben el genérico; el botón abre la pantalla del
    /// fabricante si se conoce y, si no, los ajustes de la app.
    /// </summary>
    public static string Hint(Vendor vendor) =>
        AndroidTexts.Get(vendor == Vendor.Xiaomi ? "AutostartXiaomi" : "AutostartGeneric");

    /// <summary>
    /// Pantallas conocidas de autoinicio / ahorro de batería de cada capa. Cambian entre versiones,
    /// así que se prueban en orden; si ninguna existe (o no es accesible), se abren los ajustes de la
    /// app. Se prueba lanzando y capturando el error, sin consultar el gestor de paquetes: así no
    /// hace falta declarar esos paquetes en <c>&lt;queries&gt;</c>.
    /// </summary>
    public static IReadOnlyList<(string Package, string Activity)> Screens(Vendor vendor) =>
        AutostartScreens.TryGetValue(vendor, out var screens) ? screens : [];

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

    /// <summary>
    /// Prueba las pantallas del fabricante en orden con <paramref name="tryStart"/> y, si ninguna
    /// abre, los ajustes de la app.
    /// </summary>
    public static void Open(Vendor vendor, Func<string, string, bool> tryStart, Action openAppSettings)
    {
        foreach (var (package, activity) in Screens(vendor))
        {
            if (tryStart(package, activity))
                return;
        }

        openAppSettings();
    }

    /// <summary>
    /// Estado del permiso con lo que dice el sistema. Solo con la ubicación aproximada no se puede
    /// compartir (todas las lecturas pasan de 25 m): se trata como denegado.
    /// </summary>
    public static LocationPermissionState PermissionState(bool fine, bool background) =>
        !fine ? LocationPermissionState.Denied
        : background ? LocationPermissionState.Always
        : LocationPermissionState.WhileInUse;

    /// <summary>
    /// La más reciente entre la última del servicio y las últimas conocidas de cada proveedor, como
    /// posición «vieja» para el SOS.
    /// </summary>
    public static (double Lat, double Lon, double Accuracy, DateTimeOffset At, bool Stale)? LastKnown(
        LocationReading? fromService, IEnumerable<LocationReading?> fromProviders)
    {
        var best = fromService;
        foreach (var candidate in fromProviders)
        {
            if (candidate is not null && (best is null || candidate.At > best.At))
                best = candidate;
        }

        return best is null ? null : (best.Lat, best.Lon, best.Accuracy ?? 0, best.At, true);
    }
}
