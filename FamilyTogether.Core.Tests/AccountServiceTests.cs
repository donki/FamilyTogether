using System.Net;
using FamilyTogether.Core;

namespace FamilyTogether.Core.Tests;

/// <summary>Vincular y recuperar la cuenta, con el navegador y el proveedor falsos.</summary>
public class AccountServiceTests
{
    private sealed class Browser(string redirect, Func<Uri, Uri> answer) : IOAuthBrowser
    {
        public string RedirectUri => redirect;
        public Uri? Opened { get; private set; }

        public Task<Uri> AuthenticateAsync(Uri authorizeUrl, CancellationToken cancellationToken = default)
        {
            Opened = authorizeUrl;
            return Task.FromResult(answer(authorizeUrl));
        }
    }

    private static Browser CodeBrowser(string redirect = "http://127.0.0.1/auth/") =>
        new(redirect, _ => new Uri(redirect + "?code=el%20codigo&state=x"));

    private static (AccountService Account, Phone Phone, FakeSupabase Provider) Create(IOAuthBrowser browser)
    {
        var phone = new Phone();
        var provider = new FakeSupabase().On("POST", "/token", """{"id_token":"idt"}""")
            .On("POST", "/common/oauth2/v2.0/token", """{"id_token":"idt"}""");
        return (new AccountService(phone.Client, phone.Service, phone.Store, new HttpClient(provider), browser), phone, provider);
    }

    [Fact]
    public async Task ProveedorVinculadoSeLeeDelServidorYSeRecuerda()
    {
        var (account, phone, _) = Create(CodeBrowser());
        phone.Server.Table("account_links", """[{"provider":"google"}]""");

        Assert.Equal("google", await account.LinkedProviderAsync());
        Assert.Equal("google", phone.Store.Values["linked_provider"]);
        Assert.Contains($"user_id=eq.{phone.Me:D}", phone.Server.To("GET", "/rest/v1/account_links").Single().Query);

        // Sin red: lo ultimo sabido.
        phone.Server.Table("account_links", _ => throw new HttpRequestException("sin red"));
        Assert.Equal("google", await account.LinkedProviderAsync());

        // Desvinculada: se olvida.
        phone.Server.Table("account_links", "[]");
        Assert.Null(await account.LinkedProviderAsync());
        Assert.False(phone.Store.Values.ContainsKey("linked_provider"));
    }

    [Fact]
    public async Task ErrorQueNoEsDeRedSiLanza()
    {
        var (account, phone, _) = Create(CodeBrowser());
        phone.Server.Table("account_links", _ => FakeSupabase.Status(HttpStatusCode.InternalServerError));

        await Assert.ThrowsAsync<FamilyTogetherException>(() => account.LinkedProviderAsync());
    }

    [Fact]
    public void SoloSeOfrecenLosProveedoresConfigurados()
    {
        var (account, _, _) = Create(CodeBrowser());
        Assert.Equal(
            new[] { IdentityProvider.Google, IdentityProvider.Microsoft }.Where(IdentitySignInService.IsConfigured),
            account.Available);
    }

    public static TheoryData<IdentityProvider> Providers => [IdentityProvider.Google, IdentityProvider.Microsoft];

    /// <summary>
    /// Con cliente OAuth configurado: PKCE, codigo por el token del proveedor y el id_token a la
    /// Edge Function. Sin el: not_configured sin abrir el navegador. (Depende de la compilacion.)
    /// </summary>
    [Theory]
    [MemberData(nameof(Providers))]
    public async Task VincularManda_El_IdToken_A_LinkAccount(IdentityProvider p)
    {
        var browser = CodeBrowser();
        var (account, phone, provider) = Create(browser);

        if (!IdentitySignInService.IsConfigured(p))
        {
            Assert.Equal(FamilyTogetherException.NotConfigured,
                (await Assert.ThrowsAsync<FamilyTogetherException>(() => account.LinkAsync(p))).Code);
            Assert.Null(browser.Opened);
            return;
        }

        await account.LinkAsync(p);

        var q = browser.Opened!.Query;
        Assert.Contains("code_challenge_method=S256", q);
        Assert.Contains("redirect_uri=" + Uri.EscapeDataString("http://127.0.0.1/auth/"), q);
        var token = Assert.Single(provider.Requests);
        Assert.Contains("code=el+codigo", token.Body);
        Assert.Contains("code_verifier=", token.Body);
        var link = Assert.Single(phone.Server.To("POST", "/functions/v1/link-account"));
        Assert.Equal("idt", link.Str("id_token"));
        Assert.Equal(p == IdentityProvider.Google ? "google" : "microsoft", link.Str("provider"));
        Assert.Equal(link.Str("provider"), phone.Store.Values["linked_provider"]);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task VincularCuentaDeOtroEsAlreadyLinked(IdentityProvider p)
    {
        if (!IdentitySignInService.IsConfigured(p))
            return;

        var (account, phone, _) = Create(CodeBrowser());
        phone.Server.On("POST", "/functions/v1/link-account", _ => FakeSupabase.Json("""{"error":"already_linked"}""", HttpStatusCode.Conflict));

        Assert.Equal("already_linked", (await Assert.ThrowsAsync<FamilyTogetherException>(() => account.LinkAsync(p))).Code);
        Assert.False(phone.Store.Values.ContainsKey("linked_provider"));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task RecuperarPideLuegoLasClavesYSiFallanNoRompe(IdentityProvider p)
    {
        if (!IdentitySignInService.IsConfigured(p))
            return;

        var (account, phone, _) = Create(CodeBrowser());
        phone.Server.Table("group_members", _ => FakeSupabase.Status(HttpStatusCode.InternalServerError));

        await account.RecoverAsync(p);

        Assert.Single(phone.Server.To("POST", "/functions/v1/recover-account"));
        Assert.NotEmpty(phone.Server.To("GET", "/rest/v1/group_members"));   // intento de pedir las claves
        Assert.True(phone.Store.Values.ContainsKey("linked_provider"));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ErroresDelInicioDeSesion(IdentityProvider p)
    {
        if (!IdentitySignInService.IsConfigured(p))
            return;

        // El usuario cancela: sin codigo.
        var cancel = new Browser("http://127.0.0.1/auth/", _ => new Uri("http://127.0.0.1/auth/?error=access_denied&error_description=cancelado"));
        var (a1, _, _) = Create(cancel);
        var ex = await Assert.ThrowsAsync<FamilyTogetherException>(() => a1.LinkAsync(p));
        Assert.Equal("sign_in_failed", ex.Code);
        Assert.Contains("cancelado", ex.Message);

        // El proveedor rechaza el codigo, no contesta, o no da id_token.
        foreach (var (respond, code) in new (Func<SeenRequest, HttpResponseMessage>, string)[]
        {
            (_ => FakeSupabase.Status(HttpStatusCode.BadRequest, "invalid_grant"), "sign_in_failed"),
            (_ => FakeSupabase.Status(HttpStatusCode.InternalServerError), FamilyTogetherException.Server),
            (_ => FakeSupabase.Json("""{"access_token":"x"}"""), "sign_in_failed"),
            (_ => FakeSupabase.Json("no json"), "sign_in_failed"),
            (_ => throw new HttpRequestException("sin red"), FamilyTogetherException.Network),
        })
        {
            var phone = new Phone();
            var provider = new FakeSupabase().On("POST", "/token", respond).On("POST", "/common/oauth2/v2.0/token", respond);
            var account = new AccountService(phone.Client, phone.Service, phone.Store, new HttpClient(provider), CodeBrowser());
            Assert.Equal(code, (await Assert.ThrowsAsync<FamilyTogetherException>(() => account.LinkAsync(p))).Code);
            Assert.Empty(phone.Server.To("POST", "/functions/v1/link-account"));
        }
    }

    [Fact]
    public async Task GoogleEnElMovilVuelvePorElEsquemaInvertido()
    {
        if (!IdentitySignInService.IsConfigured(IdentityProvider.Google) || FamilyTogetherConfig.GoogleRedirectScheme.Length == 0)
            return;

        var browser = CodeBrowser("myapp://callback");
        var (account, _, _) = Create(browser);

        await account.LinkAsync(IdentityProvider.Google);

        Assert.Contains("redirect_uri=" + Uri.EscapeDataString($"{FamilyTogetherConfig.GoogleRedirectScheme}:/oauth2redirect"), browser.Opened!.Query);
    }
}
