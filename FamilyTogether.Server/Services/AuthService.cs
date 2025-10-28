using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using FamilyTogether.Server.Models;

namespace FamilyTogether.Server.Services;

public class AuthService
{
    private readonly IConfiguration _configuration;
    private readonly FileService _fileService;

    public AuthService(IConfiguration configuration, FileService fileService)
    {
        _configuration = configuration;
        _fileService = fileService;
    }

    public async Task<User?> RegisterAsync(string email, string password, string name)
    {
        var users = await _fileService.GetUsersAsync();
        
        if (users.Any(u => u.Email.Equals(email, StringComparison.OrdinalIgnoreCase)))
        {
            return null; // User already exists
        }

        var user = new User
        {
            Id = users.Count > 0 ? users.Max(u => u.Id) + 1 : 1,
            Email = email.ToLowerInvariant(),
            PasswordHash = HashPassword(password),
            Name = name,
            CreatedAt = DateTime.UtcNow,
            LastSeen = DateTime.UtcNow
        };

        users.Add(user);
        await _fileService.SaveUsersAsync(users);

        return user;
    }

    public async Task<User?> LoginAsync(string email, string password)
    {
        var users = await _fileService.GetUsersAsync();
        var user = users.FirstOrDefault(u => u.Email.Equals(email, StringComparison.OrdinalIgnoreCase));

        if (user == null || !VerifyPassword(password, user.PasswordHash))
        {
            return null;
        }

        // Update last seen
        user.LastSeen = DateTime.UtcNow;
        user.IsOnline = true;
        await _fileService.SaveUsersAsync(users);

        return user;
    }

    public string GenerateJwtToken(User user)
    {
        var key = Encoding.ASCII.GetBytes(_configuration["Jwt:Key"] ?? throw new InvalidOperationException("JWT Key not configured"));
        var tokenHandler = new JwtSecurityTokenHandler();
        
        var tokenDescriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new Claim(ClaimTypes.Email, user.Email),
                new Claim(ClaimTypes.Name, user.Name)
            }),
            Expires = DateTime.UtcNow.AddHours(24),
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256Signature)
        };

        var token = tokenHandler.CreateToken(tokenDescriptor);
        return tokenHandler.WriteToken(token);
    }

    public async Task<User?> GetUserByIdAsync(int userId)
    {
        var users = await _fileService.GetUsersAsync();
        return users.FirstOrDefault(u => u.Id == userId);
    }

    private static string HashPassword(string password)
    {
        return BCrypt.Net.BCrypt.HashPassword(password);
    }

    private static bool VerifyPassword(string password, string hash)
    {
        return BCrypt.Net.BCrypt.Verify(password, hash);
    }
}