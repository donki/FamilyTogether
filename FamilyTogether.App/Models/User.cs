namespace FamilyTogether.App.Models;

public class User
{
    public int Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public DateTime LastSeen { get; set; }
    public bool IsOnline { get; set; }
}