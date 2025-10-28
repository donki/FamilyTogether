using System.ComponentModel.DataAnnotations;

namespace FamilyTogether.Server.Models;

public class User
{
    public int Id { get; set; }
    
    [Required]
    [EmailAddress]
    public string Email { get; set; } = string.Empty;
    
    [Required]
    public string PasswordHash { get; set; } = string.Empty;
    
    [Required]
    [StringLength(100)]
    public string Name { get; set; } = string.Empty;
    
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    
    public DateTime LastSeen { get; set; } = DateTime.UtcNow;
    
    public bool IsOnline { get; set; } = false;
}