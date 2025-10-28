using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using System.Net;
using Newtonsoft.Json;
using FamilyTogether.Functions.Models;
using FamilyTogether.Functions.Services;

namespace FamilyTogether.Functions.Functions;

public class LocationFunctions
{
    private readonly ILogger _logger;
    private readonly ILocationService _locationService;
    private readonly IAuthService _authService;

    public LocationFunctions(ILoggerFactory loggerFactory, ILocationService locationService, IAuthService authService)
    {
        _logger = loggerFactory.CreateLogger<LocationFunctions>();
        _locationService = locationService;
        _authService = authService;
    }

    [Function("UpdateLocation")]
    public async Task<HttpResponseData> UpdateLocation([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "location")] HttpRequestData req)
    {
        _logger.LogInformation("Update location request received");

        try
        {
            // Validate JWT token
            var authHeader = req.Headers.FirstOrDefault(h => h.Key.ToLower() == "authorization");
            if (authHeader.Key == null || !authHeader.Value.Any())
            {
                var unauthorizedResponse = req.CreateResponse(HttpStatusCode.Unauthorized);
                await unauthorizedResponse.WriteAsJsonAsync(ApiResponse.CreateError("Authorization header required"));
                return unauthorizedResponse;
            }

            var token = authHeader.Value.First().Replace("Bearer ", "");
            var userId = _authService.ValidateJwtToken(token);
            
            if (userId == null)
            {
                var unauthorizedResponse = req.CreateResponse(HttpStatusCode.Unauthorized);
                await unauthorizedResponse.WriteAsJsonAsync(ApiResponse.CreateError("Invalid or expired token"));
                return unauthorizedResponse;
            }

            var requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            var updateRequest = JsonConvert.DeserializeObject<UpdateLocationRequest>(requestBody);

            if (updateRequest == null)
            {
                var badRequestResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                await badRequestResponse.WriteAsJsonAsync(ApiResponse.CreateError("Invalid request body"));
                return badRequestResponse;
            }

            var locationUpdate = await _locationService.UpdateLocationAsync(userId.Value, updateRequest);

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(ApiResponse<LocationUpdate>.SuccessResult(locationUpdate, "Location updated successfully"));
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating location");
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteAsJsonAsync(ApiResponse.CreateError("Internal server error"));
            return errorResponse;
        }
    }

    [Function("GetFamilyLocations")]
    public async Task<HttpResponseData> GetFamilyLocations([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "location/family")] HttpRequestData req)
    {
        _logger.LogInformation("Get family locations request received");

        try
        {
            // Validate JWT token
            var authHeader = req.Headers.FirstOrDefault(h => h.Key.ToLower() == "authorization");
            if (authHeader.Key == null || !authHeader.Value.Any())
            {
                var unauthorizedResponse = req.CreateResponse(HttpStatusCode.Unauthorized);
                await unauthorizedResponse.WriteAsJsonAsync(ApiResponse.CreateError("Authorization header required"));
                return unauthorizedResponse;
            }

            var token = authHeader.Value.First().Replace("Bearer ", "");
            var userId = _authService.ValidateJwtToken(token);
            
            if (userId == null)
            {
                var unauthorizedResponse = req.CreateResponse(HttpStatusCode.Unauthorized);
                await unauthorizedResponse.WriteAsJsonAsync(ApiResponse.CreateError("Invalid or expired token"));
                return unauthorizedResponse;
            }

            var locations = await _locationService.GetFamilyLocationsAsync(userId.Value);

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(ApiResponse<List<LocationResponse>>.SuccessResult(locations, "Family locations retrieved successfully"));
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting family locations");
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteAsJsonAsync(ApiResponse.CreateError("Internal server error"));
            return errorResponse;
        }
    }

    [Function("GetLocationHistory")]
    public async Task<HttpResponseData> GetLocationHistory([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "location/history")] HttpRequestData req)
    {
        _logger.LogInformation("Get location history request received");

        try
        {
            // Validate JWT token
            var authHeader = req.Headers.FirstOrDefault(h => h.Key.ToLower() == "authorization");
            if (authHeader.Key == null || !authHeader.Value.Any())
            {
                var unauthorizedResponse = req.CreateResponse(HttpStatusCode.Unauthorized);
                await unauthorizedResponse.WriteAsJsonAsync(ApiResponse.CreateError("Authorization header required"));
                return unauthorizedResponse;
            }

            var token = authHeader.Value.First().Replace("Bearer ", "");
            var userId = _authService.ValidateJwtToken(token);
            
            if (userId == null)
            {
                var unauthorizedResponse = req.CreateResponse(HttpStatusCode.Unauthorized);
                await unauthorizedResponse.WriteAsJsonAsync(ApiResponse.CreateError("Invalid or expired token"));
                return unauthorizedResponse;
            }

            // Get hours parameter (default 24)
            var hoursParam = req.Query["hours"];
            var hours = 24;
            if (!string.IsNullOrEmpty(hoursParam) && int.TryParse(hoursParam, out var parsedHours))
            {
                hours = Math.Max(1, Math.Min(168, parsedHours)); // Between 1 hour and 1 week
            }

            var history = await _locationService.GetLocationHistoryAsync(userId.Value, hours);

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(ApiResponse<List<LocationUpdate>>.SuccessResult(history, "Location history retrieved successfully"));
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting location history");
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteAsJsonAsync(ApiResponse.CreateError("Internal server error"));
            return errorResponse;
        }
    }
}
