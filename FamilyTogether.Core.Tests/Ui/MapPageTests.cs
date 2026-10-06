using System.Reflection;
using System.Text.Json;
using FamilyTogether.Mobile;
using FamilyTogether.Mobile.Localization;
using FamilyTogether.Mobile.Pages;
using FamilyTogether.Mobile.Services;

namespace FamilyTogether.Core.Tests.Ui;

/// <summary>Inicio: el mapa del grupo, la lista de personas, los avisos y el refresco.</summary>
public class MapPageTests : IDisposable
{
    private readonly AppHost _host = new();
    private readonly FakeMap _fake = new();
    private readonly World _world;
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;

    public MapPageTests()
    {
        _world = new World(_host.Phone);
        App.PendingMapFocus = null;
        App.PendingMapFit = false;
        _host.Location.IsRunning = true;
        MapKit.GuideAndNewsSeen();
    }

    public void Dispose()
    {
        App.PendingMapFocus = null;
        App.PendingMapFit = false;
        _fake.Dispose();
        _host.Dispose();
    }

    private Guid Me => _host.Phone.Me;

    private static readonly Guid AnaId = Guid.NewGuid();
    private static readonly Guid BeaId = Guid.NewGuid();
    private static readonly Guid CarlosId = Guid.NewGuid();

    /// <summary>Casa: yo, Ana (con posicion), Bea (en pausa) y Carlos (sin posicion).</summary>
    private async Task<World.GroupData> CasaAsync()
    {
        var casa = await _world.GroupAsync("Casa", true,
            new World.MemberData(AnaId, "Ana", false),
            new World.MemberData(BeaId, "Bea", false, Paused: true),
            new World.MemberData(CarlosId, "carlos", false));
        casa.Positions.Add((Me, 40.0, -3.0, 10, _now.AddHours(-2), 80));
        casa.Positions.Add((AnaId, 41.5, 2.25, 10, _now.AddMinutes(-90), 70));
        casa.Positions.Add((AnaId, 41.4, 2.2, 10, _now.AddMinutes(-30), 55));
        casa.Positions.Add((BeaId, 42.0, 3.0, 10, _now.AddMinutes(-5), 40));
        return casa;
    }

    /// <summary>
    /// La pagina en una ventana con navegacion, con su mapa falso. Al meterla en la ventana ya
    /// aparece (OnAppearing), como en el movil.
    /// </summary>
    private (MapPage Page, FakeWebViewHandler Web, NavigationPage Nav) Open()
    {
        var page = new MapPage();
        var web = FakeMap.Attach(page);
        var nav = page.Host();
        return (page, web, nav);
    }

    private static Task UntilCall(FakeWebViewHandler web, string script, string? what = null) =>
        UiDriver.Until(() => web.Calls.Contains(script), what ?? script);

    /// <summary>Las marcas del ultimo setMembers.</summary>
    private static List<JsonElement> Markers(FakeWebViewHandler web)
    {
        var last = web.Calls.Last(c => c.StartsWith("setMembers(", StringComparison.Ordinal));
        var json = last["setMembers(".Length..^1];
        return [.. JsonDocument.Parse(json).RootElement.EnumerateArray()];
    }

    private static Grid People(MapPage page) => page.All<Grid>().Single(g => g.Children.OfType<BoxView>().Any(b => b.Opacity == 0.25));

    /// <summary>Las filas de la lista: el nombre y el detalle.</summary>
    private static List<(string Name, string Detail, Grid Row)> Rows(MapPage page) =>
        [.. People(page).All<Grid>().Where(g => g.MinimumHeightRequest == 56).Select(g =>
        {
            var labels = g.All<VerticalStackLayout>().Single().All<Label>().ToList();
            return (labels[0].Text, labels[1].Text, g);
        })];

    private static void Raise(string field)
    {
        var handler = (EventHandler?)typeof(App).GetField(field, BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null);
        handler?.Invoke(null, EventArgs.Empty);
    }

    private static TestTimer RefreshTimer() => MauiFakes.Dispatcher.Timers.Single(t => t.Interval == TimeSpan.FromSeconds(30));

    private static TestTimer EventTimer() => MauiFakes.Dispatcher.Timers.Single(t => t.Interval == TimeSpan.FromMilliseconds(400));

    private int GroupRequests => _host.Server.Requests.Count(r => r.Path == "/rest/v1/groups");

    [Fact]
    public async Task AlAbrirPintaATodosYSeCentraEnMi()
    {
        await CasaAsync();
        _host.Location.Position = (41.39, 2.17, 8, _now, false);

        var (page, web, _) = Open();
        await UntilCall(web, "center(41.39, 2.17, 15)");

        Assert.Equal(Loc.Get("MenuMap"), page.Title);
        var picker = page.All<Picker>().Single();
        Assert.Equal(["Casa"], ((IEnumerable<string>)picker.ItemsSource).ToList());
        Assert.False(picker.IsEnabled);

        // Las marcas: yo en la lectura de este movil (mas nueva que la del servidor), Ana en la
        // ultima suya; Bea (en pausa) y Carlos (sin posicion) no salen.
        var markers = Markers(web);
        Assert.Equal([Me.ToString("D"), AnaId.ToString("D")], markers.Select(m => m.GetProperty("id").GetString()));
        var me = markers[0];
        Assert.Equal(41.39, me.GetProperty("lat").GetDouble());
        Assert.Equal(2.17, me.GetProperty("lon").GetDouble());
        Assert.Equal("Y", me.GetProperty("initials").GetString());
        Assert.Equal(Avatars.ColorFor(Me), me.GetProperty("color").GetString());
        var ana = markers[1];
        Assert.Equal(41.4, ana.GetProperty("lat").GetDouble());
        Assert.Equal("Ana", ana.GetProperty("name").GetString());
        Assert.False(ana.GetProperty("stale").GetBoolean());
        Assert.Equal(JsonValueKind.Null, ana.GetProperty("avatar").ValueKind);

        // Abierto en mi posicion: no se encuadra al grupo.
        Assert.DoesNotContain("fitMembers()", web.Calls);

        // La lista: yo primero; despues por nombre sin mirar mayusculas.
        var rows = Rows(page);
        Assert.Equal([Loc.Format("MeSuffix", "Yo"), "Ana", "Bea", "carlos"], rows.Select(r => r.Name));
        Assert.Equal($"{Loc.Get("AgoNow")} · {Loc.Format("BatteryPercent", 80)}", rows[0].Detail);
        Assert.Equal($"{Loc.Format("AgoMinutes", 30)} · {Loc.Format("BatteryPercent", 55)}", rows[1].Detail);
        Assert.Equal(Loc.Get("PausedIndefinite"), rows[2].Detail);
        Assert.Equal(Loc.Get("NoPositionYet"), rows[3].Detail);
        Assert.True(page.Shows("Casa"));

        // Con todo en marcha no hay avisos.
        Assert.Empty(page.All<VerticalStackLayout>().First(v => v.Spacing == 8 && v.Padding == new Thickness(12, 0)).Children);
    }

    [Fact]
    public async Task SinLecturaPropiaSeEncuadraAlGrupo()
    {
        await CasaAsync();
        _host.Location.Position = null;

        var (page, web, _) = Open();
        await UntilCall(web, "fitMembers()");

        // Yo, en la posicion del servidor, con su hora y su bateria.
        var markers = Markers(web);
        Assert.Equal(40.0, markers[0].GetProperty("lat").GetDouble());
        Assert.True(markers[0].GetProperty("stale").GetBoolean());   // de hace dos horas
        Assert.StartsWith(Loc.Format("AgoHours", 2), Rows(page)[0].Detail);
        Assert.DoesNotContain(web.Calls, c => c.StartsWith("center(", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SinPermisoNoSeLeeLaPosicionYSaleElAviso()
    {
        await CasaAsync();
        _host.Location.Permission = LocationPermissionState.Denied;

        var (page, web, _) = Open();
        await UntilCall(web, "fitMembers()");

        Assert.True(page.Shows(Loc.Get("SharingDenied")));
        Assert.Equal(0, _host.Location.Starts);   // sin permiso no se arranca el servicio
    }

    [Fact]
    public async Task AvisoDeSoloConLaAppAbiertaYDeServicioParado()
    {
        await CasaAsync();
        _host.Location.Permission = LocationPermissionState.WhileInUse;

        var (page, web, _) = Open();
        await UntilCall(web, "center(41.39, 2.17, 15)");
        Assert.True(page.Shows(Loc.Get("SharingWhileInUse")));

        // Con el servicio parado: aviso y se arranca.
        _host.Location.Permission = LocationPermissionState.Always;
        _host.Location.IsRunning = false;
        page.Toolbar("Refresh");
        await UiDriver.Until(() => page.Shows(Loc.Get("SharingStopped")), "aviso de servicio parado");
        Assert.False(page.Shows(Loc.Get("SharingWhileInUse")));
        await UiDriver.Until(() => _host.Location.Starts == 1, "arrancar el servicio");
    }

    [Fact]
    public async Task ElAvisoDePermisoLlevaALaGuia()
    {
        await CasaAsync();
        _host.Location.Permission = LocationPermissionState.Denied;
        var page = new MapPage();
        var web = FakeMap.Attach(page);
        var shell = page.InShell("GuidePage", "GroupsPage");
        page.Appear();
        await UntilCall(web, "fitMembers()");

        page.ClickKey("OpenGuide");

        await UiDriver.Until(() => shell.Route()?.Contains("GuidePage") == true, "ir a la guia");
    }

    [Fact]
    public async Task SiNoSePuedeMirarElPermisoNoHayAvisoNiPosicionPropia()
    {
        await CasaAsync();
        _host.Replace<ILocationSharing>(new BrokenLocationSharing());

        var (page, web, _) = Open();
        await UntilCall(web, "fitMembers()");

        Assert.False(page.Shows(Loc.Get("OpenGuide")));
        Assert.Equal(40.0, Markers(web)[0].GetProperty("lat").GetDouble());
    }

    [Fact]
    public async Task SinServicioDeUbicacionTambienFunciona()
    {
        await CasaAsync();
        _host.Replace<ILocationSharing>(null!);

        var (page, web, _) = Open();
        await UntilCall(web, "fitMembers()");

        Assert.Equal(4, Rows(page).Count);
        Assert.False(page.Shows(Loc.Get("OpenGuide")));
    }

    [Fact]
    public async Task UnSosPendienteSeAvisa()
    {
        var casa = await CasaAsync();
        _host.Server.Rpc("create_sos", _ => World.Fail());
        await _host.Sos.SendAsync([casa.Id], (41, 2, 5, _now, false));
        Assert.True(_host.Sos.HasPending);

        var (page, web, _) = Open();
        await UntilCall(web, "center(41.39, 2.17, 15)");

        Assert.True(page.Shows(Loc.Get("SosPendingBanner")));
    }

    [Fact]
    public async Task SinGruposLaTarjetaLlevaAGrupos()
    {
        var page = new MapPage();
        var web = FakeMap.Attach(page);
        var shell = page.InShell("GuidePage", "GroupsPage");
        page.Appear();
        await UntilCall(web, "setMembers([])");
        await UntilCall(web, "center(41.39, 2.17, 15)");

        Assert.True(page.Shows(Loc.Get("NoGroupsTitle")));
        Assert.True(page.Shows(Loc.Get("NoGroupsBody")));
        Assert.False(page.Described("FindPerson").Visible());   // ni lupa ni SOS
        Assert.False(page.ButtonKey("SosButton").Visible());
        Assert.Empty(Rows(page));
        Assert.Equal(-1, page.All<Picker>().Single().SelectedIndex);
        Assert.False(page.Shows(Loc.Get("OpenGuide")));   // sin grupos no se avisa del permiso

        page.ClickKey("GoToGroups");
        await UiDriver.Until(() => shell.Route()?.Contains("GroupsPage") == true, "ir a grupos");
    }

    [Fact]
    public async Task GrupoSinClaveAvisaYDejaPedirla()
    {
        var casa = await CasaAsync();
        await _host.Phone.Keys.RemoveAsync(casa.Id);
        var shareId = Guid.NewGuid();
        _host.Server.Rpc("request_key_share", $"\"{shareId}\"");

        var (page, web, _) = Open();
        await UntilCall(web, "setMembers([])");
        await UiDriver.Until(() => page.Shows(Loc.Get("KeyMissingAction")), "boton de pedir la clave");

        Assert.Equal([Loc.Get("GroupNoKeyName")], ((IEnumerable<string>)page.All<Picker>().Single().ItemsSource).ToList());
        Assert.True(page.Shows(Loc.Get("KeyMissingBanner")));
        Assert.Empty(Rows(page));
        // Sin clave no se piden miembros ni posiciones.
        Assert.DoesNotContain(_host.Server.Requests, r => r.Path == "/rest/v1/last_positions");

        page.ClickKey("KeyMissingAction");
        var text = await page.DialogTextAsync();
        Assert.Contains(Loc.Get("KeyMissingRequested"), text);
        Assert.Contains(Loc.Get("KeyMissingTitle"), text);
        await page.AnswerKeyAsync("Ok");
        Assert.Contains(casa.Id.ToString("D"), _host.Server.RpcCall("request_key_share").Body);
    }

    [Fact]
    public async Task PedirLaClaveSinRedAvisaDelError()
    {
        var casa = await CasaAsync();
        await _host.Phone.Keys.RemoveAsync(casa.Id);
        _host.Server.Rpc("request_key_share", _ => World.Fail());

        var (page, web, _) = Open();
        await UiDriver.Until(() => page.Shows(Loc.Get("KeyMissingAction")), "boton de pedir la clave");

        page.ClickKey("KeyMissingAction");
        Assert.Contains(Loc.Get("Err_network"), await page.DialogTextAsync());
        await page.AnswerKeyAsync("Ok");
        Assert.Null(page.Dialog());
    }

    [Fact]
    public async Task CambiarDeGrupoCargaElOtroYLoEncuadra()
    {
        await CasaAsync();
        var abuelos = await _world.GroupAsync("Abuelos", false, new World.MemberData(CarlosId, "Abu", true));
        abuelos.Positions.Add((CarlosId, 39.5, -0.4, 10, _now.AddMinutes(-3), 90));
        AppState.SelectedGroup = _world.Groups[0].Id;

        var (page, web, _) = Open();
        await UntilCall(web, "center(41.39, 2.17, 15)");
        var picker = page.All<Picker>().Single();
        Assert.Equal(["Abuelos", "Casa"], ((IEnumerable<string>)picker.ItemsSource).ToList());
        Assert.Equal(1, picker.SelectedIndex);
        Assert.True(picker.IsEnabled);

        web.Clear();
        picker.SelectedIndex = 0;
        await UntilCall(web, "fitMembers()");

        Assert.Equal(abuelos.Id, AppState.SelectedGroup);
        Assert.Contains(CarlosId.ToString("D"), Markers(web).Select(m => m.GetProperty("id").GetString()));
        Assert.Equal(["Abu"], Rows(page).Skip(1).Select(r => r.Name));
    }

    [Fact]
    public async Task UnFalloAlCargarAvisaYEnElRefrescoPeriodicoNo()
    {
        await CasaAsync();
        var (page, web, _) = Open();
        await UntilCall(web, "center(41.39, 2.17, 15)");

        _host.Server.Table("group_members", _ => World.Fail());
        page.Toolbar("Refresh");
        Assert.Contains(Loc.Get("Err_network"), await page.DialogTextAsync());
        await page.AnswerKeyAsync("Ok");

        // En el refresco cada 30 s, en silencio y la lista sigue con lo ultimo.
        web.Clear();
        RefreshTimer().Fire();
        await UiDriver.Until(() => web.Calls.Any(c => c.StartsWith("setMembers(", StringComparison.Ordinal)), "repintar mi marca");
        Assert.Null(page.Dialog());
        Assert.Equal(4, Rows(page).Count);
    }

    [Fact]
    public async Task ElRefrescoPeriodicoActualizaSinMoverElMapa()
    {
        var casa = await CasaAsync();
        var (page, web, _) = Open();
        await UntilCall(web, "center(41.39, 2.17, 15)");
        var timer = RefreshTimer();
        Assert.True(timer.IsRunning);

        casa.Positions.Add((AnaId, 41.45, 2.3, 10, _now.AddMinutes(-1), 50));
        web.Clear();
        timer.Fire();
        await UiDriver.Until(() => web.Calls.Count(c => c.StartsWith("setMembers(", StringComparison.Ordinal)) == 2, "dos repintados");

        Assert.Equal(41.45, Markers(web)[1].GetProperty("lat").GetDouble());
        Assert.DoesNotContain(web.Calls, c => c.StartsWith("center(", StringComparison.Ordinal) || c == "fitMembers()");
        Assert.Contains(Rows(page), r => r.Detail.Contains(Loc.Format("BatteryPercent", 50)));
    }

    [Fact]
    public async Task AlVolverALaAppSeRecargaYAlIrseSeParaTodo()
    {
        await CasaAsync();
        var (page, web, _) = Open();
        await UntilCall(web, "center(41.39, 2.17, 15)");
        var before = GroupRequests;

        Raise("AppResumed");
        await UiDriver.Until(() => GroupRequests == before + 1, "recarga al volver");
        Raise("MapFocusRequested");
        await UiDriver.Until(() => GroupRequests == before + 2, "recarga por un aviso");

        page.ClickDescribed("FindPerson");
        Assert.True(People(page).IsVisible);
        page.Disappear();

        Assert.False(People(page).IsVisible);
        Assert.False(RefreshTimer().IsRunning);
        Assert.False(EventTimer().IsRunning);
        Raise("AppResumed");
        Raise("MapFocusRequested");
        await UiDriver.Settle();
        Assert.Equal(before + 2, GroupRequests);

        // Al volver a la pagina se reutilizan los temporizadores y no se vuelve a centrar en mi.
        web.Clear();
        page.Appear();
        await UiDriver.Until(() => web.Calls.Count(c => c.StartsWith("setMembers(", StringComparison.Ordinal)) == 2, "recarga al volver a la pagina");
        Assert.Single(MauiFakes.Dispatcher.Timers, t => t.Interval == TimeSpan.FromSeconds(30));
        Assert.True(RefreshTimer().IsRunning);
        Assert.DoesNotContain(web.Calls, c => c.StartsWith("center(", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EnSegundoPlanoNoRefrescaNiPreguntaAlMapa()
    {
        // MAUI no llama a OnDisappearing al pulsar Inicio y el proceso sigue vivo por el servicio:
        // el mapa tiene que pararse solo (bateria, SC-005).
        await CasaAsync();
        var (_, web, _) = Open();
        await UntilCall(web, "center(41.39, 2.17, 15)");
        Assert.True(RefreshTimer().IsRunning);

        Raise("AppStopped");
        Assert.False(RefreshTimer().IsRunning);
        Assert.False(EventTimer().IsRunning);

        var before = GroupRequests;
        Raise("AppResumed");
        await UiDriver.Until(() => GroupRequests == before + 1, "recarga al volver");
        Assert.True(RefreshTimer().IsRunning);
        Assert.True(EventTimer().IsRunning);
    }

    [Fact]
    public async Task AbiertoDesdeUnAvisoVaAlSitioDelEvento()
    {
        await CasaAsync();
        App.PendingMapFocus = (40.5, -3.75);
        App.PendingMapFit = true;

        var (_, web, _) = Open();
        await UntilCall(web, "center(40.5, -3.75, 16)");

        Assert.Null(App.PendingMapFocus);
        Assert.False(App.PendingMapFit);
        Assert.DoesNotContain("center(41.39, 2.17, 15)", web.Calls);
        Assert.DoesNotContain("fitMembers()", web.Calls);
    }

    [Fact]
    public async Task AbiertoDesdeUnAvisoSinSitioEncuadraATodos()
    {
        await CasaAsync();
        App.PendingMapFit = true;

        var (_, web, _) = Open();
        await UntilCall(web, "fitMembers()");
        await UiDriver.Settle();

        Assert.False(App.PendingMapFit);
        Assert.DoesNotContain(web.Calls, c => c.StartsWith("center(", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LaListaDePersonasSeAbreCentraYSeCierra()
    {
        await CasaAsync();
        var (page, web, _) = Open();
        await UntilCall(web, "center(41.39, 2.17, 15)");
        var people = People(page);
        Assert.False(people.IsVisible);
        Assert.False(page.HandleBack());   // sin lista, atras no es cosa del mapa

        // La lupa la abre; atras la cierra.
        page.ClickDescribed("FindPerson");
        Assert.True(people.IsVisible);
        Assert.True(page.HandleBack());
        Assert.False(people.IsVisible);

        // Tocar a alguien con posicion cierra la lista y centra en esa persona.
        page.ClickDescribed("FindPerson");
        var rows = Rows(page);
        var ana = rows.Single(r => r.Name == "Ana").Row;
        Assert.Equal($"Ana, {rows[1].Detail}", SemanticProperties.GetDescription(ana));
        ana.Tap();
        Assert.False(people.IsVisible);
        await UntilCall(web, $"focusMember('{AnaId:D}')");

        // Sin posicion no se puede tocar.
        Assert.Empty(rows.Single(r => r.Name == "carlos").Row.GestureRecognizers);

        // Tocar fuera, o la cruz, cierran.
        page.ClickDescribed("FindPerson");
        people.Children.OfType<BoxView>().Single().Tap();
        Assert.False(people.IsVisible);
        page.ClickDescribed("FindPerson");
        page.ClickDescribed("Close");
        Assert.False(people.IsVisible);

        // «Ver a todos» de la lista: la cierra y encuadra; el boton del mapa tambien encuadra.
        page.ClickDescribed("FindPerson");
        web.Clear();
        page.ClickDescribed("ShowEveryone");
        Assert.False(people.IsVisible);
        await UntilCall(web, "fitMembers()");
        web.Clear();
        page.ClickDescribed("ShowEveryone");
        await UntilCall(web, "fitMembers()");
    }

    [Fact]
    public async Task TocarUnaMarcaEnElMapaCentraEnEsaPersona()
    {
        await CasaAsync();
        var (_, web, _) = Open();
        await UntilCall(web, "center(41.39, 2.17, 15)");
        var answer = web.Answer;
        web.Answer = s => s == "takeEvents()" ? $"member|{BeaId:D}" : answer(s);

        EventTimer().Fire();

        await UntilCall(web, $"focusMember('{BeaId:D}')");
    }

    [Fact]
    public async Task ElBotonSosAbreLaPantallaDeSos()
    {
        await CasaAsync();
        var (page, web, nav) = Open();
        await UntilCall(web, "center(41.39, 2.17, 15)");
        Assert.Equal(Loc.Get("SosDescription"), SemanticProperties.GetDescription(page.ButtonKey("SosButton")));

        page.ClickKey("SosButton");

        await UiDriver.Until(() => nav.CurrentPage is SosPage, "pantalla de SOS");
    }

    [Fact]
    public async Task LaPrimeraVezVaALaGuiaSinCargarNada()
    {
        AppState.GuideDone = false;
        await CasaAsync();
        var page = new MapPage();
        var web = FakeMap.Attach(page);
        var shell = page.InShell("GuidePage", "GroupsPage");

        page.Appear();

        await UiDriver.Until(() => shell.Route()?.Contains("GuidePage") == true, "ir a la guia");
        await UiDriver.Settle();
        Assert.Empty(web.All);
        Assert.Empty(_host.Server.Requests);
    }

    [Fact]
    public async Task ConUnaVersionNuevaEnsenaLasNovedadesUnaVez()
    {
        AppState.LastVersionSeen = "2026.09.01.00";
        await CasaAsync();

        var (page, web, nav) = Open();

        await UiDriver.Until(() => nav.CurrentPage is WhatsNewPage, "novedades");
        Assert.False(AppState.HasUnseenVersion);
        Assert.Empty(web.All);
        Assert.Empty(_host.Server.Requests.Select(r => r.Method + " " + r.Path));
    }

    [Fact]
    public async Task MientrasCargaNoSeCargaOtraVez()
    {
        await CasaAsync();
        var page = new MapPage();
        var web = FakeMap.Attach(page);
        web.Hold = s => s.StartsWith("setMembers(", StringComparison.Ordinal);
        page.Host();
        await UiDriver.Until(() => web.HeldCount == 1, "primer pintado en curso");
        var spinner = page.All<ActivityIndicator>().Single();
        Assert.True(spinner.IsRunning);
        var before = GroupRequests;

        page.Toolbar("Refresh");
        RefreshTimer().Fire();
        await UiDriver.Settle();
        Assert.Equal(before, GroupRequests);

        // Lo pedido mientras tanto se junta en una sola carga al acabar.
        web.Release();
        await UntilCall(web, "center(41.39, 2.17, 15)");
        Assert.Equal(before + 1, GroupRequests);
        Assert.False(spinner.IsRunning);
        Assert.False(spinner.IsVisible);
    }

    [Fact]
    public async Task ElegirOtroGrupoMientrasCargaNoSePierde()
    {
        await CasaAsync();
        var abuelos = await _world.GroupAsync("Abuelos", false, new World.MemberData(CarlosId, "Abu", true));
        abuelos.Positions.Add((CarlosId, 39.5, -0.4, 10, _now.AddMinutes(-3), 90));
        AppState.SelectedGroup = _world.Groups[0].Id;
        var page = new MapPage();
        var web = FakeMap.Attach(page);
        web.Hold = s => s.StartsWith("setMembers(", StringComparison.Ordinal);
        page.Host();
        await UiDriver.Until(() => web.HeldCount == 1, "primer pintado en curso");

        // Antes, este cambio se perdia: el mapa seguia con Casa hasta el siguiente refresco.
        page.All<Picker>().Single().SelectedIndex = 0;
        web.Release();

        await UiDriver.Until(() => Rows(page).Any(r => r.Name == "Abu"), "lista de Abuelos");
        await UntilCall(web, "fitMembers()");
        Assert.Equal(abuelos.Id, AppState.SelectedGroup);
        Assert.Contains(CarlosId.ToString("D"), Markers(web).Select(m => m.GetProperty("id").GetString()));
    }

    [Fact]
    public async Task MiPosicionDelServidorSiEsMasNuevaQueLaDelMovil()
    {
        var casa = await CasaAsync();
        casa.Positions.Add((Me, 40.2, -3.1, 10, _now.AddMinutes(-1), 60));
        _host.Location.Position = (41.39, 2.17, 8, _now.AddMinutes(-20), false);

        var (page, web, _) = Open();
        await UntilCall(web, "center(41.39, 2.17, 15)");

        Assert.Equal(40.2, Markers(web)[0].GetProperty("lat").GetDouble());
        Assert.Equal($"{Loc.Format("AgoMinutes", 1)} · {Loc.Format("BatteryPercent", 60)}", Rows(page)[0].Detail);
    }

    [Fact]
    public async Task SinPosicionEnElServidorMiFilaUsaLaDelMovil()
    {
        var casa = await _world.GroupAsync("Solo yo");
        _host.Location.Position = (41.39, 2.17, 8, _now.AddMinutes(-3), false);

        var (page, web, _) = Open();
        await UntilCall(web, "center(41.39, 2.17, 15)");

        var row = Assert.Single(Rows(page));
        Assert.Equal($"{Loc.Format("AgoMinutes", 3)} · {Loc.Format("BatteryPercent", 0)}", row.Detail);
        Assert.Single(Markers(web));
        Assert.Equal(casa.Id, AppState.SelectedGroup);
    }

    [Fact]
    public async Task EnPausaHastaUnaHoraLoDice()
    {
        var until = DateTimeOffset.Now.AddHours(1);
        await _world.GroupAsync("Casa", true, new World.MemberData(AnaId, "Ana", false, Paused: true, Until: until));

        var (page, web, _) = Open();
        await UntilCall(web, "center(41.39, 2.17, 15)");

        Assert.Equal(Loc.Format("PausedUntil", TimeTexts.Until(until)), Rows(page)[1].Detail);
    }

    [Fact]
    public async Task EnIngles()
    {
        using var host = new AppHost(Loc.English);
        MapKit.GuideAndNewsSeen();
        var world = new World(host.Phone);
        await world.GroupAsync("Home");
        host.Location.IsRunning = false;

        var page = new MapPage();
        var web = FakeMap.Attach(page);
        page.Host();
        await UntilCall(web, "center(41.39, 2.17, 15)");

        Assert.Equal("Map", page.Title);
        Assert.True(page.Shows(Loc.Get("SharingStopped")));
        Assert.Contains("(you)", Rows(page)[0].Name);
    }
}
