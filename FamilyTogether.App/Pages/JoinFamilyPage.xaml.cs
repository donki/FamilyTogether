using FamilyTogether.App.Services;
using System.Text.RegularExpressions;

namespace FamilyTogether.App.Pages;

public partial class JoinFamilyPage : ContentPage
{
    private readonly ApiService _apiService;

    public JoinFamilyPage(ApiService apiService)
    {
        InitializeComponent();
        _apiService = apiService;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        FamilyCodeEntry.Text = string.Empty;
        StatusLabel.IsVisible = false;
    }

    private async void OnPasteClicked(object sender, EventArgs e)
    {
        try
        {
            var clipboardText = await Clipboard.GetTextAsync();
            
            if (!string.IsNullOrWhiteSpace(clipboardText))
            {
                // Limpiar el texto y verificar si parece un GUID
                var cleanText = clipboardText.Trim();
                
                if (IsValidGuid(cleanText))
                {
                    FamilyCodeEntry.Text = cleanText;
                    ShowStatus("Código pegado desde portapapeles", Colors.Green);
                }
                else
                {
                    ShowStatus("El texto del portapapeles no parece ser un código válido", Colors.Orange);
                }
            }
            else
            {
                ShowStatus("El portapapeles está vacío", Colors.Orange);
            }
        }
        catch (Exception ex)
        {
            ShowStatus($"Error accediendo al portapapeles: {ex.Message}", Colors.Red);
        }
    }

    private async void OnJoinClicked(object sender, EventArgs e)
    {
        var familyCode = FamilyCodeEntry.Text?.Trim();
        
        if (string.IsNullOrWhiteSpace(familyCode))
        {
            ShowStatus("Por favor, ingresa un código de familia", Colors.Red);
            return;
        }

        if (!IsValidGuid(familyCode))
        {
            ShowStatus("El código ingresado no tiene un formato válido", Colors.Red);
            return;
        }

        await SetLoading(true);

        try
        {
            var response = await _apiService.JoinFamilyAsync(familyCode);
            
            if (response.Success)
            {
                ShowStatus("¡Solicitud enviada exitosamente! Esperando aprobación de un administrador.", Colors.Green);
                
                // Esperar un momento y luego navegar de vuelta
                await Task.Delay(3000);
                await Shell.Current.GoToAsync("..");
            }
            else
            {
                ShowStatus(response.Message, Colors.Red);
            }
        }
        catch (Exception ex)
        {
            ShowStatus($"Error enviando solicitud: {ex.Message}", Colors.Red);
        }
        finally
        {
            await SetLoading(false);
        }
    }

    private async void OnCancelClicked(object sender, EventArgs e)
    {
        await Shell.Current.GoToAsync("..");
    }

    private bool IsValidGuid(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return false;

        // Verificar formato GUID con regex
        var guidPattern = @"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$";
        return Regex.IsMatch(input, guidPattern);
    }

    private async Task SetLoading(bool isLoading)
    {
        LoadingIndicator.IsVisible = isLoading;
        LoadingIndicator.IsRunning = isLoading;
        JoinButton.IsEnabled = !isLoading;
        FamilyCodeEntry.IsEnabled = !isLoading;
        
        if (isLoading)
        {
            JoinButton.Text = "Enviando...";
            StatusLabel.IsVisible = false;
        }
        else
        {
            JoinButton.Text = "Enviar Solicitud";
        }
    }

    private void ShowStatus(string message, Color color)
    {
        StatusLabel.Text = message;
        StatusLabel.TextColor = color;
        StatusLabel.IsVisible = true;
        
        // Ocultar el mensaje después de 5 segundos si no es de éxito
        if (color != Colors.Green)
        {
            Device.StartTimer(TimeSpan.FromSeconds(5), () =>
            {
                if (StatusLabel.Text == message)
                {
                    StatusLabel.IsVisible = false;
                }
                return false;
            });
        }
    }
}