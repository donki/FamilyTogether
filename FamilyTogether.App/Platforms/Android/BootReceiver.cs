using Android.Content;
using Android.App;

namespace FamilyTogether.App.Platforms.Android;

[BroadcastReceiver(Enabled = true, Exported = true)]
public class BootReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context == null || intent == null)
            return;

        var action = intent.Action;
        
        if (action == Intent.ActionBootCompleted || action == Intent.ActionMyPackageReplaced)
        {
            try
            {
                // Verificar si el usuario está logueado y tiene permisos
                var preferences = Microsoft.Maui.Storage.Preferences.Default;
                var isLoggedIn = preferences.Get("IsLoggedIn", false);
                var hasLocationPermission = preferences.Get("HasLocationPermission", false);

                if (isLoggedIn && hasLocationPermission)
                {
                    // Iniciar el servicio de ubicación
                    LocationForegroundService.StartService(context);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in BootReceiver: {ex.Message}");
            }
        }
    }
}