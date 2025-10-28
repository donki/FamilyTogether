using FamilyTogether.App.Models;

namespace FamilyTogether.App.Services;

public class BackgroundService
{
    private readonly PollingService _pollingService;
    private readonly LocationService _locationService;
    private readonly PermissionService _permissionService;
    private readonly BatteryOptimizationService _batteryOptimizationService;
    private readonly NotificationService _notificationService;
    private bool _isRunning = false;
    private Location? _lastLocation;
    private DateTime _lastMovementCheck = DateTime.UtcNow;

    public event EventHandler<List<LocationUpdate>>? LocationsUpdated;
    public event EventHandler<string>? StatusChanged;
    public event EventHandler<StatusNotification>? NotificationReceived;

    public BackgroundService(
        PollingService pollingService, 
        LocationService locationService, 
        PermissionService permissionService,
        NotificationService notificationService,
        ApiService apiService)
    {
        _pollingService = pollingService;
        _locationService = locationService;
        _permissionService = permissionService;
        _notificationService = notificationService;
        _batteryOptimizationService = new BatteryOptimizationService(_locationService, _pollingService);

        _pollingService.LocationsUpdated += OnLocationsUpdated;
        _pollingService.ErrorOccurred += OnPollingError;
        _pollingService.MemberStatusChanged += OnMemberStatusChanged;
        _batteryOptimizationService.OptimizationStatusChanged += OnOptimizationStatusChanged;
        _notificationService.NotificationAdded += OnNotificationAdded;
        
        // Subscribe to API service events for connection status
        apiService.ConnectionStatusChanged += OnConnectionStatusChanged;
        apiService.PendingDataCountChanged += OnPendingDataCountChanged;
    }

    private void OnConnectionStatusChanged(object? sender, string status)
    {
        StatusChanged?.Invoke(this, $"Conexión: {status}");
    }

    private void OnPendingDataCountChanged(object? sender, int count)
    {
        if (count > 0)
        {
            StatusChanged?.Invoke(this, $"Datos pendientes: {count} elementos");
        }
    }

    public async Task<bool> StartAsync()
    {
        if (_isRunning)
            return true;

        // Verificar permisos
        var hasPermissions = await _permissionService.CheckLocationPermissionsAsync();
        if (!hasPermissions)
        {
            StatusChanged?.Invoke(this, "Permisos de ubicación requeridos");
            return false;
        }

        try
        {
            _isRunning = true;
            _pollingService.StartPolling();
            
            // Iniciar monitoreo optimizado de batería y movimiento
            _ = Task.Run(MonitorDeviceStateAsync);
            
            StatusChanged?.Invoke(this, "Servicio iniciado");
            
            // Guardar estado en preferencias
            Microsoft.Maui.Storage.Preferences.Default.Set("IsLocationServiceRunning", true);
            
            return true;
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke(this, $"Error al iniciar servicio: {ex.Message}");
            return false;
        }
    }

    public void Stop()
    {
        if (!_isRunning)
            return;

        _isRunning = false;
        _pollingService.StopPolling();
        
        StatusChanged?.Invoke(this, "Servicio detenido");
        
        // Guardar estado en preferencias
        Microsoft.Maui.Storage.Preferences.Default.Set("IsLocationServiceRunning", false);
    }

    private async Task MonitorDeviceStateAsync()
    {
        while (_isRunning)
        {
            try
            {
                // Use the new battery optimization service
                var optimizationResult = await _batteryOptimizationService.OptimizeForCurrentConditionsAsync();
                
                // Get status for user feedback
                var status = await _batteryOptimizationService.GetOptimizationStatusAsync();
                StatusChanged?.Invoke(this, status);
                
                // Wait based on optimization level
                var waitTime = optimizationResult.OptimizationLevel switch
                {
                    OptimizationLevel.Critical => TimeSpan.FromMinutes(10),
                    OptimizationLevel.High => TimeSpan.FromMinutes(5),
                    OptimizationLevel.Medium => TimeSpan.FromMinutes(3),
                    _ => TimeSpan.FromMinutes(2)
                };
                
                await Task.Delay(waitTime);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in device monitoring: {ex.Message}");
                await Task.Delay(TimeSpan.FromMinutes(1)); // Esperar menos tiempo si hay error
            }
        }
    }

    private void OnLocationsUpdated(object? sender, List<LocationUpdate> locations)
    {
        LocationsUpdated?.Invoke(this, locations);
    }

    private void OnPollingError(object? sender, string error)
    {
        StatusChanged?.Invoke(this, $"Error: {error}");
    }

    private void OnOptimizationStatusChanged(object? sender, string status)
    {
        StatusChanged?.Invoke(this, $"Optimización: {status}");
    }

    private void OnMemberStatusChanged(object? sender, MemberStatusChange statusChange)
    {
        _notificationService.AddStatusChangeNotification(statusChange);
    }

    private void OnNotificationAdded(object? sender, StatusNotification notification)
    {
        NotificationReceived?.Invoke(this, notification);
    }

    public NotificationService GetNotificationService()
    {
        return _notificationService;
    }

    public PollingService GetPollingService()
    {
        return _pollingService;
    }

    public bool IsRunning => _isRunning;

    public void StartForegroundService()
    {
#if ANDROID
        var context = Platform.CurrentActivity ?? Android.App.Application.Context;
        FamilyTogether.App.Platforms.Android.LocationForegroundService.StartService(context);
#endif
    }

    public void StopForegroundService()
    {
#if ANDROID
        var context = Platform.CurrentActivity ?? Android.App.Application.Context;
        FamilyTogether.App.Platforms.Android.LocationForegroundService.StopService(context);
#endif
    }
}