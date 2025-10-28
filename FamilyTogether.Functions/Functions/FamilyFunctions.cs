using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using System.Net;
using Newtonsoft.Json;
using FamilyTogether.Functions.Models;
using FamilyTogether.Functions.Services;

namespace FamilyTogether.Functions.Functions;

public class FamilyFunctions
{
    private readonly ILogger _logger;
    private readonly IFamilyService _familyService;
    private readonly IAuthService _authService;

    public FamilyFunctions(ILoggerFactory loggerFactory, IFamilyService familyService, IAuthService authService)
    {
        _logger = loggerFactory.CreateLogger<FamilyFunctions>();
        _familyService = familyService;
        _authService = authService;
    }

    [Function("CreateFamily")]
    public async Task<HttpResponseData> CreateFamily([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "family")] HttpRequestData req)
    {
        _logger.LogInformation("Create family request received");

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
            var createRequest = JsonConvert.DeserializeObject<CreateFamilyRequest>(requestBody);

            if (createRequest == null || string.IsNullOrEmpty(createRequest.Name))
            {
                var badRequestResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                await badRequestResponse.WriteAsJsonAsync(ApiResponse.CreateError("Family name is required"));
                return badRequestResponse;
            }

            var family = await _familyService.CreateFamilyAsync(userId.Value, createRequest);

            var response = req.CreateResponse(HttpStatusCode.Created);
            await response.WriteAsJsonAsync(ApiResponse<Family>.SuccessResult(family, "Family created successfully"));
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating family");
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteAsJsonAsync(ApiResponse.CreateError("Internal server error"));
            return errorResponse;
        }
    }

    [Function("JoinFamily")]
    public async Task<HttpResponseData> JoinFamily([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "family/join")] HttpRequestData req)
    {
        _logger.LogInformation("Join family request received");

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
            var joinRequest = JsonConvert.DeserializeObject<JoinFamilyRequest>(requestBody);

            if (joinRequest == null || string.IsNullOrEmpty(joinRequest.FamilyGuid))
            {
                var badRequestResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                await badRequestResponse.WriteAsJsonAsync(ApiResponse.CreateError("Family GUID is required"));
                return badRequestResponse;
            }

            var family = await _familyService.JoinFamilyAsync(userId.Value, joinRequest);

            if (family == null)
            {
                var notFoundResponse = req.CreateResponse(HttpStatusCode.NotFound);
                await notFoundResponse.WriteAsJsonAsync(ApiResponse.CreateError("Family not found"));
                return notFoundResponse;
            }

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(ApiResponse<Family>.SuccessResult(family, "Joined family successfully"));
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error joining family");
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteAsJsonAsync(ApiResponse.CreateError("Internal server error"));
            return errorResponse;
        }
    }

    [Function("GetUserFamilies")]
    public async Task<HttpResponseData> GetUserFamilies([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "family")] HttpRequestData req)
    {
        _logger.LogInformation("Get user families request received");

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

            var families = await _familyService.GetUserFamiliesAsync(userId.Value);

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(ApiResponse<List<Family>>.SuccessResult(families, "User families retrieved successfully"));
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting user families");
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteAsJsonAsync(ApiResponse.CreateError("Internal server error"));
            return errorResponse;
        }
    }

    [Function("LeaveFamily")]
    public async Task<HttpResponseData> LeaveFamily([HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "family/{familyId}/leave")] HttpRequestData req, int familyId)
    {
        _logger.LogInformation("Leave family request received for family {FamilyId}", familyId);

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

            var success = await _familyService.LeaveFamilyAsync(userId.Value, familyId);

            if (!success)
            {
                var notFoundResponse = req.CreateResponse(HttpStatusCode.NotFound);
                await notFoundResponse.WriteAsJsonAsync(ApiResponse.CreateError("Family not found or user is not a member"));
                return notFoundResponse;
            }

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(ApiResponse.CreateSuccess("Left family successfully"));
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error leaving family");
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteAsJsonAsync(ApiResponse.CreateError("Internal server error"));
            return errorResponse;
        }
    }

    [Function("RemoveFamilyMember")]
    public async Task<HttpResponseData> RemoveFamilyMember([HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "family/{familyId}/member/{memberUserId}")] HttpRequestData req, int familyId, int memberUserId)
    {
        _logger.LogInformation("Remove family member request received for family {FamilyId}, member {MemberUserId}", familyId, memberUserId);

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
            var adminUserId = _authService.ValidateJwtToken(token);
            
            if (adminUserId == null)
            {
                var unauthorizedResponse = req.CreateResponse(HttpStatusCode.Unauthorized);
                await unauthorizedResponse.WriteAsJsonAsync(ApiResponse.CreateError("Invalid or expired token"));
                return unauthorizedResponse;
            }

            var success = await _familyService.RemoveMemberAsync(adminUserId.Value, familyId, memberUserId);

            if (!success)
            {
                var forbiddenResponse = req.CreateResponse(HttpStatusCode.Forbidden);
                await forbiddenResponse.WriteAsJsonAsync(ApiResponse.CreateError("Not authorized to remove this member or member not found"));
                return forbiddenResponse;
            }

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(ApiResponse.CreateSuccess("Member removed successfully"));
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error removing family member");
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteAsJsonAsync(ApiResponse.CreateError("Internal server error"));
            return errorResponse;
        }
    }
}
