using FamilyTogether.Functions.Models;

namespace FamilyTogether.Functions.Services;

public interface IDataService
{
    // User operations
    Task<User?> GetUserByIdAsync(int userId);
    Task<User?> GetUserByEmailAsync(string email);
    Task<User> CreateUserAsync(User user);
    Task<User> UpdateUserAsync(User user);
    Task<List<User>> GetUsersAsync();
    
    // Family operations
    Task<Family?> GetFamilyByIdAsync(int familyId);
    Task<Family?> GetFamilyByGuidAsync(string familyGuid);
    Task<Family> CreateFamilyAsync(Family family);
    Task<Family> UpdateFamilyAsync(Family family);
    Task<List<Family>> GetFamiliesByUserIdAsync(int userId);
    
    // Location operations
    Task<LocationUpdate> CreateLocationUpdateAsync(LocationUpdate locationUpdate);
    Task<LocationUpdate?> GetLatestLocationAsync(int userId);
    Task<List<LocationUpdate>> GetFamilyLocationsAsync(int familyId);
    Task<List<LocationUpdate>> GetUserLocationHistoryAsync(int userId, int hours = 24);
    
    // Data persistence
    Task SaveDataAsync();
    Task LoadDataAsync();
}
