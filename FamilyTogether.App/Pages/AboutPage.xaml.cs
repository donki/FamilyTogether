using Microsoft.Maui.Essentials;

namespace FamilyTogether.App.Pages
{
    public partial class AboutPage : ContentPage
    {
        // CONFIGURACIÓN - Personaliza estos valores
        private const string ContactEmail = "soporte@familytogether.app";
        private const string DonationUrl = "https://ko-fi.com/familytogether"; 
        private const string EmailSubject = "Contacto desde FamilyTogether";

        public AboutPage()
        {
            InitializeComponent();
        }

        private async void OnBackClicked(object? sender, EventArgs e)
        {
            await Navigation.PopAsync();
        }

        private async void OnContactEmailClicked(object? sender, EventArgs e)
        {
            try
            {
                var message = new EmailMessage
                {
                    Subject = EmailSubject,
                    To = new List<string> { ContactEmail },
                    Body = "Hola equipo de FamilyTogether,\n\n" +
                           "Me pongo en contacto para:\n" +
                           "[ ] Reportar un problema\n" +
                           "[ ] Sugerir una mejora\n" +
                           "[ ] Hacer una pregunta\n" +
                           "[ ] Otro: _______________\n\n" +
                           "Descripción:\n\n\n" +
                           "Información del dispositivo:\n" +
                           $"- Plataforma: {DeviceInfo.Platform}\n" +
                           $"- Versión OS: {DeviceInfo.VersionString}\n" +
                           $"- Modelo: {DeviceInfo.Model}\n" +
                           $"- Fabricante: {DeviceInfo.Manufacturer}\n\n" +
                           "Gracias por usar FamilyTogether!"
                };

                await Email.ComposeAsync(message);
            }
            catch (FeatureNotSupportedException)
            {
                await DisplayAlert("Error", "Cliente de correo no disponible en este dispositivo", "OK");
            }
            catch (Exception ex)
            {
                await DisplayAlert("Error", $"No se pudo abrir el cliente de correo: {ex.Message}", "OK");
            }
        }

        private async void OnDonationClicked(object? sender, EventArgs e)
        {
            try
            {
                var uri = new Uri(DonationUrl);
                var browserLaunchOptions = new BrowserLaunchOptions
                {
                    LaunchMode = BrowserLaunchMode.SystemPreferred,
                    TitleMode = BrowserTitleMode.Show,
                    PreferredToolbarColor = Color.FromArgb("#E67E22"),
                    PreferredControlColor = Color.FromArgb("#FFFFFF")
                };

                await Browser.OpenAsync(uri, browserLaunchOptions);
            }
            catch (FeatureNotSupportedException)
            {
                // Fallback: copy URL to clipboard if browser is not available
                try
                {
                    await Clipboard.SetTextAsync(DonationUrl);
                    await DisplayAlert("Navegador no disponible", 
                        $"Enlace copiado al portapapeles:\n{DonationUrl}", "OK");
                }
                catch
                {
                    await DisplayAlert("Error", 
                        $"No se pudo abrir el navegador. Visita manualmente: {DonationUrl}", "OK");
                }
            }
            catch (Exception ex)
            {
                // Fallback: copy URL to clipboard on any other error
                try
                {
                    await Clipboard.SetTextAsync(DonationUrl);
                    await DisplayAlert("Error al abrir enlace", 
                        $"No se pudo abrir el navegador ({ex.Message}), enlace copiado al portapapeles.", "OK");
                }
                catch
                {
                    await DisplayAlert("Error", 
                        $"No se pudo abrir el enlace: {DonationUrl}", "OK");
                }
            }
        }
    }
}