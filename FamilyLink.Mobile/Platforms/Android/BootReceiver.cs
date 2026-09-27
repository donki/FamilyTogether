using Android.App;
using Android.Content;

namespace FamilyLink.Mobile.Platforms.Android;

/// <summary>
/// Vuelve a arrancar el servicio de ubicación al encender el móvil y tras actualizar la app, si el
/// usuario estaba compartiendo. Sin esto, después de cada reinicio el grupo dejaría de ver su
/// posición sin que nadie se enterase.
/// </summary>
/// <remarks>
/// <para><c>BOOT_COMPLETED</c> y <c>MY_PACKAGE_REPLACED</c> están entre las excepciones que dejan
/// arrancar un servicio en primer plano desde segundo plano (Android 12+), y el tipo
/// <c>location</c> no está entre los que Android 15 prohíbe arrancar desde el arranque.</para>
/// <para>Aquí la app no está en pantalla: sin el permiso de ubicación <b>en segundo plano</b> el
/// servicio no recibiría ninguna posición, así que ni se intenta; se reanudará al abrir la app.</para>
/// <para><c>QUICKBOOT_POWERON</c> es el que mandan algunos fabricantes (HTC, algunos Xiaomi) en
/// lugar del estándar al encender rápido.</para>
/// </remarks>
[BroadcastReceiver(Enabled = true, Exported = true, DirectBootAware = false)]
[IntentFilter([Intent.ActionBootCompleted, "android.intent.action.QUICKBOOT_POWERON", Intent.ActionMyPackageReplaced])]
public class BootReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        try
        {
            if (context is null || !SharingState.Enabled)
                return;

            if (!LocationSharing.HasBackgroundPermission(context))
            {
                NativeLog.Info("Arranque: se compartía la ubicación pero falta el permiso de segundo plano; se reanudará al abrir la app.");
                return;
            }

            NativeLog.Info($"Arranque ({intent?.Action}): se reanuda la compartición de la ubicación.");
            LocationSharingForegroundService.StartService(context);
        }
        catch (Exception ex)
        {
            NativeLog.Error("Error al reanudar la compartición tras el arranque.", ex);
        }
    }
}
