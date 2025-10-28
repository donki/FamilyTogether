using FamilyTogether.App.Services;

namespace FamilyTogether.App.Pages;

public partial class LoginPage : ContentPage
{
    private readonly ApiService _apiService;

    public LoginPage(ApiService apiService)
    {
        InitializeComponent();
        _apiService = apiService;
    }

    private async void OnLoginClicked(object sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(EmailEntry.Text) || string.IsNullOrWhiteSpace(PasswordEntry.Text))
        {
            await ShowError("Por favor, completa todos los campos");
            return;
        }

        await SetLoading(true);

        try
        {
            var response = await _apiService.LoginAsync(EmailEntry.Text.Trim(), PasswordEntry.Text);
            
            if (response.Success && response.Data != null)
            {
                // Guardar información de login
                var preferences = Microsoft.Maui.Storage.Preferences.Default;
                preferences.Set("IsLoggedIn", true);
                preferences.Set("UserToken", response.Data.Token);
                preferences.Set("UserId", response.Data.User.Id);
                preferences.Set("UserName", response.Data.User.Name);
                preferences.Set("UserEmail", response.Data.User.Email);

                // Navegar a la página principal
                await Shell.Current.GoToAsync("//MainPage");
            }
            else
            {
                await ShowError(response.Message);
            }
        }
        catch (Exception ex)
        {
            await ShowError($"Error de conexión: {ex.Message}");
        }
        finally
        {
            await SetLoading(false);
        }
    }

    private async void OnRegisterTapped(object sender, EventArgs e)
    {
        await Shell.Current.GoToAsync("RegisterPage");
    }

    private async Task SetLoading(bool isLoading)
    {
        LoadingIndicator.IsVisible = isLoading;
        LoadingIndicator.IsRunning = isLoading;
        LoginButton.IsEnabled = !isLoading;
        EmailEntry.IsEnabled = !isLoading;
        PasswordEntry.IsEnabled = !isLoading;
        
        if (isLoading)
        {
            ErrorLabel.IsVisible = false;
        }
    }

    private async Task ShowError(string message)
    {
        ErrorLabel.Text = message;
        ErrorFrame.IsVisible = true;
        
        // Animación de entrada del error
        ErrorFrame.Opacity = 0;
        ErrorFrame.TranslationY = -20;
        await Task.WhenAll(
            ErrorFrame.FadeTo(1, 300, Easing.CubicOut),
            ErrorFrame.TranslateTo(0, 0, 300, Easing.CubicOut)
        );
        
        // Ocultar el error después de 5 segundos
        await Task.Delay(5000);
        if (ErrorLabel.Text == message) // Solo ocultar si no ha cambiado
        {
            await Task.WhenAll(
                ErrorFrame.FadeTo(0, 300, Easing.CubicIn),
                ErrorFrame.TranslateTo(0, -20, 300, Easing.CubicIn)
            );
            ErrorFrame.IsVisible = false;
        }
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        
        // Limpiar campos al aparecer
        EmailEntry.Text = string.Empty;
        PasswordEntry.Text = string.Empty;
        ErrorFrame.IsVisible = false;
        
        // Animaciones de entrada Material Design
        await AnimatePageEntrance();
    }

    private async Task AnimatePageEntrance()
    {
        // Configurar estado inicial
        HeaderSection.Opacity = 0;
        HeaderSection.TranslationY = -50;
        LoginCard.Opacity = 0;
        LoginCard.TranslationY = 50;
        RegisterSection.Opacity = 0;
        RegisterSection.TranslationY = 30;

        // Animar entrada secuencial
        var headerAnimation = HeaderSection.FadeTo(1, 600, Easing.CubicOut);
        var headerSlideAnimation = HeaderSection.TranslateTo(0, 0, 600, Easing.CubicOut);
        
        await Task.Delay(200);
        
        var cardAnimation = LoginCard.FadeTo(1, 500, Easing.CubicOut);
        var cardSlideAnimation = LoginCard.TranslateTo(0, 0, 500, Easing.CubicOut);
        
        await Task.Delay(300);
        
        var registerAnimation = RegisterSection.FadeTo(1, 400, Easing.CubicOut);
        var registerSlideAnimation = RegisterSection.TranslateTo(0, 0, 400, Easing.CubicOut);
        
        await Task.WhenAll(headerAnimation, headerSlideAnimation, cardAnimation, cardSlideAnimation, registerAnimation, registerSlideAnimation);
    }
}