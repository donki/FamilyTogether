using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using FamilyTogether.Server.Services;
using FamilyTogether.Server.Models;
using System.ComponentModel.DataAnnotations;

namespace FamilyTogether.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class FamilyController : ControllerBase
{
    private readonly FileService _fileService;
    private readonly AuthService _authService;

    public FamilyController(FileService fileService, AuthService authService)
    {
        _fileService = fileService;
        _authService = authService;
    }

    [HttpPost("create")]
    public async Task<IActionResult> CreateFamily([FromBody] CreateFamilyRequest request)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(new { success = false, message = "Datos inválidos", data = ModelState });
        }

        var userId = GetCurrentUserId();
        var families = await _fileService.GetFamiliesAsync();

        // Check if user is already in a family
        var existingFamily = families.FirstOrDefault(f => f.Members.Any(m => m.UserId == userId && m.Status == "active"));
        if (existingFamily != null)
        {
            return BadRequest(new { success = false, message = "Ya perteneces a una familia", data = (object?)null });
        }

        var family = new Family
        {
            Id = families.Count > 0 ? families.Max(f => f.Id) + 1 : 1,
            FamilyGuid = Guid.NewGuid().ToString(),
            Name = request.Name,
            CreatedBy = userId,
            CreatedAt = DateTime.UtcNow,
            Members = new List<FamilyMember>
            {
                new FamilyMember
                {
                    UserId = userId,
                    IsAdmin = true,
                    Status = "active",
                    JoinedAt = DateTime.UtcNow
                }
            }
        };

        families.Add(family);
        await _fileService.SaveFamiliesAsync(families);

        return Ok(new 
        { 
            success = true, 
            message = "Familia creada exitosamente", 
            data = new 
            { 
                familyId = family.Id,
                familyGuid = family.FamilyGuid,
                name = family.Name
            } 
        });
    }

    [HttpPost("join")]
    public async Task<IActionResult> JoinFamily([FromBody] JoinFamilyRequest request)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(new { success = false, message = "Datos inválidos", data = ModelState });
        }

        var userId = GetCurrentUserId();
        var families = await _fileService.GetFamiliesAsync();

        // Check if user is already in a family
        var existingFamily = families.FirstOrDefault(f => f.Members.Any(m => m.UserId == userId));
        if (existingFamily != null)
        {
            return BadRequest(new { success = false, message = "Ya perteneces a una familia", data = (object?)null });
        }

        // Find family by GUID
        var family = families.FirstOrDefault(f => f.FamilyGuid == request.FamilyGuid);
        if (family == null)
        {
            return BadRequest(new { success = false, message = "Código de familia inválido", data = (object?)null });
        }

        // Add user as pending member
        family.Members.Add(new FamilyMember
        {
            UserId = userId,
            IsAdmin = false,
            Status = "pending",
            JoinedAt = DateTime.UtcNow
        });

        await _fileService.SaveFamiliesAsync(families);

        return Ok(new 
        { 
            success = true, 
            message = "Solicitud enviada, esperando aprobación", 
            data = (object?)null 
        });
    }

    [HttpGet("members")]
    public async Task<IActionResult> GetFamilyMembers()
    {
        var userId = GetCurrentUserId();
        var families = await _fileService.GetFamiliesAsync();
        var users = await _fileService.GetUsersAsync();

        var family = families.FirstOrDefault(f => f.Members.Any(m => m.UserId == userId && m.Status == "active"));
        if (family == null)
        {
            return NotFound(new { success = false, message = "No perteneces a ninguna familia", data = (object?)null });
        }

        var members = family.Members.Select(m =>
        {
            var user = users.FirstOrDefault(u => u.Id == m.UserId);
            return new
            {
                userId = m.UserId,
                name = user?.Name ?? "Usuario desconocido",
                email = user?.Email ?? "",
                isAdmin = m.IsAdmin,
                status = m.Status,
                joinedAt = m.JoinedAt,
                lastSeen = user?.LastSeen,
                isOnline = user?.IsOnline ?? false
            };
        }).ToList();

        return Ok(new 
        { 
            success = true, 
            message = "Miembros obtenidos exitosamente", 
            data = new 
            { 
                family = new { family.Id, family.FamilyGuid, family.Name },
                members 
            } 
        });
    }

    [HttpPost("approve-member")]
    public async Task<IActionResult> ApproveMember([FromBody] ApproveMemberRequest request)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(new { success = false, message = "Datos inválidos", data = ModelState });
        }

        var userId = GetCurrentUserId();
        var families = await _fileService.GetFamiliesAsync();

        var family = families.FirstOrDefault(f => f.Members.Any(m => m.UserId == userId && m.IsAdmin && m.Status == "active"));
        if (family == null)
        {
            return Forbid("No tienes permisos de administrador");
        }

        var member = family.Members.FirstOrDefault(m => m.UserId == request.UserId && m.Status == "pending");
        if (member == null)
        {
            return NotFound(new { success = false, message = "Solicitud no encontrada", data = (object?)null });
        }

        member.Status = "active";
        await _fileService.SaveFamiliesAsync(families);

        return Ok(new 
        { 
            success = true, 
            message = "Miembro aprobado exitosamente", 
            data = (object?)null 
        });
    }

    [HttpDelete("remove-member/{memberId}")]
    public async Task<IActionResult> RemoveMember(int memberId)
    {
        var userId = GetCurrentUserId();
        var families = await _fileService.GetFamiliesAsync();

        var family = families.FirstOrDefault(f => f.Members.Any(m => m.UserId == userId && m.IsAdmin && m.Status == "active"));
        if (family == null)
        {
            return Forbid("No tienes permisos de administrador");
        }

        var member = family.Members.FirstOrDefault(m => m.UserId == memberId);
        if (member == null)
        {
            return NotFound(new { success = false, message = "Miembro no encontrado", data = (object?)null });
        }

        // Prevent removing the last admin
        if (member.IsAdmin && family.Members.Count(m => m.IsAdmin && m.Status == "active") <= 1)
        {
            return BadRequest(new { success = false, message = "No se puede eliminar el último administrador", data = (object?)null });
        }

        family.Members.Remove(member);
        await _fileService.SaveFamiliesAsync(families);

        return Ok(new 
        { 
            success = true, 
            message = "Miembro eliminado exitosamente", 
            data = (object?)null 
        });
    }

    private int GetCurrentUserId()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return int.Parse(userIdClaim ?? "0");
    }
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

public class ApproveMemberRequest
{
    [Required]
    public int UserId { get; set; }
}