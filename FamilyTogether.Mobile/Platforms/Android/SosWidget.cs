using Android.App;
using Android.Appwidget;
using Android.Content;
using Android.Widget;
using FamilyTogether.Mobile.Services.Native;
using AndroidUri = Android.Net.Uri;

namespace FamilyTogether.Mobile.Platforms.Android;

/// <summary>
/// Widget del botón SOS para la pantalla de inicio (1x1, círculo rojo).
/// </summary>
/// <remarks>
/// Al tocarlo abre la app con <c>familytogether://sos</c>: el mapa y encima la misma cuenta atrás
/// de 3 s con Cancelar que el botón SOS del mapa (<c>App.OpenSosAsync</c>). Nunca envía nada sin
/// esa cuenta atrás: un toque sin querer en la pantalla de inicio se cancela igual que en la app.
/// </remarks>
[BroadcastReceiver(Exported = true, Label = "@string/sos_widget_label")]
[IntentFilter([AppWidgetManager.ActionAppwidgetUpdate])]
[MetaData(AppWidgetManager.MetaDataAppwidgetProvider, Resource = "@xml/sos_widget_info")]
public class SosWidget : AppWidgetProvider
{
    public override void OnUpdate(Context? context, AppWidgetManager? appWidgetManager, int[]? appWidgetIds)
    {
        if (context is null || appWidgetManager is null || appWidgetIds is null)
            return;

        try
        {
            var intent = new Intent(context, typeof(MainActivity));
            intent.SetAction(Intent.ActionView);
            intent.SetData(AndroidUri.Parse(NotificationRules.SosLink));
            intent.AddFlags(ActivityFlags.NewTask | ActivityFlags.SingleTop | ActivityFlags.ClearTop);
            var open = PendingIntent.GetActivity(context, 0, intent, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

            foreach (var id in appWidgetIds)
            {
                var views = new RemoteViews(context.PackageName, Resource.Layout.sos_widget);
                views.SetOnClickPendingIntent(Resource.Id.sos_button, open);
                appWidgetManager.UpdateAppWidget(id, views);
            }
        }
        catch (Exception ex)
        {
            NativeLog.Error("No se pudo pintar el widget SOS.", ex);
        }
    }
}
