

namespace FamilyTogether.App.Services;

public class PermissionService
{
    public async Task<bool> RequestLocationPermissionsAsync()
    {
        try
        {
            // Solicitar permisos de ubicación básicos
            var status = await Permissions.RequestAsync<Permissions.LocationWhenInUse>();
            if (status != PermissionStatus.Granted)
            {
                return false;
            }

            // Solicitar permisos de ubicación de alta precisión
            var preciseStatus = await Permissions.RequestAsync<Permissions.LocationAlways>();
            
            return preciseStatus == PermissionStatus.Granted;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error requesting location permissions: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> CheckLocationPermissionsAsync()
    {
        try
        {
            var status = await Permissions.CheckStatusAsync<Permissions.LocationWhenInUse>();
            var alwaysStatus = await Permissions.CheckStatusAsync<Permissions.LocationAlways>();
            
            return status == PermissionStatus.Granted && alwaysStatus == PermissionStatus.Granted;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error checking location permissions: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> RequestBatteryOptimizationExemptionAsync()
    {
        try
        {
#if ANDROID
            var context = Platform.CurrentActivity ?? Android.App.Application.Context;
            var powerManager = context.GetSystemService(Android.Content.Context.PowerService) as Android.OS.PowerManager;
            
            if (powerManager != null && !powerManager.IsIgnoringBatteryOptimizations(context.PackageName))
            {
                var intent = new Android.Content.Intent();
                intent.SetAction(Android.Provider.Settings.ActionRequestIgnoreBatteryOptimizations);
                intent.SetData(Android.Net.Uri.Parse($"package:{context.PackageName}"));
                intent.SetFlags(Android.Content.ActivityFlags.NewTask);
                
                if (Platform.CurrentActivity != null)
                {
                    Platform.CurrentActivity.StartActivity(intent);
                    return true;
                }
            }
#endif
            return false;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error requesting battery optimization exemption: {ex.Message}");
            return false;
        }
    }

    public bool IsBatteryOptimizationIgnored()
    {
        try
        {
#if ANDROID
            var context = Platform.CurrentActivity ?? Android.App.Application.Context;
            var powerManager = context.GetSystemService(Android.Content.Context.PowerService) as Android.OS.PowerManager;
            return powerManager?.IsIgnoringBatteryOptimizations(context.PackageName) ?? false;
#else
            return true; // En otras plataformas, asumimos que no hay optimización de batería
#endif
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error checking battery optimization: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> RequestAllPermissionsAsync()
    {
        var locationGranted = await RequestLocationPermissionsAsync();
        var batteryExempted = await RequestBatteryOptimizationExemptionAsync();
        
        return locationGranted;
    }

    public async Task<PermissionStatus> CheckPermissionStatusAsync()
    {
        var locationGranted = await CheckLocationPermissionsAsync();
        var batteryIgnored = IsBatteryOptimizationIgnored();

        if (locationGranted && batteryIgnored)
            return PermissionStatus.Granted;
        else if (locationGranted)
            return PermissionStatus.Restricted; // Ubicación OK, pero batería no
        else
            return PermissionStatus.Denied;
    }
}