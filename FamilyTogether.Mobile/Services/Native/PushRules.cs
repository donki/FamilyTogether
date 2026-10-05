using System.Globalization;
using FamilyTogether.Core;
using FamilyTogether.Mobile.Localization;

namespace FamilyTogether.Mobile.Services.Native;

/// <summary>
/// Las reglas de los avisos del sistema (lo usa <c>Notifier</c>): canal, identificador estable,
/// textos de reserva y el enlace que abre la app al tocarlos.
/// </summary>
public static class NotificationRules
{
    public const string ChannelSos = "sos";
    public const string ChannelZones = "zones";
    public const string ChannelRequests = "requests";
    public const string ChannelService = "service";

    /// <summary>
    /// El SOS cuando suena la alarma de la app (<c>SosAlarm</c>): importancia alta pero sin sonido
    /// ni vibración propios, para que no se pisen con la alarma.
    /// </summary>
    public const string ChannelSosAlarm = "sos_alarm";

    /// <summary>Id de la notificación fija mientras suena la alarma SOS.</summary>
    public const int SosAlarmNotificationId = 4102;

    /// <summary>Enlace del widget: abre la cuenta atrás del SOS.</summary>
    public const string SosLink = DeepLinkScheme + "://sos";

    /// <summary>Id de la notificación fija del servicio; los avisos nunca lo usan.</summary>
    public const int ServiceNotificationId = 4101;

    /// <summary>Esquema propio de los enlaces de la app (QR de invitación y avisos).</summary>
    public const string DeepLinkScheme = "familytogether";

    /// <summary>
    /// Texto localizado de la app; si la localización aún no está lista (proceso arrancado por FCM
    /// o por el reinicio), el de reserva en inglés.
    /// </summary>
    public static string Text(string key, string fallback)
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

    public static string ChannelFor(string? channel) => channel switch
    {
        ChannelSos => ChannelSos,
        ChannelZones => ChannelZones,
        _ => ChannelRequests,
    };

    /// <summary>
    /// Id estable a partir del evento. <see cref="Guid.GetHashCode"/> es determinista (sale de los
    /// bytes), así que el mismo evento da el mismo id en cualquier proceso. Se evita el del servicio.
    /// </summary>
    public static int NotificationIdFor(Guid eventId)
    {
        var id = eventId.GetHashCode() & 0x7FFFFFFF;
        return id is ServiceNotificationId or SosAlarmNotificationId ? id + 2 : id;
    }

    /// <summary>
    /// Al tocar el aviso se abre la app en ese grupo y evento:
    /// <c>familytogether://event?type=…&amp;group=…&amp;event=…</c>, con <c>&amp;lat=…&amp;lon=…</c> si el
    /// aviso trae sitio (SOS y zonas: el mapa se centra ahí en vez de enseñar todo el grupo).
    /// </summary>
    public static string EventLink(NotificationContent content)
    {
        var link = $"{DeepLinkScheme}://event" +
                   $"?type={Uri.EscapeDataString(content.EventType ?? string.Empty)}" +
                   $"&group={content.GroupId:D}&event={content.EventId:D}";

        if (content is { Lat: { } lat, Lon: { } lon })
            link += FormattableString.Invariant($"&lat={lat:R}&lon={lon:R}");

        return link;
    }

    /// <summary>Un enlace que llega a la actividad es de la app (y se le entrega) si es de su esquema.</summary>
    public static bool IsAppLink(string? data) =>
        !string.IsNullOrWhiteSpace(data) && data.StartsWith(DeepLinkScheme + "://", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Lo que se hace con un mensaje de FCM (solo datos, ARQUITECTURA §7) y con un token nuevo; lo usa
/// <c>FamilyTogetherMessagingService</c>.
/// </summary>
/// <remarks>
/// Los mensajes llevan solo identificadores: <c>{type, group_id, event_id, actor_id, zone_id?,
/// kind?}</c>. El texto visible se monta aquí, pidiendo el evento por REST y descifrándolo con la
/// clave del grupo (<see cref="EventFeed.ResolveAsync"/>), que además descarta los repetidos.
/// </remarks>
public static class PushMessages
{
    public static async Task HandleAsync(IDictionary<string, string>? data, FamilyService? family, EventFeed? feed, Func<INotifier> notifier)
    {
        if (data is null)
            return;

        data.TryGetValue("type", out var type);
        if (string.IsNullOrEmpty(type))
            return;

        // Alguien ha recuperado su cuenta en otro móvil y pide la clave de un grupo: se le entrega
        // en silencio, sin aviso visible (ARQUITECTURA §5).
        if (type == "key_share")
        {
            if (family is not null)
                await family.FulfillPendingKeySharesAsync().ConfigureAwait(false);
            return;
        }

        if (!data.TryGetValue("group_id", out var groupText) || !Guid.TryParse(groupText, out var groupId))
            return;
        if (!data.TryGetValue("event_id", out var eventText) || !Guid.TryParse(eventText, out var eventId))
            return;

        var target = notifier();

        // Otro administrador ha resuelto una solicitud: el aviso «X quiere unirse» (mismo event_id)
        // ya no pinta nada en la barra. Si la solicitud es mía, justo después sale su resolución.
        if (type == EventTypes.RequestResolved)
            target.Cancel(eventId);

        if (feed is null)
            return;

        var content = await feed.ResolveAsync(type, groupId, eventId).ConfigureAwait(false);
        if (content is null)
            return;   // Repetido o ya no aplica.

        target.Show(content);
    }

    /// <summary>Registra el token renovado de FCM (sin red, la app lo vuelve a registrar al abrirse).</summary>
    public static async Task RegisterTokenAsync(FamilyService? family, string? token)
    {
        if (family is null || string.IsNullOrWhiteSpace(token))
            return;

        await family.RegisterPushTokenAsync(token).ConfigureAwait(false);
    }
}
