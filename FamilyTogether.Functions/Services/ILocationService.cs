using FamilyTogether.Functions.Models;

namespace FamilyTogether.Functions.Services;

public interface ILocationService
{
    Task<LocationUpdate> UpdateLocationAsync(int userId, UpdateLocationRequest request);
    Task<List<LocationResponse>> GetFamilyLocationsAsync(int userId);
    Task<List<LocationUpdate>> GetLocationHistoryAsync(int userId, int hours = 24);
}