using Android.App;
using Android.Content;
using Android.OS;
using Android.Runtime;
using FamilyTogether.Mobile.Services.Native;

namespace FamilyTogether.Mobile.Platforms.Android;

/// <summary>
/// Lo que Android (o el fabricante) hace con la app en segundo plano y puede cortar la posición con
/// la pantalla apagada aunque el servicio esté en primer plano.
/// </summary>
/// <param name="IgnoringBatteryOptimizations">Excluida de la optimización de batería (Doze no la retiene).</param>
/// <param name="BackgroundRestricted">
/// Ajustes › Apps › Batería «Restringida» (Android 9+). MIUI/HyperOS lo pone así con su ahorro de
/// batería por app: entonces Android puede parar el servicio y dejar de entregar posiciones.
/// </param>
/// <param name="LocationOffWhenScreenOff">
/// El ahorro de batería del sistema está encendido y su modo de ubicación apaga el GPS (o toda la
/// ubicación, o la limita) con la pantalla apagada (<c>PowerManager.getLocationPowerSaveMode</c>).
/// </param>
internal sealed record BackgroundLimits(bool IgnoringBatteryOptimizations, bool BackgroundRestricted, bool LocationOffWhenScreenOff)
{
    /// <summary><c>LOCATION_MODE_NO_CHANGE</c>: el ahorro de batería no toca la ubicación.</summary>
    private const int LocationModeNoChange = 0;

    /// <summary>Todo bien: nada de esto corta la posición.</summary>
    public bool AllClear => IgnoringBatteryOptimizations && !BackgroundRestricted && !LocationOffWhenScreenOff;

    public static BackgroundLimits Read(Context context)
    {
        var power = (PowerManager?)context.GetSystemService(Context.PowerService);

        var ignoring = true;
        if (OperatingSystem.IsAndroidVersionAtLeast(23))
            ignoring = Safe(() => power?.IsIgnoringBatteryOptimizations(context.PackageName) ?? false, false);

        var restricted = false;
        if (OperatingSystem.IsAndroidVersionAtLeast(28))
        {
            try { restricted = IsBackgroundRestricted(context); }
            catch (Exception ex) { NativeLog.Warn("No se pudo leer la restricción de segundo plano.", ex); }
        }

        var locationOff = false;
        if (OperatingSystem.IsAndroidVersionAtLeast(28))
            locationOff = Safe(() => power is { IsPowerSaveMode: true } && LocationPowerSaveMode(power) != LocationModeNoChange, false);

        return new BackgroundLimits(ignoring, restricted, locationOff);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("android28.0")]
    private static bool IsBackgroundRestricted(Context context) =>
        ((ActivityManager?)context.GetSystemService(Context.ActivityService))?.IsBackgroundRestricted ?? false;

    /// <summary><c>PowerManager.getLocationPowerSaveMode()</c> (Android 9+): el binding de .NET no lo trae.</summary>
    private static int LocationPowerSaveMode(PowerManager power)
    {
        var cls = JNIEnv.GetObjectClass(power.Handle);
        try
        {
            var method = JNIEnv.GetMethodID(cls, "getLocationPowerSaveMode", "()I");
            return JNIEnv.CallIntMethod(power.Handle, method);
        }
        finally
        {
            JNIEnv.DeleteLocalRef(cls);
        }
    }

    private static bool Safe(Func<bool> read, bool fallback)
    {
        try
        {
            return read();
        }
        catch (Exception ex)
        {
            NativeLog.Warn("No se pudo leer un límite de segundo plano.", ex);
            return fallback;
        }
    }
}
