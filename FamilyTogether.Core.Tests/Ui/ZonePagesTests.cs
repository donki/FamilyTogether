using FamilyTogether.Mobile.Localization;
using FamilyTogether.Mobile.Pages;
using FamilyTogether.Mobile.Services;

namespace FamilyTogether.Core.Tests.Ui;

/// <summary>Zonas: lista por grupo, crear, editar, borrar y avisos de entrada/salida.</summary>
public class ZonePagesTests : IDisposable
{
    private readonly AppHost _host = new();
    private readonly World _world;
    private static readonly Guid Ana = Guid.NewGuid();

    public ZonePagesTests() => _world = new World(_host.Phone);

    public void Dispose() => _host.Dispose();

    private async Task<World.GroupData> GroupWithZonesAsync()
    {
        var g = await _world.GroupAsync("Casa", true, new World.MemberData(Ana, "Ana", false));
        g.Zones.Add(new World.ZoneData(Guid.NewGuid(), "Colegio", 40, -3, 150, _host.Phone.Me));
        g.Zones.Add(new World.ZoneData(Guid.NewGuid(), "Abuela", 40.1, -3, 300, _host.Phone.Me));
        AppState.SelectedGroup = g.Id;
        return g;
    }

    private static (ZonesPage Page, NavigationPage Nav) OpenZones()
    {
        var page = new ZonesPage();
        var nav = page.Host();
        page.Appear();
        return (page, nav);
    }

    [Fact]
    public async Task ListaLasZonasOrdenadasConSuRadio()
    {
        await GroupWithZonesAsync();
        var (page, _) = OpenZones();
        await UiDriver.Until(() => page.Shows("Colegio"), "zonas");
        var names = page.All<Label>().Select(l => l.Text).Where(t => t is "Colegio" or "Abuela").ToList();
        Assert.Equal(["Abuela", "Colegio"], names);
        Assert.True(page.Shows(Loc.Format("RadiusMeters", 150)));

        // Tocar una zona la centra en el mapa (sin mapa cargado, no pasa nada).
        page.All<VerticalStackLayout>().First(v => v.GestureRecognizers.Count > 0).Tap();
    }

    [Fact]
    public async Task SinZonasYSinGruposLoDice()
    {
        var (empty, _) = OpenZones();
        await UiDriver.Until(() => empty.Shows(Loc.Get("NoGroupsWithKey")), "sin grupos");
        empty.ClickKey("NewZone");
        Assert.Contains(Loc.Get("NoGroupsWithKey"), await empty.DialogTextAsync());
        await empty.AnswerKeyAsync("Ok");
        empty.ClickKey("ZoneAlerts");
        Assert.Contains(Loc.Get("NoGroupsWithKey"), await empty.DialogTextAsync());
        await empty.AnswerKeyAsync("Ok");

        await _world.GroupAsync("Casa");
        var (page, _) = OpenZones();
        await UiDriver.Until(() => page.Shows(Loc.Get("NoZones")), "sin zonas");
    }

    [Fact]
    public async Task ErrorAlLeerZonasSeAvisa()
    {
        await GroupWithZonesAsync();
        _host.Server.Table("zones", _ => World.Fail());
        var (page, _) = OpenZones();
        Assert.Contains(Loc.Get("Err_network"), await page.DialogTextAsync());
    }

    [Fact]
    public async Task CambiarDeGrupoRecargaSusZonas()
    {
        await GroupWithZonesAsync();
        var otro = await _world.GroupAsync("Otro");
        otro.Zones.Add(new World.ZoneData(Guid.NewGuid(), "Piscina", 41, -3, 100, Ana));
        var (page, _) = OpenZones();
        await UiDriver.Until(() => page.Shows("Colegio"), "zonas");
        var picker = page.All<Picker>().Single();
        picker.SelectedIndex = picker.ItemsSource.Cast<string>().ToList().IndexOf("Otro");
        await UiDriver.Until(() => page.Shows("Piscina") && !page.Shows("Colegio"), "zonas del otro grupo");
        Assert.Equal(otro.Id, AppState.SelectedGroup);
    }

    [Fact]
    public async Task BorrarPideConfirmacion()
    {
        var g = await GroupWithZonesAsync();
        var (page, _) = OpenZones();
        await UiDriver.Until(() => page.Shows("Colegio"), "zonas");

        page.ClickIconKey("Delete");
        Assert.Contains("Abuela", await page.DialogTextAsync());
        await page.AnswerKeyAsync("Cancel");
        Assert.Empty(_host.Server.To("DELETE", "/rest/v1/zones"));

        page.ClickIconKey("Delete");
        await page.AnswerKeyAsync("Delete");
        await UiDriver.Until(() => _host.Server.To("DELETE", "/rest/v1/zones").Any(), "borrar");
        Assert.Contains(g.Zones.First(z => z.Name == "Abuela").Id.ToString("D"), _host.Server.To("DELETE", "/rest/v1/zones").Single().Query);
    }

    [Fact]
    public async Task NuevaZona_SinNombreSeAvisaYConNombreSeGuardaDondeEstoy()
    {
        var g = await GroupWithZonesAsync();
        var (page, nav) = OpenZones();
        await UiDriver.Until(() => page.Shows("Colegio"), "zonas");

        page.Toolbar("NewZone");
        await UiDriver.Until(() => nav.CurrentPage is ZoneEditPage, "editar");
        var edit = (ZoneEditPage)nav.CurrentPage;
        Assert.Equal(Loc.Get("NewZone"), edit.Title);
        edit.Appear();
        await UiDriver.Settle(100);

        edit.ClickKey("Save");
        Assert.Contains(Loc.Get("ZoneNameRequired"), await edit.DialogTextAsync());
        await edit.AnswerKeyAsync("Ok");

        edit.All<Entry>().Single().Text = "  Parque  ";
        var slider = edit.All<Slider>().Single();
        slider.Value = 333;   // se redondea a 330
        Assert.Equal(330, slider.Value);
        Assert.True(edit.Shows(Loc.Format("RadiusMeters", 330)));

        edit.ClickKey("Save");
        await UiDriver.Until(() => nav.CurrentPage == page, "volver");
        var insert = _host.Server.To("POST", "/rest/v1/zones").Single().Json;
        var row = insert.ValueKind == System.Text.Json.JsonValueKind.Array ? insert[0] : insert;
        Assert.Equal("Parque", Crypto.DecryptOr(row.GetProperty("name_enc").GetString(), g.Key, "x"));
        var geo = Payloads.Open<Payloads.ZoneData>(row.GetProperty("geo_enc").GetString(), g.Key)!;
        Assert.Equal(41.39, geo.Lat, 3);   // la posicion del movil (FakeLocationSharing)
        Assert.Equal(330, geo.R);
        edit.Disappear();
    }

    [Fact]
    public async Task EditarZona_TocarElMapaCambiaElCentroYErrorAlGuardarSeAvisa()
    {
        var g = await GroupWithZonesAsync();
        var zone = g.Zones[0];
        var page = new ZoneEditPage(new Group(g.Id, g.Name, true, false), new Zone(zone.Id, g.Id, zone.Name, zone.Lat, zone.Lon, zone.Radius, zone.CreatedBy));
        page.Host();
        page.Appear();
        Assert.Equal(Loc.Get("EditZone"), page.Title);
        Assert.Equal("Colegio", page.All<Entry>().Single().Text);

        var map = page.All<FamilyTogether.Mobile.Controls.MapView>().Single();
        typeof(FamilyTogether.Mobile.Controls.MapView).GetField("MapTapped", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(map).As<EventHandler<(double, double)>>()!.Invoke(map, (40.5, -3.5));

        _host.Server.On("PATCH", "/rest/v1/zones", _ => World.Fail());
        page.ClickKey("Save");
        Assert.Contains(Loc.Get("Err_network"), await page.DialogTextAsync());
        await page.AnswerKeyAsync("Ok");
        Assert.True(page.ButtonKey("Save").IsEnabled);
        var patch = _host.Server.To("PATCH", "/rest/v1/zones").Single();
        var geo = Payloads.Open<Payloads.ZoneData>(patch.Json.GetProperty("geo_enc").GetString(), g.Key)!;
        Assert.Equal(40.5, geo.Lat);
    }

    [Fact]
    public async Task EditarZona_SinPosicionSeQuedaEnElCentroPorDefecto()
    {
        var g = await GroupWithZonesAsync();
        _host.Location.Throw = true;
        var page = new ZoneEditPage(new Group(g.Id, g.Name, true, false), null);
        page.Host();
        page.Appear();
        await UiDriver.Settle(100);
        page.All<Entry>().Single().Text = "Plaza";
        _host.Server.On("POST", "/rest/v1/zones", _ => World.Fail());
        page.ClickKey("Save");
        await page.DialogTextAsync();
        var row = _host.Server.To("POST", "/rest/v1/zones").Single().Json;
        row = row.ValueKind == System.Text.Json.JsonValueKind.Array ? row[0] : row;
        Assert.Equal(40.4168, Payloads.Open<Payloads.ZoneData>(row.GetProperty("geo_enc").GetString(), g.Key)!.Lat);
    }

    // -----------------------------------------------------------------------
    // Avisos de zonas
    // -----------------------------------------------------------------------

    private async Task<(ZoneAlertsPage Page, World.GroupData Group)> OpenAlertsAsync(Action<World.GroupData>? setup = null)
    {
        var g = await GroupWithZonesAsync();
        setup?.Invoke(g);
        var (zones, nav) = OpenZones();
        await UiDriver.Until(() => zones.Shows("Colegio") || zones.Shows(Loc.Get("NoZones")), "zonas");
        zones.Toolbar("ZoneAlerts");
        await UiDriver.Until(() => nav.CurrentPage is ZoneAlertsPage, "avisos");
        var page = (ZoneAlertsPage)nav.CurrentPage;
        page.Appear();
        await UiDriver.Settle(100);
        return (page, g);
    }

    [Fact]
    public async Task Avisos_InterruptoresPorPersonaYZonaSeGuardan()
    {
        var colegio = Guid.Empty;
        _host.Server.Table("zone_subscriptions", _ => FakeSupabase.Json(FakeSupabase.Serialize(new[]
        {
            new { group_id = Guid.Empty, target_id = Ana, zone_id = colegio, on_enter = true, on_exit = false },
        })));
        var (page, g) = await OpenAlertsAsync();
        await UiDriver.Until(() => page.Shows("Ana"), "Ana");
        Assert.Equal(4, page.All<Switch>().Count());

        var enter = page.All<Switch>().First(s => SemanticProperties.GetDescription(s) == Loc.Format("AlertOnEnterA11y", "Ana", "Colegio"));
        enter.IsToggled = true;
        await UiDriver.Until(() => _host.Server.Requests.Any(r => r.Path == "/rest/v1/zone_subscriptions" && r.Method == "POST"), "guardar");
        Assert.Contains("\"on_enter\":true", _host.Server.Requests.Last(r => r.Path == "/rest/v1/zone_subscriptions" && r.Method == "POST").Body);

        // Si falla, el interruptor vuelve a como estaba.
        _host.Server.On("POST", "/rest/v1/zone_subscriptions", _ => World.Fail());
        var exit = page.All<Switch>().First(s => SemanticProperties.GetDescription(s) == Loc.Format("AlertOnExitA11y", "Ana", "Colegio"));
        exit.IsToggled = true;
        await page.AnswerKeyAsync("Ok");
        await UiDriver.Until(() => !exit.IsToggled, "volver atras");
        Assert.True(enter.IsToggled);
    }

    [Fact]
    public async Task Avisos_SinZonasOSinNadieMasLoDice()
    {
        var (page, _) = await OpenAlertsAsync(g => g.Zones.Clear());
        await UiDriver.Until(() => page.Shows(Loc.Get("NoZones")), "sin zonas");

        var g2 = await _world.GroupAsync("Solo");
        g2.Zones.Add(new World.ZoneData(Guid.NewGuid(), "Casa", 1, 1, 100, _host.Phone.Me));
        var alone = new ZoneAlertsPage(new Group(g2.Id, "Solo", true, false));
        alone.Host();
        alone.Appear();
        await UiDriver.Until(() => alone.Shows(Loc.Get("ZoneAlertsNobody")), "nadie");

        _host.Server.Table("zones", _ => World.Fail());
        var failing = new ZoneAlertsPage(new Group(g2.Id, "Solo", true, false));
        failing.Host();
        failing.Appear();
        Assert.Contains(Loc.Get("Err_network"), await failing.DialogTextAsync());
    }
}

internal static class ObjectCastExtensions
{
    public static T? As<T>(this object? value) where T : class => value as T;
}
