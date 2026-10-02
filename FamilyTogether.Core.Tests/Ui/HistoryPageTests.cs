using System.Net;
using System.Reflection;
using System.Text.Json;
using FamilyTogether.Core;
using FamilyTogether.Mobile.Localization;
using FamilyTogether.Mobile.Pages;
using FamilyTogether.Mobile.Services;

namespace FamilyTogether.Core.Tests.Ui;

/// <summary>Historial: el recorrido de 24 h o de un dia, su ajuste a calles y borrar mi historial.</summary>
public class HistoryPageTests : IDisposable
{
    private readonly AppHost _host = new();
    private readonly FakeMap _fake = new();
    private readonly World _world;
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;

    private static readonly Guid AnaId = Guid.NewGuid();

    /// <summary>Una calle recta (lon 2.17) de lat 41.38 a 41.42, la de las posiciones de las pruebas.</summary>
    private const string Street = """
        {"version":0.6,"elements":[
          {"type":"way","id":7,"bounds":{},"nodes":[1,2,3,4,5],"geometry":[
            {"lat":41.380,"lon":2.17},{"lat":41.390,"lon":2.17},{"lat":41.400,"lon":2.17},{"lat":41.410,"lon":2.17},{"lat":41.420,"lon":2.17}]}
        ]}
        """;

    public HistoryPageTests()
    {
        GroupSelectorTests.ForgetLastGroups();
        ForgetLastMembers();
        _world = new World(_host.Phone);
    }

    public void Dispose()
    {
        HistoryPage.SnapLimit = TimeSpan.FromSeconds(75);
        GroupSelectorTests.ForgetLastGroups();
        ForgetLastMembers();
        _fake.Dispose();
        _host.Dispose();
    }

    private static void ForgetLastMembers() =>
        ((System.Collections.IDictionary)typeof(HistoryPage).GetField("s_lastMembers", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!).Clear();

    private Guid Me => _host.Phone.Me;

    /// <summary>Casa: yo y Ana. Mis cuatro ultimas posiciones van por la calle, de 2 en 2 minutos.</summary>
    private async Task<World.GroupData> CasaAsync(bool withTrack = true)
    {
        var casa = await _world.GroupAsync("Casa", true, new World.MemberData(AnaId, "Ana", false));
        if (withTrack)
        {
            for (var i = 0; i < 4; i++)
                casa.Positions.Add((Me, 41.401 + i * 0.001, 2.17, 8, _now.AddMinutes(-20 + i * 2), 70));
            casa.Positions.Add((Me, 41.5, 2.5, 8, _now.AddHours(-50), 70));   // fuera de las 24 h (y de ayer)
        }
        return casa;
    }

    private (HistoryPage Page, FakeWebViewHandler Web, NavigationPage Nav) Open()
    {
        var page = new HistoryPage();
        var web = FakeMap.Attach(page);
        var nav = page.Host();   // al entrar en la ventana aparece (OnAppearing)
        return (page, web, nav);
    }

    private static T Field<T>(HistoryPage page, string name) =>
        (T)typeof(HistoryPage).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!;

    private static string Summary(HistoryPage page) => Field<Label>(page, "_summary").Text;

    private static Label SnapStatus(HistoryPage page) => Field<Label>(page, "_snapStatus");

    private static Picker PickerTitled(Page page, string key) => page.All<Picker>().Single(p => p.Title == Loc.Get(key));

    private static List<string> Items(Picker picker) => picker.ItemsSource is null ? [] : [.. picker.ItemsSource.Cast<string>()];

    /// <summary>Espera al resumen con la carga ya terminada (el recorrido ya pedido al mapa).</summary>
    private static async Task UntilSummary(HistoryPage page, string text)
    {
        try
        {
            await UiDriver.Until(() => Summary(page) == text && !Field<bool>(page, "_loading"), $"resumen «{text}»");
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException($"{ex.Message} (hay «{Summary(page)}»)");
        }
    }

    private static Task UntilSnap(HistoryPage page, string key) =>
        UiDriver.Until(() => SnapStatus(page).Text == Loc.Get(key) && SnapStatus(page).IsVisible, $"{key} (hay «{SnapStatus(page).Text}»)");

    /// <summary>Las coordenadas del ultimo setTrack.</summary>
    private static List<double[]> LastTrack(FakeWebViewHandler web)
    {
        var call = web.Calls.Last(c => c.StartsWith("setTrack(", StringComparison.Ordinal));
        var json = call["setTrack(".Length..call.IndexOf("]],", StringComparison.Ordinal)] + "]]";
        return JsonSerializer.Deserialize<List<double[]>>(json)!;
    }

    private static int Count(FakeWebViewHandler web, string prefix) => web.Calls.Count(c => c.StartsWith(prefix, StringComparison.Ordinal));

    private string TrackSummary(int count, DateTimeOffset first, DateTimeOffset last) =>
        Loc.Format("TrackSummary", count, TimeTexts.Clock(first), TimeTexts.Clock(last));

    [Fact]
    public async Task MiRecorridoDeLasUltimas24HorasSeDibujaYSeIntentaAjustar()
    {
        await CasaAsync();

        var (page, web, _) = Open();
        await UntilSummary(page, TrackSummary(4, _now.AddMinutes(-20), _now.AddMinutes(-14)));

        Assert.Equal(Loc.Get("MenuHistory"), page.Title);
        var member = PickerTitled(page, "ChooseMember");
        Assert.Equal([Loc.Format("MeSuffix", "Yo"), "Ana"], Items(member));
        Assert.Equal(0, member.SelectedIndex);
        Assert.Equal([Loc.Get("HistoryLast24h"), Loc.Get("HistoryOneDay")], Items(PickerTitled(page, "HistoryPeriod")));
        Assert.False(page.All<DatePicker>().Single().Parent is VisualElement { IsVisible: true });
        Assert.True(page.Shows(Loc.Format("HistoryRetention", 30)));
        Assert.True(page.Shows(Loc.Get("TrackStart")) && page.Shows(Loc.Get("TrackEnd")) && page.Shows(Loc.Get("TrackStop")));

        // La linea, en orden y sin la posicion de hace 30 h; con sus colores; sin paradas.
        Assert.Equal([41.401, 41.402, 41.403, 41.404], LastTrack(web).Select(p => Math.Round(p[0], 6)));
        Assert.Contains("'#27AE60', '#BA1A1A')", web.Calls.First(c => c.StartsWith("setTrack(", StringComparison.Ordinal)));
        Assert.Contains("addStops([])", web.Calls);

        // Overpass no contesta nada que sirva: se queda recto y se dice, sin dialogo.
        await UntilSnap(page, "SnapUnavailable");
        Assert.Null(page.Dialog());
        Assert.NotEmpty(_host.Overpass.Requests);
        var asked = Uri.UnescapeDataString(_host.Overpass.Requests[0].Body.Replace('+', ' '));
        Assert.DoesNotContain("41.401", asked);   // a Overpass solo va la tesela, nunca el recorrido
        Assert.Equal(1, Count(web, "setTrack("));
    }

    [Fact]
    public async Task ConLaRedDeCallesSeAjustaYSeRedibuja()
    {
        await CasaAsync();
        _host.Overpass.On("POST", "/api/interpreter", Street);

        var (page, web, _) = Open();
        await UntilSnap(page, "SnapDone");

        Assert.Equal(2, Count(web, "setTrack("));
        Assert.Equal(2, Count(web, "addStops("));
        Assert.All(LastTrack(web), p => Assert.Equal(2.17, p[1], 6));
    }

    [Fact]
    public async Task SiFaltaParteDelMapaLoDice()
    {
        var casa = await CasaAsync();
        casa.Positions.Add((Me, 41.399, 2.17, 8, _now.AddMinutes(-22), 70));   // en la tesela de abajo
        _host.Overpass.On("POST", "/api/interpreter", r => r.Body.Contains("41.42", StringComparison.Ordinal)
            ? FakeSupabase.Json(Street)
            : FakeSupabase.Status(HttpStatusCode.InternalServerError));

        var (page, web, _) = Open();
        await UiDriver.Until(() => SnapStatus(page).Text == Loc.Format("SnapPartial", 1, 2), "ajuste parcial");

        Assert.Equal(2, Count(web, "setTrack("));
    }

    [Fact]
    public async Task ConElAjusteApagadoNoSePideNadaAOverpass()
    {
        await CasaAsync();
        AppState.SnapTracks = false;

        var (page, web, _) = Open();
        await UiDriver.Until(() => Count(web, "addStops(") == 1, "dibujo");
        await UiDriver.Settle();

        Assert.False(SnapStatus(page).IsVisible);
        Assert.Empty(_host.Overpass.Requests);
    }

    [Fact]
    public async Task UnaSolaPosicionNoSeAjusta()
    {
        var casa = await CasaAsync(withTrack: false);
        casa.Positions.Add((Me, 41.401, 2.17, 8, _now.AddMinutes(-5), 70));

        var (page, web, _) = Open();
        await UntilSummary(page, TrackSummary(1, _now.AddMinutes(-5), _now.AddMinutes(-5)));
        await UiDriver.Until(() => Count(web, "addStops(") == 1, "dibujo");

        Assert.False(SnapStatus(page).IsVisible);
        Assert.Empty(_host.Overpass.Requests);
    }

    [Fact]
    public async Task LasParadasLargasSePintanComoUnPunto()
    {
        var casa = await CasaAsync(withTrack: false);
        AppState.SnapTracks = false;
        var start = _now.AddMinutes(-60);
        for (var i = 0; i < 4; i++)
            casa.Positions.Add((Me, 41.401, 2.17, 8, start.AddMinutes(i * 5), 70));   // 15 min quieto
        casa.Positions.Add((Me, 41.405, 2.17, 8, start.AddMinutes(20), 70));
        casa.Positions.Add((Me, 41.409, 2.17, 8, start.AddMinutes(25), 70));

        var (page, web, _) = Open();
        await UiDriver.Until(() => Count(web, "addStops(") == 1, "dibujo");

        var stops = web.Calls.Single(c => c.StartsWith("addStops(", StringComparison.Ordinal));
        Assert.Contains(Loc.Format("StopLabel", TimeTexts.Clock(start), TimeTexts.Clock(start.AddMinutes(15))), stops);
        Assert.DoesNotContain("addStops([])", web.Calls);
    }

    [Fact]
    public async Task LoGuardadoEnElMovilSeJuntaConLoDelServidor()
    {
        var casa = await CasaAsync();
        AppState.SnapTracks = false;
        await _host.Track.AddAsync(41.405, 2.17, 5, _now.AddMinutes(-10), [casa.Id]);
        await _host.Track.AddAsync(41.401, 2.17, 5, _now.AddMinutes(-20), [casa.Id]);   // la misma que la del servidor
        await _host.Track.AddAsync(41.3, 2.1, 5, _now.AddMinutes(-9), [Guid.NewGuid()]);   // de otro grupo

        var (page, web, _) = Open();

        await UntilSummary(page, TrackSummary(5, _now.AddMinutes(-20), _now.AddMinutes(-10)));
        Assert.Equal(41.405, LastTrack(web)[^1][0], 6);
    }

    [Fact]
    public async Task ElRecorridoDeOtroMiembroNoLlevaLoDelMovil()
    {
        var casa = await CasaAsync();
        AppState.SnapTracks = false;
        await _host.Track.AddAsync(41.405, 2.17, 5, _now.AddMinutes(-10), [casa.Id]);
        var (page, web, _) = Open();
        await UntilSummary(page, TrackSummary(5, _now.AddMinutes(-20), _now.AddMinutes(-10)));

        // Ana no tiene nada: se dice y se borra la linea.
        PickerTitled(page, "ChooseMember").SelectedIndex = 1;
        await UntilSummary(page, Loc.Get("NoPositionsLast24h"));
        Assert.Contains("clearTrack()", web.Calls);

        // Con posiciones: solo las suyas.
        casa.Positions.Add((AnaId, 41.39, 2.16, 8, _now.AddMinutes(-50), 30));
        casa.Positions.Add((AnaId, 41.391, 2.16, 8, _now.AddMinutes(-45), 30));
        PickerTitled(page, "ChooseMember").SelectedIndex = 0;
        await UntilSummary(page, TrackSummary(5, _now.AddMinutes(-20), _now.AddMinutes(-10)));
        PickerTitled(page, "ChooseMember").SelectedIndex = 1;
        await UntilSummary(page, TrackSummary(2, _now.AddMinutes(-50), _now.AddMinutes(-45)));
        Assert.Equal(41.391, LastTrack(web)[^1][0], 6);
    }

    [Fact]
    public async Task UnDiaConcreto()
    {
        var casa = await CasaAsync();
        AppState.SnapTracks = false;
        // Anteayer: fuera de las ultimas 24 h aunque la prueba corra pasada la medianoche.
        var yesterday = new DateTimeOffset(DateTime.Today.AddDays(-2).AddHours(10));
        casa.Positions.Add((Me, 41.39, 2.17, 8, yesterday, 70));
        casa.Positions.Add((Me, 41.392, 2.17, 8, yesterday.AddMinutes(30), 70));
        var (page, web, _) = Open();
        await UntilSummary(page, TrackSummary(4, _now.AddMinutes(-20), _now.AddMinutes(-14)));
        var day = page.All<DatePicker>().Single();
        var dayBox = (VisualElement)day.Parent;
        Assert.Equal(DateTime.Today, day.Date);
        Assert.Equal(DateTime.Today, day.MaximumDate);
        Assert.Equal(DateTime.Today.AddDays(-29), day.MinimumDate);

        // «Un dia» saca la fecha; hoy no tiene las de ayer.
        PickerTitled(page, "HistoryPeriod").SelectedIndex = 1;
        Assert.True(dayBox.IsVisible);
        var today = web.Calls.Count;
        await UiDriver.Until(() => web.Calls.Count > today, "redibujo de hoy");

        // Anteayer: con la hora sola.
        day.Date = DateTime.Today.AddDays(-2);
        await UntilSummary(page, Loc.Format("TrackSummary", 2, yesterday.ToString("HH:mm", Loc.Culture), yesterday.AddMinutes(30).ToString("HH:mm", Loc.Culture)));

        // Un dia sin nada.
        day.Date = DateTime.Today.AddDays(-5);
        await UntilSummary(page, Loc.Get("NoPositionsThatDay"));

        // Vuelta a las ultimas 24 h.
        PickerTitled(page, "HistoryPeriod").SelectedIndex = 0;
        Assert.False(dayBox.IsVisible);
        await UntilSummary(page, TrackSummary(4, _now.AddMinutes(-20), _now.AddMinutes(-14)));
    }

    [Fact]
    public async Task SinServidorElRecorridoDeOtroAvisa()
    {
        await CasaAsync();
        var (page, _, _) = Open();
        await UntilSummary(page, TrackSummary(4, _now.AddMinutes(-20), _now.AddMinutes(-14)));

        _host.Server.Table("positions", _ => World.Fail());
        PickerTitled(page, "ChooseMember").SelectedIndex = 1;

        Assert.Contains(Loc.Get("Err_network"), await page.DialogTextAsync());
        await page.AnswerKeyAsync("Ok");
        await UntilSummary(page, string.Empty);
    }

    [Fact]
    public async Task SinServidorElMioSaleDeLoGuardadoEnElMovil()
    {
        var casa = await CasaAsync();
        AppState.SnapTracks = false;
        _host.Server.Table("positions", _ => World.Fail());
        await _host.Track.AddAsync(41.401, 2.17, 5, _now.AddMinutes(-12), [casa.Id]);
        await _host.Track.AddAsync(41.402, 2.17, 5, _now.AddMinutes(-11), [casa.Id]);

        var (page, web, _) = Open();

        await UntilSummary(page, TrackSummary(2, _now.AddMinutes(-12), _now.AddMinutes(-11)) + Environment.NewLine + Loc.Get("HistoryOfflineLocal"));
        Assert.Null(page.Dialog());
        Assert.Equal(2, LastTrack(web).Count);
    }

    [Fact]
    public async Task SinServidorNiNadaEnElMovilDiceQueNoHayConexion()
    {
        await CasaAsync();
        _host.Server.Table("positions", _ => FakeSupabase.Status(HttpStatusCode.BadGateway, "caido"));

        var (page, web, _) = Open();

        await UntilSummary(page, Loc.Get("Err_network"));
        Assert.Null(page.Dialog());
        Assert.Empty(web.Calls);
    }

    [Fact]
    public async Task UnErrorDelServidorEnElMioQueNoEsDeRedSeAvisaYSigueLoDelMovil()
    {
        var casa = await CasaAsync();
        AppState.SnapTracks = false;
        _host.Server.Table("positions", _ => World.Fail("not_member"));
        await _host.Track.AddAsync(41.401, 2.17, 5, _now.AddMinutes(-12), [casa.Id]);

        var (page, _, _) = Open();

        Assert.Contains(Loc.Get("Err_not_member"), await page.DialogTextAsync());
        await page.AnswerKeyAsync("Ok");
        await UntilSummary(page, TrackSummary(1, _now.AddMinutes(-12), _now.AddMinutes(-12)));
    }

    [Fact]
    public async Task SinGruposConClaveLoDice()
    {
        var g = await _world.GroupAsync("Sin clave");
        await _host.Phone.Keys.RemoveAsync(g.Id);

        var (page, web, _) = Open();

        await UntilSummary(page, Loc.Get("NoGroupsWithKey"));
        Assert.Empty(Items(PickerTitled(page, "ChooseMember")));

        // Cambiar el periodo sin nadie elegido no hace nada.
        PickerTitled(page, "HistoryPeriod").SelectedIndex = 1;
        await UiDriver.Settle();
        Assert.Equal(Loc.Get("NoGroupsWithKey"), Summary(page));
        Assert.Empty(web.Calls);
    }

    [Fact]
    public async Task CambiarDePersonaMientrasCargaNoSePierde()
    {
        var casa = await CasaAsync();
        AppState.SnapTracks = false;
        casa.Positions.Add((AnaId, 41.39, 2.16, 8, _now.AddMinutes(-50), 30));
        casa.Positions.Add((AnaId, 41.391, 2.16, 8, _now.AddMinutes(-45), 30));
        var page = new HistoryPage();
        var web = FakeMap.Attach(page);
        web.Hold = s => s.StartsWith("setTrack(", StringComparison.Ordinal);
        page.Host();
        await UiDriver.Until(() => web.HeldCount == 1, "mi recorrido a medio dibujar");

        // Antes este cambio se perdia: la lista decia Ana y el mapa seguia con mi recorrido.
        PickerTitled(page, "ChooseMember").SelectedIndex = 1;
        PickerTitled(page, "HistoryPeriod").SelectedIndex = 0;
        web.Release();

        await UntilSummary(page, TrackSummary(2, _now.AddMinutes(-50), _now.AddMinutes(-45)));
        Assert.Equal(41.391, LastTrack(web)[^1][0], 6);
        Assert.Equal(2, Count(web, "setTrack("));   // la mia y la de Ana: las dos peticiones se juntan
    }

    [Fact]
    public async Task SiLoGuardadoEnElMovilNoSePuedeLeerSigueConElServidor()
    {
        await CasaAsync();
        AppState.SnapTracks = false;
        _host.Replace(new LocalTrack(_host.Folder));   // una carpeta, no una base de datos

        var (page, web, _) = Open();

        await UntilSummary(page, TrackSummary(4, _now.AddMinutes(-20), _now.AddMinutes(-14)));
        Assert.Null(page.Dialog());
        Assert.Equal(4, LastTrack(web).Count);
    }

    [Fact]
    public async Task SiLaZonaNoTieneCallesSeQuedaRectoSinDecirNada()
    {
        await CasaAsync();
        _host.Overpass.On("POST", "/api/interpreter", """{"version":0.6,"elements":[]}""");

        var (page, web, _) = Open();
        await UiDriver.Until(() => _host.Overpass.Requests.Count > 0, "pregunta a Overpass");
        await UiDriver.Until(() => !SnapStatus(page).IsVisible, "sin linea de estado");

        Assert.Equal(string.Empty, SnapStatus(page).Text);
        Assert.Equal(1, Count(web, "setTrack("));
    }

    [Fact]
    public async Task SiNoSePuedenCargarLosMiembrosLaPrimeraVezAvisa()
    {
        await CasaAsync();
        _host.Server.Table("group_members", r => r.Query.Contains("group_id=eq.", StringComparison.Ordinal)
            ? World.Fail()
            : FakeSupabase.Json(FakeSupabase.Serialize(new[] { new { group_id = _world.Groups[0].Id, user_id = Me, role = "admin", paused = false } })));

        var (page, web, _) = Open();

        Assert.Contains(Loc.Get("Err_network"), await page.DialogTextAsync());
        await page.AnswerKeyAsync("Ok");
        Assert.Empty(Items(PickerTitled(page, "ChooseMember")));
        Assert.Empty(web.Calls);
    }

    [Fact]
    public async Task SinConexionConTodoCargadoAntesSigueConLoGuardado()
    {
        var casa = await CasaAsync();
        AppState.SnapTracks = false;
        var (first, _, _) = Open();
        await UntilSummary(first, TrackSummary(4, _now.AddMinutes(-20), _now.AddMinutes(-14)));
        await _host.Track.AddAsync(41.401, 2.17, 5, _now.AddMinutes(-3), [casa.Id]);

        // Se va la red del todo: grupos, miembros y posiciones.
        _host.Server.Table("group_members", _ => World.Fail());
        _host.Server.Table("positions", _ => World.Fail());
        var (page, _, _) = Open();

        await UntilSummary(page, TrackSummary(1, _now.AddMinutes(-3), _now.AddMinutes(-3)) + Environment.NewLine + Loc.Get("HistoryOfflineLocal"));
        Assert.Equal([Loc.Format("MeSuffix", "Yo"), "Ana"], Items(PickerTitled(page, "ChooseMember")));
        Assert.Null(page.Dialog());
    }

    [Fact]
    public async Task ConLosMiembrosYaCargadosUnErrorQueNoEsDeRedSeAvisa()
    {
        await CasaAsync();
        var (first, _, _) = Open();
        await UntilSummary(first, TrackSummary(4, _now.AddMinutes(-20), _now.AddMinutes(-14)));

        _host.Server.Table("group_members", r => r.Query.Contains("group_id=eq.", StringComparison.Ordinal)
            ? World.Fail("not_member")
            : FakeSupabase.Json(FakeSupabase.Serialize(new[] { new { group_id = _world.Groups[0].Id, user_id = Me, role = "admin", paused = false } })));
        var (page, web, _) = Open();

        Assert.Contains(Loc.Get("Err_not_member"), await page.DialogTextAsync());
        await page.AnswerKeyAsync("Ok");
        Assert.Empty(web.Calls);
    }

    [Fact]
    public async Task CambiarDeGrupoMantieneALaPersonaSiEstaEnElOtro()
    {
        var casa = await CasaAsync();
        AppState.SnapTracks = false;
        var abuelos = await _world.GroupAsync("Abuelos", false, new World.MemberData(AnaId, "Ana", true));
        abuelos.Positions.Add((AnaId, 39.5, -0.4, 8, _now.AddMinutes(-7), 50));
        abuelos.Positions.Add((AnaId, 39.501, -0.4, 8, _now.AddMinutes(-6), 50));
        AppState.SelectedGroup = casa.Id;
        var (page, _, _) = Open();
        await UntilSummary(page, TrackSummary(4, _now.AddMinutes(-20), _now.AddMinutes(-14)));
        var member = PickerTitled(page, "ChooseMember");
        member.SelectedIndex = 1;
        await UntilSummary(page, Loc.Get("NoPositionsLast24h"));

        PickerTitled(page, "ChooseGroup").SelectedIndex = 0;   // Abuelos

        await UntilSummary(page, TrackSummary(2, _now.AddMinutes(-7), _now.AddMinutes(-6)));
        Assert.Equal(1, member.SelectedIndex);
        Assert.Equal(abuelos.Id, AppState.SelectedGroup);
    }

    // -----------------------------------------------------------------------
    // Ajuste a calles: tope, cancelacion y fallos
    // -----------------------------------------------------------------------

    /// <summary>Overpass que no contesta hasta que se cancela (y entonces lanza o da un 500).</summary>
    private sealed class SilentOverpass(bool answerOnCancel) : HttpMessageHandler
    {
        public int Waiting;
        public int Cancelled;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Waiting);
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException) when (answerOnCancel)
            {
                Interlocked.Increment(ref Cancelled);
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref Cancelled);
                throw;
            }
            throw new InvalidOperationException("no llega");
        }
    }

    private void UseRoads(HttpMessageHandler handler, string? cache = null) =>
        _host.Replace(new TrackSnapper(new OsmRoadSource(new HttpClient(handler), cache ?? Path.Combine(_host.Folder, "otras"), ["https://overpass.prueba/api"])));

    [Fact]
    public async Task SiElAjusteTardaDemasiadoSeQuedaRecto()
    {
        await CasaAsync();
        var overpass = new SilentOverpass(answerOnCancel: false);
        UseRoads(overpass);
        HistoryPage.SnapLimit = TimeSpan.FromMilliseconds(200);

        var (page, web, _) = Open();
        await UntilSnap(page, "SnapWorking");
        await UiDriver.Until(() => SnapStatus(page).Text == Loc.Get("SnapUnavailable"), "tope agotado");

        Assert.Equal(1, overpass.Cancelled);
        Assert.Equal(1, Count(web, "setTrack("));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AlIrseDeLaPaginaElAjusteSeCancelaSinDecirNada(bool answerOnCancel)
    {
        await CasaAsync();
        var overpass = new SilentOverpass(answerOnCancel);
        UseRoads(overpass);

        var (page, web, _) = Open();
        await UntilSnap(page, "SnapWorking");
        await UiDriver.Until(() => overpass.Waiting == 1, "peticion a Overpass");

        page.Disappear();
        await UiDriver.Until(() => overpass.Cancelled == 1, "cancelada");
        await UiDriver.Settle();

        Assert.Equal(Loc.Get("SnapWorking"), SnapStatus(page).Text);
        Assert.Equal(1, Count(web, "setTrack("));
    }

    [Fact]
    public async Task CambiarDePersonaCancelaElAjusteAnterior()
    {
        var casa = await CasaAsync();
        casa.Positions.Add((AnaId, 41.39, 2.16, 8, _now.AddMinutes(-50), 30));
        casa.Positions.Add((AnaId, 41.391, 2.16, 8, _now.AddMinutes(-45), 30));
        var overpass = new SilentOverpass(answerOnCancel: false);
        UseRoads(overpass);
        var (page, _, _) = Open();
        await UiDriver.Until(() => overpass.Waiting == 1, "peticion a Overpass");

        PickerTitled(page, "ChooseMember").SelectedIndex = 1;

        await UiDriver.Until(() => overpass.Cancelled == 1 && overpass.Waiting == 2, "la anterior cancelada y la nueva pedida");
        page.Disappear();
    }

    [Fact]
    public async Task UnFalloInesperadoDelAjusteSeQuedaRectoSinDialogo()
    {
        await CasaAsync();
        // Overpass contesta bien pero la cache de calles no se puede crear (ruta no valida): el
        // error no es de red ni de disco.
        UseRoads(new StreetOverpass(), cache: "no\0valida");

        var (page, web, _) = Open();

        await UntilSnap(page, "SnapUnavailable");
        Assert.Null(page.Dialog());
        Assert.Equal(1, Count(web, "setTrack("));
    }

    private sealed class StreetOverpass : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(FakeSupabase.Json(Street));
    }

    // -----------------------------------------------------------------------
    // Borrar mi historial (HistoryActions)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task BorrarMiHistorialBorraElServidorLaColaYLoDelMovil()
    {
        var casa = await CasaAsync();
        AppState.SnapTracks = false;
        _host.Server.Rpc("clear_my_history", "7");
        await _host.Track.AddAsync(41.405, 2.17, 5, _now.AddMinutes(-10), [casa.Id]);
        await _host.Outbox.EnqueueAsync(41.1, 2.1, 5, 50, _now.AddMinutes(-3), [casa.Id]);
        await _host.Outbox.EnqueueAsync(41.2, 2.1, 5, 50, _now.AddMinutes(-2), [casa.Id]);
        var (page, web, _) = Open();
        await UntilSummary(page, TrackSummary(5, _now.AddMinutes(-20), _now.AddMinutes(-10)));

        page.Toolbar("ClearHistory");
        var confirm = await page.DialogTextAsync();
        Assert.Contains(Loc.Get("ClearHistoryConfirm"), confirm);
        _host.Server.Table("positions", "[]");   // el servidor ya no tiene nada
        await page.AnswerKeyAsync("Delete");

        Assert.Contains(Loc.Format("ClearHistoryDone", 7), await page.DialogTextAsync());
        await page.AnswerKeyAsync("Ok");

        _host.Server.RpcCall("clear_my_history");
        Assert.Equal(0, await _host.Track.CountAsync());
        Assert.Equal(1, await _host.Outbox.PendingCountAsync());   // la ultima, para que me sigan viendo
        await UntilSummary(page, Loc.Get("NoPositionsLast24h"));
        Assert.Contains("clearTrack()", web.Calls);
    }

    [Fact]
    public async Task BorrarCanceladoNoBorraNada()
    {
        var casa = await CasaAsync();
        await _host.Track.AddAsync(41.405, 2.17, 5, _now.AddMinutes(-10), [casa.Id]);
        var (page, _, _) = Open();
        await UntilSummary(page, TrackSummary(5, _now.AddMinutes(-20), _now.AddMinutes(-10)));

        page.Toolbar("ClearHistory");
        await page.AnswerKeyAsync("Cancel");

        Assert.Empty(_host.Server.To("POST", "/rest/v1/rpc/clear_my_history"));
        Assert.Equal(1, await _host.Track.CountAsync());
        Assert.Null(page.Dialog());
    }

    [Fact]
    public async Task BorrarSinRedAvisaYNoTocaLoDelMovil()
    {
        var casa = await CasaAsync();
        await _host.Track.AddAsync(41.405, 2.17, 5, _now.AddMinutes(-10), [casa.Id]);
        _host.Server.Rpc("clear_my_history", _ => World.Fail());
        var (page, _, _) = Open();
        await UntilSummary(page, TrackSummary(5, _now.AddMinutes(-20), _now.AddMinutes(-10)));

        page.Toolbar("ClearHistory");
        await page.AnswerKeyAsync("Delete");

        Assert.Contains(Loc.Get("Err_network"), await page.DialogTextAsync());
        await page.AnswerKeyAsync("Ok");
        Assert.Equal(1, await _host.Track.CountAsync());
        Assert.False(page.Shows(Loc.Format("ClearHistoryDone", 0)));
    }

    [Fact]
    public async Task BorrarDesdeOtraPaginaDevuelveSiSeBorro()
    {
        _host.Server.Rpc("clear_my_history", "0");
        var page = new ContentPage { Content = new Grid() };
        page.Host();

        var cancel = HistoryActions.ClearMyHistoryAsync(page);
        await page.AnswerKeyAsync("Cancel");
        Assert.False(await cancel);

        var delete = HistoryActions.ClearMyHistoryAsync(page);
        await page.AnswerKeyAsync("Delete");
        Assert.Contains(Loc.Format("ClearHistoryDone", 0), await page.DialogTextAsync());
        await page.AnswerKeyAsync("Ok");
        Assert.True(await delete);
    }

    [Fact]
    public async Task EnIngles()
    {
        using var host = new AppHost(Loc.English);
        GroupSelectorTests.ForgetLastGroups();
        var world = new World(host.Phone);
        var g = await world.GroupAsync("Home");
        AppState.SnapTracks = false;
        g.Positions.Add((host.Phone.Me, 41.401, 2.17, 8, _now.AddMinutes(-9), 70));

        var page = new HistoryPage();
        FakeMap.Attach(page);
        page.Host();

        await UntilSummary(page, Loc.Format("TrackSummary", 1, TimeTexts.Clock(_now.AddMinutes(-9)), TimeTexts.Clock(_now.AddMinutes(-9))));
        Assert.StartsWith("1 positions", Summary(page));
        Assert.Equal("History", page.Title);
    }
}
