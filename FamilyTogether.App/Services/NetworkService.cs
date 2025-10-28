using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using FamilyTogether.App.Models;

namespace FamilyTogether.App.Services;

public class NetworkService
{
    private ConnectionState _connectionState;
    private readonly Timer _connectionMonitor;
    private readonly object _lockObject = new object();
    
    public event EventHandler<ConnectionState>? ConnectionStateChanged;
    public event EventHandler<NetworkError>? NetworkErrorOccurred;
    
    public NetworkService()
    {
        _connectionState = new ConnectionState
        {
            IsConnected = true,
            IsOnline = true,
            LastSuccessfulConnection = DateTime.UtcNow,
            Quality = ConnectionQuality.Good
        };
        
        // Monitor connection every 30 seconds
        _connectionMonitor = new Timer(CheckConnectionStatus, null, TimeSpan.Zero, TimeSpan.FromSeconds(30));
    }
    
    public ConnectionState GetConnectionState()
    {
        lock (_lockObject)
        {
            return new ConnectionState
            {
                IsConnected = _connectionState.IsConnected,
                IsOnline = _connectionState.IsOnline,
                LastSuccessfulConnection = _connectionState.LastSuccessfulConnection,
                ConsecutiveFailures = _connectionState.ConsecutiveFailures,
                LastError = _connectionState.LastError,
                Quality = _connectionState.Quality
            };
        }
    }
    
    public bool IsConnected => _connectionState.IsConnected;
    public bool IsOnline => _connectionState.IsOnline;
    
    public async Task<bool> TestConnectivityAsync()
    {
        try
        {
            // Test network interface availability
            if (!NetworkInterface.GetIsNetworkAvailable())
            {
                UpdateConnectionState(false, false, ConnectionQuality.Offline, 
                    new NetworkError 
                    { 
                        ErrorType = "NetworkUnavailable", 
                        Message = "No network interface available",
                        Timestamp = DateTime.UtcNow,
                        IsRetryable = true
                    });
                return false;
            }
            
            // Test internet connectivity with a quick ping
            using var ping = new Ping();
            var reply = await ping.SendPingAsync("8.8.8.8", 5000); // Google DNS, 5 second timeout
            
            if (reply.Status == IPStatus.Success)
            {
                var quality = reply.RoundtripTime switch
                {
                    < 100 => ConnectionQuality.Excellent,
                    < 300 => ConnectionQuality.Good,
                    < 1000 => ConnectionQuality.Poor,
                    _ => ConnectionQuality.Poor
                };
                
                UpdateConnectionState(true, true, quality, null);
                return true;
            }
            else
            {
                UpdateConnectionState(true, false, ConnectionQuality.Offline,
                    new NetworkError 
                    { 
                        ErrorType = "PingFailed", 
                        Message = $"Ping failed: {reply.Status}",
                        Timestamp = DateTime.UtcNow,
                        IsRetryable = true
                    });
                return false;
            }
        }
        catch (Exception ex)
        {
            UpdateConnectionState(false, false, ConnectionQuality.Offline,
                new NetworkError 
                { 
                    ErrorType = "ConnectivityTestFailed", 
                    Message = ex.Message,
                    Timestamp = DateTime.UtcNow,
                    IsRetryable = true
                });
            return false;
        }
    }
    
    public NetworkError ClassifyError(Exception exception)
    {
        var error = new NetworkError
        {
            Timestamp = DateTime.UtcNow,
            Message = exception.Message
        };
        
        switch (exception)
        {
            case HttpRequestException httpEx:
                error.ErrorType = "HttpRequest";
                error.IsRetryable = true;
                error.RetryDelay = TimeSpan.FromSeconds(5);
                break;
                
            case TaskCanceledException timeoutEx when timeoutEx.InnerException is TimeoutException:
                error.ErrorType = "Timeout";
                error.IsRetryable = true;
                error.RetryDelay = TimeSpan.FromSeconds(10);
                break;
                
            case SocketException socketEx:
                error.ErrorType = "Socket";
                error.IsRetryable = socketEx.SocketErrorCode != SocketError.HostNotFound;
                error.RetryDelay = TimeSpan.FromSeconds(15);
                break;
                
            case WebException webEx:
                error.ErrorType = "Web";
                error.IsRetryable = webEx.Status != WebExceptionStatus.NameResolutionFailure;
                error.RetryDelay = TimeSpan.FromSeconds(10);
                break;
                
            default:
                error.ErrorType = "Unknown";
                error.IsRetryable = false;
                error.RetryDelay = TimeSpan.FromMinutes(1);
                break;
        }
        
        return error;
    }
    
    public bool ShouldRetry(NetworkError error, int currentRetryCount, int maxRetries)
    {
        if (!error.IsRetryable || currentRetryCount >= maxRetries)
            return false;
            
        // Don't retry if we've been offline for too long
        var timeSinceLastSuccess = DateTime.UtcNow - _connectionState.LastSuccessfulConnection;
        if (timeSinceLastSuccess > TimeSpan.FromMinutes(30))
            return false;
            
        return true;
    }
    
    private void CheckConnectionStatus(object? state)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await TestConnectivityAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Connection monitoring error: {ex.Message}");
            }
        });
    }
    
    private void UpdateConnectionState(bool isConnected, bool isOnline, ConnectionQuality quality, NetworkError? error)
    {
        bool stateChanged = false;
        
        lock (_lockObject)
        {
            var previousState = new ConnectionState
            {
                IsConnected = _connectionState.IsConnected,
                IsOnline = _connectionState.IsOnline,
                Quality = _connectionState.Quality
            };
            
            _connectionState.IsConnected = isConnected;
            _connectionState.IsOnline = isOnline;
            _connectionState.Quality = quality;
            _connectionState.LastError = error;
            
            if (isConnected && isOnline)
            {
                _connectionState.LastSuccessfulConnection = DateTime.UtcNow;
                _connectionState.ConsecutiveFailures = 0;
            }
            else
            {
                _connectionState.ConsecutiveFailures++;
            }
            
            // Check if state actually changed
            stateChanged = previousState.IsConnected != isConnected || 
                          previousState.IsOnline != isOnline || 
                          previousState.Quality != quality;
        }
        
        if (stateChanged)
        {
            ConnectionStateChanged?.Invoke(this, GetConnectionState());
            System.Diagnostics.Debug.WriteLine($"Connection state changed: Connected={isConnected}, Online={isOnline}, Quality={quality}");
        }
        
        if (error != null)
        {
            NetworkErrorOccurred?.Invoke(this, error);
        }
    }
    
    public void ReportSuccessfulConnection()
    {
        lock (_lockObject)
        {
            _connectionState.LastSuccessfulConnection = DateTime.UtcNow;
            _connectionState.ConsecutiveFailures = 0;
            
            if (!_connectionState.IsConnected || !_connectionState.IsOnline)
            {
                UpdateConnectionState(true, true, ConnectionQuality.Good, null);
            }
        }
    }
    
    public void ReportConnectionFailure(NetworkError error)
    {
        lock (_lockObject)
        {
            _connectionState.ConsecutiveFailures++;
            _connectionState.LastError = error;
            
            // If we have too many consecutive failures, mark as offline
            if (_connectionState.ConsecutiveFailures >= 3)
            {
                UpdateConnectionState(false, false, ConnectionQuality.Offline, error);
            }
        }
        
        NetworkErrorOccurred?.Invoke(this, error);
    }
    
    public void Dispose()
    {
        _connectionMonitor?.Dispose();
    }
}