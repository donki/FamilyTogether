using FamilyTogether.Functions.Models;

namespace FamilyTogether.Functions.Services;

public class LocationService : ILocationService
{
    private readonly IDataService _dataService;

    public LocationService(IDataService dataService)
    {
        _dataService = dataService;
    }

    public async Task<LocationUpdate> UpdateLocationAsync(int userId, UpdateLocationRequest request)
    {
        var locationUpdate = new LocationUpdate
        {
            UserId = userId,
            Latitude = request.Latitude,
            Longitude = request.Longitude,
            Accuracy = request.Accuracy,
            Timestamp = DateTime.UtcNow
        };

        return await _dataService.CreateLocationUpdateAsync(locationUpdate);
    }

    public async Task<List<LocationResponse>> GetFamilyLocationsAsync(int userId)
    {
        // Get user's families
        var families = await _dataService.GetFamiliesByUserIdAsync(userId);
        var locationResponses = new List<LocationResponse>();

        foreach (var family in families)
        {
            var familyLocations = await _dataService.GetFamilyLocationsAsync(family.Id);
            
            foreach (var location in familyLocations)
            {
                // Get user info for the location
                var user = await _dataService.GetUserByIdAsync(location.UserId);
                if (user != null)
                {
                    var minutesAgo = (int)(DateTime.UtcNow - location.Timestamp).TotalMinutes;
                    
                    locationResponses.Add(new LocationResponse
                    {
                        UserId = location.UserId,
                        UserName = user.Name,
                        Latitude = location.Latitude,
                        Longitude = location.Longitude,
                        Accuracy = location.Accuracy,
                        Timestamp = location.Timestamp,
                        MinutesAgo = minutesAgo
                    });
                }
            }
        }

        // Remove duplicates (in case user is in multiple families with same members)
        return locationResponses
            .GroupBy(l => l.UserId)
            .Select(g => g.OrderByDescending(l => l.Timestamp).First())
            .ToList();
    }

    public async Task<List<LocationUpdate>> GetLocationHistoryAsync(int userId, int hours = 24)
    {
        return await _dataService.GetUserLocationHistoryAsync(userId, hours);
    }
}