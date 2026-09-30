using System.Net;
using System.Text;
using System.Text.Json;
using FamilyTogether.Core;

namespace FamilyTogether.Core.Tests;

/// <summary>Una peticion tal y como la recibio el servidor falso.</summary>
internal sealed record SeenRequest(string Method, string Path, string Query, string Body, string? Bearer, string? Prefer)
{
    /// <summary>El cuerpo como JSON (las RPC y los insert mandan JSON).</summary>
    public JsonElement Json => JsonDocument.Parse(Body).RootElement.Clone();

    public string Str(string property) => Json.GetProperty(property).GetString()!;
}

/// <summary>
/// Supabase falso para las pruebas: responde por metodo + ruta con lo que se le programe y apunta
/// todo lo que recibe. Lo no programado responde <c>200 []</c> (una tabla vacia, una RPC sin nada).
/// </summary>
internal sealed class FakeSupabase : HttpMessageHandler
{
    public const string Url = "https://prueba.local";

    private readonly List<(string Method, string Path, Func<SeenRequest, HttpResponseMessage> Respond)> _routes = [];

    public List<SeenRequest> Requests { get; } = [];

    /// <summary>Programa una respuesta. Lo programado despues gana a lo de antes.</summary>
    public FakeSupabase On(string method, string path, Func<SeenRequest, HttpResponseMessage> respond)
    {
        _routes.Insert(0, (method, path, respond));
        return this;
    }

    public FakeSupabase On(string method, string path, string json) => On(method, path, _ => Json(json));

    public FakeSupabase Rpc(string function, string json) => On("POST", "/rest/v1/rpc/" + function, json);

    public FakeSupabase Rpc(string function, Func<SeenRequest, HttpResponseMessage> respond) =>
        On("POST", "/rest/v1/rpc/" + function, respond);

    public FakeSupabase Table(string table, Func<SeenRequest, HttpResponseMessage> respond) =>
        On("GET", "/rest/v1/" + table, respond);

    /// <summary>Una tabla que devuelve siempre este JSON (ya serializado).</summary>
    public FakeSupabase Table(string table, string json) => Table(table, _ => Json(json));

    public IEnumerable<SeenRequest> To(string method, string path) =>
        Requests.Where(r => r.Method == method && r.Path == path);

    public SeenRequest RpcCall(string function) => Assert.Single(To("POST", "/rest/v1/rpc/" + function));

    public IEnumerable<SeenRequest> Notifies => To("POST", "/functions/v1/notify");

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        var seen = new SeenRequest(
            request.Method.Method,
            request.RequestUri!.AbsolutePath,
            Uri.UnescapeDataString(request.RequestUri.Query.TrimStart('?')),
            body,
            request.Headers.Authorization?.Parameter,
            request.Headers.TryGetValues("Prefer", out var p) ? string.Join(",", p) : null);
        Requests.Add(seen);

        foreach (var (method, path, respond) in _routes)
        {
            if (method == seen.Method && path == seen.Path)
                return respond(seen);
        }

        return Json("[]");
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Status(HttpStatusCode status, string body = "") =>
        new(status) { Content = new StringContent(body) };

    /// <summary>Filas en snake_case, como las devuelve PostgREST.</summary>
    public static string Serialize(object rows) => JsonSerializer.Serialize(rows, SupabaseClient.Json);

    public static string Session(string access, string refresh, Guid user, int expiresIn = 3600) =>
        $$$"""{"access_token":"{{{access}}}","refresh_token":"{{{refresh}}}","expires_in":{{{expiresIn}}},"user":{"id":"{{{user}}}"}}""";
}

/// <summary>Almacen seguro y de tokens en memoria.</summary>
internal sealed class TestStore : ISecureStore, ITokenStore
{
    public Dictionary<string, string> Values { get; } = [];

    public Task<string?> GetAsync(string key) => Task.FromResult(Values.TryGetValue(key, out var v) ? v : null);

    public Task SetAsync(string key, string value)
    {
        Values[key] = value;
        return Task.CompletedTask;
    }

    Task ITokenStore.SetAsync(string key, string? value)
    {
        if (value is null)
            Values.Remove(key);
        else
            Values[key] = value;
        return Task.CompletedTask;
    }

    public void Remove(string key) => Values.Remove(key);

    /// <summary>Con una sesion ya guardada y valida una hora: no hace falta signup.</summary>
    public static TestStore SignedIn(Guid user, string access = "acceso")
    {
        var store = new TestStore();
        store.Values["fl.access_token"] = access;
        store.Values["fl.refresh_token"] = "refresco";
        store.Values["fl.user_id"] = user.ToString("D");
        store.Values["fl.expires_at"] = DateTimeOffset.UtcNow.AddHours(1).ToString("O");
        return store;
    }
}

/// <summary>Un movil: su servidor falso, su almacen y sus servicios.</summary>
internal sealed class Phone
{
    public Phone(Guid? user = null)
    {
        Me = user ?? Guid.NewGuid();
        Server = new FakeSupabase();
        Store = TestStore.SignedIn(Me);
        Client = new SupabaseClient(new HttpClient(Server), Store, FakeSupabase.Url, "clave");
        Keys = new GroupKeyStore(Store);
        Service = new FamilyService(Client, Keys, Store);
    }

    public Guid Me { get; }
    public FakeSupabase Server { get; }
    public TestStore Store { get; }
    public SupabaseClient Client { get; }
    public GroupKeyStore Keys { get; }
    public FamilyService Service { get; }

    /// <summary>Soy miembro de estos grupos (<c>group_members</c> filtrado por mi user_id).</summary>
    public Phone MemberOf(params (Guid Group, bool Admin)[] groups)
    {
        Server.Table("group_members", r => r.Query.Contains($"user_id=eq.{Me:D}") && !r.Query.Contains("group_id=eq.")
            ? FakeSupabase.Json(FakeSupabase.Serialize(groups.Select(g => new
            {
                group_id = g.Group,
                user_id = Me,
                role = g.Admin ? "admin" : "member",
                paused = false,
            })))
            : FakeSupabase.Json("[]"));
        return this;
    }

    public async Task<byte[]> WithKeyAsync(Guid group)
    {
        var key = Crypto.NewGroupKey();
        await Keys.SetAsync(group, key);
        return key;
    }
}
