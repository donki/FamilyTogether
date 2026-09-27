using FamilyTogether.Mobile.Localization;
using FamilyTogether.Mobile.Services;

namespace FamilyTogether.Mobile.Pages;

/// <summary>
/// Pantalla Acerca de, homogenea con el resto de apps sOCratic (Mobile 7): logo, version, contacto,
/// idioma, novedades, privacidad y licencia.
/// </summary>
public partial class AboutPage : ContentPage
{
    private const string ContactEmail = "jsoladelarosa@gmail.com";

    /// <summary>La politica de privacidad vive en el repositorio (General 8.5).</summary>
    private const string PrivacyUrl = "https://github.com/donki/FamilyTogether/blob/main/PRIVACY.md";

    public AboutPage()
    {
        InitializeComponent();
        VersionLabel.Text = $"v{AppInfo.Current.VersionString}";
        ShowLanguage();
    }

    /// <summary>El idioma activo se resalta, para que se vea cual esta puesto sin adivinarlo.</summary>
    private void ShowLanguage()
    {
        var spanish = Loc.Language == Loc.Spanish;
        SpanishButton.Opacity = spanish ? 1 : 0.5;
        EnglishButton.Opacity = spanish ? 0.5 : 1;
        SpanishButton.BorderWidth = spanish ? 3 : 1;
        EnglishButton.BorderWidth = spanish ? 1 : 3;
    }

    private void OnSpanishClicked(object? sender, EventArgs e) => SetLanguage(Loc.Spanish);

    private void OnEnglishClicked(object? sender, EventArgs e) => SetLanguage(Loc.English);

    /// <summary>Se reconstruye el Shell (los enlaces de MAUI no reevaluan los textos) y se vuelve aqui.</summary>
    private static void SetLanguage(string language)
    {
        Loc.SetPreference(language);
        if (Application.Current?.Windows.FirstOrDefault() is not { } window)
            return;

        var shell = new AppShell();
        window.Page = shell;
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            try
            {
                await shell.GoToAsync("//AboutPage");
            }
            catch (Exception ex)
            {
                CrashLog.Error("AboutPage.SetLanguage", ex);
            }
        });
    }

    private async void OnWhatsNewClicked(object? sender, EventArgs e) => await Shell.Current.GoToAsync("//WhatsNewPage");

    private async void OnPrivacyClicked(object? sender, EventArgs e)
    {
        try
        {
            await Browser.Default.OpenAsync(PrivacyUrl, BrowserLaunchMode.SystemPreferred);
        }
        catch (Exception)
        {
            await Clipboard.Default.SetTextAsync(PrivacyUrl);
            await Ui.AlertAsync(this, "PrivacyPolicy", Loc.Format("LinkCopied", PrivacyUrl));
        }
    }

    private async void OnContactClicked(object? sender, EventArgs e)
    {
        try
        {
            await Email.Default.ComposeAsync(new EmailMessage
            {
                Subject = Loc.Get("AppName"),
                To = [ContactEmail],
            });
        }
        catch (Exception)
        {
            // Sin cliente de correo se copia la direccion: se puede escribir desde donde se quiera.
            await Clipboard.Default.SetTextAsync(ContactEmail);
            await Ui.AlertAsync(this, "Contact", Loc.Format("EmailCopied", ContactEmail));
        }
    }
}
