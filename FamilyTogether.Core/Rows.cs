namespace FamilyTogether.Core;

// Filas tal y como llegan de PostgREST (snake_case ↔ PascalCase por SupabaseClient.Json). Todo lo
// cifrado sigue cifrado aqui: se abre en FamilyService / EventFeed con la clave del grupo.

internal sealed class GroupRow
{
    public Guid Id { get; set; }
    public string? NameEnc { get; set; }
}

internal sealed class MemberRow
{
    public Guid GroupId { get; set; }
    public Guid UserId { get; set; }
    public string Role { get; set; } = "member";
    public string? DisplayNameEnc { get; set; }
    public string? AvatarEnc { get; set; }
    public bool Paused { get; set; }
    public DateTimeOffset? PauseUntil { get; set; }

    public bool IsAdmin => Role == "admin";

    /// <summary>La misma regla que <c>is_sharing</c> del servidor.</summary>
    public bool EffectivelyPaused(DateTimeOffset now) => Paused && (PauseUntil is null || PauseUntil > now);
}

internal sealed class InvitationRow
{
    public string Code { get; set; } = "";
    public Guid GroupId { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public string? InvPub { get; set; }
    public string? InvPrivEnc { get; set; }
}

internal sealed class JoinRequestRow
{
    public Guid Id { get; set; }
    public Guid GroupId { get; set; }
    public Guid UserId { get; set; }
    public string? InvitationCode { get; set; }
    public string? ReqPub { get; set; }
    public string? InvPub { get; set; }

    /// <summary>Copia de la invitacion (§6): la solicitud se puede aprobar aunque la invitacion ya no exista.</summary>
    public string? InvPrivEnc { get; set; }

    public string? NameBox { get; set; }
    public string Status { get; set; } = "pending";
    public string? KeyBox { get; set; }
    public Guid? ResolvedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
}

internal sealed class KeyShareRow
{
    public Guid Id { get; set; }
    public Guid GroupId { get; set; }
    public Guid UserId { get; set; }
    public string? DevicePub { get; set; }
    public string? FulfillerPub { get; set; }
    public string? KeyBox { get; set; }
}

internal sealed class PositionRow
{
    public Guid UserId { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
    public int? Battery { get; set; }
    public string? PayloadEnc { get; set; }
}

internal sealed class ZoneRow
{
    public Guid Id { get; set; }
    public Guid GroupId { get; set; }
    public string? NameEnc { get; set; }
    public string? GeoEnc { get; set; }
    public Guid CreatedBy { get; set; }
}

internal sealed class SubscriptionRow
{
    public Guid GroupId { get; set; }
    public Guid TargetId { get; set; }
    public Guid ZoneId { get; set; }
    public bool OnEnter { get; set; }
    public bool OnExit { get; set; }
}

internal sealed class ZoneEventRow
{
    public Guid Id { get; set; }
    public Guid GroupId { get; set; }
    public Guid UserId { get; set; }
    public Guid ZoneId { get; set; }
    public string Kind { get; set; } = "";
    public DateTimeOffset OccurredAt { get; set; }
}

internal sealed class SosAlertRow
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

internal sealed class SosTargetRow
{
    public Guid SosId { get; set; }
    public Guid GroupId { get; set; }
    public string? PayloadEnc { get; set; }
}

internal sealed class ProviderRow
{
    public string Provider { get; set; } = "";
}
