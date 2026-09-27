using FamilyTogether.Core;

namespace FamilyTogether.Mobile.Services;

/// <summary>
/// Navegador del sistema para vincular o recuperar la cuenta con Google o Microsoft (ARQUITECTURA 2).
/// <see cref="WebAuthenticator"/> abre una pestaña de Chrome Custom Tabs —no un WebView incrustado,
/// que Google rechaza desde 2021— y devuelve el control por un esquema propio que recoge
/// <c>Platforms\Android\WebAuthenticationCallbackActivity</c>.
/// </summary>
/// <remarks>
/// Como en Task Manager, la vuelta por intent y no por un servidor en 127.0.0.1: con la pestaña
/// delante la app cuenta como «en segundo plano» y en Android 16 el cortafuegos del sistema le corta
/// hasta el loopback.
/// </remarks>
public sealed class MauiOAuthBrowser : IOAuthBrowser
{
    /// <summary>
    /// El esquema propio de la app (su ApplicationId). Es el que vale para Microsoft; Google impone
    /// el identificador de cliente invertido y lo pone el nucleo en la URL de autorizacion.
    /// </summary>
    public string RedirectUri => "com.socratic.familytogether://auth";

    public async Task<Uri> AuthenticateAsync(Uri authorizeUrl, CancellationToken cancellationToken = default)
    {
        // La vuelta se saca de la propia URL de autorizacion: Google exige en Android el identificador
        // invertido, y dar por hecho el esquema de la app dejaria la pestaña esperando para siempre.
        var callbackUrl = new Uri(ReadParameter(authorizeUrl, "redirect_uri") ?? RedirectUri);

        var result = await MainThread.InvokeOnMainThreadAsync(() => WebAuthenticator.Default.AuthenticateAsync(
            new WebAuthenticatorOptions
            {
                Url = authorizeUrl,
                CallbackUrl = callbackUrl,
                PrefersEphemeralWebBrowserSession = false,
            })).WaitAsync(cancellationToken).ConfigureAwait(false);

        // WebAuthenticator ya ha troceado la respuesta; se rehace la URL para que el nucleo lea el
        // codigo igual que en cualquier otra plataforma.
        var query = string.Join('&', result.Properties.Select(p =>
            $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));

        return new Uri($"{callbackUrl}?{query}");
    }

    private static string? ReadParameter(Uri uri, string name)
    {
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length == 2 && parts[0] == name)
                return Uri.UnescapeDataString(parts[1]);
        }

        return null;
    }
}
