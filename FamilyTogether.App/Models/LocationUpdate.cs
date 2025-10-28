namespace FamilyTogether.App.Models;

public class LocationUpdate
{
    public int UserId { get; set; }
    public string UserName { get; set; } = string.Empty;
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public float Accuracy { get; set; }
    public DateTime Timestamp { get; set; }
    public DateTime LastSeen { get; set; }
    public bool IsOnline { get; set; }
    public int MinutesAgo { get; set; }
}