using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using FamilyTogether.Server.Services;
using System.ComponentModel.DataAnnotations;

namespace FamilyTogether.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class LocationController : ControllerBase
{
    private readonly LocationService _locationService;
    private readonly AuthService _authService;

    public LocationController(LocationService locationService, AuthService authService)
    {
        _locationService = locationService;
        _authService = authService;
    }

    [HttpPost("update")]
    public async Task<IActionResult> UpdateLocation([FromBody] UpdateLocationRequest request)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(new { success = false, message = "Datos inválidos", data = ModelState });
        }

        var userId = GetCurrentUserId();
        
        try
        {
            var location = await _locationService.SaveLocationAsync(userId, request.Latitude, request.Longitude, request.Accuracy);
            
            // Update user's last seen
            var user = await _authService.GetUserByIdAsync(userId);
            if (user != null)
            {
                user.LastSeen = DateTime.UtcNow;
                user.IsOnline = true;
            }

            return Ok(new 
            { 
                success = true, 
                message = "Ubicación actualizada exitosamente", 
                data = new 
                { 
                    locationId = location.Id,
                    timestamp = location.Timestamp
                } 
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { success = false, message = "Error interno del servidor", data = ex.Message });
        }
    }

    [HttpGet("family-locations")]
    public async Task<IActionResult> GetFamilyLocations()
    {
        var userId = GetCurrentUserId();
        
        try
        {
            var locations = await _locationService.GetFamilyLocationsAsync(userId);
            
            // Get user information for each location
            var locationData = new List<object>();
            foreach (var location in locations)
            {
                var user = await _authService.GetUserByIdAsync(location.UserId);
                if (user != null)
                {
                    locationData.Add(new
                    {
                        userId = location.UserId,
                        userName = user.Name,
                        latitude = location.Latitude,
                        longitude = location.Longitude,
                        accuracy = location.Accuracy,
                        timestamp = location.Timestamp,
                        lastSeen = user.LastSeen,
                        isOnline = user.IsOnline,
                        minutesAgo = (int)(DateTime.UtcNow - location.Timestamp).TotalMinutes
                    });
                }
            }

            return Ok(new 
            { 
                success = true, 
                message = "Ubicaciones obtenidas exitosamente", 
                data = locationData 
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { success = false, message = "Error interno del servidor", data = ex.Message });
        }
    }

    [HttpPost("cleanup")]
    public async Task<IActionResult> CleanupOldLocations()
    {
        try
        {
            await _locationService.CleanOldLocationsAsync();
            return Ok(new 
            { 
                success = true, 
                message = "Limpieza completada exitosamente", 
                data = (object?)null 
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { success = false, message = "Error interno del servidor", data = ex.Message });
        }
    }

    private int GetCurrentUserId()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return int.Parse(userIdClaim ?? "0");
    }
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