using System.Reflection;
using FamilyTogether.Mobile;
using FamilyTogether.Mobile.Localization;
using FamilyTogether.Mobile.Pages;
using FamilyTogether.Mobile.Services;
using Microsoft.Maui.ApplicationModel.DataTransfer;
using Microsoft.Maui.Storage;

namespace FamilyTogether.Core.Tests.Ui;

/// <summary>Guia de permisos, bienvenida, Acerca de, novedades y tareas de arranque.</summary>
public class GuideWelcomeAboutTests : IDisposable
{
    private readonly AppHost _host = new();

    public void Dispose()
    {
        App.PendingJoinCode = null;
        Loc.SetPreference(Loc.Spanish);
        _host.Dispose();
    }

    private AppShell Shell()
    {
        var window = (Window)typeof(App).GetMethod("CreateWindow", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_host.Application, [null])!;
        typeof(Application).GetMethod("AddWindow", BindingFlags.Instance | BindingFlags.NonPublic, [typeof(Window)])!.Invoke(_host.Application, [window]);
        return (AppShell)window.Page!;
    }

    private void CloseWindow(Window window) =>
        typeof(Application).GetMethod("RemoveWindow", BindingFlags.Instance | BindingFlags.NonPublic, [typeof(Window)])!.Invoke(_host.Application, [window]);

    private static Label StateLabel(GuidePage page) => page.All<Label>().Single(l => l.FontSize == 15 && l.FontAttributes == FontAttributes.Bold);

    // -----------------------------------------------------------------------
    // Guia
    // -----------------------------------------------------------------------

    [Fact]
    public async Task GuiaRecorreLosPasosYCadaAccionPideLoSuyo()
    {
        _host.Location.Permission = LocationPermissionState.Denied;
        _host.Location.AfterRequest = LocationPermissionState.Denied;
        _host.Notifier.AreEnabled = false;
        _host.Notifier.GrantOnRequest = false;
        var shell = Shell();
        var page = new GuidePage();
        var host = page.Host();
        page.Appear();
        Assert.True(page.Shows(Loc.Format("GuideStepOf", 1, 7)), string.Join("|", page.Texts()));

        // 1. Intro (sin accion). Siete pasos: intro, precisa, siempre, avisos, bateria, inicio automatico, fin.
        Assert.True(page.Shows(Loc.Format("GuideStepOf", 1, 7)));
        Assert.DoesNotContain(page.All<Button>(), b => b.Visible() && b.Text == Loc.Get("GuidePreciseAction"));
        Assert.False(page.ButtonKey("GuideBack").IsVisible);

        page.ClickKey("GuideNext");   // precisa: pendiente; la accion pide y, denegado, abre ajustes
        await UiDriver.Until(() => StateLabel(page).Text == Loc.Get("GuidePending"), "pendiente");
        page.ClickKey("GuidePreciseAction");
        await UiDriver.Until(() => _host.Location.Opened.Contains("app"), "ajustes");
        _host.Location.AfterRequest = LocationPermissionState.WhileInUse;
        page.ClickKey("GuidePreciseAction");
        await UiDriver.Until(() => StateLabel(page).Text == Loc.Get("GuideDone"), "hecho");

        page.ClickKey("GuideNext");   // siempre: con "mientras se usa" abre ajustes
        page.ClickKey("GuideAlwaysAction");
        await UiDriver.Until(() => _host.Location.Opened.Count(o => o == "app") == 2, "ajustes 2");
        _host.Location.AfterRequest = LocationPermissionState.Always;
        page.ClickKey("GuideAlwaysAction");
        await UiDriver.Until(() => StateLabel(page).Text == Loc.Get("GuideDone"), "siempre");

        page.ClickKey("GuideNext");   // avisos: denegados abren ajustes; concedidos, hecho
        page.ClickKey("GuideNotifAction");
        await UiDriver.Until(() => _host.Location.Opened.Count(o => o == "app") == 3, "ajustes 3");
        _host.Notifier.GrantOnRequest = true;
        page.ClickKey("GuideNotifAction");
        await UiDriver.Until(() => StateLabel(page).Text == Loc.Get("GuideDone"), "avisos");

        page.ClickKey("GuideNext");   // bateria
        page.ClickKey("GuideBatteryAction");
        Assert.Contains("battery", _host.Location.Opened);
        _host.Location.IsIgnoringBatteryOptimizations = true;
        MauiFakes.Dispatcher.Timers.Last().Fire();   // el reloj de 2 s lo ve
        await UiDriver.Until(() => StateLabel(page).Text == Loc.Get("GuideDone"), "bateria");

        page.ClickKey("GuideNext");   // inicio automatico: opcional
        await UiDriver.Until(() => StateLabel(page).Text == Loc.Get("GuideOptional"), "opcional");
        page.ClickKey("GuideAutostartAction");
        Assert.Contains("autostart", _host.Location.Opened);

        page.ClickKey("GuideBack");
        Assert.True(page.Shows(Loc.Format("GuideStepOf", 5, 7)));
        page.ClickKey("GuideNext");
        page.ClickKey("GuideNext");   // fin
        Assert.Equal(Loc.Get("GuideFinish"), page.All<Button>().Last().Text);

        // Al volver a la app se repasa el estado; al terminar se arranca la comparticion si hay grupos.
        GroupsPageTests.RaiseResumed();
        var world = new World(_host.Phone);
        await world.GroupAsync("Casa");
        CloseWindow(host.Window);   // Shell.Current solo se sabe con una ventana: la del Shell
        page.ClickKey("GuideFinish");
        await UiDriver.Until(() => _host.Location.Starts == 1, "arrancar la comparticion");
        page.Disappear();
        Assert.False(MauiFakes.Dispatcher.Timers.Last().IsRunning);
        Assert.NotNull(shell);
    }

    [Fact]
    public async Task GuiaSinParteNativaSoloTieneIntroYFin_YFallosAlComprobar()
    {
        Shell();
        FamilyTogether.Mobile.Helpers.ServiceHelper.Initialize(new NoNative(_host.Services));
        try
        {
            var page = new GuidePage();
            var host = page.Host();
            page.Appear();
            CloseWindow(host.Window);
            Assert.True(page.Shows(Loc.Format("GuideStepOf", 1, 2)));
            App.PendingJoinCode = "ABCD2345";
            page.ClickKey("GuideNext");
            _host.Server.Table("group_members", _ => World.Fail());
            page.ClickKey("GuideFinish");
            await UiDriver.Settle(150);
            page.ClickKey("GuideBack");   // al terminar se vuelve al principio: atras ya no retrocede
            Assert.True(page.Shows(Loc.Format("GuideStepOf", 2, 2)));
        }
        finally
        {
            FamilyTogether.Mobile.Helpers.ServiceHelper.Initialize(_host.Services);
        }

        _host.Location.ThrowOnCheck = true;
        var failing = new GuidePage();
        failing.Host();
        failing.Appear();
        failing.ClickKey("GuideNext");
        await UiDriver.Until(() => StateLabel(failing).Text == Loc.Get("GuidePending"), "fallo = pendiente");
    }

    private sealed class NoNative(IServiceProvider inner) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(ILocationSharing) || serviceType == typeof(INotifier) ? null : inner.GetService(serviceType);
    }

    // -----------------------------------------------------------------------
    // Arranque: tareas de fondo
    // -----------------------------------------------------------------------

    [Fact]
    public async Task TareasDeArranqueRegistranTokenYArrancanLaComparticion()
    {
        var world = new World(_host.Phone);
        await world.GroupAsync("Casa");
        AppState.WelcomeDone = false;
        await AppChores.RunAsync();
        Assert.Empty(_host.Server.Requests);

        AppState.WelcomeDone = true;
        await AppChores.RunAsync();
        if (!FamilyTogetherConfig.IsServerConfigured)
            return;
        Assert.Single(_host.Server.To("POST", "/rest/v1/rpc/register_push_token"));
        Assert.Equal(1, _host.Location.Starts);

        // Ya compartiendo, o sin permiso: no se arranca otra vez.
        await AppChores.RunAsync();
        Assert.Equal(1, _host.Location.Starts);
        _host.Location.IsRunning = false;
        _host.Location.Permission = LocationPermissionState.Denied;
        await AppChores.EnsureSharingAsync(true);
        await AppChores.EnsureSharingAsync(false);
        Assert.Equal(1, _host.Location.Starts);

        // Si algo falla, lo demas sigue.
        _host.Push.Token = null;
        _host.Server.Rpc("fulfill_key_share", _ => World.Fail());
        _host.Server.Table("group_members", _ => World.Fail());
        await AppChores.RunAsync();
    }

    // -----------------------------------------------------------------------
    // Bienvenida
    // -----------------------------------------------------------------------

    [Fact]
    public async Task BienvenidaPideNombreYLlevaALaGuia()
    {
        var shell = Shell();
        var page = new WelcomePage();
        if (!FamilyTogetherConfig.IsServerConfigured)
        {
            Assert.True(page.Shows(Loc.Get("NoServerTitle")));
            return;
        }

        Assert.False(page.ButtonKey("AvatarRemove").Visible());
        page.ClickKey("WelcomeContinue");
        Assert.Contains(Loc.Get("DisplayNameRequired"), await page.DialogTextAsync());
        await page.AnswerKeyAsync("Ok");

        // Elegir foto sin elegir nada: no cambia.
        page.ClickKey("AvatarChoose");
        await UiDriver.Settle();
        Assert.False(page.ButtonKey("AvatarRemove").Visible());

        var name = page.All<Entry>().Single();
        name.Text = " Bea ";
        name.SendCompleted();
        page.ClickKey("WelcomeContinue");
        await UiDriver.Until(() => AppState.WelcomeDone, "bienvenida hecha");
        Assert.Equal("Bea", (await _host.Phone.Service.ProfileAsync()).Name);
        Assert.Equal(MauiFakes.AppInfo.VersionString, AppState.LastVersionSeen);
        Assert.NotNull(shell);
    }

    /// <summary>Una foto de la galeria (la que elige el doble del selector).</summary>
    internal static async Task<string> PhotoAsync(string folder)
    {
        var path = Path.Combine(folder, "foto.png");
        Directory.CreateDirectory(folder);
        var bytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 };
        await File.WriteAllBytesAsync(path, bytes);
        Avatars.OpenPhoto = p => Task.FromResult<Stream>(File.OpenRead(p.FullPath));
        MauiFakes.MediaPicker.Photos = () => [new FileResult(path)];
        return Convert.ToBase64String(bytes);
    }

    [Fact]
    public async Task BienvenidaConFotoQueSePuedeQuitar()
    {
        if (!FamilyTogetherConfig.IsServerConfigured)
            return;
        var open = Avatars.OpenPhoto;
        try
        {
            await PhotoAsync(_host.Folder);
            var page = new WelcomePage();
            page.ClickKey("AvatarChoose");
            await UiDriver.Until(() => page.ButtonKey("AvatarRemove").Visible(), "foto elegida");
            page.ClickKey("AvatarRemove");
            Assert.False(page.ButtonKey("AvatarRemove").Visible());
        }
        finally
        {
            Avatars.OpenPhoto = open;
            MauiFakes.MediaPicker.Photos = null;
        }
    }

    [Fact]
    public async Task BienvenidaSinServidorAvisaYNoAvanza()
    {
        if (!FamilyTogetherConfig.IsServerConfigured)
            return;
        _host.Phone.Store.Values.Clear();   // sin sesion: hay que entrar, y el servidor no responde
        _host.Server.On("POST", "/auth/v1/signup", _ => World.Fail());
        var page = new WelcomePage();
        page.Host();
        page.All<Entry>().Single().Text = "Bea";
        page.ClickKey("WelcomeContinue");
        Assert.Contains(Loc.Get("Err_network"), await page.DialogTextAsync());
        await page.AnswerKeyAsync("Ok");
        Assert.False(AppState.WelcomeDone);
        Assert.True(page.ButtonKey("WelcomeContinue").IsEnabled);
    }

    // -----------------------------------------------------------------------
    // Acerca de y novedades
    // -----------------------------------------------------------------------

    [Fact]
    public async Task AcercaDeVersionIdiomaYEnlacesQueSeCopianSiNoSePuedenAbrir()
    {
        Shell();
        var page = new AboutPage();
        Assert.True(page.Shows("v" + MauiFakes.AppInfo.VersionString));
        Assert.Equal(3, page.Button("Español").BorderWidth);

        // Sin navegador ni correo (fuera del movil): se copian y se dice.
        page.ClickKey("PrivacyPolicy");
        Assert.Contains("PRIVACY.md", await page.DialogTextAsync());
        await page.AnswerKeyAsync("Ok");
        Assert.EndsWith("PRIVACY.md", MauiFakes.Clipboard.Text);

        page.ClickKey("WriteAuthor");
        Assert.Contains("@", await page.DialogTextAsync());
        await page.AnswerKeyAsync("Ok");

        page.ClickKey("WhatsNewOpen");
        page.Click("English");
        Assert.Equal(Loc.English, Loc.Preference);
        await UiDriver.Settle();
        var english = new AboutPage();
        Assert.Equal(3, english.Button("English").BorderWidth);
        english.Click("Español");
        Assert.Equal(Loc.Spanish, Loc.Preference);
    }

    [Fact]
    public async Task NovedadesEnsenaLasVersionesYMarcaLaActual()
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Mobile", "whatsnew.json"));
        MauiFakes.FileSystem.Package["whatsnew.json"] = json;
        try
        {
            var first = System.Text.Json.JsonDocument.Parse(json).RootElement[0].GetProperty("version").GetString()!;
            MauiFakes.AppInfo.VersionString = first;
            var page = new WhatsNewPage();
            page.Host();
            page.Appear();
            await UiDriver.Until(() => page.Shows(Loc.Format("WhatsNewCurrent", first)), "novedades");
            Assert.Equal(first, AppState.LastVersionSeen);
            Assert.False(AppState.HasUnseenVersion);
            Assert.Equal(WhatsNew.MaxVersions, page.All<Border>().Count());

            Loc.SetPreference(Loc.English);
            Loc.Apply();
            var releases = await WhatsNew.LoadAsync();
            Assert.Equal(WhatsNew.MaxVersions, releases.Count);
            Assert.DoesNotContain(releases[0].Items, i => i.Contains("ñ"));

            MauiFakes.FileSystem.Package["whatsnew.json"] = """[{"version":"1.0","fr":["x"]},{"version":"2.0","en":["y"],"date":"no"}]""";
            var odd = await WhatsNew.LoadAsync();
            Assert.Empty(odd[0].Items);
            Assert.Equal(["y"], odd[1].Items);
            var oddPage = new WhatsNewPage();
            oddPage.Host();
            oddPage.Appear();
            await UiDriver.Until(() => oddPage.Shows("y"), "sin fecha");
        }
        finally
        {
            MauiFakes.FileSystem.Package.Remove("whatsnew.json");
            MauiFakes.AppInfo.VersionString = "2026.10.02.00";
        }

        var empty = new WhatsNewPage();
        empty.Host();
        empty.Appear();
        await UiDriver.Until(() => empty.Shows(Loc.Get("WhatsNewEmpty")), "vacio");
    }

    [Fact]
    public void PreferenciasRotasNoRompenElEstado()
    {
        AppState.SelectedGroup = Guid.NewGuid();
        AppState.SelectedGroup = Guid.Empty;
        Assert.Equal(Guid.Empty, AppState.SelectedGroup);
        MauiFakes.Preferences.Broken = true;
        try
        {
            AppState.WelcomeDone = true;
            Assert.False(AppState.WelcomeDone);
            Assert.True(AppState.SnapTracks);
            Assert.Equal(string.Empty, AppState.LastVersionSeen);
        }
        finally
        {
            MauiFakes.Preferences.Broken = false;
        }
    }
}
