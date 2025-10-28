using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using System.Net;
using Newtonsoft.Json;
using FamilyTogether.Functions.Models;
using FamilyTogether.Functions.Services;

namespace FamilyTogether.Functions.Functions;

public class AuthFunctions
{
    private readonly ILogger _logger;
    private readonly IAuthService _authService;

    public AuthFunctions(ILoggerFactory loggerFactory, IAuthService authService)
    {
        _logger = loggerFactory.CreateLogger<AuthFunctions>();
        _authService = authService;
    }

    [Function("Login")]
    public async Task<HttpResponseData> Login([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "auth/login")] HttpRequestData req)
    {
        _logger.LogInformation("Login request received");

        try
        {
            var requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            var loginRequest = JsonConvert.DeserializeObject<LoginRequest>(requestBody);

            if (loginRequest == null || string.IsNullOrEmpty(loginRequest.Email) || string.IsNullOrEmpty(loginRequest.Password))
            {
                var badRequestResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                await badRequestResponse.WriteAsJsonAsync(ApiResponse.CreateError("Email and password are required"));
                return badRequestResponse;
            }

            var authResponse = await _authService.LoginAsync(loginRequest);
            
            if (authResponse == null)
            {
                var unauthorizedResponse = req.CreateResponse(HttpStatusCode.Unauthorized);
                await unauthorizedResponse.WriteAsJsonAsync(ApiResponse.CreateError("Invalid email or password"));
                return unauthorizedResponse;
            }

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(ApiResponse<AuthResponse>.SuccessResult(authResponse, "Login successful"));
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during login");
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteAsJsonAsync(ApiResponse.CreateError("Internal server error"));
            return errorResponse;
        }
    }

    [Function("Register")]
    public async Task<HttpResponseData> Register([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "auth/register")] HttpRequestData req)
    {
        _logger.LogInformation("Register request received");

        try
        {
            var requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            var registerRequest = JsonConvert.DeserializeObject<RegisterRequest>(requestBody);

            if (registerRequest == null || string.IsNullOrEmpty(registerRequest.Email) || 
                string.IsNullOrEmpty(registerRequest.Password) || string.IsNullOrEmpty(registerRequest.Name))
            {
                var badRequestResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                await badRequestResponse.WriteAsJsonAsync(ApiResponse.CreateError("Email, name, and password are required"));
                return badRequestResponse;
            }

            if (registerRequest.Password.Length < 6)
            {
                var badRequestResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                await badRequestResponse.WriteAsJsonAsync(ApiResponse.CreateError("Password must be at least 6 characters long"));
                return badRequestResponse;
            }

            var authResponse = await _authService.RegisterAsync(registerRequest);
            
            if (authResponse == null)
            {
                var conflictResponse = req.CreateResponse(HttpStatusCode.Conflict);
                await conflictResponse.WriteAsJsonAsync(ApiResponse.CreateError("User with this email already exists"));
                return conflictResponse;
            }

            var response = req.CreateResponse(HttpStatusCode.Created);
            await response.WriteAsJsonAsync(ApiResponse<AuthResponse>.SuccessResult(authResponse, "Registration successful"));
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during registration");
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteAsJsonAsync(ApiResponse.CreateError("Internal server error"));
            return errorResponse;
        }
    }
}
