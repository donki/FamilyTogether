using System.Globalization;
using FamilyTogether.Mobile.Localization;
using FamilyTogether.Mobile.Services;
using FamilyTogether.Mobile.Services.Native;
using Microsoft.Extensions.DependencyInjection;

namespace FamilyTogether.Core.Tests.Ui;

/// <summary>Almacen clave-valor en memoria (las SharedPreferences de Android).</summary>
internal sealed class MemoryStore : IKeyValueStore
{
    public Dictionary<string, object?> Values { get; } = [];
    public bool Broken { get; set; }

    public bool GetBool(string key, bool fallback) =>
        Broken ? throw new InvalidOperationException("roto") : Values.TryGetValue(key, out var v) && v is bool b ? b : fallback;

    public string? GetString(string key) =>
        Broken ? throw new InvalidOperationException("roto") : Values.TryGetValue(key, out var v) ? v as string : null;

    public void Apply(IReadOnlyDictionary<string, object?> changes)
    {
        if (Broken)
            throw new InvalidOperationException("roto");
        foreach (var (k, v) in changes)
        {
            if (v is null) Values.Remove(k); else Values[k] = v;
        }
    }
}

/// <summary>El dispositivo para el motor: red, bateria, sensor y contenedor, a mano.</summary>
internal sealed class FakeDevice(IServiceProvider services) : ISharingDevice
{
    public bool Online { get; set; } = true;
    public int Battery { get; set; } = 77;
    public bool MotionAvailable { get; set; }
    public DateTimeOffset? LastMotion { get; set; }
    public bool PushConfigured { get; set; } = true;
    public bool CoreMissing { get; set; }
    public FakeNotifier Fallback { get; } = new();
    public int WakeLocks { get; private set; }
    public int Releases { get; private set; }

    public bool IsOnline() => Online;
    public int ReadBattery() => Battery;

    /// <summary>Distancia plana en metros (suficiente a estas escalas).</summary>
    public double DistanceMeters(double lat1, double lon1, double lat2, double lon2)
    {
        var dy = (lat2 - lat1) * 111_195.0;
        var dx = (lon2 - lon1) * 111_195.0 * Math.Cos(lat1 * Math.PI / 180);
        return Math.Sqrt(dx * dx + dy * dy);
    }

    public Dictionary<Type, object> Overrides { get; } = [];
    public T? Get<T>() where T : class => CoreMissing ? null : Overrides.TryGetValue(typeof(T), out var o) ? (T)o : services.GetService<T>();
    public INotifier FallbackNotifier() => Fallback;
    public bool BrokenWakeLock { get; set; }
    public void AcquireWakeLock() { if (BrokenWakeLock) throw new InvalidOperationException("sin bloqueo"); WakeLocks++; }
    public void ReleaseWakeLock() => Releases++;
    public List<TrackingMode> Applied { get; } = [];
    public bool BrokenTracking { get; set; }
    public void ApplyTracking(TrackingMode mode)
    {
        if (BrokenTracking) throw new InvalidOperationException("sin LocationManager");
        Applied.Add(mode);
    }
}

/// <summary>La logica de la parte nativa sacada de Platforms\Android (Services\Native).</summary>
public class NativeTests : IDisposable
{
    private readonly MemoryStore _store = new();
    private readonly AppHost _host = new();

    public NativeTests()
    {
        SharingState.Store = () => _store;
        SharingState.Reset();
        SharingEngine.LastFix = null;
    }

    public void Dispose()
    {
        SharingState.Store = () => null;
        SharingState.Reset();
        NativeLog.Logcat = null;
        _host.Dispose();
    }

    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static LocationReading Gps(double lat, double lon, double acc = 8, double? speed = null, DateTimeOffset? at = null) =>
        new("gps", lat, lon, acc, speed, at ?? DateTimeOffset.UtcNow);

    // -----------------------------------------------------------------------
    // SharingState
    // -----------------------------------------------------------------------

    [Fact]
    public void EstadoActivadoYUltimaEnviadaSeGuardan()
    {
        Assert.False(SharingState.Enabled);
        SharingState.Enabled = true;
        Assert.True(SharingState.Enabled);
        Assert.True((bool)_store.Values["enabled"]!);

        Assert.Null(SharingState.LastSent);
        SharingState.LastSent = (40.123456789, -3.5);
        Assert.Equal((40.123456789, -3.5), SharingState.LastSent);
        Assert.Equal("40.123456789", _store.Values["last_lat"]);

        SharingState.LastSent = null;
        Assert.Null(SharingState.LastSent);
        Assert.False(_store.Values.ContainsKey("last_lat"));
    }

    [Fact]
    public void SinAlmacenOConAlmacenRotoNoLanza()
    {
        _store.Values["last_lat"] = "no es un numero";
        _store.Values["last_lon"] = "1";
        Assert.Null(SharingState.LastSent);

        _store.Broken = true;
        Assert.False(SharingState.Enabled);
        SharingState.Enabled = true;
        Assert.Null(SharingState.LastSent);
        SharingState.LastSent = (1, 2);

        SharingState.Store = () => null;
        Assert.False(SharingState.Enabled);
        SharingState.LastSent = (1, 2);
        Assert.Null(SharingState.LastSent);
    }

    [Fact]
    public async Task GruposDondeComparto_FiltraPausasYSinClave()
    {
        var world = new World(_host.Phone);
        var casa = await world.GroupAsync("Casa");
        var pausa = await world.GroupAsync("Pausa");
        pausa.Members[0] = pausa.Members[0] with { Paused = true };
        var fin = await world.GroupAsync("Pausa que ya acabo");
        fin.Members[0] = fin.Members[0] with { Paused = true, Until = DateTimeOffset.UtcNow.AddMinutes(-1) };
        var sinClave = await world.GroupAsync("Sin clave");
        await _host.Phone.Keys.RemoveAsync(sinClave.Id);

        var groups = await SharingState.GetSharingGroupsAsync(_host.Phone.Service, online: true, TimeSpan.FromMinutes(1));

        Assert.Equal(new[] { casa.Id, fin.Id }.Order(), groups.Order());
        Assert.Contains(casa.Id.ToString("D"), (string)_store.Values["sharing_groups"]!);
        Assert.IsType<long>(_store.Values["sharing_groups_at"]);
    }

    [Fact]
    public async Task GruposDondeComparto_SinRedUsaLaCacheGuardada()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        var until = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds();
        _store.Values["sharing_groups"] = $"{a:D}|0|;{b:D}|1|;{c:D}|1|{until};roto;{Guid.NewGuid():D}|0";

        var groups = await SharingState.GetSharingGroupsAsync(_host.Phone.Service, online: false, TimeSpan.Zero);

        Assert.Equal(new[] { a, c }.Order(), groups.Order());
        Assert.Empty(_host.Server.Requests);
    }

    [Fact]
    public async Task GruposDondeComparto_SiElServidorFallaSigueConLaCache()
    {
        var a = Guid.NewGuid();
        _store.Values["sharing_groups"] = $"{a:D}|0|";
        _host.Server.Table("group_members", _ => World.Fail());

        var groups = await SharingState.GetSharingGroupsAsync(_host.Phone.Service, online: true, TimeSpan.Zero);

        Assert.Equal([a], groups);
    }

    [Fact]
    public async Task GruposDondeComparto_CacheIlegibleEmpiezaVacia()
    {
        _store.Broken = true;
        Assert.Empty(await SharingState.GetSharingGroupsAsync(null, online: true, TimeSpan.Zero));
    }

    [Fact]
    public async Task GruposDondeComparto_RespetaLaEdadDeLaCacheEInvalidar()
    {
        var world = new World(_host.Phone);
        await world.GroupAsync("Casa");
        await SharingState.GetSharingGroupsAsync(_host.Phone.Service, true, TimeSpan.FromHours(1));
        var calls = _host.Server.Requests.Count;

        await SharingState.GetSharingGroupsAsync(_host.Phone.Service, true, TimeSpan.FromHours(1));
        Assert.Equal(calls, _host.Server.Requests.Count);

        SharingState.InvalidateGroups();
        await SharingState.GetSharingGroupsAsync(_host.Phone.Service, true, TimeSpan.FromHours(1));
        Assert.True(_host.Server.Requests.Count > calls);
    }

    [Fact]
    public async Task GruposDondeComparto_GrupoSinMiFilaNoCuenta()
    {
        var world = new World(_host.Phone);
        var g = await world.GroupAsync("Casa");
        _host.Server.Table("group_members", r => r.Query.Contains("group_id=eq.")
            ? FakeSupabase.Json("[]")
            : FakeSupabase.Json(FakeSupabase.Serialize(new[] { new { group_id = g.Id, user_id = _host.Phone.Me, role = "admin", paused = false } })));

        Assert.Empty(await SharingState.GetSharingGroupsAsync(_host.Phone.Service, true, TimeSpan.Zero));
    }

    // -----------------------------------------------------------------------
    // SharingEngine: que se hace con cada lectura
    // -----------------------------------------------------------------------

    private (SharingEngine Engine, FakeDevice Device) Engine()
    {
        var device = new FakeDevice(_host.Services);
        return (new SharingEngine(device) { Running = true }, device);
    }

    [Fact]
    public void Clasificar_SinPrecisionOViejaSeDescarta()
    {
        var (engine, _) = Engine();
        Assert.Equal(ReadingKind.Discard, engine.Classify(new LocationReading("gps", 1, 1, null, null, Now), Now));
        Assert.Equal(ReadingKind.Discard, engine.Classify(Gps(1, 1, at: Now.AddMinutes(-3)), Now));
    }

    [Fact]
    public void Clasificar_LaPrimeraBuenaSaleYLasSiguientesPorDistancia()
    {
        var (engine, device) = Engine();
        Assert.Equal(ReadingKind.Fine, engine.Classify(Gps(40, -3), Now));
        Assert.Equal(ReadingKind.Coarse, engine.Classify(new LocationReading("network", 40, -3, 30, null, Now), Now));

        // Ya hay una enviada en esta tanda: a menos de 25 m se descarta, a mas sale.
        SharingState.LastSent = (40, -3);
        typeof(SharingEngine).GetField("_sentInThisRun", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(engine, true);
        Assert.Equal(ReadingKind.Discard, engine.Classify(Gps(40.0001, -3), Now));
        Assert.Equal(ReadingKind.Fine, engine.Classify(Gps(40.001, -3), Now));

        // Con sensor de movimiento y quieto: aproximada (y se apunta en el registro una vez).
        device.MotionAvailable = true;
        device.LastMotion = Now.AddHours(-1);
        Assert.Equal(ReadingKind.Coarse, engine.Classify(Gps(40.001, -3), Now));
        Assert.Equal(ReadingKind.Coarse, engine.Classify(Gps(40.001, -3), Now));
    }

    [Fact]
    public async Task Lectura_ApagadoOPeorDe100mNoHaceNada()
    {
        var (engine, device) = Engine();
        engine.Running = false;
        await engine.ProcessReadingAsync(Gps(40, -3));
        engine.Running = true;
        await engine.ProcessReadingAsync(Gps(40, -3, acc: 150));
        await engine.ProcessReadingAsync(new LocationReading("gps", 40, -3, null, null, Now));
        Assert.Equal(0, device.WakeLocks);
        Assert.Equal(0, await _host.Outbox.PendingCountAsync());
    }

    [Fact]
    public async Task Lectura_BuenaSeEncolaSeEnviaYEntraEnMiRecorrido()
    {
        var world = new World(_host.Phone);
        var g = await world.GroupAsync("Casa");
        var (engine, device) = Engine();

        await engine.ProcessReadingAsync(Gps(40, -3));

        Assert.Equal(0, await _host.Outbox.PendingCountAsync());
        var post = Assert.Single(_host.Server.To("POST", "/rest/v1/positions"));
        Assert.Equal(g.Id, post.Json[0].GetProperty("group_id").GetGuid());
        Assert.Equal(77, post.Json[0].GetProperty("battery").GetInt32());
        Assert.Equal(1, await _host.Track.CountAsync());
        Assert.Equal((40d, -3d), SharingState.LastSent);
        Assert.NotNull(SharingEngine.LastFix);
        Assert.Equal(1, device.WakeLocks);
        Assert.Equal(1, device.Releases);
    }

    [Fact]
    public async Task Lectura_SinRedSeQuedaEnLaCola()
    {
        var world = new World(_host.Phone);
        await world.GroupAsync("Casa");
        var (engine, device) = Engine();
        await SharingState.GetSharingGroupsAsync(_host.Phone.Service, true, TimeSpan.Zero);
        device.Online = false;

        await engine.ProcessReadingAsync(Gps(40, -3));

        Assert.Equal(1, await _host.Outbox.PendingCountAsync());
        Assert.Empty(_host.Server.To("POST", "/rest/v1/positions"));
    }

    [Fact]
    public async Task Lectura_SinGruposNoSeEncola_YSinNucleoSeDescarta()
    {
        var (engine, device) = Engine();
        await engine.ProcessReadingAsync(Gps(40, -3));
        Assert.Equal(0, await _host.Outbox.PendingCountAsync());

        device.CoreMissing = true;
        await engine.ProcessReadingAsync(Gps(41, -3));
        Assert.Equal(0, await _host.Outbox.PendingCountAsync());
    }

    [Fact]
    public async Task Lectura_AproximadaVaALaColaPeroNoAlRecorridoNiAZonas()
    {
        var world = new World(_host.Phone);
        await world.GroupAsync("Casa");
        var (engine, _) = Engine();

        await engine.ProcessReadingAsync(new LocationReading("network", 40, -3, 40, null, DateTimeOffset.UtcNow));

        Assert.Single(_host.Server.To("POST", "/rest/v1/positions"));
        Assert.Equal(0, await _host.Track.CountAsync());
        Assert.Null(SharingState.LastSent);
    }

    [Fact]
    public async Task Lectura_FalloAlEncolarSeRegistraYSigue()
    {
        var world = new World(_host.Phone);
        await world.GroupAsync("Casa");
        var (engine, _) = Engine();
        SQLite.SQLiteAsyncConnection.ResetPool();
        var lines = new List<string>();
        NativeLog.Logcat = (_, l) => lines.Add(l);
        File.WriteAllText(Path.Combine(_host.Folder, "ft.db3"), "esto no es una base de datos sqlite y deberia fallar al abrirla");

        await engine.ProcessReadingAsync(Gps(40, -3));

        Assert.Contains(lines, l => l.Contains("No se pudo encolar"));
    }

    [Fact]
    public async Task Zonas_EntradaSeInformaYSiFallaSeReintentaEnElCiclo()
    {
        var world = new World(_host.Phone);
        var g = await world.GroupAsync("Casa");
        var zone = new World.ZoneData(Guid.NewGuid(), "Colegio", 40, -3, 150, _host.Phone.Me);
        g.Zones.Add(zone);
        var (engine, _) = Engine();
        var fail = true;
        _host.Server.Rpc("report_zone_event", _ => fail ? World.Fail() : FakeSupabase.Json("null"));

        await engine.ProcessReadingAsync(Gps(40.01, -3));   // fuera: primer estado, sin aviso
        Assert.Equal(0, engine.PendingZoneEvents);
        await engine.ProcessReadingAsync(Gps(40, -3));      // dentro: entrada
        Assert.Equal(1, engine.PendingZoneEvents);

        fail = false;
        await engine.RunCycleAsync();
        Assert.Equal(0, engine.PendingZoneEvents);
        Assert.Equal(2, _host.Server.To("POST", "/rest/v1/rpc/report_zone_event").Count());
    }

    [Fact]
    public async Task Zonas_PendientesTienenTope()
    {
        var (engine, _) = Engine();
        var field = (List<(Guid, Guid, string)>)typeof(SharingEngine)
            .GetField("_pendingZoneEvents", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(engine)!;
        for (var i = 0; i < 60; i++)
            field.Add((Guid.NewGuid(), Guid.NewGuid(), "enter"));
        _host.Server.Rpc("report_zone_event", _ => World.Fail());

        await engine.RunCycleAsync();

        Assert.Equal(SharingEngine.MaxPendingZoneEvents, engine.PendingZoneEvents);
    }

    [Fact]
    public async Task AlEcharAAndar_LasBuenasDeAntesEntranConSuHora()
    {
        var world = new World(_host.Phone);
        await world.GroupAsync("Casa");
        var (engine, device) = Engine();
        device.MotionAvailable = true;
        device.LastMotion = DateTimeOffset.UtcNow.AddHours(-1);

        // La primera de la tanda sale siempre; las siguientes, quieto, se guardan.
        await engine.ProcessReadingAsync(Gps(40, -3));
        var t = DateTimeOffset.UtcNow;
        await engine.ProcessReadingAsync(Gps(40.001, -3, at: t.AddSeconds(-90)));
        await engine.ProcessReadingAsync(Gps(40.00105, -3, at: t.AddSeconds(-60)));   // a 5 m de la anterior
        await engine.ProcessReadingAsync(Gps(40.002, -3, at: t.AddSeconds(-30)));
        Assert.Equal(3, engine.HeldStillReadings);

        await engine.PromoteStillReadingsAsync(t);

        Assert.Equal(3, await _host.Track.CountAsync());   // la primera y 2 de las 3 guardadas
        Assert.Equal(0, engine.HeldStillReadings);

        // Sin nada guardado o apagado: no hace nada.
        await engine.PromoteStillReadingsAsync(t);
        engine.Running = false;
        await engine.PromoteStillReadingsAsync(t);
        engine.ClearStillReadings();
    }

    // -----------------------------------------------------------------------
    // Bateria: quieto o moviendose (LocationPlan, 2026-10-06)
    // -----------------------------------------------------------------------

    [Fact]
    public void Plan_SinSensorSiempreMoviendose()
    {
        var start = Now.AddHours(-3);
        Assert.Equal(TrackingMode.Moving, LocationPlan.ModeFor(false, null, null, start, Now));
        Assert.Equal(TrackingMode.Moving, LocationPlan.ModeFor(false, Now.AddHours(-2), null, start, Now));
    }

    [Fact]
    public void Plan_QuietoTrasCincoMinutosSinMovimientoNiVelocidad()
    {
        var start = Now.AddHours(-1);
        // Recien arrancado: moviendose (la primera posicion buena sale con el GPS).
        Assert.Equal(TrackingMode.Moving, LocationPlan.ModeFor(true, null, null, Now.AddMinutes(-4), Now));
        Assert.Equal(TrackingMode.Still, LocationPlan.ModeFor(true, null, null, start, Now));
        Assert.Equal(TrackingMode.Still, LocationPlan.ModeFor(true, Now.AddMinutes(-5), null, start, Now));
        Assert.Equal(TrackingMode.Moving, LocationPlan.ModeFor(true, Now.AddMinutes(-4), null, start, Now));
        Assert.Equal(TrackingMode.Moving, LocationPlan.ModeFor(true, Now.AddMinutes(-30), Now.AddMinutes(-1), start, Now));
        Assert.Equal(LocationPlan.StillAfter, ReadingPolicy.MotionWindow);

        Assert.Equal(start + LocationPlan.StillAfter, LocationPlan.StillAt(null, null, start));
        Assert.Equal(Now.AddMinutes(-1) + LocationPlan.StillAfter, LocationPlan.StillAt(Now.AddMinutes(-20), Now.AddMinutes(-1), start));
        Assert.Equal(Now.AddMinutes(-2) + LocationPlan.StillAfter, LocationPlan.StillAt(Now.AddMinutes(-2), Now.AddMinutes(-9), start));
    }

    [Fact]
    public void Plan_QuietoSinGpsYLaRedCadaCincoMinutos()
    {
        var all = new[] { "gps", "network", "passive", "fused" };

        var moving = LocationPlan.Requests(TrackingMode.Moving, all);
        Assert.Equal(["gps", "network"], moving.Select(r => r.Provider));
        Assert.All(moving, r => Assert.Equal(TimeSpan.FromSeconds(30), r.MinTime));
        Assert.All(moving, r => Assert.Equal(25f, r.MinDistanceMeters));

        var still = LocationPlan.Requests(TrackingMode.Still, all);
        Assert.DoesNotContain(still, r => r.Provider == "gps");
        Assert.Equal(TimeSpan.FromMinutes(5), still.Single(r => r.Provider == "network").MinTime);
        Assert.Equal(TimeSpan.FromMinutes(1), still.Single(r => r.Provider == "passive").MinTime);
        Assert.All(still, r => Assert.Equal(25f, r.MinDistanceMeters));

        // Solo los proveedores que hay.
        Assert.Equal(["gps"], LocationPlan.Requests(TrackingMode.Moving, ["gps", "passive"]).Select(r => r.Provider));
        Assert.Equal(["passive"], LocationPlan.Requests(TrackingMode.Still, ["gps", "passive"]).Select(r => r.Provider));
        Assert.Empty(LocationPlan.Requests(TrackingMode.Still, []));
    }

    [Fact]
    public void Motor_PasaAQuietoYVuelveAMoverseConElSensor()
    {
        var (engine, device) = Engine();
        device.MotionAvailable = true;
        engine.BeginTracking(Now.AddMinutes(-10));
        Assert.Equal(TrackingMode.Moving, engine.Mode);
        Assert.Equal(Now.AddMinutes(-5), engine.StillAt);

        Assert.Equal(TrackingMode.Still, engine.UpdateTracking(Now));
        Assert.Equal([TrackingMode.Still], device.Applied);

        // Sin cambios no se vuelve a pedir nada al sistema.
        engine.UpdateTracking(Now);
        Assert.Single(device.Applied);

        device.LastMotion = Now;
        Assert.Equal(TrackingMode.Moving, engine.UpdateTracking(Now));
        Assert.Equal([TrackingMode.Still, TrackingMode.Moving], device.Applied);
        Assert.Equal(Now.AddMinutes(5), engine.StillAt);

        // Al arrancar otra vez, moviendose sin pedir nada (lo pide el propio servicio).
        engine.BeginTracking(Now);
        Assert.Equal(TrackingMode.Moving, engine.Mode);
        Assert.Equal(2, device.Applied.Count);
    }

    [Fact]
    public void Motor_ApagadoOConFalloAlCambiarNoLanza()
    {
        var (engine, device) = Engine();
        device.MotionAvailable = true;
        engine.BeginTracking(Now.AddMinutes(-10));

        engine.Running = false;
        Assert.Equal(TrackingMode.Moving, engine.UpdateTracking(Now));
        Assert.Empty(device.Applied);

        engine.Running = true;
        device.BrokenTracking = true;
        Assert.Equal(TrackingMode.Still, engine.UpdateTracking(Now));
        Assert.Equal(TrackingMode.Still, engine.Mode);
    }

    [Fact]
    public async Task Motor_ElGpsConVelocidadEnciendeElGpsAunqueElSensorNoSalte()
    {
        await new World(_host.Phone).GroupAsync("Casa");
        var (engine, device) = Engine();
        device.MotionAvailable = true;
        device.LastMotion = DateTimeOffset.UtcNow.AddHours(-1);
        engine.BeginTracking(DateTimeOffset.UtcNow.AddMinutes(-10));

        // Una lectura de red (o de GPS parado) con el movil quieto: pasa a quieto.
        await engine.ProcessReadingAsync(new LocationReading("network", 40, -3, 30, null, DateTimeOffset.UtcNow));
        Assert.Equal(TrackingMode.Still, engine.Mode);
        await engine.ProcessReadingAsync(Gps(40, -3, speed: 0.3));
        Assert.Equal(TrackingMode.Still, engine.Mode);

        // GPS con velocidad de ir andando (por la pasiva, de otra app): moviendose.
        await engine.ProcessReadingAsync(Gps(40.001, -3, speed: 2));
        Assert.Equal(TrackingMode.Moving, engine.Mode);
        Assert.Equal([TrackingMode.Still, TrackingMode.Moving], device.Applied);

        // Una vieja con velocidad no cuenta.
        var (other, otherDevice) = Engine();
        otherDevice.MotionAvailable = true;
        other.BeginTracking(DateTimeOffset.UtcNow.AddMinutes(-10));
        await other.ProcessReadingAsync(Gps(40, -3, speed: 2, at: DateTimeOffset.UtcNow.AddMinutes(-3)));
        Assert.Equal(TrackingMode.Still, other.Mode);
    }

    [Fact]
    public async Task Ciclo_PasaAQuietoAunqueEsteOcupado()
    {
        var (engine, device) = Engine();
        device.MotionAvailable = true;
        device.Online = false;
        engine.BeginTracking(DateTimeOffset.UtcNow.AddMinutes(-6));
        var work = (SemaphoreSlim)typeof(SharingEngine).GetField("_work", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(engine)!;
        await work.WaitAsync();
        await engine.RunCycleAsync();
        work.Release();
        Assert.Equal([TrackingMode.Still], device.Applied);
    }

    [Fact]
    public async Task Ciclo_ConFcmSoloPreguntaAlServidorCadaMediaHora()
    {
        var world = new World(_host.Phone);
        var g = await world.GroupAsync("Casa");
        var (engine, _) = Engine();

        // Primera vuelta: grupos, claves pedidas y eventos (respaldo del push), todo junto.
        await engine.RunCycleAsync();
        Assert.Contains(_host.Server.Requests, r => r.Path.Contains("key_share"));
        Assert.Contains(_host.Server.Requests, r => r.Path.Contains("sos_alerts"));
        Assert.Contains(_host.Server.Requests, r => r.Path == "/rest/v1/groups");

        // Las siguientes, sin nada pendiente, no tocan la red.
        _host.Server.Requests.Clear();
        await engine.RunCycleAsync();
        await engine.RunCycleAsync();
        Assert.Empty(_host.Server.Requests);

        // Lo pendiente sí sale en cada vuelta (SC-002).
        await _host.Outbox.EnqueueAsync(40, -3, 8, 50, DateTimeOffset.UtcNow, [g.Id]);
        await engine.RunCycleAsync();
        Assert.Equal(0, await _host.Outbox.PendingCountAsync());
        Assert.DoesNotContain(_host.Server.Requests, r => r.Path.Contains("key_share"));

        // Pasada la media hora, otra vuelta al servidor.
        typeof(SharingEngine).GetField("_lastServerCheck", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(engine, DateTimeOffset.UtcNow - SharingEngine.ServerCheckEveryWithPush);
        _host.Server.Requests.Clear();
        await engine.RunCycleAsync();
        Assert.Contains(_host.Server.Requests, r => r.Path.Contains("key_share"));
    }

    [Fact]
    public async Task Lectura_LaCacheDeGruposNoPreguntaEnCadaLectura()
    {
        await new World(_host.Phone).GroupAsync("Casa");
        var (engine, _) = Engine();
        int Groups() => _host.Server.Requests.Count(r => r.Path == "/rest/v1/groups");

        await engine.ProcessReadingAsync(new LocationReading("network", 40, -3, 30, null, DateTimeOffset.UtcNow));
        Assert.Equal(1, Groups());
        await engine.ProcessReadingAsync(new LocationReading("network", 40.01, -3, 30, null, DateTimeOffset.UtcNow));
        await engine.ProcessReadingAsync(new LocationReading("network", 40.02, -3, 30, null, DateTimeOffset.UtcNow));
        Assert.Equal(1, Groups());
        Assert.Equal(TimeSpan.FromMinutes(15), SharingEngine.GroupsMaxAgeOnReading);

        // Una aprobacion que llega por push la invalida: la siguiente lectura pregunta.
        await PushMessages.HandleAsync(new Dictionary<string, string>
        {
            ["type"] = EventTypes.RequestResolved, ["group_id"] = Guid.NewGuid().ToString(), ["event_id"] = Guid.NewGuid().ToString(),
        }, _host.Phone.Service, null, () => new FakeNotifier());
        await engine.ProcessReadingAsync(new LocationReading("network", 40.03, -3, 30, null, DateTimeOffset.UtcNow));
        Assert.Equal(2, Groups());
    }

    // -----------------------------------------------------------------------
    // SharingEngine: el ciclo de 60 s
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Ciclo_SinRedSoloPodaElRecorrido()
    {
        var (engine, device) = Engine();
        device.Online = false;
        await engine.RunCycleAsync();
        Assert.Empty(_host.Server.Requests);
        Assert.Equal(1, device.WakeLocks);

        engine.Running = false;
        await engine.RunCycleAsync();
        Assert.Equal(1, device.WakeLocks);
    }

    [Fact]
    public async Task Ciclo_ConRedVaciaLaColaReintentaElSosYEntregaClaves()
    {
        var world = new World(_host.Phone);
        var g = await world.GroupAsync("Casa");
        await _host.Outbox.EnqueueAsync(40, -3, 8, 50, DateTimeOffset.UtcNow, [g.Id]);
        _host.Server.Rpc("create_sos", _ => World.Fail());
        try { await _host.Sos.SendAsync([g.Id], (40, -3, 8, DateTimeOffset.UtcNow, false)); } catch (HttpRequestException) { }
        Assert.True(_host.Sos.HasPending);
        _host.Server.Rpc("create_sos", _ => FakeSupabase.Json("null"));
        var (engine, device) = Engine();

        await engine.RunCycleAsync();

        Assert.Equal(0, await _host.Outbox.PendingCountAsync());
        Assert.False(_host.Sos.HasPending);
        Assert.Contains(_host.Server.Requests, r => r.Path.Contains("key_share"));
        Assert.Empty(device.Fallback.Shown);
    }

    [Fact]
    public async Task Ciclo_SinFcmConsultaLosEventosYAvisa()
    {
        var world = new World(_host.Phone);
        await world.GroupAsync("Casa");
        var (engine, device) = Engine();
        device.PushConfigured = false;
        device.CoreMissing = false;
        _host.Services.GetService<INotifier>();

        await engine.RunCycleAsync();
        await engine.RunCycleAsync();   // claves cada vuelta sin FCM

        Assert.True(_host.Server.Requests.Count(r => r.Path.Contains("key_share")) >= 2);
        Assert.Contains(_host.Server.Requests, r => r.Path.Contains("sos_alerts"));
    }

    [Fact]
    public async Task Ciclo_SinFcmUnSosDeOtroSaleComoAviso()
    {
        var ana = Guid.NewGuid();
        var world = new World(_host.Phone);
        var g = await world.GroupAsync("Casa", false, new World.MemberData(ana, "Ana", true));
        var sos = Guid.NewGuid();
        var at = DateTimeOffset.UtcNow.AddMinutes(-1);
        _host.Server.Table("sos_alerts", _ => FakeSupabase.Json(FakeSupabase.Serialize(new[] { new { id = sos, user_id = ana, created_at = at } })));
        _host.Server.Table("sos_targets", _ => FakeSupabase.Json(FakeSupabase.Serialize(new[]
        {
            new { sos_id = sos, group_id = g.Id, payload_enc = Crypto.Encrypt(Payloads.Sos(40.5, -3.5, 10, at, false), g.Key) },
        })));
        var services = new ServiceCollection().AddSingleton(_host.Phone.Service).AddSingleton(_host.Feed).BuildServiceProvider();
        var device = new FakeDevice(services) { PushConfigured = false };
        var engine = new SharingEngine(device) { Running = true };

        await engine.RunCycleAsync();

        var shown = Assert.Single(device.Fallback.Shown);
        Assert.Equal(40.5, shown.Lat);
    }

    [Fact]
    public async Task Ciclo_SinNucleoOConFallosNoLanza()
    {
        var (engine, device) = Engine();
        device.CoreMissing = true;
        await engine.RunCycleAsync();

        device.CoreMissing = false;
        device.PushConfigured = false;
        foreach (var path in new[] { "sos_alerts", "key_shares", "join_requests" })
            _host.Server.Table(path, _ => World.Fail());
        _host.Server.Rpc("fulfill_key_share", _ => World.Fail());
        await engine.RunCycleAsync();
    }

    [Fact]
    public async Task Ciclo_OcupadoConUnaLecturaSeSalta()
    {
        var (engine, device) = Engine();
        var work = (SemaphoreSlim)typeof(SharingEngine).GetField("_work", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(engine)!;
        await work.WaitAsync();
        await engine.RunCycleAsync();
        Assert.Equal(0, device.WakeLocks);
        work.Release();
    }

    [Fact]
    public async Task FallosDelDispositivoODeLaBaseNoTumbanNada()
    {
        var lines = new List<string>();
        NativeLog.Logcat = (_, l) => lines.Add(l);
        var (engine, device) = Engine();
        device.BrokenWakeLock = true;
        await engine.ProcessReadingAsync(Gps(40, -3));
        await engine.PromoteStillReadingsAsync(DateTimeOffset.UtcNow);
        await engine.RunCycleAsync();
        Assert.Contains(lines, l => l.Contains("procesar una posici"));
        Assert.Contains(lines, l => l.Contains("principio del recorrido"));
        Assert.Contains(lines, l => l.Contains("ciclo peri"));

        // Bases que no se pueden abrir (una carpeta en lugar del fichero).
        var dir = Directory.CreateDirectory(Path.Combine(_host.Folder, "no-es-un-fichero")).FullName;
        device.BrokenWakeLock = false;
        device.Overrides[typeof(LocalTrack)] = new LocalTrack(dir);
        device.Overrides[typeof(LocationOutbox)] = new LocationOutbox(dir);
        device.Overrides[typeof(EventFeed)] = new EventFeed(dir, _host.Phone.Service);
        device.PushConfigured = false;
        await engine.RunCycleAsync();
        Assert.Contains(lines, l => l.Contains("podar el recorrido"));
        Assert.Contains(lines, l => l.Contains("consultar la cola"));
        Assert.Contains(lines, l => l.Contains("eventos nuevos"));
    }

    [Fact]
    public async Task CacheDeGruposQueNoSePuedeGuardarSigueEnMemoria()
    {
        var world = new World(_host.Phone);
        var g = await world.GroupAsync("Casa");
        var broken = new BrokenOnWriteStore();
        SharingState.Store = () => broken;
        var lines = new List<string>();
        NativeLog.Logcat = (_, l) => lines.Add(l);

        Assert.Equal([g.Id], await SharingState.GetSharingGroupsAsync(_host.Phone.Service, true, TimeSpan.Zero));
        Assert.Contains(lines, l => l.Contains("guardar la cach"));
    }

    private sealed class BrokenOnWriteStore : IKeyValueStore
    {
        public bool GetBool(string key, bool fallback) => fallback;
        public string? GetString(string key) => null;
        public void Apply(IReadOnlyDictionary<string, object?> changes) => throw new IOException("lleno");
    }

    // -----------------------------------------------------------------------
    // Autostart, avisos, FCM y registro
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("Xiaomi", "Redmi", "arm64-v8a", Vendor.Xiaomi)]
    [InlineData("unknown", "POCO", "arm64-v8a", Vendor.Xiaomi)]
    [InlineData("HUAWEI", "", "arm64-v8a", Vendor.Huawei)]
    [InlineData("HONOR", "", "arm64-v8a", Vendor.Huawei)]
    [InlineData("OnePlus", "", "arm64-v8a", Vendor.Oppo)]
    [InlineData("realme", "", "arm64-v8a", Vendor.Oppo)]
    [InlineData("vivo", "", "arm64-v8a", Vendor.Vivo)]
    [InlineData("x", "iQOO", "arm64-v8a", Vendor.Vivo)]
    [InlineData("samsung", "", "arm64-v8a", Vendor.Samsung)]
    [InlineData("asus", "", "arm64-v8a", Vendor.Asus)]
    [InlineData("Meizu", "", "arm64-v8a", Vendor.Meizu)]
    [InlineData("Google", "google", "arm64-v8a", Vendor.Other)]
    [InlineData("samsung", "samsung", "x86_64", Vendor.Other)]
    [InlineData(null, null, null, Vendor.Other)]
    public void Fabricante(string? manufacturer, string? brand, string? abi, Vendor expected) =>
        Assert.Equal(expected, Autostart.Detect(manufacturer, brand, abi));

    [Fact]
    public void Autostart_TextoPantallasYAperturaEnOrden()
    {
        CultureInfo.CurrentUICulture = new CultureInfo("es-ES");
        Assert.Contains("Xiaomi", Autostart.Hint(Vendor.Xiaomi));
        Assert.DoesNotContain("Samsung", Autostart.Hint(Vendor.Samsung));
        CultureInfo.CurrentUICulture = new CultureInfo("en-US");
        Assert.StartsWith("Many phones", Autostart.Hint(Vendor.Other));
        Assert.Equal("Sin clave", AndroidTexts.Get("Sin clave"));

        foreach (var v in Enum.GetValues<Vendor>().Where(v => v != Vendor.Other))
            Assert.NotEmpty(Autostart.Screens(v));
        Assert.Empty(Autostart.Screens(Vendor.Other));

        var tried = new List<string>();
        var settings = 0;
        Autostart.Open(Vendor.Huawei, (p, a) => { tried.Add(a); return tried.Count == 2; }, () => settings++);
        Assert.Equal(2, tried.Count);
        Assert.Equal(0, settings);

        tried.Clear();
        Autostart.Open(Vendor.Xiaomi, (p, a) => { tried.Add(a); return false; }, () => settings++);
        Assert.Equal(2, tried.Count);
        Assert.Equal(1, settings);
    }

    [Fact]
    public void Autostart_PermisoYUltimaConocida()
    {
        Assert.Equal(LocationPermissionState.Denied, Autostart.PermissionState(false, true));
        Assert.Equal(LocationPermissionState.WhileInUse, Autostart.PermissionState(true, false));
        Assert.Equal(LocationPermissionState.Always, Autostart.PermissionState(true, true));

        Assert.Null(Autostart.LastKnown(null, [null]));
        var vieja = new LocationReading("network", 1, 1, null, null, Now.AddHours(-2));
        var nueva = new LocationReading("gps", 2, 2, 9, null, Now);
        Assert.Equal((2, 2, 9, Now, true), Autostart.LastKnown(vieja, [null, nueva]));
        Assert.Equal((1, 1, 0, Now.AddHours(-2), true), Autostart.LastKnown(null, [vieja]));
        Assert.Equal(2, Autostart.LastKnown(nueva, [vieja])!.Value.Lat);
    }

    [Fact]
    public void AlarmaSos_EncendidaPorDefectoYSeGuarda()
    {
        Assert.True(SharingState.SosLoud);
        SharingState.SosLoud = false;
        Assert.False(SharingState.SosLoud);
        Assert.Equal(false, _store.Values["sos_loud"]);
        SharingState.SosLoud = true;
        Assert.True(SharingState.SosLoud);

        // Almacen roto o sin almacen: suena (lo seguro en un SOS) y guardar no rompe nada.
        _store.Broken = true;
        Assert.True(SharingState.SosLoud);
        SharingState.SosLoud = false;
        SharingState.Store = () => null;
        Assert.True(SharingState.SosLoud);
    }

    [Fact]
    public void Avisos_CanalIdEnlaceYTextos()
    {
        Assert.Equal("sos", NotificationRules.ChannelFor("sos"));
        Assert.Equal("zones", NotificationRules.ChannelFor("zones"));
        Assert.Equal("requests", NotificationRules.ChannelFor("otro"));
        Assert.Equal("requests", NotificationRules.ChannelFor(null));

        var id = Guid.NewGuid();
        Assert.Equal(NotificationRules.NotificationIdFor(id), NotificationRules.NotificationIdFor(id));
        Assert.True(NotificationRules.NotificationIdFor(id) >= 0);
        // Un evento cuyo hash da el id del servicio se aparta.
        var clash = Enumerable.Range(0, 1).Select(_ => GuidWithHash(NotificationRules.ServiceNotificationId)).First();
        Assert.Equal(NotificationRules.ServiceNotificationId + 2, NotificationRules.NotificationIdFor(clash));
        var alarmClash = GuidWithHash(NotificationRules.SosAlarmNotificationId);
        Assert.Equal(NotificationRules.SosAlarmNotificationId + 2, NotificationRules.NotificationIdFor(alarmClash));
        Assert.Equal("familytogether://sos", NotificationRules.SosLink);
        Assert.True(NotificationRules.IsAppLink(NotificationRules.SosLink));

        var group = Guid.NewGuid();
        var content = new NotificationContent("sos", "sos", group, id, "Casa", "Ana", null, null, 40.5, -3.25);
        Assert.Equal($"familytogether://event?type=sos&group={group:D}&event={id:D}&lat=40.5&lon=-3.25", NotificationRules.EventLink(content));
        Assert.DoesNotContain("lat=", NotificationRules.EventLink(content with { Lat = null }));
        Assert.Contains("type=&", NotificationRules.EventLink(content with { EventType = null! }));

        Assert.True(NotificationRules.IsAppLink("FamilyTogether://join?c=1"));
        Assert.False(NotificationRules.IsAppLink("https://x"));
        Assert.False(NotificationRules.IsAppLink(" "));
        Assert.False(NotificationRules.IsAppLink(null));

        Loc.SetPreference(Loc.Spanish);
        Loc.Apply();
        Assert.Equal(Loc.Get("ChannelSos"), NotificationRules.Text("ChannelSos", "SOS"));
        Assert.Equal("reserva", NotificationRules.Text("NoExiste", "reserva"));
    }

    private static Guid GuidWithHash(int hash)
    {
        // Guid.GetHashCode = a ^ ((b << 16) | c) ^ (f << 24 | k): con todo a cero menos a, el hash es a.
        return new Guid(hash, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
    }

    [Fact]
    public async Task Fcm_ClavesSolicitudResueltaYEventos()
    {
        var world = new World(_host.Phone);
        var g = await world.GroupAsync("Casa");
        var notifier = new FakeNotifier();

        await PushMessages.HandleAsync(null, _host.Phone.Service, _host.Feed, () => notifier);
        await PushMessages.HandleAsync(new Dictionary<string, string>(), _host.Phone.Service, _host.Feed, () => notifier);
        await PushMessages.HandleAsync(new Dictionary<string, string> { ["type"] = "sos", ["group_id"] = "x" }, _host.Phone.Service, _host.Feed, () => notifier);
        await PushMessages.HandleAsync(new Dictionary<string, string> { ["type"] = "sos", ["group_id"] = g.Id.ToString(), ["event_id"] = "x" }, _host.Phone.Service, _host.Feed, () => notifier);
        Assert.Empty(_host.Server.Requests);

        await PushMessages.HandleAsync(new Dictionary<string, string> { ["type"] = "key_share" }, _host.Phone.Service, _host.Feed, () => notifier);
        Assert.Contains(_host.Server.Requests, r => r.Path.Contains("key_share"));
        await PushMessages.HandleAsync(new Dictionary<string, string> { ["type"] = "key_share" }, null, _host.Feed, () => notifier);

        // Un SOS de Ana que se puede abrir: sale el aviso.
        var ana = Guid.NewGuid();
        g.Members.Add(new World.MemberData(ana, "Ana", false));
        var sos = Guid.NewGuid();
        _host.Server.Table("sos_alerts", _ => FakeSupabase.Json(FakeSupabase.Serialize(new[] { new { id = sos, user_id = ana, created_at = DateTimeOffset.UtcNow } })));
        _host.Server.Table("sos_targets", _ => FakeSupabase.Json(FakeSupabase.Serialize(new[]
        {
            new { sos_id = sos, group_id = g.Id, payload_enc = Crypto.Encrypt(Payloads.Sos(40.5, -3.5, 10, DateTimeOffset.UtcNow, false), g.Key) },
        })));
        await PushMessages.HandleAsync(new Dictionary<string, string> { ["type"] = EventTypes.Sos, ["group_id"] = g.Id.ToString(), ["event_id"] = sos.ToString() },
            _host.Phone.Service, _host.Feed, () => notifier);
        Assert.Equal(sos, Assert.Single(notifier.Shown).EventId);
        notifier.Shown.Clear();

        var ev = Guid.NewGuid();
        var data = new Dictionary<string, string> { ["type"] = EventTypes.RequestResolved, ["group_id"] = g.Id.ToString(), ["event_id"] = ev.ToString() };
        await PushMessages.HandleAsync(data, _host.Phone.Service, null, () => notifier);
        Assert.Equal([ev], notifier.Cancelled);

        // El evento no existe en el servidor: no hay aviso.
        await PushMessages.HandleAsync(data, _host.Phone.Service, _host.Feed, () => notifier);
        Assert.Empty(notifier.Shown);
    }

    [Fact]
    public async Task Fcm_TokenNuevo()
    {
        await PushMessages.RegisterTokenAsync(null, "t");
        await PushMessages.RegisterTokenAsync(_host.Phone.Service, " ");
        Assert.Empty(_host.Server.Requests);
        await PushMessages.RegisterTokenAsync(_host.Phone.Service, "token");
        Assert.NotEmpty(_host.Server.Requests);
    }

    [Fact]
    public void Registro_NuncaLanzaAunqueFalleElLogcat()
    {
        var lines = new List<(string, string)>();
        NativeLog.Logcat = (lvl, l) => lines.Add((lvl, l));
        NativeLog.Info("hola");
        NativeLog.Warn("cuidado", new InvalidOperationException("x"));
        NativeLog.Error("mal", new InvalidOperationException("y"));
        NativeLog.Error("mal sin traza");
        Assert.Equal(["INFO", "WARN", "ERROR", "ERROR"], lines.Select(l => l.Item1));
        Assert.Contains("InvalidOperationException", lines[1].Item2);

        NativeLog.Logcat = (_, _) => throw new InvalidOperationException("sin logcat");
        NativeLog.Info("sigue");
    }
}
