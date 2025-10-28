using FamilyTogether.App.Services;

namespace FamilyTogether.App.Pages;

public partial class PermissionsPage : ContentPage
{
    private readonly PermissionService _permissionService;
    private bool _locationGranted = false;
    private bool _batteryOptimized = false;

    public PermissionsPage(PermissionService permissionService)
    {
        InitializeComponent();
        _permissionService = permissionService;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await CheckPermissionStatus();
    }

    private async Task CheckPermissionStatus()
    {
        try
        {
            // Verificar permisos de ubicación
            _locationGranted = await _permissionService.CheckLocationPermissionsAsync();
            UpdateLocationStatus();

            // Verificar optimización de batería
            _batteryOptimized = _permissionService.IsBatteryOptimizationIgnored();
            UpdateBatteryStatus();

            UpdateContinueButton();
        }
        catch (Exception ex)
        {
            await DisplayAlert("Error", $"Error verificando permisos: {ex.Message}", "OK");
        }
    }

    private async void OnLocationPermissionClicked(object sender, EventArgs e)
    {
        try
        {
            LocationPermissionButton.IsEnabled = false;
            LocationPermissionButton.Text = "Solicitando...";

            _locationGranted = await _permissionService.RequestLocationPermissionsAsync();
            
            if (!_locationGranted)
            {
                var result = await DisplayAlert(
                    "Permisos Requeridos", 
                    "Los permisos de ubicación son necesarios para el funcionamiento de la aplicación. ¿Deseas abrir la configuración para concederlos manualmente?", 
                    "Abrir Configuración", 
                    "Cancelar");

                if (result)
                {
                    await OpenAppSettings();
                }
            }

            UpdateLocationStatus();
            UpdateContinueButton();
        }
        catch (Exception ex)
        {
            await DisplayAlert("Error", $"Error solicitando permisos: {ex.Message}", "OK");
        }
        finally
        {
            LocationPermissionButton.IsEnabled = true;
            LocationPermissionButton.Text = "Conceder Permiso de Ubicación";
        }
    }

    private async void OnBatteryOptimizationClicked(object sender, EventArgs e)
    {
        try
        {
            BatteryOptimizationButton.IsEnabled = false;
            BatteryOptimizationButton.Text = "Abriendo configuración...";

            var success = await _permissionService.RequestBatteryOptimizationExemptionAsync();
            
            if (success)
            {
                await DisplayAlert(
                    "Configuración de Batería", 
                    "Se abrirá la configuración de optimización de batería. Por favor, selecciona 'No optimizar' para FamilyTogether.", 
                    "OK");
            }
            else
            {
                await DisplayAlert(
                    "Información", 
                    "No se pudo abrir la configuración automáticamente. Puedes desactivar la optimización de batería manualmente en Configuración > Batería > Optimización de batería.", 
                    "OK");
            }

            // Esperar un momento y verificar nuevamente
            await Task.Delay(2000);
            _batteryOptimized = _permissionService.IsBatteryOptimizationIgnored();
            UpdateBatteryStatus();
            UpdateContinueButton();
        }
        catch (Exception ex)
        {
            await DisplayAlert("Error", $"Error configurando batería: {ex.Message}", "OK");
        }
        finally
        {
            BatteryOptimizationButton.IsEnabled = true;
            BatteryOptimizationButton.Text = "Desactivar Optimización";
        }
    }

    private async void OnContinueClicked(object sender, EventArgs e)
    {
        // Guardar estado de permisos
        var preferences = Microsoft.Maui.Storage.Preferences.Default;
        preferences.Set("HasLocationPermission", _locationGranted);
        preferences.Set("HasBatteryOptimization", _batteryOptimized);
        preferences.Set("PermissionsConfigured", true);

        await Shell.Current.GoToAsync("//MainPage");
    }

    private async void OnSkipClicked(object sender, EventArgs e)
    {
        var result = await DisplayAlert(
            "Configurar Después", 
            "Sin los permisos necesarios, la aplicación puede no funcionar correctamente. ¿Estás seguro de que deseas continuar?", 
            "Sí, continuar", 
            "Cancelar");

        if (result)
        {
            // Guardar que se saltó la configuración
            var preferences = Microsoft.Maui.Storage.Preferences.Default;
            preferences.Set("HasLocationPermission", _locationGranted);
            preferences.Set("HasBatteryOptimization", _batteryOptimized);
            preferences.Set("PermissionsConfigured", false);

            await Shell.Current.GoToAsync("//MainPage");
        }
    }

    private void UpdateLocationStatus()
    {
        if (_locationGranted)
        {
            LocationStatusLabel.Text = "✅";
            LocationPermissionButton.Text = "Permiso Concedido";
            LocationPermissionButton.IsEnabled = false;
            LocationPermissionButton.BackgroundColor = Colors.Green;
        }
        else
        {
            LocationStatusLabel.Text = "❌";
            LocationPermissionButton.Text = "Conceder Permiso de Ubicación";
            LocationPermissionButton.IsEnabled = true;
            LocationPermissionButton.BackgroundColor = Color.FromArgb("#512BD4"); // Primary color
        }
    }

    private void UpdateBatteryStatus()
    {
        if (_batteryOptimized)
        {
            BatteryStatusLabel.Text = "✅";
            BatteryOptimizationButton.Text = "Optimización Desactivada";
            BatteryOptimizationButton.IsEnabled = false;
            BatteryOptimizationButton.BackgroundColor = Colors.Green;
        }
        else
        {
            BatteryStatusLabel.Text = "❌";
            BatteryOptimizationButton.Text = "Desactivar Optimización";
            BatteryOptimizationButton.IsEnabled = true;
            BatteryOptimizationButton.BackgroundColor = Color.FromArgb("#512BD4"); // Primary color
        }
    }

    private void UpdateContinueButton()
    {
        ContinueButton.IsEnabled = _locationGranted; // Solo ubicación es requerida
        
        if (_locationGranted && _batteryOptimized)
        {
            ContinueButton.Text = "¡Todo Listo! Continuar";
            ContinueButton.BackgroundColor = Colors.Green;
        }
        else if (_locationGranted)
        {
            ContinueButton.Text = "Continuar";
            ContinueButton.BackgroundColor = Colors.White;
        }
        else
        {
            ContinueButton.Text = "Permisos Requeridos";
            ContinueButton.BackgroundColor = Colors.Gray;
        }
    }

    private async Task OpenAppSettings()
    {
        try
        {
#if ANDROID
            var intent = new Android.Content.Intent();
            intent.SetAction(Android.Provider.Settings.ActionApplicationDetailsSettings);
            intent.SetData(Android.Net.Uri.Parse($"package:{Platform.CurrentActivity?.PackageName}"));
            intent.SetFlags(Android.Content.ActivityFlags.NewTask);
            
            if (Platform.CurrentActivity != null)
            {
                Platform.CurrentActivity.StartActivity(intent);
            }
#endif
        }
        catch (Exception ex)
        {
            await DisplayAlert("Error", $"No se pudo abrir la configuración: {ex.Message}", "OK");
        }
    }
}