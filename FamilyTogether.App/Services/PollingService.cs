using System.Timers;
using FamilyTogether.App.Models;

namespace FamilyTogether.App.Services;

public class PollingService
{
    private readonly ApiService _apiService;
    private readonly LocationService _locationService;
    private System.Timers.Timer? _pollingTimer;
    private System.Timers.Timer? _locationTimer;
    private bool _isPolling = false;
    private bool _isLocationUpdating = false;
    
    // Battery optimization properties
    private int _currentPollingInterval = 30; // seconds
    private int _currentLocationInterval = 30; // seconds
    private DateTime _lastSuccessfulUpdate = DateTime.UtcNow;
    private int _consecutiveFailures = 0;
    private readonly int _maxConsecutiveFailures = 3;

    // Cache properties
    private List<LocationUpdate> _cachedLocations = new();
    private DateTime _lastCacheUpdate = DateTime.MinValue;
    private readonly TimeSpan _cacheValidityDuration = TimeSpan.FromSeconds(25); // Cache válido por 25 segundos
    private Dictionary<int, MemberStatus> _memberStatusCache = new();

    public event EventHandler<List<LocationUpdate>>? LocationsUpdated;
    public event EventHandler<string>? ErrorOccurred;
    public event EventHandler<MemberStatusChange>? MemberStatusChanged;

    public PollingService(ApiService apiService, LocationService locationService)
    {
        _apiService = apiService;
        _locationService = locationService;
        
        // Subscribe to API service events for better error handling
        _apiService.ConnectionStatusChanged += OnConnectionStatusChanged;
    }

    private void OnConnectionStatusChanged(object? sender, string status)
    {
        System.Diagnostics.Debug.WriteLine($"Connection status changed: {status}");
        
        // Adjust polling behavior based on connection status
        if (status.Contains("Desconectado"))
        {
            // Increase interval when offline to save battery
            SetPollingInterval(Math.Min(_currentPollingInterval * 2, 300)); // Max 5 minutes
        }
        else if (status.Contains("Conectado"))
        {
            // Reset to normal interval when back online
            SetPollingInterval(30);
            ResetFailureCount();
        }
    }

    public void StartPolling()
    {
        if (_isPolling) return;

        _isPolling = true;

        // Timer para obtener ubicaciones de la familia cada 30 segundos
        _pollingTimer = new System.Timers.Timer(30000); // 30 segundos
        _pollingTimer.Elapsed += OnPollingTimerElapsed;
        _pollingTimer.AutoReset = true;
        _pollingTimer.Start();

        // Timer para enviar ubicación propia cada 30 segundos
        _locationTimer = new System.Timers.Timer(30000); // 30 segundos
        _locationTimer.Elapsed += OnLocationTimerElapsed;
        _locationTimer.AutoReset = true;
        _locationTimer.Start();

        // Ejecutar inmediatamente
        _ = Task.Run(async () => await PollFamilyLocationsAsync());
        _ = Task.Run(async () => await UpdateCurrentLocationAsync());
    }

    public void StopPolling()
    {
        _isPolling = false;
        _isLocationUpdating = false;

        _pollingTimer?.Stop();
        _pollingTimer?.Dispose();
        _pollingTimer = null;

        _locationTimer?.Stop();
        _locationTimer?.Dispose();
        _locationTimer = null;
    }

    public void SetPollingInterval(int seconds)
    {
        _currentPollingInterval = seconds;
        _currentLocationInterval = seconds;
        
        if (_pollingTimer != null)
        {
            _pollingTimer.Interval = seconds * 1000;
        }
        if (_locationTimer != null)
        {
            _locationTimer.Interval = seconds * 1000;
        }
        
        System.Diagnostics.Debug.WriteLine($"Polling interval updated to {seconds} seconds");
    }

    public void SetLocationUpdateInterval(int seconds)
    {
        _currentLocationInterval = seconds;
        
        if (_locationTimer != null)
        {
            _locationTimer.Interval = seconds * 1000;
        }
        
        System.Diagnostics.Debug.WriteLine($"Location update interval updated to {seconds} seconds");
    }

    public int GetCurrentPollingInterval() => _currentPollingInterval;
    
    public int GetCurrentLocationInterval() => _currentLocationInterval;

    private async void OnPollingTimerElapsed(object? sender, ElapsedEventArgs e)
    {
        await PollFamilyLocationsAsync();
    }

    private async void OnLocationTimerElapsed(object? sender, ElapsedEventArgs e)
    {
        await UpdateCurrentLocationAsync();
    }

    private async Task PollFamilyLocationsAsync()
    {
        if (!_isPolling) return;

        try
        {
            // Verificar si el cache es válido
            if (IsCacheValid())
            {
                System.Diagnostics.Debug.WriteLine("Using cached locations to reduce server queries");
                LocationsUpdated?.Invoke(this, _cachedLocations);
                return;
            }

            // Check if we're online before attempting to poll
            if (!_apiService.IsOnline)
            {
                System.Diagnostics.Debug.WriteLine("Offline - using cached locations");
                if (_cachedLocations.Any())
                {
                    LocationsUpdated?.Invoke(this, _cachedLocations);
                }
                else
                {
                    ErrorOccurred?.Invoke(this, "Sin conexión y no hay datos en caché");
                }
                return;
            }

            var response = await _apiService.GetFamilyLocationsAsync();
            if (response.Success && response.Data != null)
            {
                // Detectar cambios de estado antes de actualizar cache
                DetectMemberStatusChanges(response.Data);
                
                // Actualizar cache
                UpdateCache(response.Data);
                
                LocationsUpdated?.Invoke(this, response.Data);
                _consecutiveFailures = 0;
                _lastSuccessfulUpdate = DateTime.UtcNow;
                
                // Reset polling interval to normal if it was increased due to failures
                if (_currentPollingInterval > 30)
                {
                    SetPollingInterval(30);
                }
            }
            else
            {
                _consecutiveFailures++;
                ErrorOccurred?.Invoke(this, response.Message);
                
                // Si hay error pero tenemos cache, usar cache
                if (_cachedLocations.Any())
                {
                    System.Diagnostics.Debug.WriteLine("Using cached locations due to server error");
                    LocationsUpdated?.Invoke(this, _cachedLocations);
                }
                
                // Increase polling interval on consecutive failures
                if (_consecutiveFailures >= _maxConsecutiveFailures)
                {
                    SetPollingInterval(Math.Min(_currentPollingInterval * 2, 300)); // Max 5 minutes
                }
            }
        }
        catch (Exception ex)
        {
            _consecutiveFailures++;
            ErrorOccurred?.Invoke(this, $"Error polling locations: {ex.Message}");
            
            // Si hay excepción pero tenemos cache, usar cache
            if (_cachedLocations.Any())
            {
                System.Diagnostics.Debug.WriteLine("Using cached locations due to exception");
                LocationsUpdated?.Invoke(this, _cachedLocations);
            }
            
            // Increase polling interval on exceptions
            if (_consecutiveFailures >= _maxConsecutiveFailures)
            {
                SetPollingInterval(Math.Min(_currentPollingInterval * 2, 300));
            }
        }
    }

    private async Task UpdateCurrentLocationAsync()
    {
        if (!_isLocationUpdating && _isPolling)
        {
            _isLocationUpdating = true;
            try
            {
                // Check if we should pause for battery optimization
                if (await _locationService.ShouldPauseForBatteryAsync())
                {
                    System.Diagnostics.Debug.WriteLine("Location update skipped due to low battery");
                    _consecutiveFailures++;
                    
                    // Increase interval if battery is consistently low
                    if (_consecutiveFailures >= _maxConsecutiveFailures)
                    {
                        SetLocationUpdateInterval(Math.Min(_currentLocationInterval * 2, 600)); // Max 10 minutes
                    }
                    return;
                }

                var location = await _locationService.GetCurrentLocationAsync();
                if (location != null)
                {
                    var response = await _apiService.UpdateLocationAsync(
                        location.Latitude, 
                        location.Longitude, 
                        (float)(location.Accuracy ?? 0));
                    
                    if (response.Success)
                    {
                        _lastSuccessfulUpdate = DateTime.UtcNow;
                        _consecutiveFailures = 0;
                        
                        // Reset to normal interval if we had increased it
                        if (_currentLocationInterval > 30)
                        {
                            SetLocationUpdateInterval(30);
                        }
                    }
                    else
                    {
                        _consecutiveFailures++;
                        ErrorOccurred?.Invoke(this, response.Message);
                        
                        // Increase interval on consecutive failures
                        if (_consecutiveFailures >= _maxConsecutiveFailures)
                        {
                            SetLocationUpdateInterval(Math.Min(_currentLocationInterval * 2, 300)); // Max 5 minutes
                        }
                    }
                }
                else
                {
                    _consecutiveFailures++;
                    System.Diagnostics.Debug.WriteLine("No location available for update");
                }
            }
            catch (Exception ex)
            {
                _consecutiveFailures++;
                ErrorOccurred?.Invoke(this, $"Error updating location: {ex.Message}");
                
                // Increase interval on exceptions
                if (_consecutiveFailures >= _maxConsecutiveFailures)
                {
                    SetLocationUpdateInterval(Math.Min(_currentLocationInterval * 2, 300));
                }
            }
            finally
            {
                _isLocationUpdating = false;
            }
        }
    }

    public bool IsPolling => _isPolling;
    
    public DateTime GetLastSuccessfulUpdate() => _lastSuccessfulUpdate;
    
    public int GetConsecutiveFailures() => _consecutiveFailures;
    
    public void ResetFailureCount()
    {
        _consecutiveFailures = 0;
    }
    
    public bool ShouldReduceFrequency()
    {
        // Reduce frequency if we haven't had a successful update in a while
        var timeSinceLastSuccess = DateTime.UtcNow - _lastSuccessfulUpdate;
        return timeSinceLastSuccess > TimeSpan.FromMinutes(5) || _consecutiveFailures >= _maxConsecutiveFailures;
    }

    private bool IsCacheValid()
    {
        return _cachedLocations.Any() && 
               DateTime.UtcNow - _lastCacheUpdate < _cacheValidityDuration;
    }

    private void UpdateCache(List<LocationUpdate> newLocations)
    {
        _cachedLocations = new List<LocationUpdate>(newLocations);
        _lastCacheUpdate = DateTime.UtcNow;
        System.Diagnostics.Debug.WriteLine($"Cache updated with {newLocations.Count} locations");
    }

    private void DetectMemberStatusChanges(List<LocationUpdate> newLocations)
    {
        foreach (var location in newLocations)
        {
            var currentStatus = new MemberStatus
            {
                UserId = location.UserId,
                UserName = location.UserName,
                IsOnline = location.IsOnline,
                LastSeen = location.LastSeen,
                MinutesAgo = location.MinutesAgo
            };

            if (_memberStatusCache.TryGetValue(location.UserId, out var previousStatus))
            {
                // Detectar cambio de estado online/offline
                if (previousStatus.IsOnline != currentStatus.IsOnline)
                {
                    var statusChange = new MemberStatusChange
                    {
                        UserId = location.UserId,
                        UserName = location.UserName,
                        PreviousStatus = previousStatus.IsOnline ? "En línea" : "Desconectado",
                        NewStatus = currentStatus.IsOnline ? "En línea" : "Desconectado",
                        ChangeType = currentStatus.IsOnline ? StatusChangeType.CameOnline : StatusChangeType.WentOffline,
                        Timestamp = DateTime.UtcNow
                    };

                    MemberStatusChanged?.Invoke(this, statusChange);
                    System.Diagnostics.Debug.WriteLine($"Status change detected: {location.UserName} is now {statusChange.NewStatus}");
                }

                // Detectar si alguien estuvo inactivo por mucho tiempo y ahora se activó
                if (previousStatus.MinutesAgo > 30 && currentStatus.MinutesAgo <= 5)
                {
                    var statusChange = new MemberStatusChange
                    {
                        UserId = location.UserId,
                        UserName = location.UserName,
                        PreviousStatus = "Inactivo",
                        NewStatus = "Activo",
                        ChangeType = StatusChangeType.BecameActive,
                        Timestamp = DateTime.UtcNow
                    };

                    MemberStatusChanged?.Invoke(this, statusChange);
                    System.Diagnostics.Debug.WriteLine($"Activity change detected: {location.UserName} became active");
                }

                // Detectar si alguien se volvió inactivo
                if (previousStatus.MinutesAgo <= 30 && currentStatus.MinutesAgo > 30)
                {
                    var statusChange = new MemberStatusChange
                    {
                        UserId = location.UserId,
                        UserName = location.UserName,
                        PreviousStatus = "Activo",
                        NewStatus = "Inactivo",
                        ChangeType = StatusChangeType.BecameInactive,
                        Timestamp = DateTime.UtcNow
                    };

                    MemberStatusChanged?.Invoke(this, statusChange);
                    System.Diagnostics.Debug.WriteLine($"Activity change detected: {location.UserName} became inactive");
                }
            }

            // Actualizar cache de estado
            _memberStatusCache[location.UserId] = currentStatus;
        }
    }

    public void ClearCache()
    {
        _cachedLocations.Clear();
        _memberStatusCache.Clear();
        _lastCacheUpdate = DateTime.MinValue;
        System.Diagnostics.Debug.WriteLine("Cache cleared");
    }

    public List<LocationUpdate> GetCachedLocations()
    {
        return new List<LocationUpdate>(_cachedLocations);
    }

    public bool HasValidCache()
    {
        return IsCacheValid();
    }

    public TimeSpan GetCacheAge()
    {
        return DateTime.UtcNow - _lastCacheUpdate;
    }
}