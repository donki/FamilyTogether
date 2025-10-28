using Microsoft.Maui.Essentials;

namespace FamilyTogether.App.Services;

public class BatteryOptimizationService
{
    private readonly LocationService _locationService;
    private readonly PollingService _pollingService;
    
    // Battery thresholds
    private const double CriticalBatteryLevel = 0.15;
    private const double LowBatteryLevel = 0.25;
    private const double MediumBatteryLevel = 0.50;
    
    // Polling intervals (in seconds)
    private const int NormalInterval = 30;
    private const int MediumInterval = 120;
    private const int LowInterval = 300;
    private const int CriticalInterval = 600;
    private const int StationaryInterval = 900;
    
    private DateTime _lastOptimizationCheck = DateTime.MinValue;
    private readonly TimeSpan _optimizationCheckInterval = TimeSpan.FromMinutes(2);
    
    public event EventHandler<string>? OptimizationStatusChanged;

    public BatteryOptimizationService(LocationService locationService, PollingService pollingService)
    {
        _locationService = locationService;
        _pollingService = pollingService;
    }

    public async Task<BatteryOptimizationResult> OptimizeForCurrentConditionsAsync()
    {
        try
        {
            // Don't check too frequently
            if (DateTime.UtcNow - _lastOptimizationCheck < _optimizationCheckInterval)
            {
                return new BatteryOptimizationResult
                {
                    ShouldUpdate = true,
                    RecommendedInterval = _pollingService.GetCurrentPollingInterval(),
                    Reason = "Recent optimization check"
                };
            }

            _lastOptimizationCheck = DateTime.UtcNow;

            var batteryLevel = Battery.Default.ChargeLevel;
            var batteryState = Battery.Default.State;
            var isCharging = batteryState == BatteryState.Charging;

            // Check if device is stationary
            var currentLocation = await _locationService.GetCurrentLocationAsync();
            var isStationary = currentLocation != null && _locationService.IsDeviceStationaryEnhanced(currentLocation);
            var stationaryDuration = _locationService.GetStationaryDuration();

            // Determine optimal settings
            var result = DetermineOptimalSettings(batteryLevel, isCharging, isStationary, stationaryDuration);
            
            // Apply optimizations
            ApplyOptimizations(result);
            
            return result;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error in battery optimization: {ex.Message}");
            return new BatteryOptimizationResult
            {
                ShouldUpdate = true,
                RecommendedInterval = NormalInterval,
                Reason = "Error in optimization"
            };
        }
    }

    private BatteryOptimizationResult DetermineOptimalSettings(
        double batteryLevel, 
        bool isCharging, 
        bool isStationary, 
        TimeSpan stationaryDuration)
    {
        var result = new BatteryOptimizationResult();

        // Critical battery - pause updates
        if (batteryLevel < CriticalBatteryLevel && !isCharging)
        {
            result.ShouldUpdate = false;
            result.RecommendedInterval = CriticalInterval;
            result.Reason = "Critical battery level";
            result.OptimizationLevel = OptimizationLevel.Critical;
            return result;
        }

        // Device stationary for extended period
        if (isStationary && stationaryDuration > TimeSpan.FromHours(1))
        {
            result.ShouldUpdate = true;
            result.RecommendedInterval = StationaryInterval;
            result.Reason = $"Device stationary for {stationaryDuration.TotalHours:F1} hours";
            result.OptimizationLevel = OptimizationLevel.High;
            return result;
        }

        // Device stationary for moderate period
        if (isStationary && stationaryDuration > TimeSpan.FromMinutes(30))
        {
            result.ShouldUpdate = true;
            result.RecommendedInterval = LowInterval;
            result.Reason = $"Device stationary for {stationaryDuration.TotalMinutes:F0} minutes";
            result.OptimizationLevel = OptimizationLevel.Medium;
            return result;
        }

        // Battery level optimizations
        if (isCharging)
        {
            result.ShouldUpdate = true;
            result.RecommendedInterval = NormalInterval;
            result.Reason = "Device charging - normal frequency";
            result.OptimizationLevel = OptimizationLevel.None;
        }
        else if (batteryLevel < LowBatteryLevel)
        {
            result.ShouldUpdate = true;
            result.RecommendedInterval = LowInterval;
            result.Reason = "Low battery level";
            result.OptimizationLevel = OptimizationLevel.High;
        }
        else if (batteryLevel < MediumBatteryLevel)
        {
            result.ShouldUpdate = true;
            result.RecommendedInterval = MediumInterval;
            result.Reason = "Medium battery level";
            result.OptimizationLevel = OptimizationLevel.Medium;
        }
        else
        {
            result.ShouldUpdate = true;
            result.RecommendedInterval = NormalInterval;
            result.Reason = "Good battery level";
            result.OptimizationLevel = OptimizationLevel.None;
        }

        return result;
    }

    private void ApplyOptimizations(BatteryOptimizationResult result)
    {
        // Update polling interval
        _pollingService.SetPollingInterval(result.RecommendedInterval);
        
        // Enable/disable battery optimization in location service
        _locationService.EnableBatteryOptimization(result.OptimizationLevel != OptimizationLevel.None);
        
        // Notify about optimization changes
        OptimizationStatusChanged?.Invoke(this, result.Reason);
        
        System.Diagnostics.Debug.WriteLine($"Battery optimization applied: {result.Reason} (Interval: {result.RecommendedInterval}s)");
    }

    public async Task<string> GetOptimizationStatusAsync()
    {
        try
        {
            var batteryLevel = Battery.Default.ChargeLevel;
            var batteryState = Battery.Default.State;
            var currentInterval = _pollingService.GetCurrentPollingInterval();
            
            var status = $"Batería: {batteryLevel:P0}";
            
            if (batteryState == BatteryState.Charging)
                status += " (Cargando)";
                
            status += $" | Intervalo: {currentInterval}s";
            
            var currentLocation = await _locationService.GetCurrentLocationAsync();
            if (currentLocation != null && _locationService.IsDeviceStationaryEnhanced(currentLocation))
            {
                var duration = _locationService.GetStationaryDuration();
                status += $" | Estático: {duration.TotalMinutes:F0}min";
            }
            
            return status;
        }
        catch (Exception ex)
        {
            return $"Error obteniendo estado: {ex.Message}";
        }
    }
}

public class BatteryOptimizationResult
{
    public bool ShouldUpdate { get; set; } = true;
    public int RecommendedInterval { get; set; } = 30;
    public string Reason { get; set; } = "";
    public OptimizationLevel OptimizationLevel { get; set; } = OptimizationLevel.None;
}

public enum OptimizationLevel
{
    None,
    Low,
    Medium,
    High,
    Critical
}