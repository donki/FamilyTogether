namespace FamilyTogether.Core;

// Modelos ya descifrados, tal y como los pinta la interfaz (ARQUITECTURA §9). Lo que no se pudo
// descifrar llega como «?» (nombres) o no llega (coordenadas): nunca como excepcion.

/// <param name="KeyMissing">Este movil no tiene la clave del grupo: el nombre es «?» y hay que
/// pedirla (<see cref="FamilyService.RequestMissingKeysAsync"/>).</param>
public sealed record Group(Guid Id, string Name, bool IAmAdmin, bool KeyMissing);

/// <param name="Paused">Pausa efectiva: en pausa y sin hora de fin, o con la hora de fin por llegar.</param>
public sealed record Member(
    Guid GroupId,
    Guid UserId,
    string Name,
    string? AvatarBase64,
    bool IsAdmin,
    bool Paused,
    DateTimeOffset? PauseUntil,
    bool IsMe);

public sealed record MemberPosition(Guid UserId, double Lat, double Lon, double Accuracy, int Battery, DateTimeOffset At);

/// <param name="Id"><see cref="Guid.Empty"/> para una zona nueva.</param>
public sealed record Zone(Guid Id, Guid GroupId, string Name, double Lat, double Lon, double Radius, Guid CreatedBy);

public sealed record ZoneSubscription(Guid TargetId, Guid ZoneId, bool OnEnter, bool OnExit);

public sealed record JoinRequest(Guid Id, Guid GroupId, string Name, DateTimeOffset CreatedAt);

public sealed record Invitation(string Code, DateTimeOffset ExpiresAt, string QrPayload);

public enum JoinState { Pending, Approved, Rejected }

/// <summary>
/// Lo que hace falta para montar un aviso. Los textos los pone la app con su localizacion.
/// </summary>
/// <param name="Channel">Canal Android: <c>sos</c>, <c>zones</c> o <c>requests</c>.</param>
/// <param name="EventType"><c>sos</c>, <c>zone_event</c>, <c>join_request</c> o <c>request_resolved</c>.</param>
/// <param name="Kind">Zonas: <c>enter</c>/<c>exit</c>. Solicitudes resueltas: <c>approved</c>/<c>rejected</c>.
/// SOS: <c>stale</c> si la posicion es la ultima conocida y no la del momento.</param>
public sealed record NotificationContent(
    string Channel,
    string EventType,
    Guid GroupId,
    Guid EventId,
    string GroupName,
    string ActorName,
    string? ZoneName,
    string? Kind,
    double? Lat,
    double? Lon);

/// <summary>Los tipos de evento de <c>notify</c> (§7).</summary>
public static class EventTypes
{
    public const string JoinRequest = "join_request";
    public const string RequestResolved = "request_resolved";
    public const string KeyShare = "key_share";
    public const string Sos = "sos";
    public const string ZoneEvent = "zone_event";
}

/// <summary>Los canales de aviso de Android (§7).</summary>
public static class Channels
{
    public const string Sos = "sos";
    public const string Zones = "zones";
    public const string Requests = "requests";
}
