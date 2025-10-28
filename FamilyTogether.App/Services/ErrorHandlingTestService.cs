using FamilyTogether.App.Models;

namespace FamilyTogether.App.Services;

/// <summary>
/// Service for testing error handling and reconnection functionality
/// This service can be used to simulate network failures and test recovery
/// </summary>
public class ErrorHandlingTestService
{
    private readonly NetworkService _networkService;
    private readonly OfflineStorageService _offlineStorage;
    private readonly ApiService _apiService;
    
    public ErrorHandlingTestService(
        NetworkService networkService, 
        OfflineStorageService offlineStorage, 
        ApiService apiService)
    {
        _networkService = networkService;
        _offlineStorage = offlineStorage;
        _apiService = apiService;
    }
    
    /// <summary>
    /// Test network connectivity and error classification
    /// </summary>
    public async Task<string> TestNetworkConnectivityAsync()
    {
        var result = await _networkService.TestConnectivityAsync();
        var connectionState = _networkService.GetConnectionState();
        
        return $"Connectivity Test Result: {result}\n" +
               $"Connection State: {connectionState.IsOnline}\n" +
               $"Quality: {connectionState.Quality}\n" +
               $"Consecutive Failures: {connectionState.ConsecutiveFailures}";
    }
    
    /// <summary>
    /// Test offline storage functionality
    /// </summary>
    public string TestOfflineStorage()
    {
        // Store test location update
        var testLocation = new LocationUpdate
        {
            UserId = 999,
            UserName = "Test User",
            Latitude = 40.7128,
            Longitude = -74.0060,
            Accuracy = 5.0f,
            Timestamp = DateTime.UtcNow
        };
        
        _offlineStorage.StoreLocationUpdate(testLocation);
        
        // Store test API request
        _offlineStorage.StoreApiRequest("POST", "test/endpoint", new { test = "data" });
        
        var pendingLocations = _offlineStorage.GetPendingLocationUpdates().Count;
        var pendingRequests = _offlineStorage.GetPendingApiRequests().Count;
        var lastSync = _offlineStorage.GetLastSyncTime();
        
        return $"Offline Storage Test:\n" +
               $"Pending Locations: {pendingLocations}\n" +
               $"Pending Requests: {pendingRequests}\n" +
               $"Last Sync: {lastSync:HH:mm:ss}\n" +
               $"Has Pending Data: {_offlineStorage.HasPendingData()}";
    }
    
    /// <summary>
    /// Test retry policy behavior
    /// </summary>
    public string TestRetryPolicy()
    {
        var retryPolicy = new RetryPolicy();
        var results = new List<string>();
        
        for (int i = 1; i <= retryPolicy.MaxRetries; i++)
        {
            var delay = retryPolicy.GetDelay(i);
            results.Add($"Attempt {i}: {delay.TotalSeconds:F1}s delay");
        }
        
        return $"Retry Policy Test:\n" +
               $"Max Retries: {retryPolicy.MaxRetries}\n" +
               $"Initial Delay: {retryPolicy.InitialDelay.TotalSeconds}s\n" +
               $"Max Delay: {retryPolicy.MaxDelay.TotalSeconds}s\n" +
               $"Backoff Multiplier: {retryPolicy.BackoffMultiplier}\n" +
               string.Join("\n", results);
    }
    
    /// <summary>
    /// Test error classification
    /// </summary>
    public string TestErrorClassification()
    {
        var errors = new List<Exception>
        {
            new HttpRequestException("HTTP request failed"),
            new TaskCanceledException("Request timeout", new TimeoutException()),
            new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.HostNotFound),
            new InvalidOperationException("Unknown error")
        };
        
        var results = new List<string>();
        
        foreach (var error in errors)
        {
            var networkError = _networkService.ClassifyError(error);
            results.Add($"{error.GetType().Name}: {networkError.ErrorType} (Retryable: {networkError.IsRetryable})");
        }
        
        return $"Error Classification Test:\n" + string.Join("\n", results);
    }
    
    /// <summary>
    /// Simulate network failure and recovery
    /// </summary>
    public async Task<string> SimulateNetworkFailureAsync()
    {
        var initialState = _networkService.GetConnectionState();
        
        // Simulate a network error
        var simulatedError = new NetworkError
        {
            ErrorType = "Simulated",
            Message = "Simulated network failure for testing",
            Timestamp = DateTime.UtcNow,
            IsRetryable = true,
            RetryDelay = TimeSpan.FromSeconds(5)
        };
        
        _networkService.ReportConnectionFailure(simulatedError);
        var failureState = _networkService.GetConnectionState();
        
        // Wait a moment
        await Task.Delay(1000);
        
        // Simulate recovery
        _networkService.ReportSuccessfulConnection();
        var recoveryState = _networkService.GetConnectionState();
        
        return $"Network Failure Simulation:\n" +
               $"Initial: Online={initialState.IsOnline}, Failures={initialState.ConsecutiveFailures}\n" +
               $"After Failure: Online={failureState.IsOnline}, Failures={failureState.ConsecutiveFailures}\n" +
               $"After Recovery: Online={recoveryState.IsOnline}, Failures={recoveryState.ConsecutiveFailures}";
    }
    
    /// <summary>
    /// Get comprehensive status report
    /// </summary>
    public string GetStatusReport()
    {
        var connectionState = _networkService.GetConnectionState();
        var offlineData = _offlineStorage.GetOfflineDataSnapshot();
        
        return $"Error Handling Status Report:\n" +
               $"=================================\n" +
               $"Network Status: {(connectionState.IsOnline ? "Online" : "Offline")}\n" +
               $"Connection Quality: {connectionState.Quality}\n" +
               $"Last Successful Connection: {connectionState.LastSuccessfulConnection:HH:mm:ss}\n" +
               $"Consecutive Failures: {connectionState.ConsecutiveFailures}\n" +
               $"Pending Location Updates: {offlineData.PendingLocationUpdates.Count}\n" +
               $"Pending API Requests: {offlineData.PendingRequests.Count}\n" +
               $"Last Sync: {offlineData.LastSync:HH:mm:ss}\n" +
               $"API Service Online: {_apiService.IsOnline}\n" +
               $"API Pending Data: {_apiService.GetPendingDataCount()}\n" +
               $"Last API Sync: {_apiService.GetLastSyncTime():HH:mm:ss}";
    }
    
    /// <summary>
    /// Clean up test data
    /// </summary>
    public void CleanupTestData()
    {
        _offlineStorage.ClearAllPendingData();
        _apiService.ClearOfflineData();
    }
}