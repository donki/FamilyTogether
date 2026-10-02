using FamilyTogether.Mobile;
using FamilyTogether.Mobile.Localization;
using FamilyTogether.Mobile.Pages;
using FamilyTogether.Mobile.Services;

namespace FamilyTogether.Core.Tests.Ui;

/// <summary>Ajustes: idioma, perfil, cuenta, historial y permisos.</summary>
public class SettingsPageTests : IDisposable
{
    private readonly AppHost _host = new();

    public void Dispose()
    {
        Loc.SetPreference(Loc.Spanish);
        _host.Dispose();
    }

    private async Task<SettingsPage> OpenAsync(string name = "Ana")
    {
        await _host.Phone.Store.SetAsync("profile_name", name);
        var page = new SettingsPage();
        page.Host();
        page.Appear();
        var states = new[] { Loc.Get("AccountNotLinked"), Loc.Get("AccountNoProviders"), Loc.Format("AccountLinked", "Google"), Loc.Format("AccountLinked", "Microsoft"), Loc.Get("Err_network") };
        await UiDriver.Until(() => page.Texts().Any(t => states.Contains(t)), "cuenta");
        return page;
    }

    private static Entry Name(SettingsPage page) => page.All<Entry>().Single();

    [Fact]
    public async Task EnsenaPermisosYEstadoDelServicio()
    {
        _host.Location.Permission = LocationPermissionState.WhileInUse;
        _host.Location.IsRunning = true;
        _host.Location.IsIgnoringBatteryOptimizations = true;
        _host.Notifier.AreEnabled = false;
        var page = await OpenAsync();
        Assert.True(page.Shows(Loc.Get("PermLocationWhileInUse") + " · " + Loc.Get("SharingRunning")));
        Assert.True(page.Shows(Loc.Get("PermBatteryOk")));
        Assert.True(page.Shows(Loc.Get("PermNotifOff")));

        // Al volver a la app se vuelven a mirar.
        _host.Location.Permission = LocationPermissionState.Always;
        _host.Location.IsRunning = false;
        _host.Location.IsIgnoringBatteryOptimizations = false;
        _host.Notifier.AreEnabled = true;
        GroupsPageTests.RaiseResumed();
        await UiDriver.Until(() => page.Shows(Loc.Get("PermLocationAlways") + " · " + Loc.Get("SharingNotRunning")), "refrescar");
        Assert.True(page.Shows(Loc.Get("PermBatteryRestricted")));
        Assert.True(page.Shows(Loc.Get("PermNotifOn")));

        _host.Location.Permission = LocationPermissionState.Denied;
        GroupsPageTests.RaiseResumed();
        await UiDriver.Until(() => page.Shows(Loc.Get("PermLocationDenied")), "denegado");
        page.Disappear();
    }

    [Fact]
    public async Task SinParteNativaLoDice()
    {
        FamilyTogether.Mobile.Helpers.ServiceHelper.Initialize(new NoNative(_host.Services));
        try
        {
            var page = new SettingsPage();
            page.Host();
            page.Appear();
            await UiDriver.Until(() => page.Shows(Loc.Get("PermUnavailable")), "sin parte nativa");
            Assert.True(page.Shows(Loc.Get("PermNotifOff")));
        }
        finally
        {
            FamilyTogether.Mobile.Helpers.ServiceHelper.Initialize(_host.Services);
        }
    }

    /// <summary>El contenedor de la app sin la parte nativa (fuera de Android).</summary>
    private sealed class NoNative(IServiceProvider inner) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(ILocationSharing) || serviceType == typeof(INotifier) ? null : inner.GetService(serviceType);
    }

    [Fact]
    public async Task ErrorAlLeerPermisosNoRompe()
    {
        _host.Location.ThrowOnCheck = true;
        var page = await OpenAsync();
        Assert.Contains(page.Texts(), t => t == Loc.Get("ProfileTitle"));
    }

    [Fact]
    public async Task CambiarElIdiomaRehaceElShell()
    {
        var page = new SettingsPage();
        var window = new Window(page);
        typeof(Application).GetMethod("AddWindow", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, [typeof(Window)])!
            .Invoke(_host.Application, [window]);
        page.Appear();
        page.Click("English");
        Assert.Equal(Loc.English, Loc.Preference);
        Assert.IsType<AppShell>(window.Page);
        await UiDriver.Settle();

        var again = new SettingsPage();
        again.Host();
        again.Appear();
        Assert.Equal(3, again.Button("English").BorderWidth);
        Assert.Equal(1, again.Button("Español").BorderWidth);
    }

    [Fact]
    public async Task NombreSeGuardaAlSalirYVacioSeAvisa()
    {
        await _host.Phone.Keys.SetAsync(Guid.NewGuid(), Crypto.NewGroupKey());
        var page = await OpenAsync();
        var name = Name(page);

        // Sin cambios: nada.
        SendUnfocused(name);
        await UiDriver.Settle();
        Assert.Equal("Ana", _host.Phone.Store.Values["profile_name"]);

        name.Text = "   ";
        SendUnfocused(name);
        Assert.Contains(Loc.Get("DisplayNameRequired"), await page.DialogTextAsync());
        await page.AnswerKeyAsync("Ok");

        name.Text = "Ana Maria";
        name.SendCompleted();
        SendUnfocused(name);
        await UiDriver.Until(() => _host.Phone.Store.Values.GetValueOrDefault("profile_name") == "Ana Maria", "guardar nombre");
    }

    private static void SendUnfocused(VisualElement element)
    {
        var raise = typeof(VisualElement).GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)
            .FirstOrDefault(m => m.Name == "OnUnfocused" || m.Name == "InvokeUnfocused");
        if (raise is not null && raise.GetParameters().Length == 0)
        {
            raise.Invoke(element, null);
            return;
        }

        var field = typeof(VisualElement).GetField("Unfocused", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        (field?.GetValue(element) as EventHandler<FocusEventArgs>)?.Invoke(element, new FocusEventArgs(element, false));
    }

    [Fact]
    public async Task FotoSeEligeYSeQuita()
    {
        await _host.Phone.Keys.SetAsync(Guid.NewGuid(), Crypto.NewGroupKey());
        var page = await OpenAsync();
        Assert.False(page.ButtonKey("AvatarRemove").Visible());

        // Sin foto elegida: nada.
        page.ClickKey("AvatarChoose");
        await UiDriver.Settle();
        Assert.False(_host.Phone.Store.Values.ContainsKey("profile_avatar"));

        await _host.Phone.Store.SetAsync("profile_avatar", "iVBORw0KGgo=");
        var withPhoto = await OpenAsync();
        Assert.True(withPhoto.ButtonKey("AvatarRemove").Visible());
        {
            withPhoto.ClickKey("AvatarRemove");
            await UiDriver.Until(() => !_host.Phone.Store.Values.ContainsKey("profile_avatar"), "quitar foto");
            Assert.False(withPhoto.ButtonKey("AvatarRemove").Visible());
        }
    }

    [Fact]
    public async Task FotoNuevaSeGuardaEnElPerfil()
    {
        var open = Avatars.OpenPhoto;
        try
        {
            var expected = await GuideWelcomeAboutTests.PhotoAsync(_host.Folder);
            var page = await OpenAsync();
            page.ClickKey("AvatarChoose");
            await UiDriver.Until(() => _host.Phone.Store.Values.GetValueOrDefault("profile_avatar") == expected, "foto guardada");
            Assert.True(page.ButtonKey("AvatarRemove").Visible());
        }
        finally
        {
            Avatars.OpenPhoto = open;
            MauiFakes.MediaPicker.Photos = null;
        }
    }

    [Fact]
    public async Task SnapYBorrarHistorial()
    {
        var page = await OpenAsync();
        var snap = page.All<Switch>().Single(s => SemanticProperties.GetDescription(s) == Loc.Get("SnapTracks"));
        Assert.True(snap.IsToggled);
        snap.IsToggled = false;
        Assert.False(AppState.SnapTracks);

        page.ClickKey("ClearHistory");
        await page.AnswerKeyAsync("Cancel");
    }

    [Fact]
    public async Task CuentaSinVincularYVincularCancelado()
    {
        var page = await OpenAsync();
        await UiDriver.Until(() => page.Shows(Loc.Get("AccountNotLinked")) || page.Shows(Loc.Get("AccountNoProviders")), "estado de la cuenta");
        if (_host.Account.Available.Count == 0)
        {
            Assert.True(page.Shows(Loc.Get("AccountNoProviders")));
            page.ClickKey("RecoverAction");
            Assert.Contains(Loc.Get("AccountNoProviders"), await page.DialogTextAsync());
            return;
        }

        var first = _host.Account.Available[0] == IdentityProvider.Microsoft ? "Microsoft" : "Google";
        page.Click(Loc.Format("LinkWith", first));
        Assert.Contains(Loc.Get("Err_cancelled"), await page.DialogTextAsync());
        await page.AnswerKeyAsync("Ok");
        Assert.Single(_host.Browser.Opened);
    }

    private void ProviderAnswers()
    {
        _host.Browser.Respond = _ => Task.FromResult(new Uri("http://127.0.0.1/auth/?code=el%20codigo&state=x"));
        _host.Server.On("POST", "/token", """{"id_token":"idt"}""");
        _host.Server.On("POST", "/common/oauth2/v2.0/token", """{"id_token":"idt"}""");
    }

    [Fact]
    public async Task VincularYRecuperarConExito()
    {
        if (_host.Account.Available.Count == 0)
            return;
        ProviderAnswers();
        var page = await OpenAsync();
        var first = _host.Account.Available[0] == IdentityProvider.Microsoft ? "Microsoft" : "Google";
        page.Click(Loc.Format("LinkWith", first));
        Assert.Contains(Loc.Format("AccountLinkedDone", first), await page.DialogTextAsync());
        _host.Server.Table("account_links", _ => FakeSupabase.Json($$"""[{"provider":"{{first.ToLowerInvariant()}}"}]"""));
        await page.AnswerKeyAsync("Ok");
        await UiDriver.Until(() => page.Shows(Loc.Format("AccountLinked", first)), "vinculada");

        AppState.SelectedGroup = Guid.NewGuid();
        page.ClickKey("RecoverAction");
        await page.AnswerKeyAsync("RecoverContinue");
        if (_host.Account.Available.Count > 1)
            await page.AnswerAsync(Loc.Format("LinkWith", "Google"));
        Assert.Contains(Loc.Get("RecoverDone"), await page.DialogTextAsync());
        Assert.Equal(Guid.Empty, AppState.SelectedGroup);
        Assert.Single(_host.Server.To("POST", "/functions/v1/recover-account"));
    }

    [Fact]
    public async Task CuentaVinculadaYErrorAlConsultar()
    {
        _host.Server.Table("account_links", _ => FakeSupabase.Json("""[{"provider":"google"}]"""));
        var page = await OpenAsync();
        await UiDriver.Until(() => page.Shows(Loc.Format("AccountLinked", "Google")), "vinculada");

        _host.Server.Table("account_links", _ => FakeSupabase.Json("""[{"provider":"microsoft"}]"""));
        var ms = await OpenAsync();
        await UiDriver.Until(() => ms.Shows(Loc.Format("AccountLinked", "Microsoft")), "vinculada ms");

        _host.Server.Table("account_links", _ => World.Fail());
        var broken = new SettingsPage();
        broken.Host();
        broken.Appear();
        // Sin servidor vale lo ultimo que se supo (guardado en el almacen seguro).
        await UiDriver.Until(() => broken.Shows(Loc.Format("AccountLinked", "Microsoft")), "lo ultimo sabido");
    }

    [Fact]
    public async Task RecuperarPideConfirmarYSePuedeCancelar()
    {
        var page = await OpenAsync();
        if (_host.Account.Available.Count == 0)
            return;

        page.ClickKey("RecoverAction");
        await page.AnswerKeyAsync("Cancel");
        Assert.Empty(_host.Browser.Opened);

        page.ClickKey("RecoverAction");
        await page.AnswerKeyAsync("RecoverContinue");
        if (_host.Account.Available.Count > 1)
        {
            await page.AnswerKeyAsync("Cancel");   // elegir proveedor: cancelar
            Assert.Empty(_host.Browser.Opened);
            page.ClickKey("RecoverAction");
            await page.AnswerKeyAsync("RecoverContinue");
            await page.AnswerAsync(Loc.Format("LinkWith", "Microsoft"));
        }

        Assert.Contains(Loc.Get("Err_cancelled"), await page.DialogTextAsync());
        Assert.Single(_host.Browser.Opened);
    }

    [Fact]
    public async Task EnlacesAGuiaNovedadesYAcercaDe()
    {
        AppState.WelcomeDone = true;
        var window = (Window)typeof(App).GetMethod("CreateWindow", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(_host.Application, [null])!;
        typeof(Application).GetMethod("AddWindow", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, [typeof(Window)])!
            .Invoke(_host.Application, [window]);
        var page = await OpenAsync();
        page.ClickKey("OpenGuide");
        page.ClickKey("MenuWhatsNew");
        page.ClickKey("MenuAbout");
        await UiDriver.Settle(100);
    }
}
