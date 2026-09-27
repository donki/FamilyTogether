using Android.App;
using Firebase.Messaging;
using FamilyLink.Core;
using FamilyLink.Mobile.Services;

namespace FamilyLink.Mobile.Platforms.Android;

/// <summary>
/// Recibe los mensajes de FCM (solo datos, ARQUITECTURA §7) y el token renovado.
/// </summary>
/// <remarks>
/// <para>Los mensajes llevan solo identificadores: <c>{type, group_id, event_id, actor_id, zone_id?,
/// kind?}</c>. El texto visible se monta aquí, pidiendo el evento por REST y descifrándolo con la
/// clave del grupo (<see cref="EventFeed.ResolveAsync"/>), que además descarta los repetidos.</para>
/// <para><see cref="OnMessageReceived"/> corre en un hilo de trabajo de Firebase y el proceso puede
/// morir en cuanto vuelve: por eso se espera al resultado (con tiempo máximo) en vez de lanzar la
/// tarea y olvidarse.</para>
/// </remarks>
[Service(Exported = false)]
[IntentFilter(["com.google.firebase.MESSAGING_EVENT"])]
public class FamilyLinkMessagingService : FirebaseMessagingService
{
    /// <summary>Firebase da unos 20 s a un mensaje de alta prioridad; se deja margen.</summary>
    private static readonly TimeSpan HandleTimeout = TimeSpan.FromSeconds(15);

    // El binding 125.x marca OnNewToken como «deprecated» sin alternativa: en el SDK de Firebase
    // sigue siendo la forma de enterarse de un token nuevo. Se silencia solo aquí.
#pragma warning disable CS0618, CS0672
    public override void OnNewToken(string token)
    {
        base.OnNewToken(token);
#pragma warning restore CS0618, CS0672

        try
        {
            var family = PlatformServiceLocator.Get<FamilyService>();
            if (family is null || string.IsNullOrWhiteSpace(token))
                return;

            family.RegisterPushTokenAsync(token).WaitAsync(HandleTimeout).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            // Sin red en este momento: la app vuelve a registrar el token al abrirse.
            NativeLog.Warn("No se pudo registrar el token nuevo de FCM.", ex);
        }
    }

    public override void OnMessageReceived(RemoteMessage message)
    {
        base.OnMessageReceived(message);

        try
        {
            HandleAsync(message).WaitAsync(HandleTimeout).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            // Si falla (sin red, sin clave), el servicio de ubicación lo recogerá en su consulta.
            NativeLog.Warn("No se pudo procesar un mensaje de FCM.", ex);
        }
    }

    private static async Task HandleAsync(RemoteMessage message)
    {
        var data = message.Data;
        if (data is null)
            return;

        data.TryGetValue("type", out var type);
        if (string.IsNullOrEmpty(type))
            return;

        // Alguien ha recuperado su cuenta en otro móvil y pide la clave de un grupo: se le entrega
        // en silencio, sin aviso visible (ARQUITECTURA §5).
        if (type == "key_share")
        {
            var family = PlatformServiceLocator.Get<FamilyService>();
            if (family is not null)
                await family.FulfillPendingKeySharesAsync().ConfigureAwait(false);
            return;
        }

        if (!data.TryGetValue("group_id", out var groupText) || !Guid.TryParse(groupText, out var groupId))
            return;
        if (!data.TryGetValue("event_id", out var eventText) || !Guid.TryParse(eventText, out var eventId))
            return;

        var feed = PlatformServiceLocator.Get<EventFeed>();
        if (feed is null)
            return;

        var content = await feed.ResolveAsync(type, groupId, eventId).ConfigureAwait(false);
        if (content is null)
            return;   // Repetido o ya no aplica.

        var notifier = PlatformServiceLocator.Get<INotifier>() ?? new Notifier();
        notifier.Show(content);
    }
}
