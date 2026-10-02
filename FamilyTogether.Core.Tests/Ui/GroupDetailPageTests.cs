using FamilyTogether.Mobile.Localization;
using FamilyTogether.Mobile.Pages;
using FamilyTogether.Mobile.Services;

namespace FamilyTogether.Core.Tests.Ui;

/// <summary>Detalle de un grupo: miembros, solicitudes, invitar, pausa y abandonar.</summary>
public class GroupDetailPageTests : IDisposable
{
    private readonly AppHost _host = new();
    private readonly World _world;

    public GroupDetailPageTests() => _world = new World(_host.Phone);

    public void Dispose() => _host.Dispose();

    private static readonly Guid Ana = Guid.NewGuid();
    private static readonly Guid Blas = Guid.NewGuid();

    private async Task<(GroupDetailPage Page, NavigationPage Nav, World.GroupData Group)> OpenAsync(bool admin = true, Action<World.GroupData>? setup = null)
    {
        var g = await _world.GroupAsync("Casa", admin,
            new World.MemberData(Ana, "Ana", true),
            new World.MemberData(Blas, "Blas", false, Paused: true, Until: DateTimeOffset.UtcNow.AddHours(3)));
        setup?.Invoke(g);
        var root = new ContentPage();
        var nav = root.Host();
        var page = new GroupDetailPage(new Group(g.Id, g.Name, admin, false));
        await nav.PushAsync(page);
        page.Appear();
        await UiDriver.Until(() => page.Shows("Blas") || page.Dialog() is not null, "cargar miembros");
        return (page, nav, g);
    }

    /// <summary>Una solicitud pendiente bien formada (el nombre se abre con la clave del grupo).</summary>
    private void PendingRequest(World.GroupData g, Guid id, string name)
    {
        var (invPub, invPriv) = Crypto.NewEcdhKeyPair();
        var (reqPub, reqPriv) = Crypto.NewEcdhKeyPair();
        _host.Server.Table("join_requests", _ => FakeSupabase.Json(FakeSupabase.Serialize(new[]
        {
            new
            {
                id,
                group_id = g.Id,
                user_id = Guid.NewGuid(),
                invitation_code = "ABCD2345",
                req_pub = reqPub,
                inv_pub = invPub,
                inv_priv_enc = Crypto.Encrypt(invPriv, g.Key),
                name_box = Crypto.Encrypt(name, Crypto.SharedKey(reqPriv, invPub)),
                status = "pending",
                created_at = DateTimeOffset.UtcNow.AddMinutes(-3),
            },
        })));
    }

    [Fact]
    public async Task AdminVeMiembrosConPapelYPausaYPuedeInvitar()
    {
        var (page, nav, _) = await OpenAsync();
        Assert.True(page.Shows("Casa"));
        Assert.True(page.Shows(Loc.Format("MeSuffix", "Yo")));
        Assert.True(page.Shows(Loc.Get("SharingInGroup")));
        Assert.True(page.ButtonKey("Invite").IsVisible);
        Assert.NotNull(page.Icon(Loc.Get("MakeAdmin")));
        Assert.NotNull(page.Icon(Loc.Get("RemoveAdmin")));

        page.ClickKey("Invite");
        await UiDriver.Until(() => nav.CurrentPage is InvitePage, "invitar");
    }

    [Fact]
    public async Task MiembroNoGestionaANadieNiInvita()
    {
        var (page, _, _) = await OpenAsync(admin: false);
        Assert.False(page.ButtonKey("Invite").IsVisible);
        Assert.Empty(page.All<ImageButton>());
    }

    [Fact]
    public async Task SinClaveSeAvisaConLaBanda()
    {
        var g = await _world.GroupAsync("Casa");
        await _host.Phone.Keys.RemoveAsync(g.Id);
        var page = new GroupDetailPage(new Group(g.Id, "Casa", true, true));
        page.Host();
        page.Appear();
        await UiDriver.Until(() => page.Shows(Loc.Get("KeyMissingBanner")), "banda");
        Assert.True(page.Shows(Loc.Get("GroupNoKeyName")));
        Assert.False(page.ButtonKey("Invite").IsVisible);
    }

    [Fact]
    public async Task SiYaNoSoyMiembroSeDiceYSeVuelve()
    {
        var root = new ContentPage();
        var nav = root.Host();
        var page = new GroupDetailPage(new Group(Guid.NewGuid(), "Viejo", true, false));
        await nav.PushAsync(page);
        page.Appear();
        Assert.Contains(Loc.Get("Err_not_member"), await page.DialogTextAsync());
        await page.AnswerKeyAsync("Ok");
        await UiDriver.Until(() => nav.CurrentPage == root, "volver");
    }

    [Fact]
    public async Task ErrorAlLeerElGrupoSeAvisa()
    {
        _host.Server.Table("group_members", _ => World.Fail());
        var page = new GroupDetailPage(new Group(Guid.NewGuid(), "x", true, false));
        page.Host();
        page.Appear();
        Assert.Contains(Loc.Get("Err_network"), await page.DialogTextAsync());
    }

    [Fact]
    public async Task SolicitudSeApruebaYSeQuitaElAviso()
    {
        var request = Guid.NewGuid();
        var (page, _, g) = await OpenAsync(setup: g => PendingRequest(g, request, "Marta"));
        await UiDriver.Until(() => page.Shows("Marta"), "solicitud");
        Assert.True(page.Shows(Loc.Format("RequestedAgo", "").Trim().Split(' ')[0]));

        page.ClickKey("Approve");
        await UiDriver.Until(() => _host.Server.To("POST", "/rest/v1/rpc/approve_request").Any(), "aprobar");
        await UiDriver.Until(() => _host.Notifier.Cancelled.Contains(request), "quitar aviso");
    }

    [Fact]
    public async Task SolicitudSeRechaza_YNombreIlegibleSaleComoDesconocido()
    {
        var request = Guid.NewGuid();
        var (page, _, _) = await OpenAsync(setup: g =>
        {
            PendingRequest(g, request, "Marta");
            var real = g.Key;
            _host.Server.Table("join_requests", _ => FakeSupabase.Json(FakeSupabase.Serialize(new[]
            {
                new { id = request, group_id = g.Id, user_id = Guid.NewGuid(), invitation_code = "X", req_pub = "roto", inv_pub = "roto", inv_priv_enc = "enc1:roto", name_box = "enc1:roto", status = "pending", created_at = DateTimeOffset.UtcNow },
            })));
        });
        await UiDriver.Until(() => page.Shows(Loc.Get("Unknown")), "desconocido");
        page.ClickKey("Reject");
        await UiDriver.Until(() => _host.Server.To("POST", "/rest/v1/rpc/reject_request").Any(), "rechazar");
    }

    [Fact]
    public async Task NombrarYQuitarAdministrador()
    {
        var (page, _, _) = await OpenAsync();

        page.ClickIconKey("MakeAdmin");
        Assert.Contains("Blas", await page.DialogTextAsync());
        await page.AnswerKeyAsync("Cancel");
        Assert.Empty(_host.Server.To("POST", "/rest/v1/rpc/set_role"));

        page.ClickIconKey("MakeAdmin");
        await page.AnswerKeyAsync("MakeAdmin");
        await UiDriver.Until(() => _host.Server.To("POST", "/rest/v1/rpc/set_role").Any(), "set_role");
        Assert.Equal("admin", _host.Server.RpcCall("set_role").Str("p_role"));

        page.ClickIconKey("RemoveAdmin");
        await page.AnswerKeyAsync("RemoveAdmin");
        await UiDriver.Until(() => _host.Server.To("POST", "/rest/v1/rpc/set_role").Count() == 2, "quitar admin");
    }

    [Fact]
    public async Task Expulsar()
    {
        var (page, _, _) = await OpenAsync();
        page.All<ImageButton>().First(b => SemanticProperties.GetDescription(b) == Loc.Get("Kick")).SendClickedForTests();
        await page.AnswerKeyAsync("Cancel");
        page.All<ImageButton>().First(b => SemanticProperties.GetDescription(b) == Loc.Get("Kick")).SendClickedForTests();
        await page.AnswerKeyAsync("Kick");
        await UiDriver.Until(() => _host.Server.To("POST", "/rest/v1/rpc/remove_member").Any(), "expulsar");
    }

    [Theory]
    [InlineData("PauseOneHour", 1)]
    [InlineData("PauseEightHours", 8)]
    [InlineData("PauseTomorrow", -1)]
    [InlineData("PauseIndefinite", 0)]
    public async Task PausarConCadaOpcion(string option, int hours)
    {
        var (page, _, _) = await OpenAsync();
        page.ClickKey("Pause");
        await page.AnswerKeyAsync(option);
        await UiDriver.Until(() => _host.Server.To("POST", "/rest/v1/rpc/set_pause").Any(), "pausa");
        var call = _host.Server.RpcCall("set_pause").Json;
        Assert.True(call.GetProperty("p_paused").GetBoolean());
        var until = call.GetProperty("p_until");
        if (hours == 0)
            Assert.Equal(System.Text.Json.JsonValueKind.Null, until.ValueKind);
        else if (hours > 0)
            Assert.InRange((until.GetDateTimeOffset() - DateTimeOffset.Now).TotalHours, hours - 0.1, hours + 0.1);
        else
            Assert.Equal(DateTime.Today.AddDays(1).AddHours(8), until.GetDateTimeOffset().LocalDateTime);
    }

    [Fact]
    public async Task PausarHastaUnaHoraElegidaOCancelar()
    {
        var (page, _, _) = await OpenAsync();
        page.ClickKey("Pause");
        await page.AnswerKeyAsync("Cancel");
        Assert.Empty(_host.Server.To("POST", "/rest/v1/rpc/set_pause"));

        page.ClickKey("Pause");
        await page.AnswerKeyAsync("PauseChooseTime");
        var picker = page.All<TimePicker>().Single();
        Assert.True(((View)picker.Parent.Parent).IsVisible);
        picker.Time = TimeSpan.FromMinutes(1);   // 00:01: ya ha pasado, es mañana
        page.ClickKey("PauseConfirm");
        await UiDriver.Until(() => _host.Server.To("POST", "/rest/v1/rpc/set_pause").Any(), "pausa");
        var until = _host.Server.RpcCall("set_pause").Json.GetProperty("p_until").GetDateTimeOffset().LocalDateTime;
        Assert.Equal(DateTime.Today.AddDays(1).AddMinutes(1), until);
    }

    [Fact]
    public async Task EnPausaSeReanuda()
    {
        var (page, _, _) = await OpenAsync(setup: g => g.Members[0] = g.Members[0] with { Paused = true });
        Assert.True(page.ButtonKey("Resume").Visible());
        Assert.False(page.All<Button>().Any(b => b.Text == Loc.Get("Pause") && b.Visible() && b.Parent == page.ButtonKey("Resume").Parent));
        page.ClickKey("Resume");
        await UiDriver.Until(() => _host.Server.To("POST", "/rest/v1/rpc/set_pause").Any(), "reanudar");
        Assert.False(_host.Server.RpcCall("set_pause").Json.GetProperty("p_paused").GetBoolean());
    }

    [Fact]
    public async Task AbandonarOlvidaElGrupoElegidoYVuelve()
    {
        var (page, nav, g) = await OpenAsync();
        AppState.SelectedGroup = g.Id;

        page.ClickKey("LeaveGroup");
        await page.AnswerKeyAsync("Cancel");
        Assert.Same(page, nav.CurrentPage);

        _host.Server.Rpc("leave_group", _ => World.Fail());
        page.ClickKey("LeaveGroup");
        await page.AnswerKeyAsync("LeaveGroup");
        await page.AnswerKeyAsync("Ok");
        Assert.Same(page, nav.CurrentPage);

        _host.Server.Rpc("leave_group", "null");
        page.ClickKey("LeaveGroup");
        await page.AnswerKeyAsync("LeaveGroup");
        await UiDriver.Until(() => nav.CurrentPage != page, "volver");
        Assert.Equal(Guid.Empty, AppState.SelectedGroup);
    }
}

internal static class ButtonTestExtensions
{
    public static void SendClickedForTests(this ImageButton button) => ((IButtonController)button).SendClicked();
}
