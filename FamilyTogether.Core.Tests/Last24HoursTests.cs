using FamilyTogether.Core;

namespace FamilyTogether.Core.Tests;

/// <summary>
/// «Ultimas 24 horas» del Historial (2026-10-01): la consulta por tramo que cruza la medianoche, la
/// copia local de mi recorrido y su caducidad, el primer tramo al echar a andar y la cola.
/// </summary>
public class Last24HoursTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"fl-24h-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SQLite.SQLiteAsyncConnection.ResetPool();
        try { File.Delete(_db); } catch (IOException) { }
    }

    private static string Rows(object rows) => FakeSupabase.Serialize(rows);

    // -----------------------------------------------------------------------
    // Consulta del servidor por tramo
    // -----------------------------------------------------------------------

    [Fact]
    public void TramoDeLasUltimas24HorasConMargenDeReloj()
    {
        var now = new DateTimeOffset(2026, 10, 1, 8, 30, 0, TimeSpan.FromHours(2));
        var (from, to) = FamilyService.RecentRange(now);

        Assert.Equal(now.AddHours(-24), from);
        Assert.Equal(now + FamilyService.ClockSlack, to);
        Assert.Equal(TimeSpan.FromHours(24), FamilyService.RecentWindow);
    }

    [Fact]
    public async Task Ultimas24HorasCruzanLaMedianocheYSalenOrdenadas()
    {
        var group = Guid.NewGuid();
        var user = Guid.NewGuid();
        var phone = new Phone();
        var key = await phone.WithKeyAsync(group);
        var now = new DateTimeOffset(2026, 10, 1, 6, 0, 0, TimeSpan.Zero);   // 08:00 en Madrid
        var (from, to) = FamilyService.RecentRange(now);

        object Row(DateTimeOffset at, double lat) =>
            new { user_id = user, recorded_at = at, battery = 50, payload_enc = Crypto.Encrypt(Payloads.Position(lat, -3.7, 6), key) };

        // Desordenadas a proposito, de ayer por la tarde a esta manana, mas dos fuera del tramo
        // (el servidor de verdad no las mandaria, pero se filtran igual).
        phone.Server.Table("positions", Rows(new[]
        {
            Row(now.AddHours(-1), 40.43),             // hoy 07:00 local
            Row(now.AddHours(-20), 40.41),            // ayer 12:00 local
            Row(now.AddHours(-7), 40.42),             // hoy 01:00 local, tras la medianoche
            Row(now.AddHours(-24), 40.40),            // justo el limite: entra
            Row(now.AddHours(-24).AddSeconds(-1), 0), // un segundo antes: fuera
            Row(to, 0),                               // el final es exclusivo: fuera
        }));

        var history = await phone.Service.GetHistoryAsync(group, user, from, to);

        Assert.Equal([40.40, 40.41, 40.42, 40.43], history.Select(p => p.Lat));
        var call = Assert.Single(phone.Server.To("GET", "/rest/v1/positions"));
        Assert.Contains("recorded_at=gte.2026-09-30T06:00:00.000Z", call.Query);
        Assert.Contains("recorded_at=lt.2026-10-01T06:05:00.000Z", call.Query);
        Assert.Contains("coarse=is.false", call.Query);
        Assert.Contains("order=recorded_at.asc", call.Query);
    }

    [Fact]
    public async Task TramoVacioOAlReves_NoPreguntaAlServidor()
    {
        var phone = new Phone();
        var at = DateTimeOffset.UtcNow;
        Assert.Empty(await phone.Service.GetHistoryAsync(Guid.NewGuid(), Guid.NewGuid(), at, at));
        Assert.Empty(await phone.Service.GetHistoryAsync(Guid.NewGuid(), Guid.NewGuid(), at, at.AddHours(-1)));
        Assert.Empty(phone.Server.Requests);
    }

    [Fact]
    public async Task Ultimas24HorasPaginanDeMilEnMil()
    {
        var group = Guid.NewGuid();
        var user = Guid.NewGuid();
        var phone = new Phone();
        var key = await phone.WithKeyAsync(group);
        var now = DateTimeOffset.UtcNow;
        var (from, to) = FamilyService.RecentRange(now);
        var payload = Crypto.Encrypt(Payloads.Position(40, -3, 5), key);

        object Row(DateTimeOffset at) => new { user_id = user, recorded_at = at, battery = 50, payload_enc = payload };
        var page1 = Enumerable.Range(0, 1000).Select(i => Row(from.AddSeconds(30 * i))).ToList();
        var page2 = new[] { Row(now.AddMinutes(-1)) };
        phone.Server.Table("positions", r => FakeSupabase.Json(Rows(r.Query.Contains("offset=0") ? page1 : page2)));

        var history = await phone.Service.GetHistoryAsync(group, user, from, to);

        Assert.Equal(1001, history.Count);
        Assert.Equal(2, phone.Server.To("GET", "/rest/v1/positions").Count());
    }

    // -----------------------------------------------------------------------
    // Copia local de mi recorrido
    // -----------------------------------------------------------------------

    private sealed class Clock
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);
    }

    [Fact]
    public async Task GuardaPorGrupoYDevuelveElTramoOrdenado()
    {
        var clock = new Clock();
        var track = new LocalTrack(_db, () => clock.Now);
        var me = Guid.NewGuid();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        await track.AddAsync(40.2, -3.7, 8, clock.Now.AddHours(-2), [a]);
        await track.AddAsync(40.1, -3.7, 8, clock.Now.AddHours(-10), [a, b]);   // ayer por la noche
        await track.AddAsync(40.3, -3.7, 8, clock.Now.AddHours(-1), [b]);       // en pausa en A

        var (from, to) = FamilyService.RecentRange(clock.Now);
        var forA = await track.GetAsync(me, a, from, to);
        Assert.Equal([40.1, 40.2], forA.Select(p => p.Lat));
        Assert.All(forA, p => Assert.Equal(me, p.UserId));
        Assert.Equal([40.1, 40.3], (await track.GetAsync(me, b, from, to)).Select(p => p.Lat));
        Assert.Empty(await track.GetAsync(me, Guid.NewGuid(), from, to));

        // Limites: el inicio entra, el final no.
        var exact = clock.Now.AddHours(-2);
        Assert.Single(await track.GetAsync(me, a, exact, exact.AddTicks(1)));
        Assert.Empty(await track.GetAsync(me, a, exact.AddTicks(-1), exact));
    }

    [Fact]
    public async Task NoGuardaSinGruposNiCoordenadasMalasNiLoCaducado()
    {
        var clock = new Clock();
        var track = new LocalTrack(_db, () => clock.Now);
        var a = Guid.NewGuid();

        await track.AddAsync(40, -3, 8, clock.Now, []);
        await track.AddAsync(double.NaN, -3, 8, clock.Now, [a]);
        await track.AddAsync(40, double.NaN, 8, clock.Now, [a]);
        await track.AddAsync(40, -3, 8, clock.Now - LocalTrack.Keep - TimeSpan.FromSeconds(1), [a]);

        Assert.Equal(0, await track.CountAsync());
    }

    [Fact]
    public async Task CaducaALas24Horas()
    {
        var clock = new Clock();
        var track = new LocalTrack(_db, () => clock.Now);
        var me = Guid.NewGuid();
        var a = Guid.NewGuid();
        await track.AddAsync(40.1, -3, 8, clock.Now.AddHours(-23), [a]);
        await track.AddAsync(40.2, -3, 8, clock.Now.AddHours(-1), [a]);

        clock.Now = clock.Now.AddHours(2);   // la primera ya tiene 25 h
        Assert.Equal(1, await track.PruneAsync());
        Assert.Equal(1, await track.CountAsync());

        clock.Now = clock.Now.AddHours(30);
        var (from, to) = FamilyService.RecentRange(clock.Now.AddDays(-2));
        Assert.Empty(await track.GetAsync(me, a, from, to));   // consultar tambien poda
        Assert.Equal(0, await track.CountAsync());
    }

    [Fact]
    public async Task BorrarMiHistorialBorraLaCopiaLocal()
    {
        var track = new LocalTrack(_db);
        await track.AddAsync(40, -3, 8, DateTimeOffset.UtcNow, [Guid.NewGuid()]);
        Assert.Equal(1, await track.CountAsync());

        await track.ClearAsync();
        Assert.Equal(0, await track.CountAsync());
    }

    [Fact]
    public void JuntarServidorYMovilSinRepetirYOrdenado()
    {
        var me = Guid.NewGuid();
        var t0 = new DateTimeOffset(2026, 9, 30, 23, 50, 0, TimeSpan.Zero);
        var server = new[]
        {
            new MemberPosition(me, 40.3, -3, 6, 70, t0.AddMinutes(20)),
            new MemberPosition(me, 40.1, -3, 6, 71, t0),
        };
        var local = new[]
        {
            new MemberPosition(me, 40.1, -3, 6, -1, t0.AddMilliseconds(400)),   // la misma lectura
            new MemberPosition(me, 40.2, -3, 6, -1, t0.AddMinutes(10)),          // aun en la cola
            new MemberPosition(me, 40.4, -3, 6, -1, t0.AddMinutes(30)),
        };

        var merged = LocalTrack.Merge(server, local);

        Assert.Equal([40.1, 40.2, 40.3, 40.4], merged.Select(p => p.Lat));
        Assert.Equal(71, merged[0].Battery);   // gana la del servidor
        Assert.Equal([40.1, 40.3], LocalTrack.Merge(server, []).Select(p => p.Lat));
        Assert.Equal([40.1, 40.2, 40.4], LocalTrack.Merge([], local).Select(p => p.Lat));
    }

    // -----------------------------------------------------------------------
    // Cola: lo que se encola es lo que se guarda en local
    // -----------------------------------------------------------------------

    [Fact]
    public async Task LaColaDiceSiEncolo()
    {
        var a = Guid.NewGuid();
        var outbox = new LocationOutbox(_db);
        var at = DateTimeOffset.UtcNow;

        Assert.True(await outbox.EnqueueAsync(40.0, -3.0, 8, 50, at, [a]));
        Assert.False(await outbox.EnqueueAsync(40.0001, -3.0, 8, 50, at.AddSeconds(30), [a]));   // a 11 m
        Assert.False(await outbox.EnqueueAsync(40.0, -3.0, 8, 50, at, []));
        Assert.True(await outbox.EnqueueAsync(40.001, -3.0, 8, 50, at.AddMinutes(1), [a]));      // a 111 m
        // Una aproximada justo despues de una buena no entra, y no corta: la siguiente buena sale.
        Assert.False(await outbox.EnqueueAsync(40.01, -3.0, 60, 50, at.AddMinutes(2), [a], coarse: true));
        Assert.True(await outbox.EnqueueAsync(40.0015, -3.0, 8, 50, at.AddMinutes(3), [a]));     // a 55 m de la buena
        Assert.Equal(3, await outbox.PendingCountAsync());
    }

    [Fact]
    public async Task SinConexionLasPosicionesEsperanConSuHoraYLuegoSalenTodas()
    {
        var group = Guid.NewGuid();
        var phone = new Phone();
        await phone.WithKeyAsync(group);
        var outbox = new LocationOutbox(_db);
        var start = new DateTimeOffset(2026, 9, 30, 23, 55, 0, TimeSpan.Zero);
        for (var i = 0; i < 5; i++)
            Assert.True(await outbox.EnqueueAsync(40.0 + i * 0.001, -3.7, 8, 60, start.AddMinutes(2 * i), [group]));

        phone.Server.On("POST", "/rest/v1/positions", _ => throw new HttpRequestException("sin red"));
        Assert.Equal(0, await outbox.FlushAsync(phone.Service));
        Assert.Equal(5, await outbox.PendingCountAsync());

        phone.Server.On("POST", "/rest/v1/positions", _ => FakeSupabase.Status(System.Net.HttpStatusCode.Created));
        Assert.Equal(5, await outbox.FlushAsync(phone.Service));
        Assert.Equal(0, await outbox.PendingCountAsync());

        var sent = phone.Server.To("POST", "/rest/v1/positions").Last().Json.EnumerateArray().ToList();
        Assert.Equal(5, sent.Count);
        Assert.Equal(start.UtcDateTime, sent[0].GetProperty("recorded_at").GetDateTimeOffset().UtcDateTime);
        Assert.All(sent, r => Assert.StartsWith("enc", r.GetProperty("payload_enc").GetString()));   // cifradas
        Assert.All(sent, r => Assert.False(r.GetProperty("coarse").GetBoolean()));
    }

    // -----------------------------------------------------------------------
    // Al echar a andar
    // -----------------------------------------------------------------------

    private static readonly DateTimeOffset T = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    private static TrackPoint P(double minutes, double acc = 8) => new(40.4 + minutes / 1000, -3.7, acc, T.AddMinutes(minutes));

    [Fact]
    public void AlSaltarElSensorDevuelveLasDeLosCincoMinutosAnteriores()
    {
        var buffer = new MotionStartBuffer();
        buffer.Hold("gps", P(0));      // deriva de estar en casa, de hace rato
        buffer.Hold("gps", P(7));
        buffer.Hold("gps", P(8));
        buffer.Hold("gps", P(9));

        var released = buffer.Release(T.AddMinutes(10));

        Assert.Equal([T.AddMinutes(7), T.AddMinutes(8), T.AddMinutes(9)], released.Select(p => p.At));
        Assert.Equal(0, buffer.Count);
        Assert.Empty(buffer.Release(T.AddMinutes(11)));   // una sola vez
    }

    [Fact]
    public void SoloGuardaGpsBuenoYUnMaximo()
    {
        var buffer = new MotionStartBuffer();
        buffer.Hold("network", P(1));
        buffer.Hold("gps", P(1, acc: 40));
        buffer.Hold("gps", P(1, acc: 0));
        buffer.Hold(null, P(1));
        Assert.Equal(0, buffer.Count);

        for (var i = 0; i < 25; i++)
            buffer.Hold("GPS", P(i * 0.1));
        Assert.Equal(MotionStartBuffer.Capacity, buffer.Count);

        var released = buffer.Release(T.AddMinutes(3));
        Assert.Equal(MotionStartBuffer.Capacity, released.Count);
        Assert.Equal(T.AddMinutes(2.4), released[^1].At, TimeSpan.FromMilliseconds(1));   // las mas nuevas
        Assert.True(released.Zip(released.Skip(1)).All(x => x.First.At < x.Second.At));

        buffer.Hold("gps", P(1));
        buffer.Clear();
        Assert.Equal(0, buffer.Count);
    }

    [Fact]
    public void LasPosterioresAlAvisoNoSeDevuelven()
    {
        var buffer = new MotionStartBuffer();
        buffer.Hold("gps", P(1));
        buffer.Hold("gps", P(3));
        Assert.Equal([T.AddMinutes(1)], buffer.Release(T.AddMinutes(2)).Select(p => p.At));
    }
}
