using System.Net;
using FamilyTogether.Core;

namespace FamilyTogether.Core.Tests;

/// <summary>El cliente HTTP de Supabase contra un servidor falso: sesion, renovacion, errores y verbos.</summary>
public class SupabaseClientTests
{
    private static SupabaseClient Client(FakeSupabase server, TestStore store, string url = FakeSupabase.Url, string key = "clave") =>
        new(new HttpClient(server), store, url, key);

    private sealed class Throwing(Exception ex) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw ex;
    }

    [Fact]
    public async Task SinSesionCreaElUsuarioAnonimoYLoGuarda()
    {
        var user = Guid.NewGuid();
        var server = new FakeSupabase().On("POST", "/auth/v1/signup", FakeSupabase.Session("a1", "r1", user));
        var store = new TestStore();
        var client = Client(server, store);

        Assert.Equal(user.ToString("D"), await client.EnsureSignedInAsync());
        Assert.Equal(user, client.UserGuid);

        var signup = Assert.Single(server.Requests);
        Assert.Equal("{}", signup.Body);
        Assert.Equal("clave", signup.Bearer);   // sin sesion, con la clave publicable
        Assert.Equal("a1", store.Values["fl.access_token"]);
        Assert.Equal("r1", store.Values["fl.refresh_token"]);
        Assert.Equal(user.ToString("D"), store.Values["fl.user_id"]);
        Assert.True(DateTimeOffset.Parse(store.Values["fl.expires_at"]) > DateTimeOffset.UtcNow.AddMinutes(50));

        // La segunda vez ya no llama.
        await client.EnsureSignedInAsync();
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task ConSesionGuardadaNoLlamaYUsaSuToken()
    {
        var user = Guid.NewGuid();
        var server = new FakeSupabase();
        var client = Client(server, TestStore.SignedIn(user, "guardado"));

        await client.SelectAsync<object>("groups", "select=id");

        var request = Assert.Single(server.Requests);
        Assert.Equal("/rest/v1/groups", request.Path);
        Assert.Equal("select=id", request.Query);
        Assert.Equal("guardado", request.Bearer);
        Assert.Equal(user.ToString("D"), client.UserId);
    }

    [Fact]
    public async Task SinProyectoConfiguradoLanzaNotConfigured()
    {
        var server = new FakeSupabase();
        var client = Client(server, new TestStore(), url: "", key: "");

        Assert.False(client.IsConfigured);
        var ex = await Assert.ThrowsAsync<FamilyTogetherException>(() => client.EnsureSignedInAsync());
        Assert.Equal(FamilyTogetherException.NotConfigured, ex.Code);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public void UserGuidSinSesionLanzaUnauthorized()
    {
        var client = Client(new FakeSupabase(), new TestStore());
        Assert.Equal(FamilyTogetherException.Unauthorized, Assert.Throws<FamilyTogetherException>(() => client.UserGuid).Code);
    }

    [Fact]
    public async Task TokenAPuntoDeCaducarSeRenuevaAntesDeLlamar()
    {
        var user = Guid.NewGuid();
        var store = TestStore.SignedIn(user, "viejo");
        store.Values["fl.expires_at"] = DateTimeOffset.UtcNow.AddMinutes(1).ToString("O");
        var server = new FakeSupabase()
            .On("POST", "/auth/v1/token", r => FakeSupabase.Json(FakeSupabase.Session("nuevo", "r2", user)));
        var client = Client(server, store);

        await client.RpcAsync("algo", new { });

        Assert.Equal(2, server.Requests.Count);
        Assert.Equal("grant_type=refresh_token", server.Requests[0].Query);
        Assert.Contains("\"refresh_token\":\"refresco\"", server.Requests[0].Body);
        Assert.Equal("nuevo", server.Requests[1].Bearer);
        Assert.Equal("r2", store.Values["fl.refresh_token"]);
    }

    [Fact]
    public async Task RenovacionSinRefreshTokenNuevoConservaElAnterior()
    {
        var user = Guid.NewGuid();
        var store = TestStore.SignedIn(user);
        store.Values["fl.expires_at"] = "no es una fecha";   // se trata como caducado
        var server = new FakeSupabase()
            .On("POST", "/auth/v1/token", """{"access_token":"nuevo"}""");
        var client = Client(server, store);

        await client.DeleteAsync("zones", "id=eq.1");

        Assert.Equal("refresco", store.Values["fl.refresh_token"]);
        Assert.Equal("nuevo", server.Requests[1].Bearer);
        // Sin expires_in se da una hora.
        Assert.True(DateTimeOffset.Parse(store.Values["fl.expires_at"]) > DateTimeOffset.UtcNow.AddMinutes(55));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task RefrescoRechazadoEsUnauthorizedYNoCreaOtroUsuario(HttpStatusCode status)
    {
        var store = TestStore.SignedIn(Guid.NewGuid());
        store.Values["fl.expires_at"] = DateTimeOffset.UtcNow.AddMinutes(-5).ToString("O");
        var server = new FakeSupabase().On("POST", "/auth/v1/token", _ => FakeSupabase.Status(status, "invalid"));
        var client = Client(server, store);

        var ex = await Assert.ThrowsAsync<FamilyTogetherException>(() => client.RpcAsync("x", new { }));

        Assert.Equal(FamilyTogetherException.Unauthorized, ex.Code);
        Assert.DoesNotContain(server.Requests, r => r.Path == "/auth/v1/signup");
    }

    [Fact]
    public async Task Un401RenuevaUnaVezYRepite()
    {
        var user = Guid.NewGuid();
        var server = new FakeSupabase()
            .On("POST", "/auth/v1/token", FakeSupabase.Session("nuevo", "r2", user))
            .Rpc("x", r => r.Bearer == "nuevo" ? FakeSupabase.Json("7") : FakeSupabase.Status(HttpStatusCode.Unauthorized));
        var client = Client(server, TestStore.SignedIn(user, "revocado"));

        Assert.Equal(7, await client.RpcAsync<int>("x", new { }));
        Assert.Equal(["/rest/v1/rpc/x", "/auth/v1/token", "/rest/v1/rpc/x"], server.Requests.Select(r => r.Path));
    }

    [Fact]
    public async Task Un401TrasRenovarEsUnauthorized()
    {
        var user = Guid.NewGuid();
        var server = new FakeSupabase()
            .On("POST", "/auth/v1/token", FakeSupabase.Session("nuevo", "r2", user))
            .Rpc("x", _ => FakeSupabase.Status(HttpStatusCode.Unauthorized, "{}"));
        var client = Client(server, TestStore.SignedIn(user));

        var ex = await Assert.ThrowsAsync<FamilyTogetherException>(() => client.RpcAsync("x", new { }));
        Assert.Equal(FamilyTogetherException.Unauthorized, ex.Code);
        Assert.Equal(3, server.Requests.Count);   // no entra en bucle
    }

    [Fact]
    public async Task ErrorDeNegocioP0001SaleConSuClave()
    {
        var server = new FakeSupabase().Rpc("leave_group",
            _ => FakeSupabase.Json("""{"code":"P0001","message":"last_admin"}""", HttpStatusCode.BadRequest));
        var client = Client(server, TestStore.SignedIn(Guid.NewGuid()));

        var ex = await Assert.ThrowsAsync<FamilyTogetherException>(() => client.RpcAsync("leave_group", new { p_group = Guid.Empty }));
        Assert.Equal("last_admin", ex.Code);
        Assert.False(ex.IsNetwork);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, """{"code":"42501","message":"rls"}""", "unauthorized")]
    [InlineData(HttpStatusCode.InternalServerError, "no es json", "server")]
    [InlineData(HttpStatusCode.Conflict, """{"code":"23505","message":"duplicate"}""", "server")]
    [InlineData(HttpStatusCode.BadRequest, """["P0001"]""", "server")]
    public void ErroresQueNoSonDeNegocio(HttpStatusCode status, string body, string code) =>
        Assert.Equal(code, SupabaseClient.ErrorFrom(status, body).Code);

    [Fact]
    public async Task SinRedEsNetwork()
    {
        var client = new SupabaseClient(new HttpClient(new Throwing(new HttpRequestException("sin red"))),
            TestStore.SignedIn(Guid.NewGuid()), FakeSupabase.Url, "clave");

        var ex = await Assert.ThrowsAsync<FamilyTogetherException>(() => client.RpcAsync("x", new { }));
        Assert.True(ex.IsNetwork);
    }

    [Fact]
    public async Task IOExceptionEsNetwork()
    {
        var client = new SupabaseClient(new HttpClient(new Throwing(new IOException("cortada"))),
            TestStore.SignedIn(Guid.NewGuid()), FakeSupabase.Url, "clave");

        Assert.True((await Assert.ThrowsAsync<FamilyTogetherException>(() => client.SelectAsync<object>("t", "q"))).IsNetwork);
    }

    [Fact]
    public async Task TiempoAgotadoEsNetworkPeroCancelarNo()
    {
        var timeout = new SupabaseClient(new HttpClient(new Throwing(new TaskCanceledException("timeout"))),
            TestStore.SignedIn(Guid.NewGuid()), FakeSupabase.Url, "clave");
        Assert.True((await Assert.ThrowsAsync<FamilyTogetherException>(() => timeout.RpcAsync("x", new { }))).IsNetwork);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var cancelled = new SupabaseClient(new HttpClient(new Throwing(new TaskCanceledException("cancel"))),
            TestStore.SignedIn(Guid.NewGuid()), FakeSupabase.Url, "clave");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.RpcAsync("x", new { }, cts.Token));
    }

    [Fact]
    public async Task SelectLeeSnakeCaseYVacioEsListaVacia()
    {
        var g = Guid.NewGuid();
        var server = new FakeSupabase()
            .Table("groups", _ => FakeSupabase.Json($$"""[{"id":"{{g}}","name_enc":"enc1:x"}]"""))
            .Table("vacia", _ => FakeSupabase.Json(""));
        var client = Client(server, TestStore.SignedIn(Guid.NewGuid()));

        var rows = await client.SelectAsync<GroupRow>("groups", "select=*");
        Assert.Equal(g, Assert.Single(rows).Id);
        Assert.Equal("enc1:x", rows[0].NameEnc);
        Assert.Empty(await client.SelectAsync<GroupRow>("vacia", "select=*"));
    }

    [Fact]
    public async Task RespuestaIlegibleEsServer()
    {
        var server = new FakeSupabase().Table("groups", _ => FakeSupabase.Json("{roto"));
        var client = Client(server, TestStore.SignedIn(Guid.NewGuid()));

        Assert.Equal(FamilyTogetherException.Server,
            (await Assert.ThrowsAsync<FamilyTogetherException>(() => client.SelectAsync<GroupRow>("groups", "q"))).Code);
    }

    [Fact]
    public async Task RpcSinCuerpoDevuelveElValorPorDefecto()
    {
        var server = new FakeSupabase().Rpc("nada", _ => FakeSupabase.Status(HttpStatusCode.NoContent));
        var client = Client(server, TestStore.SignedIn(Guid.NewGuid()));

        Assert.Equal(Guid.Empty, await client.RpcAsync<Guid>("nada", new { }));
    }

    [Fact]
    public async Task LosNulosDeUnaRpcViajanComoNull()
    {
        var server = new FakeSupabase();
        var client = Client(server, TestStore.SignedIn(Guid.NewGuid()));

        await client.RpcAsync("set_pause", new { p_until = (DateTimeOffset?)null });

        Assert.Equal("""{"p_until":null}""", server.Requests[0].Body);
    }

    [Fact]
    public async Task InsertUpsertUpdateYDeleteUsanElVerboYLasCabecerasDePostgrest()
    {
        var server = new FakeSupabase()
            .On("PATCH", "/rest/v1/zones", """[{"id":1},{"id":2}]""")
            .On("PATCH", "/rest/v1/nada", "");
        var client = Client(server, TestStore.SignedIn(Guid.NewGuid()));

        await client.InsertAsync("positions", new[] { new { a = 1 } });
        await client.UpsertAsync("zone_subscriptions", new { a = 1 }, "observer_id,target_id");
        Assert.Equal(2, await client.UpdateAsync("zones", "id=eq.1", new { name_enc = "x" }));
        Assert.Equal(0, await client.UpdateAsync("nada", "id=eq.1", new { name_enc = "x" }));
        await client.DeleteAsync("zones", "id=eq.1");

        Assert.Equal(("POST", "return=minimal", """[{"a":1}]"""), (server.Requests[0].Method, server.Requests[0].Prefer, server.Requests[0].Body));
        Assert.Equal("on_conflict=observer_id,target_id", server.Requests[1].Query);
        Assert.Equal("resolution=merge-duplicates,return=minimal", server.Requests[1].Prefer);
        Assert.Equal(("PATCH", "return=representation"), (server.Requests[2].Method, server.Requests[2].Prefer));
        Assert.Equal(("DELETE", "id=eq.1"), (server.Requests[4].Method, server.Requests[4].Query));
    }

    [Fact]
    public async Task ResetSessionBorraLaSesionYLaSiguienteLlamadaCreaOtroUsuario()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var store = TestStore.SignedIn(first);
        var server = new FakeSupabase().On("POST", "/auth/v1/signup", FakeSupabase.Session("a", "r", second));
        var client = Client(server, store);
        await client.EnsureSignedInAsync();

        await client.ResetSessionAsync();

        Assert.Null(client.UserId);
        Assert.Empty(store.Values);
        Assert.Equal(second.ToString("D"), await client.EnsureSignedInAsync());
    }

    [Theory]
    [InlineData("""{"access_token":"a","refresh_token":"r"}""")]   // sin usuario
    [InlineData("""{"refresh_token":"r","user":{"id":"x"}}""")]      // sin access_token
    [InlineData("no es json")]
    public async Task SesionIlegibleEsServer(string body)
    {
        var server = new FakeSupabase().On("POST", "/auth/v1/signup", body);
        var client = Client(server, new TestStore());

        Assert.Equal(FamilyTogetherException.Server,
            (await Assert.ThrowsAsync<FamilyTogetherException>(() => client.EnsureSignedInAsync())).Code);
    }

    [Fact]
    public async Task SignupRechazadoTraduceElError()
    {
        var server = new FakeSupabase().On("POST", "/auth/v1/signup", _ => FakeSupabase.Status(HttpStatusCode.TooManyRequests, "rate"));
        var client = Client(server, new TestStore());

        Assert.Equal(FamilyTogetherException.Server,
            (await Assert.ThrowsAsync<FamilyTogetherException>(() => client.EnsureSignedInAsync())).Code);
    }

    [Fact]
    public async Task InvokeFunctionDevuelveLaRespuestaTalCual()
    {
        var server = new FakeSupabase().On("POST", "/functions/v1/link-account",
            _ => FakeSupabase.Json("""{"error":"already_linked"}""", HttpStatusCode.Conflict));
        var client = Client(server, TestStore.SignedIn(Guid.NewGuid(), "tok"));

        using var response = await client.InvokeFunctionAsync("link-account", new { provider = "google" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("tok", server.Requests[0].Bearer);
        Assert.Equal("already_linked", (await SupabaseClient.FunctionErrorAsync(response)).Code);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "no json", "unauthorized")]
    [InlineData(HttpStatusCode.Forbidden, """{"error":""}""", "unauthorized")]
    [InlineData(HttpStatusCode.InternalServerError, """{"error":42}""", "server")]
    [InlineData(HttpStatusCode.BadGateway, "[]", "server")]
    public async Task FunctionErrorSinClaveUsaElEstado(HttpStatusCode status, string body, string code)
    {
        using var response = FakeSupabase.Status(status, body);
        Assert.Equal(code, (await SupabaseClient.FunctionErrorAsync(response)).Code);
    }
}
