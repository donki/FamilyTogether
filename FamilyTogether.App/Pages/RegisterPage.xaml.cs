using FamilyTogether.App.Services;
using System.Text.RegularExpressions;

namespace FamilyTogether.App.Pages;

public partial class RegisterPage : ContentPage
{
    private readonly ApiService _apiService;

    public RegisterPage(ApiService apiService)
    {
        InitializeComponent();
        _apiService = apiService;
    }

    private async void OnRegisterClicked(object sender, EventArgs e)
    {
        if (!ValidateForm())
            return;

        await SetLoading(true);

        try
        {
            var response = await _apiService.RegisterAsync(
                EmailEntry.Text.Trim(), 
                PasswordEntry.Text, 
                NameEntry.Text.Trim());
            
            if (response.Success && response.Data != null)
            {
                // Guardar información de login
                var preferences = Microsoft.Maui.Storage.Preferences.Default;
                preferences.Set("IsLoggedIn", true);
                preferences.Set("UserToken", response.Data.Token);
                preferences.Set("UserId", response.Data.User.Id);
                preferences.Set("UserName", response.Data.User.Name);
                preferences.Set("UserEmail", response.Data.User.Email);

                // Mostrar mensaje de éxito y navegar
                await DisplayAlert("Éxito", "Cuenta creada exitosamente", "OK");
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

    private async void OnLoginTapped(object sender, EventArgs e)
    {
        await Shell.Current.GoToAsync("..");
    }

    private bool ValidateForm()
    {
        // Validar nombre
        if (string.IsNullOrWhiteSpace(NameEntry.Text))
        {
            ShowError("El nombre es requerido");
            return false;
        }

        if (NameEntry.Text.Trim().Length < 2)
        {
            ShowError("El nombre debe tener al menos 2 caracteres");
            return false;
        }

        // Validar email
        if (string.IsNullOrWhiteSpace(EmailEntry.Text))
        {
            ShowError("El correo electrónico es requerido");
            return false;
        }

        if (!IsValidEmail(EmailEntry.Text.Trim()))
        {
            ShowError("Por favor, ingresa un correo electrónico válido");
            return false;
        }

        // Validar contraseña
        if (string.IsNullOrWhiteSpace(PasswordEntry.Text))
        {
            ShowError("La contraseña es requerida");
            return false;
        }

        if (PasswordEntry.Text.Length < 6)
        {
            ShowError("La contraseña debe tener al menos 6 caracteres");
            return false;
        }

        // Validar confirmación de contraseña
        if (PasswordEntry.Text != ConfirmPasswordEntry.Text)
        {
            ShowError("Las contraseñas no coinciden");
            return false;
        }

        return true;
    }

    private bool IsValidEmail(string email)
    {
        try
        {
            var emailRegex = new Regex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$");
            return emailRegex.IsMatch(email);
        }
        catch
        {
            return false;
        }
    }

    private async Task SetLoading(bool isLoading)
    {
        LoadingIndicator.IsVisible = isLoading;
        LoadingIndicator.IsRunning = isLoading;
        RegisterButton.IsEnabled = !isLoading;
        NameEntry.IsEnabled = !isLoading;
        EmailEntry.IsEnabled = !isLoading;
        PasswordEntry.IsEnabled = !isLoading;
        ConfirmPasswordEntry.IsEnabled = !isLoading;
        
        if (isLoading)
        {
            ErrorLabel.IsVisible = false;
        }
    }

    private async Task ShowError(string message)
    {
        ErrorLabel.Text = message;
        ErrorLabel.IsVisible = true;
        
        // Ocultar el error después de 5 segundos
        await Task.Delay(5000);
        if (ErrorLabel.Text == message) // Solo ocultar si no ha cambiado
        {
            ErrorLabel.IsVisible = false;
        }
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        
        // Limpiar campos al aparecer
        NameEntry.Text = string.Empty;
        EmailEntry.Text = string.Empty;
        PasswordEntry.Text = string.Empty;
        ConfirmPasswordEntry.Text = string.Empty;
        ErrorLabel.IsVisible = false;
    }
}