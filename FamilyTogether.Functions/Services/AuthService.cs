using FamilyTogether.Functions.Models;
using BCrypt.Net;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.IdentityModel.Tokens;
using System.Text;

namespace FamilyTogether.Functions.Services;

public class AuthService : IAuthService
{
    private readonly IDataService _dataService;
    private readonly string _jwtKey;

    public AuthService(IDataService dataService)
    {
        _dataService = dataService;
        _jwtKey = Environment.GetEnvironmentVariable("JWT_KEY") ?? "your-super-secret-jwt-key-that-is-at-least-32-characters-long";
    }

    public async Task<AuthResponse?> LoginAsync(LoginRequest request)
    {
        var user = await _dataService.GetUserByEmailAsync(request.Email);
        
        if (user == null || !BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash))
        {
            return null;
        }

        // Update last seen
        user.LastSeen = DateTime.UtcNow;
        user.IsOnline = true;
        await _dataService.UpdateUserAsync(user);

        var token = GenerateJwtToken(user);
        
        return new AuthResponse
        {
            Token = token,
            User = user
        };
    }

    public async Task<AuthResponse?> RegisterAsync(RegisterRequest request)
    {
        // Check if user already exists
        var existingUser = await _dataService.GetUserByEmailAsync(request.Email);
        if (existingUser != null)
        {
            return null;
        }

        // Create new user
        var user = new User
        {
            Email = request.Email,
            Name = request.Name,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
            CreatedAt = DateTime.UtcNow,
            LastSeen = DateTime.UtcNow,
            IsOnline = true
        };

        user = await _dataService.CreateUserAsync(user);
        var token = GenerateJwtToken(user);

        return new AuthResponse
        {
            Token = token,
            User = user
        };
    }

    public string GenerateJwtToken(User user)
    {
        var tokenHandler = new JwtSecurityTokenHandler();
        var key = Encoding.ASCII.GetBytes(_jwtKey);
        
        var tokenDescriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(new[]
            {
                new Claim("userId", user.Id.ToString()),
                new Claim("email", user.Email),
                new Claim("name", user.Name)
            }),
            Expires = DateTime.UtcNow.AddDays(7),
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256Signature)
        };
        
        var token = tokenHandler.CreateToken(tokenDescriptor);
        return tokenHandler.WriteToken(token);
    }

    public int? ValidateJwtToken(string token)
    {
        try
        {
            var tokenHandler = new JwtSecurityTokenHandler();
            var key = Encoding.ASCII.GetBytes(_jwtKey);
            
            tokenHandler.ValidateToken(token, new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(key),
                ValidateIssuer = false,
                ValidateAudience = false,
                ClockSkew = TimeSpan.Zero
            }, out SecurityToken validatedToken);

            var jwtToken = (JwtSecurityToken)validatedToken;
            var userIdClaim = jwtToken.Claims.FirstOrDefault(x => x.Type == "userId");
            
            if (userIdClaim != null && int.TryParse(userIdClaim.Value, out int userId))
            {
                return userId;
            }
        }
        catch
        {
            // Token validation failed
        }
        
        return null;
    }
}