using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FamilyTogether.App.Models;

namespace FamilyTogether.App.Services;

public class ApiService
{
    private readonly HttpClient _httpClient;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly NetworkService _networkService;
    private readonly OfflineStorageService _offlineStorage;
    private readonly RetryPolicy _retryPolicy;
    private string? _authToken;

    public event EventHandler<string>? ConnectionStatusChanged;
    public event EventHandler<int>? PendingDataCountChanged;

    public ApiService(NetworkService networkService, OfflineStorageService offlineStorage)
    {
        _httpClient = new HttpClient();
        _httpClient.BaseAddress = new Uri("https://localhost:7000/api/"); // Cambiar por la URL del servidor
        _httpClient.Timeout = TimeSpan.FromSeconds(30); // 30 second timeout
        
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        
        _networkService = networkService;
        _offlineStorage = offlineStorage;
        _retryPolicy = new RetryPolicy();
        
        // Subscribe to network events
        _networkService.ConnectionStateChanged += OnConnectionStateChanged;
        _offlineStorage.PendingDataCountChanged += OnPendingDataCountChanged;
        
        // Start background sync process
        _ = Task.Run(BackgroundSyncProcess);
    }

    public void SetAuthToken(string token)
    {
        _authToken = token;
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private async Task<ApiResponse<T>> ExecuteWithRetryAsync<T>(Func<Task<ApiResponse<T>>> operation, string operationName)
    {
        var lastError = new NetworkError();
        
        for (int attempt = 1; attempt <= _retryPolicy.MaxRetries; attempt++)
        {
            try
            {
                // Check network connectivity before attempting
                if (!_networkService.IsOnline && attempt == 1)
                {
                    return new ApiResponse<T> 
                    { 
                        Success = false, 
                        Message = "Sin conexión a internet. Los datos se guardarán localmente." 
                    };
                }
                
                var result = await operation();
                
                if (result.Success)
                {
                    _networkService.ReportSuccessfulConnection();
                    return result;
                }
                
                // If it's the last attempt, return the result
                if (attempt == _retryPolicy.MaxRetries)
                {
                    return result;
                }
                
                // Wait before retry
                var delay = _retryPolicy.GetDelay(attempt);
                System.Diagnostics.Debug.WriteLine($"{operationName} failed (attempt {attempt}), retrying in {delay.TotalSeconds} seconds");
                await Task.Delay(delay);
            }
            catch (Exception ex)
            {
                lastError = _networkService.ClassifyError(ex);
                _networkService.ReportConnectionFailure(lastError);
                
                System.Diagnostics.Debug.WriteLine($"{operationName} exception (attempt {attempt}): {ex.Message}");
                
                // If it's not retryable or last attempt, return error
                if (!_networkService.ShouldRetry(lastError, attempt, _retryPolicy.MaxRetries) || 
                    attempt == _retryPolicy.MaxRetries)
                {
                    return new ApiResponse<T> 
                    { 
                        Success = false, 
                        Message = $"Error de conexión: {lastError.Message}" 
                    };
                }
                
                // Wait before retry
                var delay = _retryPolicy.GetDelay(attempt);
                await Task.Delay(delay);
            }
        }
        
        return new ApiResponse<T> 
        { 
            Success = false, 
            Message = $"Error después de {_retryPolicy.MaxRetries} intentos: {lastError.Message}" 
        };
    }

    private void OnConnectionStateChanged(object? sender, ConnectionState connectionState)
    {
        var status = connectionState.IsOnline ? "Conectado" : "Desconectado";
        if (!connectionState.IsOnline && _offlineStorage.HasPendingData())
        {
            status += $" ({_offlineStorage.GetPendingDataCount()} elementos pendientes)";
        }
        
        ConnectionStatusChanged?.Invoke(this, status);
        
        // If we're back online, trigger sync
        if (connectionState.IsOnline && _offlineStorage.HasPendingData())
        {
            _ = Task.Run(SyncPendingDataAsync);
        }
    }

    private void OnPendingDataCountChanged(object? sender, int count)
    {
        PendingDataCountChanged?.Invoke(this, count);
    }

    private async Task BackgroundSyncProcess()
    {
        while (true)
        {
            try
            {
                if (_networkService.IsOnline && _offlineStorage.HasPendingData())
                {
                    await SyncPendingDataAsync();
                }
                
                // Clean up expired data
                _offlineStorage.CleanupExpiredData();
                
                // Wait 2 minutes before next sync attempt
                await Task.Delay(TimeSpan.FromMinutes(2));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Background sync error: {ex.Message}");
                await Task.Delay(TimeSpan.FromMinutes(5)); // Wait longer on error
            }
        }
    }

    private async Task SyncPendingDataAsync()
    {
        if (!_networkService.IsOnline)
            return;
            
        System.Diagnostics.Debug.WriteLine("Starting sync of pending offline data");
        
        // Sync pending location updates
        var pendingLocations = _offlineStorage.GetPendingLocationUpdates();
        foreach (var location in pendingLocations)
        {
            try
            {
                var result = await UpdateLocationAsync(location.Latitude, location.Longitude, location.Accuracy);
                if (result.Success)
                {
                    _offlineStorage.RemoveLocationUpdate(location);
                    System.Diagnostics.Debug.WriteLine($"Synced location update for user {location.UserId}");
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to sync location update: {result.Message}");
                    break; // Stop syncing if we encounter errors
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error syncing location update: {ex.Message}");
                break;
            }
        }
        
        // Sync pending API requests
        var pendingRequests = _offlineStorage.GetPendingApiRequests();
        foreach (var request in pendingRequests)
        {
            try
            {
                var success = await ReplayApiRequest(request);
                if (success)
                {
                    _offlineStorage.RemoveApiRequest(request.Id);
                    System.Diagnostics.Debug.WriteLine($"Synced API request: {request.Method} {request.Endpoint}");
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to sync API request: {request.Method} {request.Endpoint}");
                    break;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error syncing API request: {ex.Message}");
                break;
            }
        }
        
        if (!_offlineStorage.HasPendingData())
        {
            _offlineStorage.UpdateLastSyncTime();
            System.Diagnostics.Debug.WriteLine("All pending data synced successfully");
        }
    }

    private async Task<bool> ReplayApiRequest(ApiRequest request)
    {
        try
        {
            HttpResponseMessage response;
            
            if (request.Method.ToUpper() == "POST" && !string.IsNullOrEmpty(request.JsonData))
            {
                var content = new StringContent(request.JsonData, Encoding.UTF8, "application/json");
                response = await _httpClient.PostAsync(request.Endpoint, content);
            }
            else if (request.Method.ToUpper() == "GET")
            {
                response = await _httpClient.GetAsync(request.Endpoint);
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"Unsupported method for replay: {request.Method}");
                return false;
            }
            
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error replaying API request: {ex.Message}");
            return false;
        }
    }

    public async Task<ApiResponse<LoginResponse>> LoginAsync(string email, string password)
    {
        return await ExecuteWithRetryAsync(async () =>
        {
            var request = new { email, password };
            var json = JsonSerializer.Serialize(request, _jsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync("auth/login", content);
            var responseJson = await response.Content.ReadAsStringAsync();
            
            if (response.IsSuccessStatusCode)
            {
                var apiResponse = JsonSerializer.Deserialize<ApiResponse<LoginResponse>>(responseJson, _jsonOptions);
                if (apiResponse?.Data?.Token != null)
                {
                    SetAuthToken(apiResponse.Data.Token);
                }
                return apiResponse ?? new ApiResponse<LoginResponse> { Success = false, Message = "Error de deserialización" };
            }
            else
            {
                var errorResponse = JsonSerializer.Deserialize<ApiResponse<object>>(responseJson, _jsonOptions);
                return new ApiResponse<LoginResponse> 
                { 
                    Success = false, 
                    Message = errorResponse?.Message ?? "Error de login" 
                };
            }
        }, "Login");
    }

    public async Task<ApiResponse<LoginResponse>> RegisterAsync(string email, string password, string name)
    {
        return await ExecuteWithRetryAsync(async () =>
        {
            var request = new { email, password, name };
            var json = JsonSerializer.Serialize(request, _jsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync("auth/register", content);
            var responseJson = await response.Content.ReadAsStringAsync();
            
            if (response.IsSuccessStatusCode)
            {
                var apiResponse = JsonSerializer.Deserialize<ApiResponse<LoginResponse>>(responseJson, _jsonOptions);
                if (apiResponse?.Data?.Token != null)
                {
                    SetAuthToken(apiResponse.Data.Token);
                }
                return apiResponse ?? new ApiResponse<LoginResponse> { Success = false, Message = "Error de deserialización" };
            }
            else
            {
                var errorResponse = JsonSerializer.Deserialize<ApiResponse<object>>(responseJson, _jsonOptions);
                return new ApiResponse<LoginResponse> 
                { 
                    Success = false, 
                    Message = errorResponse?.Message ?? "Error de registro" 
                };
            }
        }, "Register");
    }

    public async Task<ApiResponse<FamilyResponse>> CreateFamilyAsync(string name)
    {
        if (!_networkService.IsOnline)
        {
            _offlineStorage.StoreApiRequest("POST", "family/create", new { name });
            return new ApiResponse<FamilyResponse> 
            { 
                Success = false, 
                Message = "Sin conexión. La solicitud se procesará cuando se restablezca la conexión." 
            };
        }

        return await ExecuteWithRetryAsync(async () =>
        {
            var request = new { name };
            var json = JsonSerializer.Serialize(request, _jsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync("family/create", content);
            var responseJson = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<ApiResponse<FamilyResponse>>(responseJson, _jsonOptions) 
                ?? new ApiResponse<FamilyResponse> { Success = false, Message = "Error de deserialización" };
        }, "CreateFamily");
    }

    public async Task<ApiResponse<object>> JoinFamilyAsync(string familyGuid)
    {
        if (!_networkService.IsOnline)
        {
            _offlineStorage.StoreApiRequest("POST", "family/join", new { familyGuid });
            return new ApiResponse<object> 
            { 
                Success = false, 
                Message = "Sin conexión. La solicitud se procesará cuando se restablezca la conexión." 
            };
        }

        return await ExecuteWithRetryAsync(async () =>
        {
            var request = new { familyGuid };
            var json = JsonSerializer.Serialize(request, _jsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync("family/join", content);
            var responseJson = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<ApiResponse<object>>(responseJson, _jsonOptions) 
                ?? new ApiResponse<object> { Success = false, Message = "Error de deserialización" };
        }, "JoinFamily");
    }

    public async Task<ApiResponse<FamilyMembersResponse>> GetFamilyMembersAsync()
    {
        return await ExecuteWithRetryAsync(async () =>
        {
            var response = await _httpClient.GetAsync("family/members");
            var responseJson = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<ApiResponse<FamilyMembersResponse>>(responseJson, _jsonOptions) 
                ?? new ApiResponse<FamilyMembersResponse> { Success = false, Message = "Error de deserialización" };
        }, "GetFamilyMembers");
    }

    public async Task<ApiResponse<object>> UpdateLocationAsync(double latitude, double longitude, float accuracy)
    {
        // Always store location updates offline first for backup
        var locationUpdate = new LocationUpdate
        {
            UserId = 0, // Will be set by server
            Latitude = latitude,
            Longitude = longitude,
            Accuracy = accuracy,
            Timestamp = DateTime.UtcNow
        };

        if (!_networkService.IsOnline)
        {
            _offlineStorage.StoreLocationUpdate(locationUpdate);
            return new ApiResponse<object> 
            { 
                Success = false, 
                Message = "Sin conexión. Ubicación guardada localmente." 
            };
        }

        return await ExecuteWithRetryAsync(async () =>
        {
            var request = new { latitude, longitude, accuracy };
            var json = JsonSerializer.Serialize(request, _jsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync("location/update", content);
            var responseJson = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<ApiResponse<object>>(responseJson, _jsonOptions) 
                ?? new ApiResponse<object> { Success = false, Message = "Error de deserialización" };

            // If successful, we don't need to store offline
            if (!result.Success)
            {
                _offlineStorage.StoreLocationUpdate(locationUpdate);
            }

            return result;
        }, "UpdateLocation");
    }

    public async Task<ApiResponse<List<LocationUpdate>>> GetFamilyLocationsAsync()
    {
        return await ExecuteWithRetryAsync(async () =>
        {
            var response = await _httpClient.GetAsync("location/family-locations");
            var responseJson = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<ApiResponse<List<LocationUpdate>>>(responseJson, _jsonOptions) 
                ?? new ApiResponse<List<LocationUpdate>> { Success = false, Message = "Error de deserialización" };
        }, "GetFamilyLocations");
    }

    public bool IsOnline => _networkService.IsOnline;
    public bool HasPendingData => _offlineStorage.HasPendingData();
    public int GetPendingDataCount() => _offlineStorage.GetPendingDataCount();
    public DateTime GetLastSyncTime() => _offlineStorage.GetLastSyncTime();

    public async Task<bool> TestConnectionAsync()
    {
        return await _networkService.TestConnectivityAsync();
    }

    public void ClearOfflineData()
    {
        _offlineStorage.ClearAllPendingData();
    }

    public ConnectionState GetConnectionState()
    {
        return _networkService.GetConnectionState();
    }
}

public class ApiResponse<T>
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public T? Data { get; set; }
}

public class LoginResponse
{
    public User User { get; set; } = new();
    public string Token { get; set; } = string.Empty;
}

public class FamilyResponse
{
    public int FamilyId { get; set; }
    public string FamilyGuid { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}

public class FamilyMembersResponse
{
    public Family Family { get; set; } = new();
    public List<FamilyMember> Members { get; set; } = new();
}