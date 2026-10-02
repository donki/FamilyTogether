using FamilyTogether.Mobile;
using FamilyTogether.Mobile.Localization;
using FamilyTogether.Mobile.Pages;
using Microsoft.Maui.ApplicationModel.DataTransfer;

namespace FamilyTogether.Core.Tests.Ui;

/// <summary>SOS: cuenta atras, a que grupos, sin posicion, cancelar y reintento sin red.</summary>
public class SosPageTests : IDisposable
{
    private readonly AppHost _host = new();
    private readonly World _world;

    public SosPageTests()
    {
        _world = new World(_host.Phone);
        SosPage.CountdownTick = TimeSpan.FromMilliseconds(5);
    }

    public void Dispose()
    {
        SosPage.CountdownTick = TimeSpan.FromSeconds(1);
        _host.Dispose();
    }

    private static (SosPage Page, NavigationPage Nav) Open()
    {
        var root = new ContentPage();
        var nav = root.Host();
        var page = new SosPage();
        nav.PushAsync(page).GetAwaiter().GetResult();
        page.Appear();
        return (page, nav);
    }

    private Label Headline(SosPage page) => page.All<Label>().First(l => l.FontSize == 20);

    [Fact]
    public async Task EnviaATodosLosGruposConClave()
    {
        var casa = await _world.GroupAsync("Casa");
        var playa = await _world.GroupAsync("Playa");
        var perdido = await _world.GroupAsync("Perdido");
        await _host.Phone.Keys.RemoveAsync(perdido.Id);

        var (page, nav) = Open();
        await UiDriver.Until(() => page.Shows(Loc.Get("SosSent")), "enviado");

        var sent = _host.Server.RpcCall("create_sos").Body;
        Assert.Contains(casa.Id.ToString("D"), sent);
        Assert.Contains(playa.Id.ToString("D"), sent);
        Assert.DoesNotContain(perdido.Id.ToString("D"), sent);
        Assert.True(page.Shows(Loc.Format("SosSentBody", 2)));
        Assert.True(page.Shows(Loc.Get("GroupNoKeyName")));

        // Ya enviado: atras no cancela nada, y «Volver al mapa» vuelve.
        Assert.False(((IBackHandler)page).HandleBack());
        page.ClickKey("BackToMap");
        await UiDriver.Until(() => nav.CurrentPage != page, "volver");
        page.Disappear();
    }

    [Fact]
    public async Task ConPosicionViejaLoDice()
    {
        await _world.GroupAsync("Casa");
        _host.Location.Position = (40, -3, 30, DateTimeOffset.UtcNow.AddMinutes(-20), true);
        var (page, _) = Open();
        await UiDriver.Until(() => page.Shows(Loc.Get("SosSent")), "enviado");
        Assert.True(page.Texts().Any(t => t.Contains(Loc.Format("SosStale", "").Split('{')[0].Trim().Split(' ')[0])));
    }

    [Fact]
    public async Task SinPosicionNoSeEnviaYSeAvisa()
    {
        await _world.GroupAsync("Casa");
        _host.Location.Position = null;
        var (page, _) = Open();
        Assert.Contains(Loc.Get("SosNoLocation"), await page.DialogTextAsync());
        await page.AnswerKeyAsync("Ok");
        Assert.Equal(Loc.Get("SosNotSent"), Headline(page).Text);
        Assert.Empty(_host.Server.To("POST", "/rest/v1/rpc/create_sos"));
    }

    [Fact]
    public async Task SinGruposMarcadosNoSeEnvia()
    {
        SosPage.CountdownTick = TimeSpan.FromMilliseconds(400);
        await _world.GroupAsync("Casa");
        var (page, _) = Open();
        await UiDriver.Until(() => page.Shows("Casa"), "grupos");
        var label = page.All<Label>().First(l => l.Text == "Casa");
        label.Tap();   // desmarca
        await UiDriver.Until(() => page.Shows(Loc.Get("SosNoTargets")), "sin destinos", 8000);
        label.Tap();   // ya no cambia
        Assert.False(page.All<CheckBox>().Single().IsChecked);
        Assert.Empty(_host.Server.To("POST", "/rest/v1/rpc/create_sos"));
    }

    [Fact]
    public async Task SinGruposLoDice_YCancelarConAtrasNoEnvia()
    {
        SosPage.CountdownTick = TimeSpan.FromSeconds(2);
        var (page, nav) = Open();
        await UiDriver.Until(() => page.Shows(Loc.Get("SosNoGroups")), "sin grupos");

        Assert.False(((IBackHandler)page).HandleBack());
        Assert.Equal(Loc.Get("SosCancelled"), Headline(page).Text);
        page.ClickKey("SosCancel");   // ya cancelado: no pasa nada
        page.Appear();                // volver a la pantalla no rearma la cuenta atras
        await Task.Delay(50);
        Assert.Empty(_host.Server.To("POST", "/rest/v1/rpc/create_sos"));
        page.Disappear();
    }

    [Fact]
    public async Task CancelarConElBoton()
    {
        SosPage.CountdownTick = TimeSpan.FromSeconds(2);
        await _world.GroupAsync("Casa");
        var (page, _) = Open();
        await UiDriver.Until(() => page.Shows("Casa"), "grupos");
        page.ClickKey("SosCancel");
        Assert.True(page.Shows(Loc.Get("SosCancelledBody")));
        Assert.False(page.All<CheckBox>().Single().IsEnabled);
    }

    [Fact]
    public async Task ErrorAlLeerLosGruposSeEnsenaEnLaLista()
    {
        _host.Server.Table("group_members", _ => World.Fail());
        var (page, _) = Open();
        await UiDriver.Until(() => page.Shows(Loc.Get("Err_network")), "error");
        await UiDriver.Until(() => page.Shows(Loc.Get("SosNoTargets")), "sin destinos");
    }

    [Fact]
    public async Task ErrorAlLocalizarCuentaComoSinPosicion()
    {
        await _world.GroupAsync("Casa");
        _host.Location.Throw = true;
        var (page, _) = Open();
        Assert.Contains(Loc.Get("SosNoLocation"), await page.DialogTextAsync());
    }

    [Fact]
    public async Task SinRedQuedaPendienteYSeReintentaSolo()
    {
        await _world.GroupAsync("Casa");
        var fail = true;
        _host.Server.Rpc("create_sos", _ => fail ? World.Fail() : FakeSupabase.Json("null"));
        var (page, _) = Open();
        await UiDriver.Until(() => page.Shows(Loc.Get("SosPendingBody")), "pendiente");

        var timer = MauiFakes.Dispatcher.Timers.Last();
        Assert.True(timer.IsRunning);
        timer.Fire();   // sigue sin red
        await UiDriver.Settle(200);
        Assert.True(timer.IsRunning);

        fail = false;
        timer.Fire();
        await UiDriver.Until(() => page.Shows(Loc.Get("SosSent")), "enviado al reintentar");
        Assert.False(timer.IsRunning);
        page.Disappear();
    }
}

/// <summary>Invitar: QR y codigo, cuenta atras, caducidad, codigo nuevo y compartir.</summary>
public class InvitePageTests : IDisposable
{
    private readonly AppHost _host = new();

    public InvitePageTests() => MauiFakes.Share.Requests.Clear();

    public void Dispose() => _host.Dispose();

    private void Invitation(string code, TimeSpan left) =>
        _host.Server.Rpc("create_invitation", FakeSupabase.Serialize(new[] { new { code, group_id = Guid.NewGuid(), expires_at = DateTimeOffset.UtcNow.Add(left) } }));

    private async Task<(InvitePage Page, NavigationPage Nav, Group Group)> OpenAsync()
    {
        var g = Guid.NewGuid();
        await _host.Phone.WithKeyAsync(g);
        var group = new Group(g, "Casa", true, false);
        var root = new ContentPage();
        var nav = root.Host();
        var page = new InvitePage(group);
        await nav.PushAsync(page);
        page.Appear();
        return (page, nav, group);
    }

    [Fact]
    public async Task EnsenaElCodigoLaCuentaAtrasYComparte()
    {
        Invitation("ABCD2345", TimeSpan.FromMinutes(5));
        var (page, nav, _) = await OpenAsync();
        await UiDriver.Until(() => page.Shows("ABCD-2345"), "codigo");
        Assert.NotNull(page.All<Image>().First(i => i.WidthRequest == 260).Source);
        Assert.True(page.Shows(Loc.Format("InviteExpiresIn", "4:").Split('{')[0]));

        page.ClickKey("ShareCode");
        await UiDriver.Until(() => MauiFakes.Share.Requests.Count == 1, "compartir");
        var text = ((ShareTextRequest)MauiFakes.Share.Requests[0]).Text;
        Assert.Contains("ABCD-2345", text);
        Assert.Contains("Casa", text);

        // El reloj de la pagina avanza con el temporizador; al salir se para.
        var timer = MauiFakes.Dispatcher.Timers.Last();
        timer.Fire();
        page.Disappear();
        Assert.False(timer.IsRunning);
        page.Appear();   // vuelve: no pide otro codigo
        Assert.Single(_host.Server.To("POST", "/rest/v1/rpc/create_invitation"));

        page.Toolbar("Close");
        await UiDriver.Until(() => nav.CurrentPage != page, "cerrar");
    }

    [Fact]
    public async Task CaducadoSeAtenuaYSePideOtro()
    {
        Invitation("ABCD2345", TimeSpan.FromSeconds(-1));
        var (page, _, _) = await OpenAsync();
        await UiDriver.Until(() => page.Shows(Loc.Get("InviteExpired")), "caducado");
        Assert.False(page.ButtonKey("ShareCode").IsEnabled);

        Invitation("WXYZ6789", TimeSpan.FromMinutes(5));
        page.ClickKey("InviteNewCode");
        await UiDriver.Until(() => page.Shows("WXYZ-6789"), "codigo nuevo");
        Assert.True(page.ButtonKey("ShareCode").IsEnabled);
    }

    [Fact]
    public async Task ErrorAlCrearSeAvisaYCompartirNoHaceNada()
    {
        _host.Server.Rpc("create_invitation", _ => World.Fail());
        var (page, _, _) = await OpenAsync();
        Assert.Contains(Loc.Get("Err_network"), await page.DialogTextAsync());
        await page.AnswerKeyAsync("Ok");
        page.ClickKey("ShareCode");
        MauiFakes.Dispatcher.Timers.Last().Fire();
        Assert.Empty(MauiFakes.Share.Requests);
    }

    [Fact]
    public async Task SiCompartirFallaSeAvisa()
    {
        Invitation("ABCD2345", TimeSpan.FromMinutes(5));
        var (page, _, _) = await OpenAsync();
        await UiDriver.Until(() => page.Shows("ABCD-2345"), "codigo");
        MauiFakes.Share.Fail = true;
        try
        {
            page.ClickKey("ShareCode");
            Assert.Contains(Loc.Get("ErrorTitle"), await page.DialogTextAsync());
        }
        finally
        {
            MauiFakes.Share.Fail = false;
        }
    }
}
