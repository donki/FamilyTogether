using FamilyTogether.Functions.Models;
using Newtonsoft.Json;
using System.Collections.Concurrent;

namespace FamilyTogether.Functions.Services;

public class JsonDataService : IDataService
{
    private readonly string _dataPath;
    private readonly ConcurrentDictionary<int, User> _users = new();
    private readonly ConcurrentDictionary<int, Family> _families = new();
    private readonly ConcurrentDictionary<int, LocationUpdate> _locations = new();
    
    private int _nextUserId = 1;
    private int _nextFamilyId = 1;
    private int _nextLocationId = 1;
    
    private readonly SemaphoreSlim _saveSemaphore = new(1, 1);

    public JsonDataService()
    {
        _dataPath = Environment.GetEnvironmentVariable("DATA_PATH") ?? "data";
        Directory.CreateDirectory(_dataPath);
        
        // Load data on startup
        _ = Task.Run(LoadDataAsync);
    }

    #region User Operations

    public async Task<User?> GetUserByIdAsync(int userId)
    {
        await Task.CompletedTask;
        return _users.TryGetValue(userId, out var user) ? user : null;
    }

    public async Task<User?> GetUserByEmailAsync(string email)
    {
        await Task.CompletedTask;
        return _users.Values.FirstOrDefault(u => u.Email.Equals(email, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<User> CreateUserAsync(User user)
    {
        user.Id = Interlocked.Increment(ref _nextUserId);
        user.CreatedAt = DateTime.UtcNow;
        user.LastSeen = DateTime.UtcNow;
        
        _users[user.Id] = user;
        await SaveDataAsync();
        
        return user;
    }

    public async Task<User> UpdateUserAsync(User user)
    {
        _users[user.Id] = user;
        await SaveDataAsync();
        return user;
    }

    public async Task<List<User>> GetUsersAsync()
    {
        await Task.CompletedTask;
        return _users.Values.ToList();
    }

    #endregion

    #region Family Operations

    public async Task<Family?> GetFamilyByIdAsync(int familyId)
    {
        await Task.CompletedTask;
        return _families.TryGetValue(familyId, out var family) ? family : null;
    }

    public async Task<Family?> GetFamilyByGuidAsync(string familyGuid)
    {
        await Task.CompletedTask;
        return _families.Values.FirstOrDefault(f => f.FamilyGuid == familyGuid);
    }

    public async Task<Family> CreateFamilyAsync(Family family)
    {
        family.Id = Interlocked.Increment(ref _nextFamilyId);
        family.CreatedAt = DateTime.UtcNow;
        family.FamilyGuid = Guid.NewGuid().ToString();
        
        _families[family.Id] = family;
        await SaveDataAsync();
        
        return family;
    }

    public async Task<Family> UpdateFamilyAsync(Family family)
    {
        _families[family.Id] = family;
        await SaveDataAsync();
        return family;
    }

    public async Task<List<Family>> GetFamiliesByUserIdAsync(int userId)
    {
        await Task.CompletedTask;
        return _families.Values
            .Where(f => f.Members.Any(m => m.UserId == userId && m.Status == "active"))
            .ToList();
    }

    #endregion

    #region Location Operations

    public async Task<LocationUpdate> CreateLocationUpdateAsync(LocationUpdate locationUpdate)
    {
        locationUpdate.Id = Interlocked.Increment(ref _nextLocationId);
        locationUpdate.Timestamp = DateTime.UtcNow;
        
        _locations[locationUpdate.Id] = locationUpdate;
        
        // Update user's last seen
        if (_users.TryGetValue(locationUpdate.UserId, out var user))
        {
            user.LastSeen = DateTime.UtcNow;
            user.IsOnline = true;
            _users[user.Id] = user;
        }
        
        await SaveDataAsync();
        return locationUpdate;
    }

    public async Task<LocationUpdate?> GetLatestLocationAsync(int userId)
    {
        await Task.CompletedTask;
        return _locations.Values
            .Where(l => l.UserId == userId)
            .OrderByDescending(l => l.Timestamp)
            .FirstOrDefault();
    }

    public async Task<List<LocationUpdate>> GetFamilyLocationsAsync(int familyId)
    {
        await Task.CompletedTask;
        
        var family = await GetFamilyByIdAsync(familyId);
        if (family == null) return new List<LocationUpdate>();
        
        var activeMembers = family.Members
            .Where(m => m.Status == "active")
            .Select(m => m.UserId)
            .ToList();
        
        var latestLocations = new List<LocationUpdate>();
        
        foreach (var userId in activeMembers)
        {
            var latestLocation = await GetLatestLocationAsync(userId);
            if (latestLocation != null)
            {
                latestLocations.Add(latestLocation);
            }
        }
        
        return latestLocations;
    }

    public async Task<List<LocationUpdate>> GetUserLocationHistoryAsync(int userId, int hours = 24)
    {
        await Task.CompletedTask;
        var cutoffTime = DateTime.UtcNow.AddHours(-hours);
        
        return _locations.Values
            .Where(l => l.UserId == userId && l.Timestamp >= cutoffTime)
            .OrderByDescending(l => l.Timestamp)
            .ToList();
    }

    #endregion

    #region Data Persistence

    public async Task SaveDataAsync()
    {
        await _saveSemaphore.WaitAsync();
        try
        {
            var data = new
            {
                Users = _users.Values.ToList(),
                Families = _families.Values.ToList(),
                Locations = _locations.Values.ToList(),
                NextUserId = _nextUserId,
                NextFamilyId = _nextFamilyId,
                NextLocationId = _nextLocationId,
                LastSaved = DateTime.UtcNow
            };

            var json = JsonConvert.SerializeObject(data, Formatting.Indented);
            var filePath = Path.Combine(_dataPath, "familytogether_data.json");
            
            await File.WriteAllTextAsync(filePath, json);
        }
        finally
        {
            _saveSemaphore.Release();
        }
    }

    public async Task LoadDataAsync()
    {
        await _saveSemaphore.WaitAsync();
        try
        {
            var filePath = Path.Combine(_dataPath, "familytogether_data.json");
            
            if (!File.Exists(filePath))
            {
                // Create initial data file
                await SaveDataAsync();
                return;
            }

            var json = await File.ReadAllTextAsync(filePath);
            var data = JsonConvert.DeserializeAnonymousType(json, new
            {
                Users = new List<User>(),
                Families = new List<Family>(),
                Locations = new List<LocationUpdate>(),
                NextUserId = 1,
                NextFamilyId = 1,
                NextLocationId = 1,
                LastSaved = DateTime.UtcNow
            });

            if (data != null)
            {
                // Load users
                _users.Clear();
                foreach (var user in data.Users)
                {
                    _users[user.Id] = user;
                }

                // Load families
                _families.Clear();
                foreach (var family in data.Families)
                {
                    _families[family.Id] = family;
                }

                // Load locations
                _locations.Clear();
                foreach (var location in data.Locations)
                {
                    _locations[location.Id] = location;
                }

                // Set next IDs
                _nextUserId = data.NextUserId;
                _nextFamilyId = data.NextFamilyId;
                _nextLocationId = data.NextLocationId;
            }
        }
        catch (Exception ex)
        {
            // Log error but don't throw - start with empty data
            Console.WriteLine($"Error loading data: {ex.Message}");
        }
        finally
        {
            _saveSemaphore.Release();
        }
    }

    #endregion
}