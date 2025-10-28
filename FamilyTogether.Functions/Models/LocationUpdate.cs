using System.ComponentModel.DataAnnotations;

namespace FamilyTogether.Functions.Models;

public class LocationUpdate
{
    public int Id { get; set; }
    
    public int UserId { get; set; }
    
    [Required]
    [Range(-90, 90)]
    public double Latitude { get; set; }
    
    [Required]
    [Range(-180, 180)]
    public double Longitude { get; set; }
    
    [Range(0, 1000)]
    public float Accuracy { get; set; }
    
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

public class UpdateLocationRequest
{
    [Required]
    [Range(-90, 90)]
    public double Latitude { get; set; }
    
    [Required]
    [Range(-180, 180)]
    public double Longitude { get; set; }
    
    [Range(0, 1000)]
    public float Accuracy { get; set; }
}

public class LocationResponse
{
    public int UserId { get; set; }
    public string UserName { get; set; } = string.Empty;
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public float Accuracy { get; set; }
    public DateTime Timestamp { get; set; }
    public int MinutesAgo { get; set; }
}
