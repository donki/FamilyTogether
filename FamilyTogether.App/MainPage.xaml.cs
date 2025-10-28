using FamilyTogether.App.Services;
using FamilyTogether.App.Models;
using FamilyTogether.App.Controls;
using FamilyTogether.App.Helpers;
using Mapsui;
using Mapsui.Layers;
using Mapsui.Providers;
using Mapsui.Tiling;
using Mapsui.Extensions;
using System.Collections.ObjectModel;
using MauiColor = Microsoft.Maui.Graphics.Color;
using MauiBrush = Microsoft.Maui.Controls.Brush;
using MapsuiColor = Mapsui.Styles.Color;
using MapsuiBrush = Mapsui.Styles.Brush;
using MapsuiPen = Mapsui.Styles.Pen;
using MapsuiSymbolStyle = Mapsui.Styles.SymbolStyle;

namespace FamilyTogether.App;

public partial class MainPage : ContentPage
{
    private readonly ApiService _apiService;
    private readonly BackgroundService _backgroundService;
    private readonly LocationService _locationService;
    private readonly NotificationService _notificationService;
    private ObservableCollection<LocationUpdate> _familyLocations = new();
    private Mapsui.Map? _map;

    public MainPage(ApiService apiService, BackgroundService backgroundService, LocationService locationService)
    {
        InitializeComponent();
        _apiService = apiService;
        _backgroundService = backgroundService;
        _locationService = locationService;
        _notificationService = _backgroundService.GetNotificationService();
        
        FamilyMembersCollection.ItemsSource = _familyLocations;
        
        InitializeMap();
        SetupBackgroundService();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        
        // Verificar si el usuario está logueado
        var preferences = Microsoft.Maui.Storage.Preferences.Default;
        var isLoggedIn = preferences.Get("IsLoggedIn", false);
        
        if (!isLoggedIn)
        {
            await Shell.Current.GoToAsync("//LoginPage");
            return;
        }

        // Configurar token de API
        var token = preferences.Get("UserToken", string.Empty);
        if (!string.IsNullOrEmpty(token))
        {
            _apiService.SetAuthToken(token);
        }

        // Verificar permisos
        var permissionsConfigured = preferences.Get("PermissionsConfigured", false);
        if (!permissionsConfigured)
        {
            await Shell.Current.GoToAsync("PermissionsPage");
            return;
        }

        // Animación de entrada de página
        await MaterialAnimations.PageEntranceAsync(this);

        // Iniciar servicio de background
        await _backgroundService.StartAsync();
        
        // Cargar ubicaciones iniciales
        await LoadFamilyLocationsAsync();
    }

    private void InitializeMap()
    {
        _map = new Mapsui.Map();
        
        // Agregar capa de OpenStreetMaps
        var osmLayer = OpenStreetMap.CreateTileLayer();
        _map.Layers.Add(osmLayer);
        
        // Configurar vista inicial (Madrid, España como ejemplo)
        _map.Home = n => n.CenterOnAndZoomTo(new MPoint(-3.7038, 40.4168), n.Resolutions[10]);
        
        MapView.Map = _map;
    }

    private void SetupBackgroundService()
    {
        _backgroundService.LocationsUpdated += OnLocationsUpdated;
        _backgroundService.StatusChanged += OnStatusChanged;
        _backgroundService.NotificationReceived += OnNotificationReceived;
        
        // Subscribe to API service events for connection status
        _apiService.ConnectionStatusChanged += OnConnectionStatusChanged;
        _apiService.PendingDataCountChanged += OnPendingDataCountChanged;
    }

    private void OnConnectionStatusChanged(object? sender, string status)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            // Update status with connection info
            var connectionIcon = status.Contains("Conectado") ? "🟢" : "🔴";
            StatusLabel.Text = $"{connectionIcon} {status}";
            
            // Update cache status when connection changes
            UpdateCacheStatus();
        });
    }

    private void OnPendingDataCountChanged(object? sender, int count)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (count > 0)
            {
                CacheStatusLabel.Text = $"Pendientes: {count} elementos";
            }
            else
            {
                UpdateCacheStatus();
            }
        });
    }

    private void OnLocationsUpdated(object? sender, List<LocationUpdate> locations)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            UpdateFamilyLocations(locations);
            UpdateMapMarkers(locations);
            UpdateCacheStatus();
            UpdateNotificationBadge();
        });
    }

    private void OnStatusChanged(object? sender, string status)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            StatusLabel.Text = status;
        });
    }

    private void OnNotificationReceived(object? sender, StatusNotification notification)
    {
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            // Mostrar notificación como toast o alert no intrusivo
            var message = $"{notification.Title}: {notification.Message}";
            
            // Usar DisplayAlert para notificaciones importantes
            if (notification.Type == NotificationType.Warning || notification.Type == NotificationType.Error)
            {
                await DisplayAlert(notification.Title, notification.Message, "OK");
            }
            else
            {
                // Para notificaciones informativas, solo actualizar el status
                StatusLabel.Text = $"📱 {message}";
                
                // Limpiar el mensaje después de 5 segundos
                _ = Task.Delay(5000).ContinueWith(_ =>
                {
                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        if (StatusLabel.Text.StartsWith("📱"))
                        {
                            StatusLabel.Text = "Monitoreo activo";
                        }
                    });
                });
            }
        });
    }

    private async Task LoadFamilyLocationsAsync()
    {
        try
        {
            var response = await _apiService.GetFamilyLocationsAsync();
            if (response.Success && response.Data != null)
            {
                UpdateFamilyLocations(response.Data);
                UpdateMapMarkers(response.Data);
            }
        }
        catch (Exception ex)
        {
            await DisplayAlert("Error", $"Error cargando ubicaciones: {ex.Message}", "OK");
        }
    }

    private void UpdateFamilyLocations(List<LocationUpdate> locations)
    {
        _familyLocations.Clear();
        foreach (var location in locations)
        {
            _familyLocations.Add(location);
        }
    }

    private void UpdateMapMarkers(List<LocationUpdate> locations)
    {
        if (_map == null) return;

        // Remover marcadores existentes
        var markersLayer = _map.Layers.FirstOrDefault(l => l.Name == "FamilyMarkers") as MemoryLayer;
        if (markersLayer != null)
        {
            _map.Layers.Remove(markersLayer);
        }

        // Crear nueva capa de marcadores
        var features = new List<IFeature>();
        
        foreach (var location in locations)
        {
            var point = new MPoint(location.Longitude, location.Latitude);
            var feature = new PointFeature(point);
            
            // Configurar estilo del marcador según el estado
            var color = location.MinutesAgo <= 5 ? MapsuiColor.Green : 
                       location.MinutesAgo <= 30 ? MapsuiColor.Orange : MapsuiColor.Red;
            
            feature.Styles.Add(new MapsuiSymbolStyle
            {
                SymbolScale = 0.8,
                Fill = new MapsuiBrush(color),
                Outline = new MapsuiPen(MapsuiColor.White, 2)
            });
            
            features.Add(feature);
        }

        if (features.Any())
        {
            var memoryProvider = new MemoryProvider(features);
            markersLayer = new MemoryLayer();
            markersLayer.Name = "FamilyMarkers";
            
            // Use reflection or try different approach
            try
            {
                var dataSourceProperty = markersLayer.GetType().GetProperty("DataSource");
                if (dataSourceProperty != null)
                {
                    dataSourceProperty.SetValue(markersLayer, memoryProvider);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error setting DataSource: {ex.Message}");
                // Fallback: just create empty layer for now
            }
            
            _map.Layers.Add(markersLayer);
            
            // Centrar el mapa en las ubicaciones
            var extents = features.Select(f => f.Extent).Where(e => e != null);
            if (extents.Any())
            {
                var envelope = extents.Aggregate((e1, e2) => e1.Join(e2));
                if (envelope != null)
                {
                    _map.Navigator.ZoomToBox(envelope.Grow(envelope.Width * 0.1));
                }
            }
        }
    }

    private async void OnRefreshClicked(object sender, EventArgs e)
    {
        // Animación de botón presionado
        if (sender is Button button)
        {
            await MaterialAnimations.ButtonPressAsync(button);
        }
        
        // Test connection first
        var isConnected = await _apiService.TestConnectionAsync();
        
        if (!isConnected)
        {
            await DisplayAlert("Sin Conexión", 
                "No hay conexión a internet. Mostrando datos en caché.", "OK");
        }
        
        await LoadFamilyLocationsAsync();
        UpdateCacheStatus();
    }

    private async void OnNotificationsClicked(object sender, EventArgs e)
    {
        var notifications = _notificationService.GetNotifications();
        var unreadCount = _notificationService.GetUnreadCount();
        
        if (!notifications.Any())
        {
            await DisplayAlert("Notificaciones", "No hay notificaciones", "OK");
            return;
        }

        var notificationMessages = notifications.Take(5).Select(n => 
            $"{(n.IsRead ? "" : "🔴 ")}{n.Title}: {n.Message} ({n.Timestamp:HH:mm})").ToArray();
        
        var action = await DisplayActionSheet(
            $"Notificaciones ({unreadCount} sin leer)", 
            "Cerrar", 
            unreadCount > 0 ? "Marcar todas como leídas" : null, 
            notificationMessages);

        if (action == "Marcar todas como leídas")
        {
            _notificationService.MarkAllAsRead();
            UpdateNotificationBadge();
        }
    }

    private void UpdateCacheStatus()
    {
        var pollingService = _backgroundService.GetPollingService();
        var pendingCount = _apiService.GetPendingDataCount();
        
        if (pendingCount > 0)
        {
            CacheStatusLabel.Text = $"Pendientes: {pendingCount} elementos";
        }
        else if (pollingService.HasValidCache())
        {
            var cacheAge = pollingService.GetCacheAge();
            CacheStatusLabel.Text = $"Cache: {cacheAge.TotalSeconds:F0}s";
        }
        else
        {
            CacheStatusLabel.Text = "Cache: No válido";
        }
    }

    private void UpdateNotificationBadge()
    {
        var unreadCount = _notificationService.GetUnreadCount();
        NotificationButton.Text = unreadCount > 0 ? $"🔔({unreadCount})" : "🔔";
    }

    private async void OnFamilyManagementClicked(object sender, EventArgs e)
    {
        await Shell.Current.GoToAsync("FamilyManagementPage");
    }

    private async void OnSettingsClicked(object sender, EventArgs e)
    {
        var connectionState = _apiService.GetConnectionState();
        var lastSync = _apiService.GetLastSyncTime();
        
        var options = new List<string> { "Gestionar Familia", "Permisos", "Estado de Conexión" };
        
        if (_apiService.HasPendingData)
        {
            options.Add("Limpiar Datos Pendientes");
        }
        
        options.Add("Cerrar Sesión");
        
        var action = await DisplayActionSheet("Configuración", "Cancelar", null, options.ToArray());

        switch (action)
        {
            case "Gestionar Familia":
                await Shell.Current.GoToAsync("FamilyManagementPage");
                break;
            case "Permisos":
                await Shell.Current.GoToAsync("PermissionsPage");
                break;
            case "Estado de Conexión":
                await ShowConnectionStatusAsync();
                break;
            case "Limpiar Datos Pendientes":
                await ClearPendingDataAsync();
                break;
            case "Cerrar Sesión":
                await LogoutAsync();
                break;
        }
    }

    private async Task ShowConnectionStatusAsync()
    {
        var connectionState = _apiService.GetConnectionState();
        var lastSync = _apiService.GetLastSyncTime();
        var pendingCount = _apiService.GetPendingDataCount();
        
        var status = $"Estado: {(connectionState.IsOnline ? "Conectado" : "Desconectado")}\n" +
                    $"Calidad: {connectionState.Quality}\n" +
                    $"Última conexión exitosa: {connectionState.LastSuccessfulConnection:HH:mm:ss}\n" +
                    $"Fallos consecutivos: {connectionState.ConsecutiveFailures}\n" +
                    $"Última sincronización: {lastSync:HH:mm:ss}\n" +
                    $"Datos pendientes: {pendingCount}";
        
        if (connectionState.LastError != null)
        {
            status += $"\nÚltimo error: {connectionState.LastError.Message}";
        }
        
        await DisplayAlert("Estado de Conexión", status, "OK");
    }

    private async Task ClearPendingDataAsync()
    {
        var result = await DisplayAlert("Limpiar Datos Pendientes", 
            "¿Estás seguro? Se perderán los datos que no se han sincronizado.", "Sí", "No");
            
        if (result)
        {
            _apiService.ClearOfflineData();
            await DisplayAlert("Datos Limpiados", "Los datos pendientes han sido eliminados.", "OK");
            UpdateCacheStatus();
        }
    }

    private async Task LogoutAsync()
    {
        var result = await DisplayAlert("Cerrar Sesión", 
            "¿Estás seguro de que deseas cerrar sesión?", "Sí", "No");

        if (result)
        {
            // Detener servicios
            _backgroundService.Stop();
            _backgroundService.StopForegroundService();

            // Limpiar preferencias
            var preferences = Microsoft.Maui.Storage.Preferences.Default;
            preferences.Clear();

            // Navegar al login
            await Shell.Current.GoToAsync("//LoginPage");
        }
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        
        // Limpiar eventos
        _backgroundService.LocationsUpdated -= OnLocationsUpdated;
        _backgroundService.StatusChanged -= OnStatusChanged;
        _backgroundService.NotificationReceived -= OnNotificationReceived;
        _apiService.ConnectionStatusChanged -= OnConnectionStatusChanged;
        _apiService.PendingDataCountChanged -= OnPendingDataCountChanged;
    }
}