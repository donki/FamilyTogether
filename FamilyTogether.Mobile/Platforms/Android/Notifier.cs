using Android.App;
using Android.Content;
using Android.Media;
using AndroidX.Core.App;
using FamilyTogether.Core;
using FamilyTogether.Mobile.Localization;
using FamilyTogether.Mobile.Services;
using FamilyTogether.Mobile.Services.Native;
using AndroidUri = Android.Net.Uri;

namespace FamilyTogether.Mobile.Platforms.Android;

/// <inheritdoc cref="INotifier"/>
/// <remarks>
/// <para>Cinco canales separados (Mobile §10), para que el usuario pueda silenciar uno sin perder
/// los demás:</para>
/// <list type="bullet">
/// <item><c>sos</c>: importancia alta, sonido de alarma y vibración. Saltarse «No molestar» no lo
/// puede decidir la app: <c>SetBypassDnd</c> solo tiene efecto si la app tiene acceso a la política
/// de notificaciones (<c>ACCESS_NOTIFICATION_POLICY</c>, que no se pide). Lo decide el usuario en
/// Ajustes > Notificaciones > SOS > «Ignorar No molestar». Se marca igualmente al crear el canal,
/// por si el sistema lo respeta, y el aviso lleva la categoría de alarma, que muchos perfiles de
/// No molestar dejan pasar.</item>
/// <item><c>sos_alarm</c>: el SOS cuando suena la alarma de la app (<see cref="SosAlarm"/>, ajuste
/// «Sonar aunque esté en silencio»): importancia alta sin sonido ni vibración propios, con
/// «Silenciar»; descartarlo también para la alarma.</item>
/// <item><c>zones</c>: entradas y salidas de zonas, importancia normal.</item>
/// <item><c>requests</c>: solicitudes para unirse y su resolución, importancia normal.</item>
/// <item><c>service</c>: la notificación fija del servicio de ubicación, importancia baja (sin
/// sonido ni icono en la barra de estado).</item>
/// </list>
/// <para>El identificador de cada aviso sale del <c>event_id</c>: si el mismo evento llega por FCM
/// y por la consulta, se sustituye en vez de duplicarse.</para>
/// </remarks>
public sealed class Notifier : INotifier
{
    private const string ChannelSos = NotificationRules.ChannelSos;
    private const string ChannelZones = NotificationRules.ChannelZones;
    private const string ChannelRequests = NotificationRules.ChannelRequests;
    private const string ChannelService = NotificationRules.ChannelService;
    private const string ChannelSosAlarm = NotificationRules.ChannelSosAlarm;

    private static readonly long[] SosVibration = [0, 600, 250, 600, 250, 600];

    public bool AreEnabled
    {
        get
        {
            try
            {
                return NotificationManagerCompat.From(global::Android.App.Application.Context)?.AreNotificationsEnabled() ?? false;
            }
            catch (Exception ex)
            {
                NativeLog.Warn("No se pudo consultar si las notificaciones están activadas.", ex);
                return false;
            }
        }
    }

    public async Task<bool> RequestPermissionAsync()
    {
        try
        {
            if (!OperatingSystem.IsAndroidVersionAtLeast(33))
                return AreEnabled;

            var status = await MainThread.InvokeOnMainThreadAsync(Permissions.RequestAsync<Permissions.PostNotifications>);
            return status == PermissionStatus.Granted && AreEnabled;
        }
        catch (Exception ex)
        {
            NativeLog.Warn("No se pudo pedir el permiso de notificaciones.", ex);
            return AreEnabled;
        }
    }

    public void Show(NotificationContent content)
    {
        try
        {
            var context = global::Android.App.Application.Context;
            EnsureChannels(context);

            var (title, body) = NotificationTexts.Build(content);
            var channel = NotificationRules.ChannelFor(content.Channel);
            var id = NotificationRules.NotificationIdFor(content.EventId);
            var isSos = channel == ChannelSos;

            // Con «Sonar aunque esté en silencio», suena la alarma de la app y el aviso va por el
            // canal sin sonido propio, para que no se pisen. Si la alarma no arranca, canal normal.
            var loud = isSos && SharingState.SosLoud && SosAlarm.Start(context);
            if (loud)
                channel = ChannelSosAlarm;

            // Cada Set* del binding devuelve un Builder anulable: se llama sobre la misma variable.
            var builder = new NotificationCompat.Builder(context, channel);
            builder.SetSmallIcon(Resource.Drawable.ic_notification);
            builder.SetContentTitle(title);
            builder.SetContentText(body);
            var style = new NotificationCompat.BigTextStyle();
            style.BigText(body);
            builder.SetStyle(style);
            builder.SetAutoCancel(true);
            builder.SetContentIntent(OpenAppIntent(context, content, id));
            builder.SetWhen(Java.Lang.JavaSystem.CurrentTimeMillis());
            builder.SetShowWhen(true);
            builder.SetPriority(isSos ? (int)NotificationPriority.Max : (int)NotificationPriority.Default);
            builder.SetCategory(isSos ? NotificationCompat.CategoryAlarm
                : channel == ChannelZones ? NotificationCompat.CategoryStatus
                : NotificationCompat.CategorySocial);

            if (loud)
            {
                var silence = SosAlarm.SilenceIntent(context);
                builder.AddAction(0, Text("SosAlarmSilence", "Silence"), silence);
                builder.SetDeleteIntent(silence);
            }
            else if (isSos)
            {
                // Antes de Android 8 no hay canales: sonido y vibración van en el propio aviso.
                builder.SetVibrate(SosVibration);
                builder.SetSound(RingtoneManager.GetDefaultUri(RingtoneType.Alarm));
            }

            if (builder.Build() is { } notification)
                NotificationManagerCompat.From(context)!.Notify(id, notification);
        }
        catch (Java.Lang.SecurityException ex)
        {
            // Permiso de notificaciones retirado: no hay nada que mostrar ni que romper.
            NativeLog.Warn("Aviso no mostrado: sin permiso de notificaciones.", ex);
        }
        catch (Exception ex)
        {
            NativeLog.Error("No se pudo mostrar un aviso.", ex);
        }
    }

    public void Cancel(Guid eventId)
    {
        try
        {
            NotificationManagerCompat.From(global::Android.App.Application.Context)?.Cancel(NotificationRules.NotificationIdFor(eventId));
        }
        catch (Exception ex)
        {
            NativeLog.Warn("No se pudo quitar un aviso.", ex);
        }
    }

    /// <summary>
    /// Crea (o actualiza el nombre de) los cinco canales. Idempotente: Android solo cambia el
    /// nombre y la descripción de un canal existente; sonido, importancia y demás los manda ya el
    /// usuario.
    /// </summary>
    public static void EnsureChannels(Context context)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(26))
            return;

        try
        {
            var manager = (NotificationManager?)context.GetSystemService(Context.NotificationService);
            if (manager is null)
                return;

            var sos = new NotificationChannel(ChannelSos, Text("ChannelSos", "SOS"), NotificationImportance.High);
            sos.EnableVibration(true);
            sos.SetVibrationPattern(SosVibration);
            sos.EnableLights(true);
            sos.LockscreenVisibility = NotificationVisibility.Public;
            sos.SetBypassDnd(true);   // Solo tiene efecto con acceso a la política; ver el resumen de la clase.
            var alarmAttributes = new AudioAttributes.Builder()
                .SetUsage(AudioUsageKind.Alarm)!
                .SetContentType(AudioContentType.Sonification)!
                .Build();
            sos.SetSound(RingtoneManager.GetDefaultUri(RingtoneType.Alarm), alarmAttributes);

            // El SOS cuando suena la alarma de la app (SosAlarm): emergente y en la pantalla de
            // bloqueo, pero sin sonido ni vibración propios (los pone la alarma).
            var sosAlarm = new NotificationChannel(ChannelSosAlarm, Text("ChannelSosAlarm", "SOS with alarm"), NotificationImportance.High);
            sosAlarm.EnableVibration(false);
            sosAlarm.SetSound(null, null);
            sosAlarm.EnableLights(true);
            sosAlarm.LockscreenVisibility = NotificationVisibility.Public;
            sosAlarm.SetBypassDnd(true);

            var zones = new NotificationChannel(ChannelZones, Text("ChannelZones", "Zones"), NotificationImportance.Default);
            var requests = new NotificationChannel(ChannelRequests, Text("ChannelRequests", "Requests"), NotificationImportance.Default);

            var service = new NotificationChannel(ChannelService, Text("ChannelService", "Location sharing"), NotificationImportance.Low);
            service.SetShowBadge(false);
            service.EnableVibration(false);
            service.SetSound(null, null);

            manager.CreateNotificationChannels([sos, sosAlarm, zones, requests, service]);
        }
        catch (Exception ex)
        {
            NativeLog.Error("No se pudieron crear los canales de notificación.", ex);
        }
    }

    private static string Text(string key, string fallback) => NotificationRules.Text(key, fallback);

    /// <summary>
    /// Al tocar el aviso se abre la app en ese grupo y evento: <see cref="MainActivity"/> recibe
    /// <c>familytogether://event?type=…&amp;group=…&amp;event=…</c> (con <c>&amp;lat=…&amp;lon=…</c> si el
    /// aviso trae sitio) y se lo pasa a
    /// <c>App.HandleDeepLink</c>. PendingIntent inmutable (obligatorio desde Android 12).
    /// </summary>
    private static PendingIntent? OpenAppIntent(Context context, NotificationContent content, int requestCode)
    {
        var link = NotificationRules.EventLink(content);

        var intent = new Intent(context, typeof(MainActivity));
        intent.SetAction(Intent.ActionView);
        intent.SetData(AndroidUri.Parse(link));
        intent.AddFlags(ActivityFlags.NewTask | ActivityFlags.SingleTop | ActivityFlags.ClearTop);

        return PendingIntent.GetActivity(context, requestCode, intent,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
    }
}
