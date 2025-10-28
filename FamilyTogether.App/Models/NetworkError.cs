namespace FamilyTogether.App.Models;

public class NetworkError
{
    public string ErrorType { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public int RetryCount { get; set; }
    public bool IsRetryable { get; set; }
    public TimeSpan RetryDelay { get; set; }
}

public class ConnectionState
{
    public bool IsConnected { get; set; }
    public bool IsOnline { get; set; }
    public DateTime LastSuccessfulConnection { get; set; }
    public int ConsecutiveFailures { get; set; }
    public NetworkError? LastError { get; set; }
    public ConnectionQuality Quality { get; set; }
}

public enum ConnectionQuality
{
    Excellent,
    Good,
    Poor,
    Offline
}

public class OfflineData
{
    public List<LocationUpdate> PendingLocationUpdates { get; set; } = new();
    public List<ApiRequest> PendingRequests { get; set; } = new();
    public DateTime LastSync { get; set; }
    public bool HasPendingData => PendingLocationUpdates.Any() || PendingRequests.Any();
}

public class ApiRequest
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Method { get; set; } = string.Empty;
    public string Endpoint { get; set; } = string.Empty;
    public string? JsonData { get; set; }
    public DateTime CreatedAt { get; set; }
    public int RetryCount { get; set; }
    public int MaxRetries { get; set; } = 3;
    public bool IsExpired => DateTime.UtcNow - CreatedAt > TimeSpan.FromHours(24);
}

public class RetryPolicy
{
    public int MaxRetries { get; set; } = 3;
    public TimeSpan InitialDelay { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromMinutes(5);
    public double BackoffMultiplier { get; set; } = 2.0;
    public bool UseJitter { get; set; } = true;
    
    public TimeSpan GetDelay(int attemptNumber)
    {
        var delay = TimeSpan.FromTicks((long)(InitialDelay.Ticks * Math.Pow(BackoffMultiplier, attemptNumber - 1)));
        
        if (delay > MaxDelay)
            delay = MaxDelay;
            
        if (UseJitter)
        {
            var jitter = new Random().NextDouble() * 0.1; // 10% jitter
            delay = TimeSpan.FromTicks((long)(delay.Ticks * (1 + jitter)));
        }
        
        return delay;
    }
}