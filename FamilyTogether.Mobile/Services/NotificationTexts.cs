using FamilyTogether.Core;
using FamilyTogether.Mobile.Localization;

namespace FamilyTogether.Mobile.Services;

/// <summary>
/// El texto visible de cada aviso, montado en el movil a partir de lo que trae el evento ya
/// descifrado (FCM solo lleva identificadores: Mobile 10). Lo usa <c>Platforms\Android\Notifier</c>.
/// </summary>
public static class NotificationTexts
{
    public static (string Title, string Body) Build(NotificationContent c)
    {
        var actor = Name(c.ActorName);
        var group = Name(c.GroupName);

        switch (c.EventType)
        {
            case EventTypes.Sos:
                return (
                    Loc.Format("NotifSosTitle", actor),
                    c.Kind == "stale"
                        ? Loc.Format("NotifSosBodyStale", group)
                        : Loc.Format("NotifSosBody", group));

            case EventTypes.ZoneEvent:
                var zone = Name(c.ZoneName);
                return (
                    Loc.Format(c.Kind == "exit" ? "NotifZoneExitTitle" : "NotifZoneEnterTitle", actor, zone),
                    Loc.Format("NotifZoneBody", group));

            case EventTypes.JoinRequest:
                return (Loc.Get("NotifJoinTitle"), Loc.Format("NotifJoinBody", actor, group));

            case EventTypes.RequestResolved:
                return c.Kind == "rejected"
                    ? (Loc.Get("NotifRejectedTitle"), Loc.Get("NotifRejectedBody"))
                    : (Loc.Get("NotifApprovedTitle"), Loc.Format("NotifApprovedBody", group));

            default:
                return (Loc.Get("AppName"), group);
        }
    }

    /// <summary>Un nombre que no se pudo descifrar llega como «?»: se dice con palabras.</summary>
    private static string Name(string? value) =>
        string.IsNullOrWhiteSpace(value) || value == "?" ? Loc.Get("Unknown") : value;
}
