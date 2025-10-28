using FamilyTogether.Functions.Models;

namespace FamilyTogether.Functions.Services;

public interface IAuthService
{
    Task<AuthResponse?> LoginAsync(LoginRequest request);
    Task<AuthResponse?> RegisterAsync(RegisterRequest request);
    string GenerateJwtToken(User user);
    int? ValidateJwtToken(string token);
}