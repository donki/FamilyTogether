using FamilyTogether.Mobile;
using FamilyTogether.Mobile.Localization;
using FamilyTogether.Mobile.Pages;
using FamilyTogether.Mobile.Services;
using Microsoft.Maui.ApplicationModel;

namespace FamilyTogether.Core.Tests.Ui;

/// <summary>Grupos: lista, crear, unirse con codigo o QR y el estado de mis solicitudes.</summary>
public class GroupsPageTests : IDisposable
{
    private readonly AppHost _host = new();
    private readonly World _world;

    public GroupsPageTests()
    {
        _world = new World(_host.Phone);
        ScanQrPage.CameraPermission = () => Task.FromResult(PermissionStatus.Granted);
        App.PendingJoinCode = null;
    }

    public void Dispose()
    {
        App.PendingJoinCode = null;
        _host.Dispose();
    }

    private async Task<(GroupsPage Page, NavigationPage Nav)> OpenAsync()
    {
        var page = new GroupsPage();
        var nav = page.Host();
        page.Appear();
        await UiDriver.Until(() => page.Shows(Loc.Get("NoGroupsBody")) || page.All<Label>().Any(l => _world.Groups.Any(g => g.Name == l.Text)) || page.Dialog() is not null, "cargar grupos");
        return (page, nav);
    }

    [Fact]
    public async Task SinGruposLoDiceYConGruposEnsenaPapelYPausa()
    {
        var (empty, _) = await OpenAsync();
        Assert.True(empty.Shows(Loc.Get("NoGroupsBody")));
        empty.Disappear();

        var casa = await _world.GroupAsync("Casa", admin: true);
        var playa = await _world.GroupAsync("Playa", admin: false);
        playa.Members[0] = playa.Members[0] with { Paused = true, Until = DateTimeOffset.UtcNow.AddHours(2) };
        var perdido = await _world.GroupAsync("Perdido");
        await _host.Phone.Keys.RemoveAsync(perdido.Id);

        var (page, _) = await OpenAsync();
        await UiDriver.Until(() => page.Shows("Playa"));
        Assert.True(page.Shows(Loc.Get("RoleAdmin")));
        Assert.True(page.Shows(Loc.Get("RoleMember") + " · "));
        Assert.True(page.Shows(Loc.Get("GroupNoKeyName")));
        Assert.True(page.Shows(Loc.Get("KeyMissingShort")));
    }

    [Fact]
    public async Task SiFallanLosMiembrosDeUnGrupoSeEnsenaSinPausa()
    {
        var casa = await _world.GroupAsync("Casa");
        _host.Server.Table("group_members", r => r.Query.Contains("group_id=eq.")
            ? World.Fail()
            : FakeSupabase.Json(FakeSupabase.Serialize(new[] { new { group_id = casa.Id, user_id = _host.Phone.Me, role = "admin", paused = false } })));
        var (page, _) = await OpenAsync();
        await UiDriver.Until(() => page.Shows("Casa"), "grupo sin miembros");
        Assert.True(page.Shows(Loc.Get("RoleAdmin")));
    }

    [Fact]
    public async Task SiElServidorFallaSaleElAvisoDeRed()
    {
        _host.Server.Table("group_members", _ => World.Fail());
        var page = new GroupsPage();
        page.Host();
        page.Appear();
        Assert.Contains(Loc.Get("Err_network"), await page.DialogTextAsync());
        await page.AnswerKeyAsync("Ok");
    }

    [Fact]
    public async Task CrearGrupo_VacioSeAvisaYConNombreSeCreaYAbreElDetalle()
    {
        var (page, nav) = await OpenAsync();

        page.ClickKey("CreateGroup");
        await page.AnswerKeyAsync("Cancel");
        Assert.Empty(_host.Server.To("POST", "/rest/v1/rpc/create_group"));

        page.ClickKey("CreateGroup");
        await page.PromptAsync("   ", "Create");
        Assert.Contains(Loc.Get("GroupNameRequired"), await page.DialogTextAsync());
        await page.AnswerKeyAsync("Ok");

        var id = Guid.NewGuid();
        _host.Server.Rpc("create_group", $"\"{id}\"");
        page.Toolbar("CreateGroup");
        await page.PromptAsync("Familia", "Create");

        await UiDriver.Until(() => nav.CurrentPage is GroupDetailPage, "abrir el detalle");
        Assert.Equal(id, AppState.SelectedGroup);
        Assert.Single(_host.Server.To("POST", "/rest/v1/rpc/create_group"));
    }

    [Fact]
    public async Task CrearGrupo_ErrorDelServidorSeTraduce()
    {
        var (page, nav) = await OpenAsync();
        _host.Server.Rpc("create_group", _ => World.Fail("server_error"));
        page.ClickKey("CreateGroup");
        await page.PromptAsync("Familia", "Create");
        await page.DialogTextAsync();
        await page.AnswerKeyAsync("Ok");
        Assert.Same(page, nav.CurrentPage);
    }

    [Fact]
    public async Task UnirseConCodigo_InvalidoSeAvisaYValidoSeEnvia()
    {
        var (page, _) = await OpenAsync();

        page.ClickKey("JoinWithCode");
        await page.AnswerKeyAsync("Cancel");

        page.ClickKey("JoinWithCode");
        await page.PromptAsync("123", "Join");
        Assert.Contains(Loc.Get("JoinCodeInvalid"), await page.DialogTextAsync());
        await page.AnswerKeyAsync("Ok");

        var (invPub, _) = Crypto.NewEcdhKeyPair();
        _host.Server.Rpc("invitation_info", FakeSupabase.Serialize(new[] { new { code = "ABCD2345", group_id = Guid.NewGuid(), inv_pub = invPub } }));
        _host.Server.Rpc("request_join", $"\"{Guid.NewGuid()}\"");
        page.ClickKey("JoinWithCode");
        await page.PromptAsync("abcd-2345", "Join");
        Assert.Contains(Loc.Get("JoinSent"), await page.DialogTextAsync());
        await page.AnswerKeyAsync("Ok");

        // La solicitud queda pendiente y se ve en «Mis solicitudes».
        _host.Server.Table("join_requests", _ => FakeSupabase.Json("[]"));
        await UiDriver.Until(() => page.Shows(Loc.Get("RequestPending")) || page.Dialog() is not null, "solicitud pendiente");
    }

    [Fact]
    public async Task UnirseConCodigo_ServidorQueRechazaElCodigo()
    {
        var (page, _) = await OpenAsync();
        _host.Server.Rpc("invitation_info", _ => World.Fail("invalid_code"));
        page.ClickKey("JoinWithCode");
        await page.PromptAsync("ABCD2345", "Join");
        var text = await page.DialogTextAsync();
        Assert.Contains(Loc.Get("ErrorTitle"), text);
        await page.AnswerKeyAsync("Ok");
    }

    [Fact]
    public async Task EnlaceDeInvitacionPendientePideConfirmar()
    {
        App.PendingJoinCode = "ABCD2345";
        var page = new GroupsPage();
        page.Host();
        page.Appear();
        Assert.Contains("ABCD-2345", await page.DialogTextAsync());
        await page.AnswerKeyAsync("Cancel");
        Assert.Null(App.PendingJoinCode);
        Assert.Empty(_host.Server.To("POST", "/rest/v1/rpc/request_join"));
    }

    [Fact]
    public async Task EnlaceDeInvitacionConfirmadoEnviaLaSolicitud()
    {
        var (invPub, _) = Crypto.NewEcdhKeyPair();
        _host.Server.Rpc("invitation_info", FakeSupabase.Serialize(new[] { new { code = "ABCD2345", group_id = Guid.NewGuid(), inv_pub = invPub } }));
        _host.Server.Rpc("request_join", $"\"{Guid.NewGuid()}\"");
        App.PendingJoinCode = "ABCD2345";
        var page = new GroupsPage();
        page.Host();
        page.Appear();
        await page.AnswerKeyAsync("Join");
        Assert.Contains(Loc.Get("JoinSent"), await page.DialogTextAsync());
        Assert.Single(_host.Server.To("POST", "/rest/v1/rpc/request_join"));
    }

    [Fact]
    public async Task EscanearQr_SinCamaraLoDice()
    {
        ScanQrPage.CameraPermission = () => Task.FromResult(PermissionStatus.Denied);
        var (page, _) = await OpenAsync();
        page.ClickKey("ScanQr");
        Assert.Contains(Loc.Get("ScanNoCamera"), await page.DialogTextAsync());
        await page.AnswerKeyAsync("Ok");
    }

    [Fact]
    public async Task EscanearQr_AbreLaCamaraYAlCerrarSinLeerNoPasaNada()
    {
        var (page, nav) = await OpenAsync();
        page.Toolbar("ScanQr");
        await UiDriver.Until(() => nav.CurrentPage is ScanQrPage, "abrir la camara");
        await nav.PopAsync();
        await UiDriver.Settle();
        Assert.Same(page, nav.CurrentPage);
        Assert.Empty(_host.Server.To("POST", "/rest/v1/rpc/request_join"));
    }

    [Fact]
    public async Task MisSolicitudes_AprobadaYRechazadaSeDicenUnaVez()
    {
        var approved = Guid.NewGuid();
        var rejected = Guid.NewGuid();
        var pending = Guid.NewGuid();
        _host.Phone.Store.Values["pending_join_requests"] = $"{approved},{rejected},{pending}";
        var groupKey = Crypto.NewGroupKey();
        var group = Guid.NewGuid();
        var (invPub, invPriv) = Crypto.NewEcdhKeyPair();
        var (reqPub, reqPriv) = Crypto.NewEcdhKeyPair();
        _host.Phone.Store.Values[$"req_priv_{approved:D}"] = reqPriv;
        var keyBox = FamilyService.SealKeyBox(groupKey, Crypto.SharedKey(invPriv, reqPub));
        _host.Server.Table("join_requests", r =>
            r.Query.Contains(approved.ToString("D"))
                ? FakeSupabase.Json(FakeSupabase.Serialize(new[] { new { id = approved, group_id = group, status = "approved", key_box = keyBox, inv_pub = invPub } }))
            : r.Query.Contains(rejected.ToString("D"))
                ? FakeSupabase.Json(FakeSupabase.Serialize(new[] { new { id = rejected, group_id = group, status = "rejected" } }))
                : FakeSupabase.Json(FakeSupabase.Serialize(new[] { new { id = pending, group_id = group, status = "pending" } })));

        var page = new GroupsPage();
        page.Host();
        page.Appear();

        Assert.Contains(Loc.Get("RequestApprovedBody"), await page.DialogTextAsync());
        await page.AnswerKeyAsync("Ok");
        Assert.Contains(Loc.Get("RequestRejectedBody"), await page.DialogTextAsync());
        await page.AnswerKeyAsync("Ok");
        await UiDriver.Until(() => page.Shows(Loc.Get("RequestPending")), "la pendiente en la tarjeta");
        Assert.Equal(groupKey, await _host.Phone.Keys.GetAsync(group));
    }

    [Fact]
    public async Task MisSolicitudes_SinRedQuedaComoPendiente()
    {
        var id = Guid.NewGuid();
        _host.Phone.Store.Values["pending_join_requests"] = id.ToString();
        _host.Server.Table("join_requests", _ => World.Fail());
        var (page, _) = await OpenAsync();
        await UiDriver.Until(() => page.Shows(Loc.Get("RequestPending")), "pendiente");
    }

    [Fact]
    public async Task VolverALaAppRecargaYTocarUnGrupoAbreSuDetalle()
    {
        var casa = await _world.GroupAsync("Casa");
        var (page, nav) = await OpenAsync();
        await UiDriver.Until(() => page.Shows("Casa"));
        var before = _host.Server.Requests.Count;

        // Ha entrado en otro grupo mientras tanto: se recarga y se avisa al servicio.
        await _world.GroupAsync("Nuevo");
        RaiseResumed();
        await UiDriver.Until(() => page.Shows("Nuevo"), "recargar al volver");
        Assert.True(_host.Server.Requests.Count > before);

        var row = page.All<Grid>().First(g => g.GestureRecognizers.Count > 0 && g.Shows("Casa"));
        row.Tap();
        await UiDriver.Until(() => nav.CurrentPage is GroupDetailPage, "detalle");

        page.Disappear();
    }

    internal static void RaiseResumed()
    {
        var field = typeof(App).GetField("AppResumed", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        (field.GetValue(null) as EventHandler)?.Invoke(null, EventArgs.Empty);
    }

    [Fact]
    public void CodigoBonito()
    {
        Assert.Equal("ABCD-2345", GroupsPage.Pretty("ABCD2345"));
        Assert.Equal("ABC", GroupsPage.Pretty("ABC"));
    }
}
