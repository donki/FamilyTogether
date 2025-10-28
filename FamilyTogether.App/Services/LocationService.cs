namespace FamilyTogether.App.Services;

public class LocationService
{
    private Location? _lastKnownLocation;
    private DateTime _lastLocationTime = DateTime.MinValue;
    private readonly TimeSpan _locationCacheTime = TimeSpan.FromMinutes(1);
    
    // Geofencing properties
    private Location? _geofenceCenter;
    private double _geofenceRadius = 100; // metros
    private DateTime _lastGeofenceCheck = DateTime.MinValue;
    private bool _isInsideGeofence = false;
    
    // Stationary detection properties
    private Location? _stationaryLocation;
    private DateTime _stationaryStartTime = DateTime.MinValue;
    private readonly double _stationaryThreshold = 50; // metros
    private readonly TimeSpan _stationaryTimeThreshold = TimeSpan.FromMinutes(10);
    
    // Battery optimization properties
    private bool _batteryOptimizationEnabled = true;
    private DateTime _lastBatteryCheck = DateTime.MinValue;
    private double _lastBatteryLevel = 1.0;

    public async Task<Location?> GetCurrentLocationAsync()
    {
        // Use optimized location method if battery optimization is enabled
        if (_batteryOptimizationEnabled)
        {
            return await GetOptimizedLocationAsync();
        }

        // Fallback to original method
        try
        {
            // Usar ubicación en caché si es reciente
            if (_lastKnownLocation != null && 
                DateTime.UtcNow - _lastLocationTime < _locationCacheTime)
            {
                return _lastKnownLocation;
            }

            var request = new GeolocationRequest
            {
                DesiredAccuracy = GeolocationAccuracy.Best,
                Timeout = TimeSpan.FromSeconds(10)
            };

            var location = await Geolocation.Default.GetLocationAsync(request);
            
            if (location != null)
            {
                _lastKnownLocation = location;
                _lastLocationTime = DateTime.UtcNow;
            }

            return location;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error getting location: {ex.Message}");
            return _lastKnownLocation; // Devolver última ubicación conocida si hay error
        }
    }

    public async Task<Location?> GetCachedLocationAsync()
    {
        try
        {
            var location = await Geolocation.Default.GetLastKnownLocationAsync();
            return location;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error getting cached location: {ex.Message}");
            return null;
        }
    }

    public double CalculateDistance(double lat1, double lon1, double lat2, double lon2)
    {
        var R = 6371; // Radio de la Tierra en kilómetros
        var dLat = ToRadians(lat2 - lat1);
        var dLon = ToRadians(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(ToRadians(lat1)) * Math.Cos(ToRadians(lat2)) *
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return R * c * 1000; // Convertir a metros
    }

    private static double ToRadians(double degrees)
    {
        return degrees * Math.PI / 180;
    }

    public bool IsLocationStale(DateTime timestamp, int maxMinutes = 30)
    {
        return DateTime.UtcNow - timestamp > TimeSpan.FromMinutes(maxMinutes);
    }

    public string GetTimeAgoString(DateTime timestamp)
    {
        var timeSpan = DateTime.UtcNow - timestamp;
        
        if (timeSpan.TotalMinutes < 1)
            return "Ahora";
        else if (timeSpan.TotalMinutes < 60)
            return $"hace {(int)timeSpan.TotalMinutes} min";
        else if (timeSpan.TotalHours < 24)
            return $"hace {(int)timeSpan.TotalHours} h";
        else
            return $"hace {(int)timeSpan.TotalDays} días";
    }

    public bool IsDeviceStationary(Location? currentLocation, Location? previousLocation, double thresholdMeters = 50)
    {
        if (currentLocation == null || previousLocation == null)
            return false;

        var distance = CalculateDistance(
            currentLocation.Latitude, currentLocation.Longitude,
            previousLocation.Latitude, previousLocation.Longitude);

        return distance < thresholdMeters;
    }

    public void ClearCache()
    {
        _lastKnownLocation = null;
        _lastLocationTime = DateTime.MinValue;
    }

    // Geofencing methods
    public void SetGeofence(Location center, double radiusMeters = 100)
    {
        _geofenceCenter = center;
        _geofenceRadius = radiusMeters;
        _lastGeofenceCheck = DateTime.UtcNow;
    }

    public bool IsInsideGeofence(Location currentLocation)
    {
        if (_geofenceCenter == null)
            return false;

        var distance = CalculateDistance(
            currentLocation.Latitude, currentLocation.Longitude,
            _geofenceCenter.Latitude, _geofenceCenter.Longitude);

        _isInsideGeofence = distance <= _geofenceRadius;
        return _isInsideGeofence;
    }

    public bool ShouldUpdateLocationBasedOnGeofence(Location currentLocation)
    {
        if (_geofenceCenter == null)
        {
            // Si no hay geofence establecido, crear uno en la ubicación actual
            SetGeofence(currentLocation);
            return true;
        }

        var wasInside = _isInsideGeofence;
        var isInside = IsInsideGeofence(currentLocation);

        // Actualizar si cambió el estado del geofence o si está fuera del geofence
        if (wasInside != isInside || !isInside)
        {
            if (!isInside)
            {
                // Mover el geofence a la nueva ubicación
                SetGeofence(currentLocation);
            }
            return true;
        }

        return false;
    }

    // Enhanced stationary detection
    public bool IsDeviceStationaryEnhanced(Location currentLocation)
    {
        if (_stationaryLocation == null)
        {
            _stationaryLocation = currentLocation;
            _stationaryStartTime = DateTime.UtcNow;
            return false;
        }

        var distance = CalculateDistance(
            currentLocation.Latitude, currentLocation.Longitude,
            _stationaryLocation.Latitude, _stationaryLocation.Longitude);

        if (distance < _stationaryThreshold)
        {
            // Dispositivo sigue estático
            var stationaryDuration = DateTime.UtcNow - _stationaryStartTime;
            return stationaryDuration >= _stationaryTimeThreshold;
        }
        else
        {
            // Dispositivo se movió, reiniciar detección
            _stationaryLocation = currentLocation;
            _stationaryStartTime = DateTime.UtcNow;
            return false;
        }
    }

    public TimeSpan GetStationaryDuration()
    {
        if (_stationaryLocation == null)
            return TimeSpan.Zero;

        return DateTime.UtcNow - _stationaryStartTime;
    }

    // Battery optimization methods
    public async Task<bool> ShouldPauseForBatteryAsync()
    {
        try
        {
            var batteryLevel = Battery.Default.ChargeLevel;
            var batteryState = Battery.Default.State;
            
            _lastBatteryLevel = batteryLevel;
            _lastBatteryCheck = DateTime.UtcNow;

            // Pausar si la batería está por debajo del 15% y no está cargando
            if (batteryLevel < 0.15 && batteryState != BatteryState.Charging)
            {
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error checking battery: {ex.Message}");
            return false;
        }
    }

    public async Task<GeolocationAccuracy> GetOptimalAccuracyForBatteryAsync()
    {
        try
        {
            var batteryLevel = Battery.Default.ChargeLevel;
            var batteryState = Battery.Default.State;

            // Si está cargando, usar la mejor precisión
            if (batteryState == BatteryState.Charging)
                return GeolocationAccuracy.Best;

            // Ajustar precisión según nivel de batería
            if (batteryLevel > 0.5)
                return GeolocationAccuracy.Best;
            else if (batteryLevel > 0.3)
                return GeolocationAccuracy.Medium;
            else
                return GeolocationAccuracy.Low;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error getting battery level: {ex.Message}");
            return GeolocationAccuracy.Medium;
        }
    }

    public async Task<Location?> GetOptimizedLocationAsync()
    {
        try
        {
            // Verificar si debemos pausar por batería baja
            if (await ShouldPauseForBatteryAsync())
            {
                System.Diagnostics.Debug.WriteLine("Location paused due to low battery");
                return _lastKnownLocation; // Devolver última ubicación conocida
            }

            // Usar ubicación en caché si es reciente y el dispositivo está estático
            if (_lastKnownLocation != null && 
                DateTime.UtcNow - _lastLocationTime < _locationCacheTime)
            {
                if (IsDeviceStationaryEnhanced(_lastKnownLocation))
                {
                    System.Diagnostics.Debug.WriteLine("Using cached location - device stationary");
                    return _lastKnownLocation;
                }
            }

            // Obtener precisión óptima según batería
            var accuracy = await GetOptimalAccuracyForBatteryAsync();
            
            var request = new GeolocationRequest
            {
                DesiredAccuracy = accuracy,
                Timeout = TimeSpan.FromSeconds(accuracy == GeolocationAccuracy.Best ? 15 : 10)
            };

            var location = await Geolocation.Default.GetLocationAsync(request);
            
            if (location != null)
            {
                // Verificar si necesitamos actualizar basado en geofencing
                var shouldUpdate = ShouldUpdateLocationBasedOnGeofence(location);
                
                if (shouldUpdate)
                {
                    _lastKnownLocation = location;
                    _lastLocationTime = DateTime.UtcNow;
                    System.Diagnostics.Debug.WriteLine($"Location updated - Accuracy: {accuracy}, Battery optimized: {_batteryOptimizationEnabled}");
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("Location update skipped - inside geofence");
                    return _lastKnownLocation; // No actualizar si está dentro del geofence
                }
            }

            return location;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error getting optimized location: {ex.Message}");
            return _lastKnownLocation; // Devolver última ubicación conocida si hay error
        }
    }

    public void EnableBatteryOptimization(bool enable)
    {
        _batteryOptimizationEnabled = enable;
    }

    public bool IsBatteryOptimizationEnabled => _batteryOptimizationEnabled;

    public double GetLastBatteryLevel() => _lastBatteryLevel;

    public DateTime GetLastBatteryCheck() => _lastBatteryCheck;
}