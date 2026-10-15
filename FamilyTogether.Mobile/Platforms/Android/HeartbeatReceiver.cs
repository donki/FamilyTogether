using Android.Content;
using FamilyTogether.Mobile.Services.Native;

namespace FamilyTogether.Mobile.Platforms.Android;

/// <summary>
/// La alarma del latido de la posición (una vez por hora aunque el móvil no se mueva,
/// <c>SendPolicy</c>). La programa <see cref="LocationSharingForegroundService"/> con
/// <c>setAndAllowWhileIdle</c>, que salta también en Doze; aquí solo se le avisa. También la del
/// reintento de lo que quedó sin enviar (acción <see cref="LocationSharingForegroundService.RetryAction"/>).
/// </summary>
/// <remarks>No exportado: solo la recibe la propia app, por la alarma que ella misma programa.</remarks>
[BroadcastReceiver(Exported = false)]
public class HeartbeatReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        try
        {
            if (intent?.Action == LocationSharingForegroundService.RetryAction)
                LocationSharingForegroundService.OnRetryAlarm();
            else
                LocationSharingForegroundService.OnHeartbeatAlarm();
        }
        catch (Exception ex)
        {
            NativeLog.Error("Error en la alarma del latido de la posición.", ex);
        }
    }
}
