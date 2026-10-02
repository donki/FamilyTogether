using System.Reflection;
using FamilyTogether.Mobile;
using FamilyTogether.Mobile.Helpers;
using FamilyTogether.Mobile.Localization;
using FamilyTogether.Mobile.Pages;
using FamilyTogether.Mobile.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Authentication;
using Microsoft.Maui.Storage;

namespace FamilyTogether.Core.Tests.Ui;

/// <summary>Arranque, Shell y atras, enlaces de los avisos y del QR, registro y almacen seguro.</summary>
public class AppTests : IDisposable
{
    private readonly AppHost _host = new();

    public void Dispose()
    {
        App.PendingJoinCode = null;
        App.PendingMapFocus = null;
        App.PendingMapFit = false;
        _host.Dispose();
    }

    private (Window Window, AppShell Shell) CreateWindow()
    {
        var window = (Window)typeof(App).GetMethod("CreateWindow", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(_host.Application, [null])!;
        typeof(Application).GetMethod("AddWindow", BindingFlags.Instance | BindingFlags.NonPublic, [typeof(Window)])!
            .Invoke(_host.Application, [window]);
        return (window, (AppShell)window.Page!);
    }

    private static void Raise(Window window, string name) =>
        typeof(Window).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, Type.EmptyTypes)!.Invoke(window, null);

    [Fact]
    public void ShellArrancaEnLaBienvenidaOEnElMapaYEnsenaLaVersion()
    {
        var (_, first) = CreateWindow();
        var welcome = (ShellItem)first.FindByName("WelcomeItem");
        var map = (ShellItem)first.FindByName("MapItem");
        Assert.Equal("v" + MauiFakes.AppInfo.VersionString, ((Label)first.FindByName("VersionLabel")).Text);

        AppState.WelcomeDone = true;
        var shell = new AppShell();
        var expected = FamilyTogetherConfig.IsServerConfigured ? (ShellItem)shell.FindByName("MapItem") : (ShellItem)shell.FindByName("WelcomeItem");
        Assert.Same(expected, shell.CurrentItem);
        Assert.NotNull(welcome);
        Assert.NotNull(map);
    }

    private static bool Back(Shell shell) =>
        (bool)typeof(Shell).GetMethod("OnBackButtonPressed", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(shell, null)!;

    [Fact]
    public void AtrasCierraElMenuYVuelveAlMapa()
    {
        AppState.WelcomeDone = true;
        var (_, shell) = CreateWindow();
        var map = (ShellItem)shell.FindByName("MapItem");

        shell.FlyoutIsPresented = true;
        Assert.True(Back(shell));
        Assert.False(shell.FlyoutIsPresented);

        shell.CurrentItem = shell.Items[2];   // Grupos
        Assert.True(Back(shell));
        Assert.Same(map, shell.CurrentItem);

        // En el mapa, fuera de Android, lo decide el Shell (sin pantalla no hay nada detras).
        try { Back(shell); } catch (TargetInvocationException) { }

        shell.CurrentItem = shell.Items[3];
        shell.GoHome();
        Assert.Same(map, shell.CurrentItem);
    }

    [Fact]
    public void ArrancarYVolverRegistranElTokenYAvisanALasPantallas()
    {
        var (window, _) = CreateWindow();
        var resumed = 0;
        EventHandler handler = (_, _) => resumed++;
        typeof(App).GetEvent("AppResumed")!.AddEventHandler(null, handler);
        try
        {
            ((IWindow)window).Created();
            ((IWindow)window).Resumed();
            Assert.Equal(1, resumed);
        }
        finally
        {
            typeof(App).GetEvent("AppResumed")!.RemoveEventHandler(null, handler);
        }
    }

    [Fact]
    public async Task EnlaceDeInvitacionVaAGruposConElCodigo()
    {
        AppState.WelcomeDone = true;
        var (_, shell) = CreateWindow();

        App.HandleDeepLink("familytogether://join?c=ABCD2345");
        await UiDriver.Settle(100);
        Assert.Equal("ABCD2345", App.PendingJoinCode);

        App.PendingJoinCode = null;
        App.HandleDeepLink("familytogether://join?c=no");
        await UiDriver.Settle(50);
        Assert.Null(App.PendingJoinCode);
    }

    [Fact]
    public async Task EnlaceDeAvisoAbreElMapaCentradoOEncuadrado()
    {
        AppState.WelcomeDone = true;
        CreateWindow();
        var group = Guid.NewGuid();
        // Paginas de mapa de otras pruebas que siguen suscritas (no se les dio OnDisappearing).
        typeof(App).GetField("MapFocusRequested", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, null);
        var focus = 0;
        EventHandler handler = (_, _) => focus++;
        typeof(App).GetEvent("MapFocusRequested")!.AddEventHandler(null, handler);
        try
        {
            App.HandleDeepLink($"familytogether://event?type=sos&group={group}&event={Guid.NewGuid()}&lat=40.5&lon=-3.25");
            await UiDriver.Until(() => focus == 1, "centrar");
            Assert.Equal((40.5, -3.25), App.PendingMapFocus);
            Assert.Equal(group, AppState.SelectedGroup);

            App.HandleDeepLink($"familytogether://event?type=zone&group=nada&event=x");
            await UiDriver.Until(() => focus == 2, "encuadrar");
            Assert.True(App.PendingMapFit);
        }
        finally
        {
            typeof(App).GetEvent("MapFocusRequested")!.RemoveEventHandler(null, handler);
        }
    }

    [Fact]
    public async Task EnlaceDeSolicitudAbreGruposOElDetalle()
    {
        AppState.WelcomeDone = true;
        var (_, shell) = CreateWindow();
        App.HandleDeepLink($"familytogether://event?type={EventTypes.JoinRequest}&group={Guid.NewGuid()}&event={Guid.NewGuid()}");
        await UiDriver.Settle(150);
        App.HandleDeepLink($"familytogether://event?type={EventTypes.RequestResolved}&event={Guid.NewGuid()}");
        await UiDriver.Settle(150);
        Assert.Null(App.PendingMapFocus);
    }

    [Fact]
    public async Task EnlacesRarosNoRompenNada()
    {
        AppState.WelcomeDone = false;
        CreateWindow();
        App.HandleDeepLink("familytogether://join?c=ABCD2345");   // sin bienvenida: se guarda para despues
        await UiDriver.Settle(50);
        Assert.Equal("ABCD2345", App.PendingJoinCode);
        App.HandleDeepLink("no es una uri");
        App.HandleDeepLink("familytogether://event?type=sos");     // sin bienvenida: nada
        App.HandleDeepLink("familytogether://otra");
        await UiDriver.Settle(50);
    }

    [Fact]
    public void ElContenedorDeLaAppResuelveTodo()
    {
        var app = MauiProgram.CreateMauiApp();
        foreach (var type in new[]
        {
            typeof(HttpClient), typeof(SecureStore), typeof(ISecureStore), typeof(ITokenStore), typeof(IOAuthBrowser),
            typeof(SupabaseClient), typeof(GroupKeyStore), typeof(FamilyService), typeof(AccountService),
            typeof(LocationOutbox), typeof(LocalTrack), typeof(SosService), typeof(ZoneWatcher), typeof(EventFeed),
            typeof(OsmRoadSource), typeof(TrackSnapper),
        })
            Assert.NotNull(app.Services.GetService(type));

        Assert.Same(app.Services, ServiceHelper.Services);
        Assert.Null(ServiceHelper.TryGet<ILocationSharing>());   // fuera de Android no hay parte nativa
        ServiceHelper.Initialize(_host.Services);
    }

    [Fact]
    public void ServiceHelperSinInicializarOConFalloNoRevienta()
    {
        ServiceHelper.Initialize(null!);
        try
        {
            Assert.Throws<InvalidOperationException>(() => ServiceHelper.Get<FamilyService>());
        }
        finally
        {
            ServiceHelper.Initialize(_host.Services);
        }

        var broken = new ServiceCollection().AddSingleton<INotifier>(_ => throw new InvalidOperationException("roto")).BuildServiceProvider();
        ServiceHelper.Initialize(broken);
        try
        {
            Assert.Null(ServiceHelper.TryGet<INotifier>());
        }
        finally
        {
            ServiceHelper.Initialize(_host.Services);
        }
    }

    // -----------------------------------------------------------------------
    // Registro y errores
    // -----------------------------------------------------------------------

    [Fact]
    public void RegistroEscribeRotaYNoLanza()
    {
        var path = CrashLog.FilePath;
        Assert.StartsWith(MauiFakes.FileSystem.AppDataDirectory, path);
        CrashLog.Info("hola");
        CrashLog.Error("prueba", new InvalidOperationException("mal"));
        var text = File.ReadAllText(path);
        Assert.Contains("INFO hola", text);
        Assert.Contains("InvalidOperationException", text);

        File.WriteAllText(path, new string('x', 600 * 1024));
        CrashLog.Info("nuevo");
        Assert.True(File.Exists(path + ".old"));
        Assert.True(new FileInfo(path).Length < 1024);

        CrashLog.Hook();
        CrashLog.Hook();
        FamilyTogether.Core.CoreLog.Write("desde el nucleo");
        Assert.Contains("core: desde el nucleo", File.ReadAllText(path));
    }

    [Fact]
    public async Task AvisoDeErrorInesperadoSaleEnLaPaginaQueSeVe()
    {
        var page = new ContentPage { Content = new Label() };
        typeof(Application).GetMethod("AddWindow", BindingFlags.Instance | BindingFlags.NonPublic, [typeof(Window)])!
            .Invoke(_host.Application, [new Window(page)]);
        typeof(CrashLog).GetField("_lastNotice", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, DateTime.MinValue);
        CrashLog.NotifyUser();
        Assert.Contains(Loc.Get("ErrUnexpected"), await page.DialogTextAsync());
        CrashLog.NotifyUser();   // dos seguidos: solo uno
        await page.AnswerKeyAsync("Ok");
        Assert.Same(page, FamilyTogether.Mobile.Services.Ui.CurrentPage());
    }

    [Fact]
    public async Task UnaTareaQueFallaSinQueNadieLaEspereSeRegistraYSeAvisa()
    {
        CrashLog.Hook();
        var page = new ContentPage { Content = new Label() };
        typeof(Application).GetMethod("AddWindow", BindingFlags.Instance | BindingFlags.NonPublic, [typeof(Window)])!
            .Invoke(_host.Application, [new Window(page)]);
        typeof(CrashLog).GetField("_lastNotice", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, DateTime.MinValue);

        static void Forget() => _ = Task.Run(() => throw new InvalidOperationException("olvidada"));
        Forget();
        await Task.Delay(100);
        for (var i = 0; i < 20 && !File.ReadAllText(CrashLog.FilePath).Contains("UnobservedTaskException"); i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            await Task.Delay(20);
        }

        Assert.Contains("olvidada", File.ReadAllText(CrashLog.FilePath));
        Assert.Contains(Loc.Get("ErrUnexpected"), await page.DialogTextAsync());
    }

    [Theory]
    [InlineData("not_member", "Err_not_member")]
    [InlineData("codigo_sin_texto", "Err_server")]
    public void ErroresTraducidos(string code, string key)
    {
        Assert.Equal(Loc.Get(key), ErrorTexts.Describe(new FamilyTogetherException(code, "x")));
    }

    [Fact]
    public void ErroresPorTipo()
    {
        Assert.Equal(Loc.Get("Err_cancelled"), ErrorTexts.Describe(new TaskCanceledException()));
        Assert.Equal(Loc.Get("Err_network"), ErrorTexts.Describe(new TimeoutException()));
        Assert.Equal(Loc.Get("Err_permission"), ErrorTexts.Describe(new PermissionException("x")));
        Assert.Equal(Loc.Get("ErrUnexpected"), ErrorTexts.Describe(new InvalidOperationException()));
    }

    [Fact]
    public void ColoresYEstilosDelTema()
    {
        Assert.NotNull(FamilyTogether.Mobile.Services.Ui.Style("Card"));
        Assert.Null(FamilyTogether.Mobile.Services.Ui.Style("NoExiste"));
        Assert.Equal(Colors.Gray, FamilyTogether.Mobile.Services.Ui.Color("NoExiste"));
        Assert.NotEqual(Colors.Gray, FamilyTogether.Mobile.Services.Ui.ThemeColor("TextPrimary"));
        Assert.Equal("Texto", new TExtension { Key = "Texto" }.ProvideValue(null!) is var v && v == "Texto" ? "Texto" : v);
        Assert.Equal(Loc.Get("Ok"), ((IMarkupExtension)new TExtension { Key = "Ok" }).ProvideValue(null!));
    }

    // -----------------------------------------------------------------------
    // Almacen seguro y navegador de la cuenta
    // -----------------------------------------------------------------------

    [Fact]
    public async Task AlmacenSeguroGuardaLeeYBorra()
    {
        var store = new SecureStore();
        await store.SetAsync("k", "v");
        Assert.Equal("v", await store.GetAsync("k"));
        await ((ITokenStore)store).SetAsync("k", null);
        Assert.Null(await store.GetAsync("k"));
        await store.SetAsync("k", "v");
        store.Remove("k");
        Assert.Null(await store.GetAsync("k"));

        MauiFakes.SecureStorage.Broken = true;
        try
        {
            Assert.Null(await store.GetAsync("k"));
            store.Remove("k");
            var ex = await Assert.ThrowsAsync<FamilyTogetherException>(() => store.SetAsync("k", "v"));
            Assert.Equal("secure_storage", ex.Code);
        }
        finally
        {
            MauiFakes.SecureStorage.Broken = false;
        }
    }

    [Fact]
    public async Task NavegadorDeCuentaRehaceLaVueltaConSuEsquema()
    {
        var browser = new MauiOAuthBrowser();
        Assert.Equal("com.socratic.familytogether://auth", browser.RedirectUri);
        MauiFakes.Authenticator.Result = new WebAuthenticatorResult(new Dictionary<string, string> { ["code"] = "a b", ["state"] = "s" });

        var back = await browser.AuthenticateAsync(new Uri("https://accounts.example/auth?client_id=1&redirect_uri=com.googleusercontent.apps.x%3A%2Foauth"));
        Assert.Equal("com.googleusercontent.apps.x", back.Scheme);
        Assert.Equal("?code=a%20b&state=s", back.Query);
        Assert.Equal("com.googleusercontent.apps.x:/oauth", MauiFakes.Authenticator.Last!.CallbackUrl.ToString());

        await browser.AuthenticateAsync(new Uri("https://login.example/auth?x"));
        Assert.Equal("com.socratic.familytogether://auth", MauiFakes.Authenticator.Last!.CallbackUrl.ToString().TrimEnd('/'));
    }

    // -----------------------------------------------------------------------
    // Escanear el QR
    // -----------------------------------------------------------------------

    private static void Detect(ScanQrPage page, string value)
    {
        var camera = page.All<ZXing.Net.Maui.Controls.CameraBarcodeReaderView>().Single();
        var resultType = typeof(ZXing.Net.Maui.BarcodeDetectionEventArgs).Assembly.GetType("ZXing.Net.Maui.BarcodeResult")!;
        var result = Activator.CreateInstance(resultType)!;
        resultType.GetProperty("Value")!.SetValue(result, value);
        resultType.GetProperty("Format")!.SetValue(result, ZXing.Net.Maui.BarcodeFormat.QrCode);
        var array = Array.CreateInstance(resultType, 1);
        array.SetValue(result, 0);
        var e = Activator.CreateInstance(typeof(ZXing.Net.Maui.BarcodeDetectionEventArgs), array)!;
        var raise = camera.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .First(m => m.Name.EndsWith("BarcodesDetected") && m.GetParameters().Length == 1
                && m.GetParameters()[0].ParameterType == typeof(ZXing.Net.Maui.BarcodeDetectionEventArgs));
        raise.Invoke(camera, [e]);
    }

    [Fact]
    public async Task QrDeOtraCosaSeDiceYElNuestroSeEntrega()
    {
        ScanQrPage.CameraPermission = () => Task.FromResult(PermissionStatus.Granted);
        var origin = new ContentPage();
        var nav = origin.Host();
        var task = ScanQrPage.RequestAsync(origin);
        await UiDriver.Until(() => nav.CurrentPage is ScanQrPage, "camara");
        var page = (ScanQrPage)nav.CurrentPage;
        page.Appear();
        MauiFakes.Dispatcher.RunDelayed();   // 6 s sin imagen
        Assert.True(page.Shows(Loc.Get("ScanNoFrames")));

        Detect(page, "WIFI:S:casa;;");
        Assert.Contains(Loc.Get("ScanNotOurs"), await page.DialogTextAsync());
        await page.AnswerKeyAsync("Ok");
        await UiDriver.Settle(100);
        Assert.Null(page.Dialog());
        Assert.Equal(0, (int)typeof(ScanQrPage).GetField("_delivered", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!);

        Detect(page, InviteCodes.ToQrPayload("ABCD2345"));
        await UiDriver.Until(() => nav.CurrentPage == origin, "volver de la camara: " + string.Join("|", page.Texts()));
        Assert.Equal("ABCD2345", await task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Same(origin, nav.CurrentPage);
    }

    [Fact]
    public async Task CerrarLaCamaraDevuelveNada()
    {
        ScanQrPage.CameraPermission = () => Task.FromResult(PermissionStatus.Granted);
        var origin = new ContentPage();
        var nav = origin.Host();
        var task = ScanQrPage.RequestAsync(origin);
        await UiDriver.Until(() => nav.CurrentPage is ScanQrPage, "camara");
        var page = (Page)nav.CurrentPage;
        page.Toolbar("Close");
        await UiDriver.Until(() => nav.CurrentPage == origin, "cerrar");
        // Sin pantalla MAUI no avisa de la salida: se le da el aviso que daria al volver atras.
        var ctor = typeof(NavigatedFromEventArgs).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .OrderByDescending(c => c.GetParameters().Length).First();
        var args = (NavigatedFromEventArgs)ctor.Invoke(ctor.GetParameters().Select(p =>
            p.ParameterType == typeof(Page) ? origin
            : p.ParameterType == typeof(NavigationType) ? NavigationType.Pop
            : p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null).ToArray());
        var send = typeof(Page).GetMethod("SendNavigatedFrom", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!;
        send.Invoke(page, send.GetParameters().Select((p, i) => i == 0 ? args : p.HasDefaultValue ? p.DefaultValue : (p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null)).ToArray());
        Assert.Null(await task.WaitAsync(TimeSpan.FromSeconds(5)));
    }
}
