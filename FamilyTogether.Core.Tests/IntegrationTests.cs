using FamilyTogether.Core;

namespace FamilyTogether.Core.Tests;

/// <summary>
/// Prueba de extremo a extremo contra el proyecto de Supabase de verdad, con el núcleo de la app y
/// tres usuarios anónimos. Solo corre con <c>FT_INTEGRATION=1</c> (usa la red y crea filas); los
/// datos de conexión salen de la configuración generada (<c>familytogether.local.props</c>).
/// </summary>
public class IntegrationTests
{
    private sealed class MemoryStore : ISecureStore, ITokenStore
    {
        private readonly Dictionary<string, string> _d = new();
        public Task<string?> GetAsync(string key) => Task.FromResult(_d.TryGetValue(key, out var v) ? v : null);
        public Task SetAsync(string key, string value) { _d[key] = value; return Task.CompletedTask; }
        Task ITokenStore.SetAsync(string key, string? value)
        {
            if (value is null) _d.Remove(key); else _d[key] = value;
            return Task.CompletedTask;
        }
        public void Remove(string key) => _d.Remove(key);
    }

    private static FamilyService NewUser()
    {
        var store = new MemoryStore();
        var client = new SupabaseClient(new HttpClient(), store);
        return new FamilyService(client, new GroupKeyStore(store), store);
    }

    private static bool Enabled => Environment.GetEnvironmentVariable("FT_INTEGRATION") == "1"
                                   && FamilyTogetherConfig.IsServerConfigured;

    /// <summary>
    /// La Edge Function <c>notify</c> entra en FCM con la cuenta de servicio: con un token inventado,
    /// Google tiene que responder que el token no vale (y eso solo pasa si antes aceptó las credenciales).
    /// </summary>
    [Fact]
    public async Task Sos_llega_a_FCM_con_la_cuenta_de_servicio()
    {
        if (!Enabled)
            return;

        var a = NewUser();
        var b = NewUser();
        await a.Client.EnsureSignedInAsync();
        await b.Client.EnsureSignedInAsync();
        var group = await a.CreateGroupAsync("FCM " + DateTime.UtcNow.ToString("HHmmss"), "Ana", null);
        var request = await b.RequestJoinAsync((await a.CreateInvitationAsync(group.Id)).Code, "Blas");
        await a.ApproveAsync(Assert.Single(await a.GetPendingRequestsAsync(group.Id)));
        Assert.Equal(JoinState.Approved, await b.CheckMyRequestAsync(request));
        await b.RegisterPushTokenAsync("token-inventado-para-la-prueba-" + Guid.NewGuid().ToString("N"));

        var sos = Guid.NewGuid();
        await a.Client.RpcAsync("create_sos", new
        {
            p_id = sos,
            p_targets = new[] { new { group_id = group.Id, payload_enc = "enc1:prueba" } },
        });
        using var response = await a.Client.InvokeFunctionAsync("notify", new { type = "sos", id = sos });
        var body = await response.Content.ReadAsStringAsync();
        Console.WriteLine($"notify: {(int)response.StatusCode} {body}");
        Assert.True(response.IsSuccessStatusCode, body);
        Assert.DoesNotContain("fcm_not_configured", body);
        Assert.DoesNotContain("auth", body, StringComparison.OrdinalIgnoreCase);

        await b.LeaveGroupAsync(group.Id);
        await a.LeaveGroupAsync(group.Id);
    }

    [Fact]
    public async Task Ciclo_completo_contra_el_servidor()
    {
        if (!Enabled)
            return;

        var a = NewUser();
        var b = NewUser();
        var c = NewUser();
        await a.Client.EnsureSignedInAsync();
        await b.Client.EnsureSignedInAsync();
        await c.Client.EnsureSignedInAsync();

        // A crea el grupo e invita.
        var group = await a.CreateGroupAsync("Prueba " + DateTime.UtcNow.ToString("HHmmss"), "Ana", null);
        Assert.True(group.IAmAdmin);
        var invitation = await a.CreateInvitationAsync(group.Id);
        Assert.True(invitation.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(4));

        // B pide entrar con el QR; A ve la solicitud con el nombre descifrado y la aprueba.
        var request = await b.RequestJoinAsync(invitation.QrPayload, "Blas");
        var pending = await a.GetPendingRequestsAsync(group.Id);
        var mine = Assert.Single(pending, r => r.Id == request);
        Assert.Equal("Blas", mine.Name);
        await a.ApproveAsync(mine);

        // A B le llega la clave del grupo y ve el grupo con su nombre.
        Assert.Equal(JoinState.Approved, await b.CheckMyRequestAsync(request));
        var bGroups = await b.GetGroupsAsync();
        var bGroup = Assert.Single(bGroups, g => g.Id == group.Id);
        Assert.False(bGroup.KeyMissing);
        Assert.Equal(group.Name, bGroup.Name);

        // B envía una posición; A la ve descifrada.
        var db = Path.Combine(Path.GetTempPath(), $"ft-it-{Guid.NewGuid():N}.db");
        var outbox = new LocationOutbox(db);
        await outbox.EnqueueAsync(40.4168, -3.7038, 8, 77, DateTimeOffset.UtcNow, [group.Id]);
        Assert.Equal(1, await outbox.FlushAsync(b));
        var positions = await a.GetLastPositionsAsync(group.Id);
        var pos = Assert.Single(positions, p => p.UserId == b.Client.UserGuid);
        Assert.Equal(40.4168, pos.Lat, 4);
        Assert.Equal(77, pos.Battery);

        // Los dos se ven como miembros con sus nombres.
        var members = await a.GetMembersAsync(group.Id);
        Assert.Contains(members, m => m.Name == "Blas");

        // C, de fuera, no ve nada del grupo.
        Assert.DoesNotContain(await c.GetGroupsAsync(), g => g.Id == group.Id);
        Assert.Empty(await c.Client.SelectAsync<Dictionary<string, object>>("positions", $"select=id&group_id=eq.{group.Id}"));
        Assert.Empty(await c.Client.SelectAsync<Dictionary<string, object>>("group_members", $"select=user_id&group_id=eq.{group.Id}"));

        // Pausa: B pausa y A deja de ver su posición.
        await b.SetPauseAsync(group.Id, true, null);
        Assert.DoesNotContain(await a.GetLastPositionsAsync(group.Id), p => p.UserId == b.Client.UserGuid);

        // Limpieza: B se va y A (único miembro) se va, lo que borra el grupo.
        await b.LeaveGroupAsync(group.Id);
        await a.LeaveGroupAsync(group.Id);
        Assert.DoesNotContain(await a.GetGroupsAsync(), g => g.Id == group.Id);
    }
}
