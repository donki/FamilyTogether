using FamilyTogether.Mobile.Controls;
using FamilyTogether.Mobile.Localization;
using FamilyTogether.Mobile.Services;

namespace FamilyTogether.Core.Tests.Ui;

/// <summary>El mapa: el WebView con map.html, la espera a que este listo y la cola de toques.</summary>
public class MapViewTests : IDisposable
{
    private readonly AppHost _host = new();
    private readonly FakeMap _fake = new();

    public void Dispose()
    {
        MapView.ReadyPoll = TimeSpan.FromMilliseconds(150);
        _fake.Dispose();
        _host.Dispose();
    }

    private static TestTimer EventTimer() => MauiFakes.Dispatcher.Timers.Single(t => t.Interval == TimeSpan.FromMilliseconds(400));

    [Fact]
    public async Task EsperaAQueElMapaEsteListoYLuegoEjecuta()
    {
        var map = new MapView();
        var web = FakeMap.Attach(map);
        var answers = 0;
        // Las dos primeras veces el WebView aun no acepta JavaScript; la tercera, la pagina no esta;
        // la cuarta, si.
        web.Answer = s =>
        {
            if (!s.Contains("mapReady", StringComparison.Ordinal))
                return null;
            answers++;
            return answers switch
            {
                <= 2 => throw new InvalidOperationException("aun no"),
                3 => "no",
                _ => "\"yes\"",
            };
        };
        MapView.ReadyPoll = TimeSpan.FromMilliseconds(1);

        await map.RunAsync("fitMembers()");

        Assert.Equal(4, answers);
        Assert.Equal(["fitMembers()"], web.Calls);

        // Se carga una sola vez, y lo siguiente va directo.
        await map.RunAsync("clearTrack()");
        Assert.Equal(4, answers);
        Assert.Equal(["fitMembers()", "clearTrack()"], web.Calls);
    }

    [Fact]
    public async Task ElHtmlLlevaLosTextosDelLectorEnElIdioma()
    {
        Loc.SetPreference(Loc.English);
        Loc.Apply();
        var map = new MapView();
        FakeMap.Attach(map);

        await map.RunAsync("x()");

        var html = Assert.IsType<HtmlWebViewSource>(FakeMap.Web(map).Source).Html;
        Assert.DoesNotContain("/*MAP_TEXT*/", html);
        Assert.Contains(System.Text.Json.JsonSerializer.Serialize(Loc.Get("MapA11yZoomIn")), html);
        Assert.Contains("\"start\":" + System.Text.Json.JsonSerializer.Serialize(Loc.Get("TrackStart")), html);
        Assert.Contains("\"me\":", html);
    }

    [Fact]
    public async Task AlCargarseEnPantallaEmpiezaACargarSolo()
    {
        var map = new MapView();
        var web = FakeMap.Attach(map);

        map.SendLoaded();

        await UiDriver.Until(() => web.All.Any(s => s.Contains("mapReady", StringComparison.Ordinal)), "pregunta de listo");
        Assert.IsType<HtmlWebViewSource>(FakeMap.Web(map).Source);
    }

    [Fact]
    public async Task SinMapHtmlNoSeEjecutaNadaYNoCuelga()
    {
        _fake.Dispose();
        var map = new MapView();
        var web = FakeMap.Attach(map);

        await map.RunAsync("fitMembers()");

        Assert.Empty(web.All);
        Assert.Null(FakeMap.Web(map).Source);
    }

    [Fact]
    public async Task SiElMapaNoContestaSeRindeYNoEjecuta()
    {
        var map = new MapView();
        var web = FakeMap.Attach(map);
        web.Answer = _ => "no";
        MapView.ReadyPoll = TimeSpan.Zero;

        await map.RunAsync("fitMembers()");

        Assert.Equal(100, web.All.Count);
        Assert.Empty(web.Calls);

        // Los toques tampoco se recogen.
        map.StartEvents();
        EventTimer().Fire();
        Assert.DoesNotContain("takeEvents()", web.All);
    }

    [Fact]
    public async Task UnGuionQueFallaNoSube()
    {
        var map = new MapView();
        var web = FakeMap.Attach(map);
        web.Answer = s => s.Contains("mapReady", StringComparison.Ordinal) ? "yes" : throw new InvalidOperationException("error de JavaScript");

        await map.RunAsync("roto()");

        Assert.Equal(["roto()"], web.Calls);
    }

    [Fact]
    public async Task LosToquesDeLaColaLleganComoEventos()
    {
        var map = new MapView();
        var web = FakeMap.Attach(map);
        var member = Guid.NewGuid();
        var queue = $"\"tap|41.5|2.25;member|{member:D};tap|x|1;tap|1;member|no-es-un-id;otro|1;;\"";
        web.Answer = s => s switch
        {
            _ when s.Contains("mapReady", StringComparison.Ordinal) => "yes",
            "takeEvents()" => queue,
            _ => null,
        };
        var taps = new List<(double, double)>();
        var members = new List<Guid>();
        map.MapTapped += (_, p) => taps.Add(p);
        map.MemberTapped += (_, id) => members.Add(id);

        // Antes de que el mapa este listo no se pregunta.
        map.StartEvents();
        var timer = EventTimer();
        Assert.True(timer.IsRunning);
        timer.Fire();
        Assert.Empty(web.All);

        await map.RunAsync("x()");
        timer.Fire();
        await UiDriver.Until(() => members.Count == 1, "toque en un miembro");

        Assert.Equal([(41.5, 2.25)], taps);
        Assert.Equal([member], members);

        // Cola vacia o «null»: nada.
        queue = "null";
        timer.Fire();
        queue = "";
        timer.Fire();
        await UiDriver.Settle();
        Assert.Single(taps);

        // Mismo temporizador al volver; parar lo detiene.
        map.StopEvents();
        Assert.False(timer.IsRunning);
        map.StartEvents();
        Assert.Same(timer, EventTimer());
        Assert.True(timer.IsRunning);
    }

    [Fact]
    public async Task NoSePreguntaDosVecesALaVezYUnFalloNoSube()
    {
        var map = new MapView();
        var web = FakeMap.Attach(map);
        await map.RunAsync("x()");
        map.StartEvents();
        var timer = EventTimer();

        web.Hold = s => s == "takeEvents()";
        timer.Fire();
        timer.Fire();
        Assert.Equal(1, web.HeldCount);

        // La respuesta llega y es un error: se registra y se puede volver a preguntar.
        web.Answer = s => s == "takeEvents()" ? throw new InvalidOperationException("WebView cerrado") : null;
        web.Release();
        await UiDriver.Settle();
        web.Answer = _ => "";
        timer.Fire();
        await UiDriver.Until(() => web.All.Count(s => s == "takeEvents()") == 2, "segunda pregunta");
    }

    [Fact]
    public void NumerosYJsonSinCultura()
    {
        using var _ = new CultureSwap("es-ES");
        Assert.Equal("41.123456789", MapView.Num(41.123456789));
        Assert.Equal("-0.5", MapView.Num(-0.5));
        Assert.Equal("[[1.5,2]]", MapView.Json(new[] { new[] { 1.5, 2 } }));
    }

    private sealed class CultureSwap : IDisposable
    {
        private readonly System.Globalization.CultureInfo _old = System.Globalization.CultureInfo.CurrentCulture;

        public CultureSwap(string name) => System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo(name);

        public void Dispose() => System.Globalization.CultureInfo.CurrentCulture = _old;
    }
}
