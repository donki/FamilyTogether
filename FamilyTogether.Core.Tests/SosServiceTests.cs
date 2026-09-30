using System.Net;
using FamilyTogether.Core;

namespace FamilyTogether.Core.Tests;

/// <summary>SOS: se guarda, se envia cifrado por grupo y, sin red, se reintenta sin duplicar.</summary>
public class SosServiceTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"fl-sos-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SQLite.SQLiteAsyncConnection.ResetPool();
        try { File.Delete(_db); } catch (IOException) { }
    }

    private static readonly DateTimeOffset At = new(2026, 9, 27, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task EnviaUnDestinoCifradoPorGrupoYAvisa()
    {
        var phone = new Phone();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var sinClave = Guid.NewGuid();
        var ka = await phone.WithKeyAsync(a);
        var kb = await phone.WithKeyAsync(b);
        var sos = new SosService(_db, phone.Service);

        var id = await sos.SendAsync([a, b, a, sinClave], (40.5, -3.5, 12, At, false));

        var call = phone.Server.RpcCall("create_sos");
        Assert.Equal(id.ToString(), call.Str("p_id"));
        var targets = call.Json.GetProperty("p_targets").EnumerateArray().ToList();
        Assert.Equal(2, targets.Count);   // repetido y sin clave, fuera
        var data = Payloads.Open<Payloads.SosData>(targets.Single(t => t.GetProperty("group_id").GetGuid() == b).GetProperty("payload_enc").GetString(), kb)!;
        Assert.Equal((40.5, -3.5, 12.0, "2026-09-27T10:00:00Z", false), (data.Lat, data.Lon, data.Acc, data.At, data.Stale));
        Assert.NotNull(Payloads.Open<Payloads.SosData>(targets.Single(t => t.GetProperty("group_id").GetGuid() == a).GetProperty("payload_enc").GetString(), ka));
        var notify = Assert.Single(phone.Server.Notifies);
        Assert.Equal(("sos", id.ToString()), (notify.Str("type"), notify.Str("id")));
        Assert.False(sos.HasPending);
    }

    [Fact]
    public async Task SinGruposNoSeAcepta()
    {
        var sos = new SosService(_db, new Phone().Service);
        await Assert.ThrowsAsync<ArgumentException>(() => sos.SendAsync([], (1, 2, 3, At, false)));
    }

    [Fact]
    public async Task SinRedQuedaPendienteSobreviveAlReinicioYSeReenviaConElMismoId()
    {
        var phone = new Phone();
        var g = Guid.NewGuid();
        await phone.WithKeyAsync(g);
        var offline = true;
        phone.Server.Rpc("create_sos", _ => offline ? throw new HttpRequestException("sin red") : FakeSupabase.Json(""));
        var sos = new SosService(_db, phone.Service);
        var changes = 0;
        sos.PendingChanged += (_, _) => changes++;

        var id = await sos.SendAsync([g], (1, 2, 3, At, true));

        Assert.True(sos.HasPending);
        Assert.Equal(1, changes);

        // Reinicio del movil: otra instancia ve lo pendiente desde el principio.
        var again = new SosService(_db, phone.Service);
        Assert.True(again.HasPending);

        await again.RetryPendingAsync();   // sigue sin red
        Assert.True(again.HasPending);

        offline = false;
        await again.RetryPendingAsync();
        Assert.False(again.HasPending);
        var ids = phone.Server.To("POST", "/rest/v1/rpc/create_sos").Select(r => r.Str("p_id")).Distinct();
        Assert.Equal([id.ToString()], ids);   // siempre el mismo id: el servidor no duplica
    }

    [Fact]
    public async Task ErrorDelServidorTambienSeReintenta()
    {
        var phone = new Phone();
        var g = Guid.NewGuid();
        await phone.WithKeyAsync(g);
        phone.Server.Rpc("create_sos", _ => FakeSupabase.Status(HttpStatusCode.ServiceUnavailable, "caido"));
        var sos = new SosService(_db, phone.Service);

        await sos.SendAsync([g], (1, 2, 3, At, false));

        Assert.True(sos.HasPending);
    }

    [Fact]
    public async Task RechazoQueNoCambiaReintentandoSeAbandona()
    {
        var phone = new Phone();
        var g = Guid.NewGuid();
        await phone.WithKeyAsync(g);
        phone.Server.Rpc("create_sos", _ => FakeSupabase.Json("""{"code":"P0001","message":"not_member"}""", HttpStatusCode.BadRequest));
        var sos = new SosService(_db, phone.Service);

        await sos.SendAsync([g], (1, 2, 3, At, false));

        Assert.False(sos.HasPending);
        Assert.Empty(phone.Server.Notifies);
    }

    [Fact]
    public async Task SinClaveDeNingunGrupoNoSeEnviaNiQuedaPendiente()
    {
        var phone = new Phone();
        var sos = new SosService(_db, phone.Service);

        await sos.SendAsync([Guid.NewGuid()], (1, 2, 3, At, false));

        Assert.DoesNotContain(phone.Server.Requests, r => r.Path.Contains("create_sos"));
        Assert.False(sos.HasPending);
    }

    [Fact]
    public async Task AvisoConErrorDaElSosPorEnviadoPeroSinRedNo()
    {
        var phone = new Phone();
        var g = Guid.NewGuid();
        await phone.WithKeyAsync(g);
        phone.Server.On("POST", "/functions/v1/notify", _ => FakeSupabase.Status(HttpStatusCode.InternalServerError, "fcm"));
        var sos = new SosService(_db, phone.Service);

        await sos.SendAsync([g], (1, 2, 3, At, false));
        Assert.False(sos.HasPending);

        phone.Server.On("POST", "/functions/v1/notify", _ => throw new HttpRequestException("sin red"));
        await sos.SendAsync([g], (1, 2, 3, At, false));
        Assert.True(sos.HasPending);
    }

    [Fact]
    public async Task AvisoRechazadoPorSesionNoDejaElSosPendiente()
    {
        var phone = new Phone();
        var g = Guid.NewGuid();
        await phone.WithKeyAsync(g);
        phone.Server.On("POST", "/auth/v1/token", _ => FakeSupabase.Status(HttpStatusCode.BadRequest, "revocado"));
        phone.Server.On("POST", "/functions/v1/notify", _ => FakeSupabase.Status(HttpStatusCode.Unauthorized));
        var sos = new SosService(_db, phone.Service);

        await sos.SendAsync([g], (1, 2, 3, At, false));

        Assert.False(sos.HasPending);
    }

    [Fact]
    public async Task ReintentarSinPendientesNoHaceNada()
    {
        var phone = new Phone();
        await new SosService(_db, phone.Service).RetryPendingAsync();
        Assert.Empty(phone.Server.Requests);
    }
}
