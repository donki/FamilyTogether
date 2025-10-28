using FamilyTogether.Server.Models;

namespace FamilyTogether.Server.Services;

public class LocationService
{
    private readonly FileService _fileService;

    public LocationService(FileService fileService)
    {
        _fileService = fileService;
    }

    public async Task<LocationUpdate> SaveLocationAsync(int userId, double latitude, double longitude, float accuracy)
    {
        var locations = await _fileService.GetLocationsAsync();

        var location = new LocationUpdate
        {
            Id = locations.Count > 0 ? locations.Max(l => l.Id) + 1 : 1,
            UserId = userId,
            Latitude = latitude,
            Longitude = longitude,
            Accuracy = accuracy,
            Timestamp = DateTime.UtcNow
        };

        locations.Add(location);

        // Keep only the last 100 locations per user to prevent file from growing too large
        var userLocations = locations.Where(l => l.UserId == userId).OrderByDescending(l => l.Timestamp).ToList();
        if (userLocations.Count > 100)
        {
            var locationsToRemove = userLocations.Skip(100);
            foreach (var locationToRemove in locationsToRemove)
            {
                locations.Remove(locationToRemove);
            }
        }

        await _fileService.SaveLocationsAsync(locations);
        return location;
    }

    public async Task<List<LocationUpdate>> GetFamilyLocationsAsync(int userId)
    {
        var families = await _fileService.GetFamiliesAsync();
        var locations = await _fileService.GetLocationsAsync();

        // Find user's family
        var family = families.FirstOrDefault(f => f.Members.Any(m => m.UserId == userId && m.Status == "active"));
        if (family == null)
        {
            return new List<LocationUpdate>();
        }

        // Get active family member IDs
        var familyMemberIds = family.Members
            .Where(m => m.Status == "active")
            .Select(m => m.UserId)
            .ToList();

        // Get latest location for each family member
        var familyLocations = new List<LocationUpdate>();
        foreach (var memberId in familyMemberIds)
        {
            var latestLocation = locations
                .Where(l => l.UserId == memberId)
                .OrderByDescending(l => l.Timestamp)
                .FirstOrDefault();

            if (latestLocation != null)
            {
                familyLocations.Add(latestLocation);
            }
        }

        return familyLocations;
    }

    public async Task CleanOldLocationsAsync()
    {
        var locations = await _fileService.GetLocationsAsync();
        var cutoffDate = DateTime.UtcNow.AddDays(-7); // Keep locations for 7 days

        var recentLocations = locations.Where(l => l.Timestamp > cutoffDate).ToList();
        
        if (recentLocations.Count != locations.Count)
        {
            await _fileService.SaveLocationsAsync(recentLocations);
        }
    }
}