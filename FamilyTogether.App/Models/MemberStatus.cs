namespace FamilyTogether.App.Models;

public class MemberStatus
{
    public int UserId { get; set; }
    public string UserName { get; set; } = string.Empty;
    public bool IsOnline { get; set; }
    public DateTime LastSeen { get; set; }
    public int MinutesAgo { get; set; }
}

public class MemberStatusChange
{
    public int UserId { get; set; }
    public string UserName { get; set; } = string.Empty;
    public string PreviousStatus { get; set; } = string.Empty;
    public string NewStatus { get; set; } = string.Empty;
    public StatusChangeType ChangeType { get; set; }
    public DateTime Timestamp { get; set; }
}

public enum StatusChangeType
{
    CameOnline,
    WentOffline,
    BecameActive,
    BecameInactive
}