namespace FamilyTogether.App.Models;

public class Family
{
    public int Id { get; set; }
    public string FamilyGuid { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public List<FamilyMember> Members { get; set; } = new();
}

public class FamilyMember
{
    public int UserId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public bool IsAdmin { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime JoinedAt { get; set; }
    public DateTime LastSeen { get; set; }
    public bool IsOnline { get; set; }
}