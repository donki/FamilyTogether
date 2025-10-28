using System.ComponentModel.DataAnnotations;

namespace FamilyTogether.Functions.Models;

public class Family
{
    public int Id { get; set; }
    
    [Required]
    [StringLength(36)]
    public string FamilyGuid { get; set; } = string.Empty;
    
    [Required]
    [StringLength(100)]
    public string Name { get; set; } = string.Empty;
    
    public int CreatedBy { get; set; }
    
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    
    public List<FamilyMember> Members { get; set; } = new();
}

public class FamilyMember
{
    public int UserId { get; set; }
    
    [Required]
    [StringLength(100)]
    public string Name { get; set; } = string.Empty;
    
    public bool IsAdmin { get; set; } = false;
    
    [Required]
    public string Status { get; set; } = "pending"; // pending, active
    
    public DateTime JoinedAt { get; set; } = DateTime.UtcNow;
}

public class CreateFamilyRequest
{
    [Required]
    [StringLength(100)]
    public string Name { get; set; } = string.Empty;
}

public class JoinFamilyRequest
{
    [Required]
    [StringLength(36)]
    public string FamilyGuid { get; set; } = string.Empty;
}
