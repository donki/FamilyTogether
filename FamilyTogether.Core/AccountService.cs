using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FamilyTogether.Core;

/// <summary>Con que cuenta se vincula el usuario anonimo.</summary>
public enum IdentityProvider
{
    Google,
    Microsoft,
}

/// <summary>Abre el navegador del sistema y devuelve la URL de vuelta con el codigo. Lo implementa la app.</summary>
public interface IOAuthBrowser
{
    /// <summary>A donde vuelve el proveedor. Tiene que estar dado de alta en el cliente OAuth.</summary>
    string RedirectUri { get; }

    Task<Uri> AuthenticateAsync(Uri authorizeUrl, CancellationToken cancellationToken = default);
}

/// <summary>
/// Vincular la cuenta anonima con Google o Microsoft, y recuperarla en un movil nuevo (§2).
/// </summary>
/// <remarks>
/// <para><b>Solo se usa el proveedor para demostrar quien es.</b> PKCE contra Google o Microsoft
/// (<see cref="IdentitySignInService"/>, adaptado de Task Manager) → id_token → Edge Function
/// <c>link-account</c> o <c>recover-account</c>, que verifica la firma con el JWKS del proveedor y
/// la audiencia. No se pasa por <c>/auth/v1/user/identities</c> de GoTrue: el usuario sigue siendo
/// el anonimo de siempre.</para>
///
/// <para><b>Recuperar</b> pasa al usuario de este movil todo lo del viejo y borra el viejo. Las
/// claves de los grupos no viajan por el servidor, asi que despues se piden a los demas miembros
/// (<see cref="FamilyService.RequestMissingKeysAsync"/>).</para>
/// </remarks>
public sealed class AccountService
{
    private const string KeyLinkedProvider = "linked_provider";

    private readonly SupabaseClient _client;
    private readonly FamilyService _service;
    private readonly ISecureStore _secure;
    private readonly IdentitySignInService _identity;

    public AccountService(SupabaseClient client, FamilyService service, ISecureStore secure, HttpClient http, IOAuthBrowser browser)
    {
        _client = client;
        _service = service;
        _secure = secure;
        _identity = new IdentitySignInService(http, browser);
    }

    /// <summary>Los proveedores que esta compilacion puede ofrecer (con cliente OAuth configurado).</summary>
    public IReadOnlyList<IdentityProvider> Available => _identity.Available;

    /// <summary>
    /// Con que proveedor esta vinculada la cuenta (<c>google</c> / <c>microsoft</c>), o null. Lo
    /// pregunta al servidor (<c>account_links</c>, RLS: solo las propias); sin red, lo ultimo sabido.
    /// </summary>
    public async Task<string?> LinkedProviderAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _client.EnsureSignedInAsync(cancellationToken).ConfigureAwait(false);
            var rows = await _client.SelectAsync<ProviderRow>(
                "account_links", $"select=provider&user_id=eq.{_client.UserId}", cancellationToken).ConfigureAwait(false);

            var provider = rows.FirstOrDefault()?.Provider;
            if (provider is null)
                _secure.Remove(KeyLinkedProvider);
            else
                await _secure.SetAsync(KeyLinkedProvider, provider).ConfigureAwait(false);

            return provider;
        }
        catch (FamilyTogetherException ex) when (ex.IsNetwork)
        {
            return await _secure.GetAsync(KeyLinkedProvider).ConfigureAwait(false);
        }
    }

    /// <summary>Vincula este usuario con la cuenta. <c>already_linked</c> si la cuenta es de otro usuario.</summary>
    public async Task LinkAsync(IdentityProvider p, CancellationToken cancellationToken = default)
    {
        await _client.EnsureSignedInAsync(cancellationToken).ConfigureAwait(false);
        var idToken = await _identity.GetIdTokenAsync(p, cancellationToken).ConfigureAwait(false);
        await CallAsync("link-account", p, idToken, cancellationToken).ConfigureAwait(false);
        await _secure.SetAsync(KeyLinkedProvider, Name(p)).ConfigureAwait(false);
    }

    /// <summary>
    /// Recupera en este movil el usuario vinculado a la cuenta. <c>not_empty</c> si este movil ya
    /// tiene grupos (nunca se fusiona). Despues pide las claves de los grupos.
    /// </summary>
    public async Task RecoverAsync(IdentityProvider p, CancellationToken cancellationToken = default)
    {
        await _client.EnsureSignedInAsync(cancellationToken).ConfigureAwait(false);
        var idToken = await _identity.GetIdTokenAsync(p, cancellationToken).ConfigureAwait(false);
        await CallAsync("recover-account", p, idToken, cancellationToken).ConfigureAwait(false);
        await _secure.SetAsync(KeyLinkedProvider, Name(p)).ConfigureAwait(false);

        try
        {
            await _service.RequestMissingKeysAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (FamilyTogetherException ex)
        {
            // La cuenta ya esta recuperada; las claves se volveran a pedir en la siguiente vuelta.
            CoreLog.Write($"Tras recuperar, pedir claves: {ex.Code}");
        }
    }

    private async Task CallAsync(string function, IdentityProvider p, string idToken, CancellationToken cancellationToken)
    {
        using var response = await _client.InvokeFunctionAsync(function, new { provider = Name(p), id_token = idToken }, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw await SupabaseClient.FunctionErrorAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private static string Name(IdentityProvider p) => p == IdentityProvider.Microsoft ? "microsoft" : "google";
}

/// <summary>
/// PKCE contra Google o Microsoft desde el navegador del sistema, solo para obtener el id_token.
/// Adaptado de <c>IdentitySignInService</c> de Task Manager.
/// </summary>
/// <remarks>
/// <para><b>Google: cliente de escritorio y esquema invertido.</b> Se vuelve por
/// <c>com.googleusercontent.apps.&lt;id&gt;:/oauth2redirect</c>
/// (<see cref="FamilyTogetherConfig.GoogleRedirectScheme"/>), que Google admite para ese cliente sin
/// validar paquete ni huella. En Task Manager la vuelta por servidor local dejo de funcionar en
/// Android 16 (2026-09-12): con la pestaña del navegador delante la app cuenta como «en segundo
/// plano» y el cortafuegos del sistema le tira hasta el loopback. Una vuelta por intent no necesita
/// red. Si el navegador de la app vuelve a <c>http://127.0.0.1</c> (pruebas en escritorio), se usa
/// esa.</para>
///
/// <para>Microsoft: cliente publico, <c>common</c>, y la redireccion que diga el navegador de la app.</para>
///
/// <para>No se comprueba la firma del id_token aqui: la comprueba la Edge Function, que es la que
/// tiene que fiarse de el.</para>
/// </remarks>
public sealed class IdentitySignInService
{
    private readonly HttpClient _http;
    private readonly IOAuthBrowser _browser;

    public IdentitySignInService(HttpClient http, IOAuthBrowser browser)
    {
        _http = http;
        _browser = browser;
    }

    public IReadOnlyList<IdentityProvider> Available =>
        [.. new[] { IdentityProvider.Google, IdentityProvider.Microsoft }.Where(IsConfigured)];

    public static bool IsConfigured(IdentityProvider provider) => ClientId(provider).Length > 0;

    /// <summary>Abre el navegador y devuelve el id_token de la cuenta elegida.</summary>
    public async Task<string> GetIdTokenAsync(IdentityProvider provider, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured(provider))
            throw new FamilyTogetherException(FamilyTogetherException.NotConfigured, $"Sin cliente OAuth de {provider}.");

        var verifier = Base64Url(RandomNumberGenerator.GetBytes(64));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var redirect = RedirectUri(provider);

        var authorize = new Uri(
            $"{AuthorizeUrl(provider)}" +
            $"?client_id={Uri.EscapeDataString(ClientId(provider))}" +
            "&response_type=code" +
            $"&redirect_uri={Uri.EscapeDataString(redirect)}" +
            $"&scope={Uri.EscapeDataString("openid email profile")}" +
            $"&code_challenge={challenge}" +
            "&code_challenge_method=S256" +
            "&prompt=select_account");

        var callback = await _browser.AuthenticateAsync(authorize, cancellationToken).ConfigureAwait(false);
        var code = ReadParameter(callback, "code");
        if (string.IsNullOrEmpty(code))
        {
            var error = ReadParameter(callback, "error_description") ?? ReadParameter(callback, "error") ?? "sin código";
            throw new FamilyTogetherException("sign_in_failed", $"{provider}: {error}");
        }

        var form = new Dictionary<string, string>
        {
            ["client_id"] = ClientId(provider),
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirect,
            ["code_verifier"] = verifier,
        };

        if (provider == IdentityProvider.Google && FamilyTogetherConfig.GoogleClientSecret.Length > 0)
            form["client_secret"] = FamilyTogetherConfig.GoogleClientSecret;

        HttpResponseMessage response;
        try
        {
            using var content = new FormUrlEncodedContent(form);
            response = await _http.PostAsync(TokenUrl(provider), content, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new FamilyTogetherException(FamilyTogetherException.Network, ex.Message, ex);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new FamilyTogetherException(
                    response.StatusCode == HttpStatusCode.BadRequest ? "sign_in_failed" : FamilyTogetherException.Server,
                    $"{provider} ({(int)response.StatusCode}): {body}");
            }

            try
            {
                using var document = JsonDocument.Parse(body);
                if (document.RootElement.TryGetProperty("id_token", out var id) && id.GetString() is { Length: > 0 } idToken)
                    return idToken;
            }
            catch (JsonException)
            {
            }

            throw new FamilyTogetherException("sign_in_failed", $"{provider} no devolvió el id_token.");
        }
    }

    private static string AuthorizeUrl(IdentityProvider provider) => provider == IdentityProvider.Google
        ? "https://accounts.google.com/o/oauth2/v2/auth"
        : "https://login.microsoftonline.com/common/oauth2/v2.0/authorize";

    private static string TokenUrl(IdentityProvider provider) => provider == IdentityProvider.Google
        ? "https://oauth2.googleapis.com/token"
        : "https://login.microsoftonline.com/common/oauth2/v2.0/token";

    private static string ClientId(IdentityProvider provider) => provider == IdentityProvider.Google
        ? FamilyTogetherConfig.GoogleClientId
        : FamilyTogetherConfig.MicrosoftClientId;

    private bool UsesLoopback => _browser.RedirectUri.StartsWith("http://127.0.0.1", StringComparison.Ordinal);

    private string RedirectUri(IdentityProvider provider) =>
        provider == IdentityProvider.Google && !UsesLoopback && FamilyTogetherConfig.GoogleRedirectScheme.Length > 0
            ? $"{FamilyTogetherConfig.GoogleRedirectScheme}:/oauth2redirect"
            : _browser.RedirectUri;

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

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
