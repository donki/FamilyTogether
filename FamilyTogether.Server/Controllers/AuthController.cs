using Microsoft.AspNetCore.Mvc;
using FamilyTogether.Server.Services;
using System.ComponentModel.DataAnnotations;

namespace FamilyTogether.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly AuthService _authService;

    public AuthController(AuthService authService)
    {
        _authService = authService;
    }

    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] RegisterRequest request)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(new { success = false, message = "Datos inválidos", data = ModelState });
        }

        var user = await _authService.RegisterAsync(request.Email, request.Password, request.Name);
        
        if (user == null)
        {
            return BadRequest(new { success = false, message = "El usuario ya existe", data = (object?)null });
        }

        var token = _authService.GenerateJwtToken(user);
        
        return Ok(new 
        { 
            success = true, 
            message = "Usuario registrado exitosamente", 
            data = new 
            { 
                user = new { user.Id, user.Email, user.Name },
                token 
            } 
        });
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(new { success = false, message = "Datos inválidos", data = ModelState });
        }

        var user = await _authService.LoginAsync(request.Email, request.Password);
        
        if (user == null)
        {
            return Unauthorized(new { success = false, message = "Credenciales inválidas", data = (object?)null });
        }

        var token = _authService.GenerateJwtToken(user);
        
        return Ok(new 
        { 
            success = true, 
            message = "Login exitoso", 
            data = new 
            { 
                user = new { user.Id, user.Email, user.Name },
                token 
            } 
        });
    }
}

public class RegisterRequest
{
    [Required]
    [EmailAddress]
    public string Email { get; set; } = string.Empty;

    [Required]
    [MinLength(6)]
    public string Password { get; set; } = string.Empty;

    [Required]
    [StringLength(100)]
    public string Name { get; set; } = string.Empty;
}

public class LoginRequest
{
    [Required]
    [EmailAddress]
    public string Email { get; set; } = string.Empty;

    [Required]
    public string Password { get; set; } = string.Empty;
}