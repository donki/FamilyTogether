using Android.App;
using Android.Content;
using Android.Media;
using AndroidX.Core.App;
using FamilyTogether.Core;
using FamilyTogether.Mobile.Localization;
using FamilyTogether.Mobile.Services;
using AndroidUri = Android.Net.Uri;

namespace FamilyTogether.Mobile.Platforms.Android;

/// <inheritdoc cref="INotifier"/>
/// <remarks>
/// <para>Cuatro canales separados (Mobile §10), para que el usuario pueda silenciar uno sin perder
/// los demás:</para>
/// <list type="bullet">
/// <item><c>sos</c>: importancia alta, sonido de alarma y vibración. Saltarse «No molestar» no lo
/// puede decidir la app: <c>SetBypassDnd</c> solo tiene efecto si la app tiene acceso a la política
/// de notificaciones (<c>ACCESS_NOTIFICATION_POLICY</c>, que no se pide). Lo decide el usuario en
/// Ajustes > Notificaciones > SOS > «Ignorar No molestar». Se marca igualmente al crear el canal,
/// por si el sistema lo respeta, y el aviso lleva la categoría de alarma, que muchos perfiles de
/// No molestar dejan pasar.</item>
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
    public const string ChannelSos = "sos";
    public const string ChannelZones = "zones";
    public const string ChannelRequests = "requests";
    public const string ChannelService = "service";

    /// <summary>Id de la notificación fija del servicio; los avisos nunca lo usan.</summary>
    internal const int ServiceNotificationId = 4101;

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
            var channel = ChannelFor(content.Channel);
            var id = NotificationIdFor(content.EventId);
            var isSos = channel == ChannelSos;

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

            if (isSos)
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
            NotificationManagerCompat.From(global::Android.App.Application.Context)?.Cancel(NotificationIdFor(eventId));
        }
        catch (Exception ex)
        {
            NativeLog.Warn("No se pudo quitar un aviso.", ex);
        }
    }

    /// <summary>
    /// Crea (o actualiza el nombre de) los cuatro canales. Idempotente: Android solo cambia el
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

            var zones = new NotificationChannel(ChannelZones, Text("ChannelZones", "Zones"), NotificationImportance.Default);
            var requests = new NotificationChannel(ChannelRequests, Text("ChannelRequests", "Requests"), NotificationImportance.Default);

            var service = new NotificationChannel(ChannelService, Text("ChannelService", "Location sharing"), NotificationImportance.Low);
            service.SetShowBadge(false);
            service.EnableVibration(false);
            service.SetSound(null, null);

            manager.CreateNotificationChannels([sos, zones, requests, service]);
        }
        catch (Exception ex)
        {
            NativeLog.Error("No se pudieron crear los canales de notificación.", ex);
        }
    }

    /// <summary>
    /// Texto localizado de la app; si la localización aún no está lista (proceso arrancado por FCM
    /// o por el reinicio), el de reserva en inglés.
    /// </summary>
    internal static string Text(string key, string fallback)
    {
        try
        {
            var text = Loc.Get(key);
            return string.IsNullOrWhiteSpace(text) || text == key ? fallback : text;
        }
        catch
        {
            return fallback;
        }
    }

    private static string ChannelFor(string? channel) => channel switch
    {
        ChannelSos => ChannelSos,
        ChannelZones => ChannelZones,
        _ => ChannelRequests,
    };

    /// <summary>
    /// Id estable a partir del evento. <see cref="Guid.GetHashCode"/> es determinista (sale de los
    /// bytes), así que el mismo evento da el mismo id en cualquier proceso. Se evita el del servicio.
    /// </summary>
    internal static int NotificationIdFor(Guid eventId)
    {
        var id = eventId.GetHashCode() & 0x7FFFFFFF;
        return id == ServiceNotificationId ? id + 1 : id;
    }

    /// <summary>
    /// Al tocar el aviso se abre la app en ese grupo y evento: <see cref="MainActivity"/> recibe
    /// <c>familytogether://event?type=…&amp;group=…&amp;event=…</c> (con <c>&amp;lat=…&amp;lon=…</c> si el
    /// aviso trae sitio) y se lo pasa a
    /// <c>App.HandleDeepLink</c>. PendingIntent inmutable (obligatorio desde Android 12).
    /// </summary>
    private static PendingIntent? OpenAppIntent(Context context, NotificationContent content, int requestCode)
    {
        var link = $"{MainActivity.DeepLinkScheme}://event" +
                   $"?type={Uri.EscapeDataString(content.EventType ?? string.Empty)}" +
                   $"&group={content.GroupId:D}&event={content.EventId:D}";

        // SOS y zonas: el mapa se centra en el sitio del aviso en vez de enseñar todo el grupo.
        if (content is { Lat: { } lat, Lon: { } lon })
            link += FormattableString.Invariant($"&lat={lat:R}&lon={lon:R}");

        var intent = new Intent(context, typeof(MainActivity));
        intent.SetAction(Intent.ActionView);
        intent.SetData(AndroidUri.Parse(link));
        intent.AddFlags(ActivityFlags.NewTask | ActivityFlags.SingleTop | ActivityFlags.ClearTop);

        return PendingIntent.GetActivity(context, requestCode, intent,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
    }
}
