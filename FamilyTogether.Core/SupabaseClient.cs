using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace FamilyTogether.Core;

/// <summary>
/// Cliente de Supabase por <see cref="HttpClient"/>: GoTrue (<c>/auth/v1</c>), PostgREST
/// (<c>/rest/v1</c>) y Edge Functions (<c>/functions/v1</c>). Sin SDK, como Task Manager.
/// </summary>
/// <remarks>
/// <para><b>Usuario anonimo.</b> El primer arranque hace <c>POST /auth/v1/signup {}</c> y la sesion
/// (access, refresh, user id) se guarda en <see cref="ITokenStore"/>. Nada mas: no hay login.</para>
///
/// <para><b>Renueva solo.</b> Antes de cada llamada, si al token le quedan menos de dos minutos, se
/// renueva; y si aun asi el servidor responde 401 (reloj adelantado, token revocado) se renueva
/// una vez y se repite. Si el token de refresco tampoco vale —el usuario se recupero en otro movil
/// y este quedo borrado, §2— se lanza <c>unauthorized</c> y <b>no</b> se crea otro usuario a
/// escondidas: eso lo decide la app (<see cref="ResetSessionAsync"/>).</para>
///
/// <para><b>Errores.</b> Los de negocio llegan de PostgREST como <c>code = P0001</c> con la clave en
/// <c>message</c> y salen como <see cref="FamilyTogetherException"/> con esa clave. Sin red (o sin
/// respuesta a tiempo) → <c>network</c>; 401/403 → <c>unauthorized</c>; lo demas → <c>server</c>.</para>
/// </remarks>
public sealed class SupabaseClient
{
    private const string KeyAccess = "fl.access_token";
    private const string KeyRefresh = "fl.refresh_token";
    private const string KeyExpires = "fl.expires_at";
    private const string KeyUser = "fl.user_id";

    /// <summary>
    /// Columnas en snake_case: <c>GroupId</c> ↔ <c>group_id</c>. Los nulos se escriben: un
    /// argumento de RPC a null tiene que llegar como null, no desaparecer.
    /// </summary>
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;
    private readonly ITokenStore _tokens;
    private readonly string _baseUrl;
    private readonly string _apiKey;
    private readonly SemaphoreSlim _sessionLock = new(1, 1);

    private string? _access;
    private string? _refresh;
    private DateTimeOffset _expiresAt;
    private bool _loaded;

    public SupabaseClient(HttpClient http, ITokenStore tokens)
        : this(http, tokens, FamilyTogetherConfig.SupabaseUrl, FamilyTogetherConfig.PublishableKey)
    {
    }

    /// <summary>Con otro proyecto (pruebas, o uno de desarrollo elegido en tiempo de ejecucion).</summary>
    public SupabaseClient(HttpClient http, ITokenStore tokens, string url, string publishableKey)
    {
        _http = http;
        _tokens = tokens;
        _baseUrl = url.TrimEnd('/');
        _apiKey = publishableKey;
    }

    public bool IsConfigured => _baseUrl.Length > 0 && _apiKey.Length > 0;

    /// <summary>El <c>auth.uid()</c> de la sesion, en cuanto se conoce (tras <see cref="EnsureSignedInAsync"/>).</summary>
    public string? UserId { get; private set; }

    /// <summary>El mismo, como Guid. Lanza si todavia no hay sesion.</summary>
    public Guid UserGuid =>
        Guid.TryParse(UserId, out var id)
            ? id
            : throw new FamilyTogetherException(FamilyTogetherException.Unauthorized, "Sin sesion.");

    // -----------------------------------------------------------------------
    // Sesion
    // -----------------------------------------------------------------------

    /// <summary>Crea el usuario anonimo si no hay sesion guardada. Devuelve su id.</summary>
    public async Task<string> EnsureSignedInAsync(CancellationToken cancellationToken = default)
    {
        await LoadAsync().ConfigureAwait(false);
        if (UserId is { Length: > 0 } && !string.IsNullOrEmpty(_refresh))
            return UserId;

        EnsureConfigured();

        await _sessionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (UserId is { Length: > 0 } && !string.IsNullOrEmpty(_refresh))
                return UserId;

            using var response = await SendRawAsync(
                () => Request(HttpMethod.Post, "/auth/v1/signup", "{}", bearer: null),
                cancellationToken).ConfigureAwait(false);

            await StoreSessionAsync(response, cancellationToken).ConfigureAwait(false);
            return UserId ?? throw new FamilyTogetherException(FamilyTogetherException.Server, "signup sin usuario");
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    /// <summary>
    /// Tira la sesion guardada. La siguiente llamada crea un usuario anonimo nuevo, vacio. Es lo que
    /// hace la app cuando este movil quedo huerfano tras recuperar la cuenta en otro.
    /// </summary>
    public async Task ResetSessionAsync()
    {
        await _sessionLock.WaitAsync().ConfigureAwait(false);
        try
        {
            _access = _refresh = UserId = null;
            _expiresAt = DateTimeOffset.MinValue;
            _loaded = true;
            await _tokens.SetAsync(KeyAccess, null).ConfigureAwait(false);
            await _tokens.SetAsync(KeyRefresh, null).ConfigureAwait(false);
            await _tokens.SetAsync(KeyExpires, null).ConfigureAwait(false);
            await _tokens.SetAsync(KeyUser, null).ConfigureAwait(false);
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    private async Task LoadAsync()
    {
        if (_loaded)
            return;

        _access = await _tokens.GetAsync(KeyAccess).ConfigureAwait(false);
        _refresh = await _tokens.GetAsync(KeyRefresh).ConfigureAwait(false);
        UserId = await _tokens.GetAsync(KeyUser).ConfigureAwait(false);
        _expiresAt = DateTimeOffset.TryParse(
            await _tokens.GetAsync(KeyExpires).ConfigureAwait(false),
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : DateTimeOffset.MinValue;
        _loaded = true;
    }

    /// <summary>Token valido para la siguiente llamada; lo renueva si caduca en menos de 2 min.</summary>
    private async Task<string> AccessTokenAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        await EnsureSignedInAsync(cancellationToken).ConfigureAwait(false);

        if (!forceRefresh && !string.IsNullOrEmpty(_access) && _expiresAt > DateTimeOffset.UtcNow.AddMinutes(2))
            return _access;

        var stale = _access;
        await _sessionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Otro hilo pudo renovarlo mientras se esperaba el cerrojo.
            if (_access != stale && !string.IsNullOrEmpty(_access))
                return _access;

            if (string.IsNullOrEmpty(_refresh))
                throw new FamilyTogetherException(FamilyTogetherException.Unauthorized, "Sin token de refresco.");

            var body = JsonSerializer.Serialize(new { refresh_token = _refresh });
            using var response = await SendRawAsync(
                () => Request(HttpMethod.Post, "/auth/v1/token?grant_type=refresh_token", body, bearer: null),
                cancellationToken).ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                // El refresco ya no vale: usuario borrado (recuperado en otro movil) o sesion revocada.
                var detail = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                throw new FamilyTogetherException(FamilyTogetherException.Unauthorized, $"Refresco rechazado: {detail}");
            }

            await StoreSessionAsync(response, cancellationToken).ConfigureAwait(false);
            return _access ?? throw new FamilyTogetherException(FamilyTogetherException.Unauthorized, "Sin token.");
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    private async Task StoreSessionAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw ErrorFrom(response.StatusCode, body);

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            _access = root.GetProperty("access_token").GetString();
            if (root.TryGetProperty("refresh_token", out var r) && r.GetString() is { Length: > 0 } refresh)
                _refresh = refresh;

            var expiresIn = root.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var seconds) ? seconds : 3600;
            _expiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn);

            if (root.TryGetProperty("user", out var user)
                && user.ValueKind == JsonValueKind.Object
                && user.TryGetProperty("id", out var id)
                && id.GetString() is { Length: > 0 } userId)
            {
                UserId = userId;
            }
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new FamilyTogetherException(FamilyTogetherException.Server, "Respuesta de sesion ilegible.", ex);
        }

        await _tokens.SetAsync(KeyAccess, _access).ConfigureAwait(false);
        await _tokens.SetAsync(KeyRefresh, _refresh).ConfigureAwait(false);
        await _tokens.SetAsync(KeyExpires, _expiresAt.ToString("O", CultureInfo.InvariantCulture)).ConfigureAwait(false);
        await _tokens.SetAsync(KeyUser, UserId).ConfigureAwait(false);
    }

    // -----------------------------------------------------------------------
    // PostgREST
    // -----------------------------------------------------------------------

    public async Task<T?> RpcAsync<T>(string function, object args, CancellationToken cancellationToken = default)
    {
        var body = await SendCheckedAsync(HttpMethod.Post, $"/rest/v1/rpc/{function}", Serialize(args), null, cancellationToken)
            .ConfigureAwait(false);
        return Deserialize<T>(body);
    }

    public Task RpcAsync(string function, object args, CancellationToken cancellationToken = default) =>
        SendCheckedAsync(HttpMethod.Post, $"/rest/v1/rpc/{function}", Serialize(args), null, cancellationToken);

    /// <summary>GET de una tabla. <paramref name="query"/> estilo PostgREST: <c>select=*&amp;group_id=eq.X</c>.</summary>
    public async Task<List<T>> SelectAsync<T>(string table, string query, CancellationToken cancellationToken = default)
    {
        var body = await SendCheckedAsync(HttpMethod.Get, $"/rest/v1/{table}?{query}", null, null, cancellationToken)
            .ConfigureAwait(false);
        return Deserialize<List<T>>(body) ?? [];
    }

    /// <summary>Inserta una fila o una lista de filas (un array se manda en una sola peticion).</summary>
    public Task InsertAsync(string table, object row, CancellationToken cancellationToken = default) =>
        SendCheckedAsync(HttpMethod.Post, $"/rest/v1/{table}", Serialize(row), "return=minimal", cancellationToken);

    public Task UpsertAsync(string table, object row, string onConflict, CancellationToken cancellationToken = default) =>
        SendCheckedAsync(
            HttpMethod.Post,
            $"/rest/v1/{table}?on_conflict={Uri.EscapeDataString(onConflict)}",
            Serialize(row),
            "resolution=merge-duplicates,return=minimal",
            cancellationToken);

    /// <summary>PATCH de las filas que cumplan <paramref name="query"/>. Devuelve cuantas cambio.</summary>
    public async Task<int> UpdateAsync(string table, string query, object values, CancellationToken cancellationToken = default)
    {
        var body = await SendCheckedAsync(
            HttpMethod.Patch, $"/rest/v1/{table}?{query}", Serialize(values), "return=representation", cancellationToken)
            .ConfigureAwait(false);
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "[]" : body);
        return document.RootElement.ValueKind == JsonValueKind.Array ? document.RootElement.GetArrayLength() : 0;
    }

    public Task DeleteAsync(string table, string query, CancellationToken cancellationToken = default) =>
        SendCheckedAsync(HttpMethod.Delete, $"/rest/v1/{table}?{query}", null, "return=minimal", cancellationToken);

    // -----------------------------------------------------------------------
    // Edge Functions
    // -----------------------------------------------------------------------

    /// <summary>
    /// Llama a una Edge Function con el JWT del usuario. Devuelve la respuesta tal cual (el que
    /// llama mira el estado); solo la falta de red y la sesion invalida se convierten en excepcion.
    /// </summary>
    public async Task<HttpResponseMessage> InvokeFunctionAsync(string name, object body, CancellationToken cancellationToken = default)
    {
        var json = Serialize(body);
        return await SendAuthorizedAsync(HttpMethod.Post, $"/functions/v1/{name}", json, null, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Clave de error de la respuesta de una Edge Function (<c>{"error":"already_linked"}</c>), o
    /// <c>server</c> si no trae ninguna.
    /// </summary>
    public static async Task<FamilyTogetherException> FunctionErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken = default)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.String
                && error.GetString() is { Length: > 0 } key)
            {
                return new FamilyTogetherException(key, key);
            }
        }
        catch (JsonException)
        {
        }

        return response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            ? new FamilyTogetherException(FamilyTogetherException.Unauthorized, body)
            : new FamilyTogetherException(FamilyTogetherException.Server, $"{(int)response.StatusCode}: {body}");
    }

    // -----------------------------------------------------------------------
    // Transporte
    // -----------------------------------------------------------------------

    private async Task<string> SendCheckedAsync(HttpMethod method, string path, string? json, string? prefer, CancellationToken cancellationToken)
    {
        using var response = await SendAuthorizedAsync(method, path, json, prefer, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw ErrorFrom(response.StatusCode, body);

        return body;
    }

    /// <summary>Con el token del usuario; si responde 401 se renueva una vez y se repite.</summary>
    private async Task<HttpResponseMessage> SendAuthorizedAsync(HttpMethod method, string path, string? json, string? prefer, CancellationToken cancellationToken)
    {
        EnsureConfigured();

        var token = await AccessTokenAsync(false, cancellationToken).ConfigureAwait(false);
        var response = await SendRawAsync(() => Request(method, path, json, token, prefer), cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
            return response;

        response.Dispose();
        token = await AccessTokenAsync(true, cancellationToken).ConfigureAwait(false);
        return await SendRawAsync(() => Request(method, path, json, token, prefer), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Envia y convierte la falta de red (o de respuesta a tiempo) en <c>network</c>.</summary>
    private async Task<HttpResponseMessage> SendRawAsync(Func<HttpRequestMessage> build, CancellationToken cancellationToken)
    {
        using var request = build();
        try
        {
            return await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new FamilyTogetherException(FamilyTogetherException.Network, ex.Message, ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // El HttpClient se canso de esperar: para la app es lo mismo que no tener red.
            throw new FamilyTogetherException(FamilyTogetherException.Network, "Tiempo de espera agotado.", ex);
        }
        catch (IOException ex)
        {
            throw new FamilyTogetherException(FamilyTogetherException.Network, ex.Message, ex);
        }
    }

    private HttpRequestMessage Request(HttpMethod method, string path, string? json, string? bearer, string? prefer = null)
    {
        var request = new HttpRequestMessage(method, _baseUrl + path);
        request.Headers.Add("apikey", _apiKey);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer ?? _apiKey);
        if (prefer is not null)
            request.Headers.Add("Prefer", prefer);
        if (json is not null)
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return request;
    }

    /// <summary>Traduce un error de PostgREST/GoTrue a <see cref="FamilyTogetherException"/>.</summary>
    internal static FamilyTogetherException ErrorFrom(HttpStatusCode status, string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("code", out var code)
                && code.ValueKind == JsonValueKind.String
                && code.GetString() == "P0001"
                && root.TryGetProperty("message", out var message)
                && message.GetString() is { Length: > 0 } key)
            {
                return new FamilyTogetherException(key, key);
            }
        }
        catch (JsonException)
        {
        }

        return status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            ? new FamilyTogetherException(FamilyTogetherException.Unauthorized, $"{(int)status}: {body}")
            : new FamilyTogetherException(FamilyTogetherException.Server, $"{(int)status}: {body}");
    }

    private void EnsureConfigured()
    {
        if (!IsConfigured)
            throw new FamilyTogetherException(FamilyTogetherException.NotConfigured, "Falta familytogether.local.props.");
    }

    private static string Serialize(object value) => JsonSerializer.Serialize(value, value.GetType(), Json);

    private static T? Deserialize<T>(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return default;

        try
        {
            return JsonSerializer.Deserialize<T>(body, Json);
        }
        catch (JsonException ex)
        {
            throw new FamilyTogetherException(FamilyTogetherException.Server, $"Respuesta ilegible: {ex.Message}", ex);
        }
    }
}
