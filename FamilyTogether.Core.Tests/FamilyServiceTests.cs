using System.Net;
using System.Text.Json;
using FamilyTogether.Core;

namespace FamilyTogether.Core.Tests;

/// <summary>FamilyService contra un Supabase falso: que manda cifrado, que descifra y como reacciona.</summary>
public class FamilyServiceTests
{
    private static string Rows(object rows) => FakeSupabase.Serialize(rows);

    // -----------------------------------------------------------------------
    // Grupos
    // -----------------------------------------------------------------------

    [Fact]
    public async Task CrearGrupoCifraTodoYGuardaLaClave()
    {
        var phone = new Phone();
        var id = Guid.NewGuid();
        phone.Server.Rpc("create_group", $"\"{id}\"");

        var group = await phone.Service.CreateGroupAsync("Familia", "Ana", "avatar64");

        Assert.Equal(new Group(id, "Familia", true, false), group);
        var key = await phone.Keys.GetAsync(id);
        Assert.NotNull(key);

        var call = phone.Server.RpcCall("create_group");
        Assert.Equal("Familia", Crypto.DecryptOr(call.Str("p_name_enc"), key, "x"));
        Assert.Equal("Ana", Crypto.DecryptOr(call.Str("p_display_name_enc"), key, "x"));
        Assert.Equal("avatar64", Crypto.DecryptOr(call.Str("p_avatar_enc"), key, "x"));
        Assert.DoesNotContain("Familia", call.Body);
        Assert.Equal(("Ana", "avatar64"), await phone.Service.ProfileAsync());
    }

    [Fact]
    public async Task CrearGrupoSinAvatarMandaNullYLoBorraDelPerfil()
    {
        var phone = new Phone();
        phone.Store.Values["profile_avatar"] = "viejo";
        phone.Server.Rpc("create_group", $"\"{Guid.NewGuid()}\"");

        await phone.Service.CreateGroupAsync("G", "Ana", null);

        Assert.Equal(JsonValueKind.Null, phone.Server.RpcCall("create_group").Json.GetProperty("p_avatar_enc").ValueKind);
        Assert.Equal(("Ana", (string?)null), await phone.Service.ProfileAsync());
    }

    [Fact]
    public async Task GruposDescifradosOrdenadosYSinClaveComoInterrogacion()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var sinClave = Guid.NewGuid();
        var otraClave = Guid.NewGuid();
        var phone = new Phone().MemberOf((a, true), (b, false), (sinClave, false), (otraClave, false));
        var ka = await phone.WithKeyAsync(a);
        var kb = await phone.WithKeyAsync(b);
        await phone.WithKeyAsync(otraClave);
        phone.Server.Table("groups", Rows(new object[]
        {
            new { id = a, name_enc = Crypto.Encrypt("zeta", ka) },
            new { id = b, name_enc = Crypto.Encrypt("Alfa", kb) },
            new { id = sinClave, name_enc = Crypto.Encrypt("secreto", Crypto.NewGroupKey()) },
            new { id = otraClave, name_enc = Crypto.Encrypt("otro", Crypto.NewGroupKey()) },
        }));

        var groups = await phone.Service.GetGroupsAsync();

        Assert.Equal(["?", "?", "Alfa", "zeta"], groups.Select(g => g.Name));
        Assert.True(groups.Single(g => g.Id == a).IAmAdmin);
        Assert.False(groups.Single(g => g.Id == b).KeyMissing);
        Assert.True(groups.Single(g => g.Id == sinClave).KeyMissing);
        Assert.True(groups.Single(g => g.Id == otraClave).KeyMissing);   // clave que no abre el nombre
        var select = Assert.Single(phone.Server.To("GET", "/rest/v1/groups"));
        Assert.Contains($"id=in.({a:D},{b:D},{sinClave:D},{otraClave:D})", select.Query);
    }

    [Fact]
    public async Task SinGruposNoPreguntaPorEllos()
    {
        var phone = new Phone().MemberOf();
        Assert.Empty(await phone.Service.GetGroupsAsync());
        Assert.Empty(phone.Server.To("GET", "/rest/v1/groups"));
    }

    // -----------------------------------------------------------------------
    // Invitaciones y solicitudes: el flujo entero entre dos moviles
    // -----------------------------------------------------------------------

    [Fact]
    public async Task InvitarPedirAprobarYEntrar()
    {
        var group = Guid.NewGuid();
        var admin = new Phone().MemberOf((group, true));
        var groupKey = await admin.WithKeyAsync(group);
        var joiner = new Phone();

        // 1. El admin crea la invitacion.
        admin.Server.Rpc("create_invitation", Rows(new[] { new { code = "ABCD2345", group_id = group, expires_at = DateTimeOffset.UtcNow.AddDays(1) } }));
        var invitation = await admin.Service.CreateInvitationAsync(group);
        Assert.Equal("ABCD2345", invitation.Code);
        Assert.Equal(InviteCodes.ToQrPayload("ABCD2345"), invitation.QrPayload);
        var inv = admin.Server.RpcCall("create_invitation");
        var invPub = inv.Str("p_inv_pub");
        var invPrivEnc = inv.Str("p_inv_priv_enc");
        Assert.True(Crypto.TryDecrypt(invPrivEnc, groupKey, out _));   // la privada viaja cifrada con la clave del grupo

        // 2. El otro movil pide entrar leyendo el QR.
        var requestId = Guid.NewGuid();
        joiner.Server.Rpc("invitation_info", Rows(new[] { new { code = "ABCD2345", group_id = group, inv_pub = invPub } }));
        joiner.Server.Rpc("request_join", $"\"{requestId}\"");
        Assert.Equal(requestId, await joiner.Service.RequestJoinAsync(invitation.QrPayload, "Blas"));
        var join = joiner.Server.RpcCall("request_join");
        Assert.Equal("ABCD2345", join.Str("p_code"));
        Assert.Equal([requestId], await joiner.Service.GetMyPendingRequestIdsAsync());
        Assert.Equal("join_request", Assert.Single(joiner.Server.Notifies).Str("type"));

        // 3. El admin ve la solicitud con el nombre descifrado.
        var row = new
        {
            id = requestId,
            group_id = group,
            user_id = joiner.Me,
            invitation_code = "ABCD2345",
            req_pub = join.Str("p_req_pub"),
            inv_pub = invPub,
            inv_priv_enc = invPrivEnc,
            name_box = join.Str("p_name_box"),
            status = "pending",
            created_at = DateTimeOffset.UtcNow,
        };
        admin.Server.Table("join_requests", Rows(new[] { row }));
        var pending = Assert.Single(await admin.Service.GetPendingRequestsAsync(group));
        Assert.Equal("Blas", pending.Name);

        // 4. Aprueba: la clave va en key_box.
        await admin.Service.ApproveAsync(pending);
        var keyBox = admin.Server.RpcCall("approve_request").Str("p_key_box");
        Assert.Equal("request_resolved", Assert.Single(admin.Server.Notifies).Str("type"));

        // 5. El que pidio ve la aprobacion, abre la clave y publica su nombre cifrado.
        joiner.Server.Table("join_requests", Rows(new[] { new { id = requestId, group_id = group, status = "approved", key_box = keyBox, inv_pub = invPub } }));
        Assert.Equal(JoinState.Approved, await joiner.Service.CheckMyRequestAsync(requestId));
        Assert.Equal(groupKey, await joiner.Keys.GetAsync(group));
        var update = joiner.Server.RpcCall("update_my_member");
        Assert.Equal("Blas", Crypto.DecryptOr(update.Str("p_display_name_enc"), groupKey, "x"));
        Assert.Empty(await joiner.Service.GetMyPendingRequestIdsAsync());
        Assert.DoesNotContain(joiner.Store.Values.Keys, k => k.StartsWith("req_priv_") || k.StartsWith("req_group_"));
    }

    [Fact]
    public async Task InvitacionSinClaveDelGrupoEsKeyMissing()
    {
        var phone = new Phone();
        var ex = await Assert.ThrowsAsync<FamilyTogetherException>(() => phone.Service.CreateInvitationAsync(Guid.NewGuid()));
        Assert.Equal(FamilyTogetherException.KeyMissing, ex.Code);
        Assert.Empty(phone.Server.Requests);
    }

    [Fact]
    public async Task InvitacionSinCodigoDeVueltaEsServer()
    {
        var phone = new Phone();
        var group = Guid.NewGuid();
        await phone.WithKeyAsync(group);

        var ex = await Assert.ThrowsAsync<FamilyTogetherException>(() => phone.Service.CreateInvitationAsync(group));
        Assert.Equal(FamilyTogetherException.Server, ex.Code);
    }

    [Theory]
    [InlineData("no vale")]
    [InlineData("ABCD2345")]   // valido, pero el servidor no lo conoce
    public async Task PedirEntrarConCodigoMaloONoEncontradoEsNotFound(string code)
    {
        var phone = new Phone();
        var ex = await Assert.ThrowsAsync<FamilyTogetherException>(() => phone.Service.RequestJoinAsync(code, "Blas"));
        Assert.Equal("not_found", ex.Code);
        Assert.Empty(phone.Server.To("POST", "/rest/v1/rpc/request_join"));
    }

    [Fact]
    public async Task PedirEntrarDosVecesNoDuplicaLaPendiente()
    {
        var phone = new Phone();
        var id = Guid.NewGuid();
        var (invPub, _) = Crypto.NewEcdhKeyPair();
        phone.Server.Rpc("invitation_info", Rows(new[] { new { code = "ABCD2345", group_id = Guid.NewGuid(), inv_pub = invPub } }));
        phone.Server.Rpc("request_join", $"\"{id}\"");
        phone.Server.On("POST", "/functions/v1/notify", _ => FakeSupabase.Status(HttpStatusCode.InternalServerError, "fcm"));

        await phone.Service.RequestJoinAsync("abcd-2345", "Blas");
        await phone.Service.RequestJoinAsync("ABCD2345", "Blas");

        Assert.Equal([id], await phone.Service.GetMyPendingRequestIdsAsync());   // y el aviso fallido no rompe nada
    }

    [Theory]
    [InlineData(null)]
    [InlineData("rejected")]
    public async Task SolicitudRechazadaOBorradaSeOlvida(string? status)
    {
        var phone = new Phone();
        var id = Guid.NewGuid();
        phone.Store.Values["pending_join_requests"] = $"{id},{Guid.NewGuid()},basura";
        phone.Store.Values[$"req_priv_{id:D}"] = "priv";
        phone.Server.Table("join_requests", status is null ? "[]" : Rows(new[] { new { id, group_id = Guid.NewGuid(), status } }));

        Assert.Equal(JoinState.Rejected, await phone.Service.CheckMyRequestAsync(id));
        Assert.DoesNotContain(id, await phone.Service.GetMyPendingRequestIdsAsync());
        Assert.Single(await phone.Service.GetMyPendingRequestIdsAsync());
        Assert.False(phone.Store.Values.ContainsKey($"req_priv_{id:D}"));
    }

    [Fact]
    public async Task SolicitudPendienteSigueComoEsta()
    {
        var phone = new Phone();
        var id = Guid.NewGuid();
        phone.Store.Values["pending_join_requests"] = id.ToString();
        phone.Server.Table("join_requests", Rows(new[] { new { id, group_id = Guid.NewGuid(), status = "pending" } }));

        Assert.Equal(JoinState.Pending, await phone.Service.CheckMyRequestAsync(id));
        Assert.Equal([id], await phone.Service.GetMyPendingRequestIdsAsync());
    }

    [Fact]
    public async Task AprobadaSinPoderAbrirLaClaveEntraSinClaveYNoPublicaNombre()
    {
        var phone = new Phone();
        var id = Guid.NewGuid();
        var group = Guid.NewGuid();
        phone.Store.Values["pending_join_requests"] = id.ToString();   // sin req_priv: reinstalacion
        phone.Server.Table("join_requests", Rows(new[] { new { id, group_id = group, status = "approved", key_box = "enc1:x", inv_pub = "y" } }));

        Assert.Equal(JoinState.Approved, await phone.Service.CheckMyRequestAsync(id));
        Assert.Null(await phone.Keys.GetAsync(group));
        Assert.Empty(phone.Server.To("POST", "/rest/v1/rpc/update_my_member"));
        Assert.Empty(await phone.Service.GetMyPendingRequestIdsAsync());
    }

    [Fact]
    public async Task AprobarRequiereClaveFilaYPrivadaLegible()
    {
        var group = Guid.NewGuid();
        var request = new JoinRequest(Guid.NewGuid(), group, "Blas", DateTimeOffset.UtcNow);
        var phone = new Phone();

        Assert.Equal(FamilyTogetherException.KeyMissing,
            (await Assert.ThrowsAsync<FamilyTogetherException>(() => phone.Service.ApproveAsync(request))).Code);

        await phone.WithKeyAsync(group);
        Assert.Equal("not_found",
            (await Assert.ThrowsAsync<FamilyTogetherException>(() => phone.Service.ApproveAsync(request))).Code);

        phone.Server.Table("join_requests", Rows(new[] { new { id = request.Id, group_id = group, inv_priv_enc = "enc1:basura", req_pub = "x" } }));
        Assert.Equal(FamilyTogetherException.KeyMissing,
            (await Assert.ThrowsAsync<FamilyTogetherException>(() => phone.Service.ApproveAsync(request))).Code);
        Assert.Empty(phone.Server.To("POST", "/rest/v1/rpc/approve_request"));
    }

    [Fact]
    public async Task RechazarLlamaYAvisa()
    {
        var phone = new Phone();
        var request = new JoinRequest(Guid.NewGuid(), Guid.NewGuid(), "Blas", DateTimeOffset.UtcNow);

        await phone.Service.RejectAsync(request);

        Assert.Equal(request.Id.ToString(), phone.Server.RpcCall("reject_request").Str("p_request"));
        Assert.Equal(request.Id.ToString(), Assert.Single(phone.Server.Notifies).Str("id"));
    }

    // -----------------------------------------------------------------------
    // Miembros y posiciones
    // -----------------------------------------------------------------------

    [Fact]
    public async Task MiembrosConYoPrimeroPausaEfectivaYAvatar()
    {
        var group = Guid.NewGuid();
        var phone = new Phone();
        var key = await phone.WithKeyAsync(group);
        var until = DateTimeOffset.UtcNow.AddHours(2);
        var otro = Guid.NewGuid();
        var viejo = Guid.NewGuid();
        phone.Server.Table("group_members", Rows(new object[]
        {
            new { group_id = group, user_id = otro, role = "admin", display_name_enc = Crypto.Encrypt("Carla", key), avatar_enc = Crypto.Encrypt("png", key), paused = true, pause_until = until },
            new { group_id = group, user_id = viejo, role = "member", display_name_enc = Crypto.Encrypt("Bea", key), paused = true, pause_until = DateTimeOffset.UtcNow.AddHours(-1) },
            new { group_id = group, user_id = phone.Me, role = "member", display_name_enc = Crypto.Encrypt("Zoe", key), paused = true },
            new { group_id = group, user_id = Guid.NewGuid(), role = "member", display_name_enc = "enc1:roto", avatar_enc = "enc1:roto" },
        }));

        var members = await phone.Service.GetMembersAsync(group);

        Assert.Equal(["Zoe", "?", "Bea", "Carla"], members.Select(m => m.Name));
        var me = members[0];
        Assert.True(me.IsMe);
        Assert.True(me.Paused);           // en pausa sin hora de fin
        Assert.Null(me.PauseUntil);
        var carla = members.Single(m => m.UserId == otro);
        Assert.True(carla.IsAdmin);
        Assert.Equal("png", carla.AvatarBase64);
        Assert.Equal(until.ToUnixTimeSeconds(), carla.PauseUntil!.Value.ToUnixTimeSeconds());
        var bea = members.Single(m => m.UserId == viejo);
        Assert.False(bea.Paused);         // la pausa ya acabo
        Assert.Null(bea.PauseUntil);
        Assert.Null(members[1].AvatarBase64);
    }

    [Fact]
    public async Task UltimasPosicionesDescifradasYLasIlegiblesFuera()
    {
        var group = Guid.NewGuid();
        var phone = new Phone();
        var key = await phone.WithKeyAsync(group);
        var u = Guid.NewGuid();
        var at = new DateTimeOffset(2026, 9, 27, 10, 0, 0, TimeSpan.Zero);
        phone.Server.Table("last_positions", Rows(new object[]
        {
            new { user_id = u, recorded_at = at, battery = 80, payload_enc = Crypto.Encrypt(Payloads.Position(40.5, -3.25, 12), key) },
            new { user_id = Guid.NewGuid(), recorded_at = at, battery = (int?)null, payload_enc = Crypto.Encrypt(Payloads.Position(1, 2, 3), key) },
            new { user_id = Guid.NewGuid(), recorded_at = at, battery = 10, payload_enc = Crypto.Encrypt("no es json", key) },
            new { user_id = Guid.NewGuid(), recorded_at = at, battery = 10, payload_enc = Crypto.Encrypt(Payloads.Position(1, 2, 3), Crypto.NewGroupKey()) },
        }));

        var positions = await phone.Service.GetLastPositionsAsync(group);

        Assert.Equal(2, positions.Count);
        Assert.Equal(new MemberPosition(u, 40.5, -3.25, 12, 80, at), positions[0]);
        Assert.Equal(-1, positions[1].Battery);
    }

    [Fact]
    public async Task HistorialPaginaDeMilEnMilYFiltraPorElDiaLocal()
    {
        var group = Guid.NewGuid();
        var user = Guid.NewGuid();
        var phone = new Phone();
        var key = await phone.WithKeyAsync(group);
        var tz = TimeZoneInfo.CreateCustomTimeZone("mas2", TimeSpan.FromHours(2), "mas2", "mas2");
        var day = new DateOnly(2026, 9, 27);
        var start = new DateTimeOffset(2026, 9, 26, 22, 0, 0, TimeSpan.Zero);   // medianoche local
        var payload = Crypto.Encrypt(Payloads.Position(40, -3, 5), key);

        object Row(DateTimeOffset at) => new { user_id = user, recorded_at = at, battery = 50, payload_enc = payload };
        var page1 = Enumerable.Range(0, 1000).Select(i => Row(start.AddSeconds(i))).ToList();
        var page2 = new[] { Row(start.AddHours(5)), Row(start.AddHours(-1)) };   // la segunda es del dia anterior
        phone.Server.Table("positions", r => FakeSupabase.Json(Rows(r.Query.Contains("offset=0") ? page1 : page2)));

        var history = await phone.Service.GetHistoryAsync(group, user, day, tz);

        Assert.Equal(1001, history.Count);
        var calls = phone.Server.To("GET", "/rest/v1/positions").ToList();
        Assert.Equal(2, calls.Count);
        Assert.Contains("offset=1000", calls[1].Query);
        Assert.Contains("recorded_at=gte.2026-09-26T22:00:00.000Z", calls[0].Query);
        Assert.Contains("recorded_at=lt.2026-09-27T22:00:00.000Z", calls[0].Query);
        Assert.Contains("coarse=is.false", calls[0].Query);
    }

    [Fact]
    public void DiaEnUnaZonaDondeLaMedianocheNoExiste()
    {
        // Cambio de hora a medianoche: el 2026-09-06 pasa de 00:00 a 01:00 (como Chile).
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            new DateTime(2026, 1, 1), new DateTime(2026, 12, 31), TimeSpan.FromHours(1),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 0, 0, 0), 9, 6),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 0, 0, 0), 4, 5));
        var tz = TimeZoneInfo.CreateCustomTimeZone("cl", TimeSpan.FromHours(-4), "cl", "cl", "cl-dst", [rule]);

        var (from, to) = FamilyService.DayRange(new DateOnly(2026, 9, 6), tz);

        Assert.Equal(new DateTimeOffset(2026, 9, 6, 4, 0, 0, TimeSpan.Zero), from);   // la 01:00 local = 00:00 estandar
        Assert.Equal(new DateTimeOffset(2026, 9, 7, 3, 0, 0, TimeSpan.Zero), to);
        Assert.Equal(TimeSpan.FromHours(23), to - from);
    }

    [Fact]
    public void StampEInUsanElFormatoDePostgrest()
    {
        var a = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var b = Guid.Parse("22222222-2222-2222-2222-222222222222");
        Assert.Equal($"in.({a},{b})", FamilyService.In([a, b]));
        Assert.Equal("2026-09-27T08:00:00.123Z",
            FamilyService.Stamp(new DateTimeOffset(2026, 9, 27, 10, 0, 0, 123, TimeSpan.FromHours(2))));
    }

    [Fact]
    public async Task BorrarHistorialDevuelveLasFilas()
    {
        var phone = new Phone();
        phone.Server.Rpc("clear_my_history", "42");
        Assert.Equal(42, await phone.Service.ClearMyHistoryAsync());
    }

    [Fact]
    public async Task RolExpulsarSalirYPausa()
    {
        var group = Guid.NewGuid();
        var user = Guid.NewGuid();
        var phone = new Phone();
        await phone.WithKeyAsync(group);
        var until = new DateTimeOffset(2026, 9, 27, 12, 0, 0, TimeSpan.FromHours(2));

        await phone.Service.SetRoleAsync(group, user, admin: true);
        await phone.Service.SetRoleAsync(group, user, admin: false);
        await phone.Service.RemoveMemberAsync(group, user);
        await phone.Service.SetPauseAsync(group, true, until);
        await phone.Service.SetPauseAsync(group, false, until);
        await phone.Service.LeaveGroupAsync(group);

        var roles = phone.Server.To("POST", "/rest/v1/rpc/set_role").Select(r => r.Str("p_role"));
        Assert.Equal(["admin", "member"], roles);
        Assert.Equal(user.ToString(), phone.Server.RpcCall("remove_member").Str("p_user"));
        var pauses = phone.Server.To("POST", "/rest/v1/rpc/set_pause").ToList();
        Assert.Equal(until.UtcDateTime, pauses[0].Json.GetProperty("p_until").GetDateTimeOffset().UtcDateTime);
        Assert.Equal(JsonValueKind.Null, pauses[1].Json.GetProperty("p_until").ValueKind);   // al quitar la pausa no va hora
        Assert.Null(await phone.Keys.GetAsync(group));   // al salir se olvida la clave
    }

    [Fact]
    public async Task PerfilSoloSePublicaEnLosGruposConClave()
    {
        var conClave = Guid.NewGuid();
        var sinClave = Guid.NewGuid();
        var phone = new Phone().MemberOf((conClave, false), (sinClave, false));
        var key = await phone.WithKeyAsync(conClave);

        await phone.Service.UpdateMyProfileAsync("Nuevo", null);

        var call = phone.Server.RpcCall("update_my_member");
        Assert.Equal(conClave.ToString(), call.Str("p_group"));
        Assert.Equal("Nuevo", Crypto.DecryptOr(call.Str("p_display_name_enc"), key, "x"));
        Assert.Equal("Nuevo", (await phone.Service.ProfileAsync()).Name);
    }

    [Fact]
    public async Task PerfilVacioAlPrincipio()
    {
        Assert.Equal(("", (string?)null), await new Phone().Service.ProfileAsync());
    }

    [Fact]
    public async Task InsertarPosicionesCifraYAcotaLaBateria()
    {
        var group = Guid.NewGuid();
        var phone = new Phone();
        var key = await phone.WithKeyAsync(group);
        var at = new DateTimeOffset(2026, 9, 27, 12, 0, 0, TimeSpan.FromHours(2));

        var n = await phone.Service.InsertPositionsAsync(group, [(40.1, -3.2, 9, 150, at, false), (40.2, -3.3, 60, -5, at, true)], default);

        Assert.Equal(2, n);
        var rows = phone.Server.To("POST", "/rest/v1/positions").Single().Json.EnumerateArray().ToList();
        Assert.Equal(100, rows[0].GetProperty("battery").GetInt32());
        Assert.Equal(0, rows[1].GetProperty("battery").GetInt32());
        Assert.True(rows[1].GetProperty("coarse").GetBoolean());
        Assert.Equal(phone.Me, rows[0].GetProperty("user_id").GetGuid());
        Assert.Equal(TimeSpan.Zero, rows[0].GetProperty("recorded_at").GetDateTimeOffset().Offset);
        var p = Payloads.Open<Payloads.PositionData>(rows[0].GetProperty("payload_enc").GetString(), key)!;
        Assert.Equal((40.1, -3.2, 9.0), (p.Lat, p.Lon, p.Acc));
    }

    [Fact]
    public async Task InsertarPosicionesSinClaveEsKeyMissing()
    {
        var phone = new Phone();
        var ex = await Assert.ThrowsAsync<FamilyTogetherException>(() =>
            phone.Service.InsertPositionsAsync(Guid.NewGuid(), [(1, 2, 3, 4, DateTimeOffset.UtcNow, false)], default));
        Assert.Equal(FamilyTogetherException.KeyMissing, ex.Code);
    }

    [Fact]
    public async Task NombresDeMiembroYDeGrupo()
    {
        var group = Guid.NewGuid();
        var user = Guid.NewGuid();
        var phone = new Phone();

        Assert.Equal("?", await phone.Service.MemberNameAsync(group, user, default));   // sin fila
        Assert.Equal("?", await phone.Service.GroupNameAsync(group, default));

        var key = await phone.WithKeyAsync(group);
        phone.Server.Table("group_members", Rows(new[] { new { group_id = group, user_id = user, display_name_enc = Crypto.Encrypt("Ana", key) } }));
        phone.Server.Table("groups", Rows(new[] { new { id = group, name_enc = Crypto.Encrypt("Casa", key) } }));
        Assert.Equal("Ana", await phone.Service.MemberNameAsync(group, user, default));
        Assert.Equal("Casa", await phone.Service.GroupNameAsync(group, default));
    }

    [Fact]
    public async Task NotifyNoLanzaNiConErrorNiSinRed()
    {
        var phone = new Phone();
        phone.Server.On("POST", "/functions/v1/notify", _ => FakeSupabase.Status(HttpStatusCode.BadGateway, "x"));
        Assert.False(await phone.Service.NotifyAsync("sos", Guid.NewGuid(), default));

        phone.Server.On("POST", "/functions/v1/notify", _ => throw new HttpRequestException("sin red"));
        Assert.False(await phone.Service.NotifyAsync("sos", Guid.NewGuid(), default));

        phone.Server.On("POST", "/functions/v1/notify", "{}");
        Assert.True(await phone.Service.NotifyAsync("sos", Guid.NewGuid(), default));
    }

    [Fact]
    public async Task RegistrarTokenDeAvisos()
    {
        var phone = new Phone();
        await phone.Service.RegisterPushTokenAsync("tok");
        Assert.Equal("tok", phone.Server.RpcCall("register_push_token").Str("p_token"));
    }

    // -----------------------------------------------------------------------
    // Zonas
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ZonasDescifradasOrdenadasYSinCentroFuera()
    {
        var group = Guid.NewGuid();
        var phone = new Phone();
        var key = await phone.WithKeyAsync(group);
        var creator = Guid.NewGuid();
        phone.Server.Table("zones", Rows(new object[]
        {
            new { id = Guid.NewGuid(), group_id = group, name_enc = Crypto.Encrypt("Colegio", key), geo_enc = Crypto.Encrypt(Payloads.Zone(40, -3, 150), key), created_by = creator },
            new { id = Guid.NewGuid(), group_id = group, name_enc = Crypto.Encrypt("casa", key), geo_enc = Crypto.Encrypt(Payloads.Zone(41, -4, 80), key), created_by = creator },
            new { id = Guid.NewGuid(), group_id = group, name_enc = "enc1:roto", geo_enc = Crypto.Encrypt(Payloads.Zone(1, 1, 1), key), created_by = creator },
            new { id = Guid.NewGuid(), group_id = group, name_enc = Crypto.Encrypt("Sin centro", key), geo_enc = "enc1:roto", created_by = creator },
        }));

        var zones = await phone.Service.GetZonesAsync(group);

        Assert.Equal(["?", "casa", "Colegio"], zones.Select(z => z.Name));
        Assert.Equal((41.0, -4.0, 80.0, creator), (zones[1].Lat, zones[1].Lon, zones[1].Radius, zones[1].CreatedBy));
    }

    [Fact]
    public async Task GuardarZonaNuevaInsertaConIdNuevoYYoComoAutor()
    {
        var group = Guid.NewGuid();
        var phone = new Phone();
        var key = await phone.WithKeyAsync(group);

        var id = await phone.Service.SaveZoneReturningIdAsync(new Zone(Guid.Empty, group, "Casa", 40, -3, 100, Guid.Empty));

        Assert.NotEqual(Guid.Empty, id);
        Assert.Empty(phone.Server.To("PATCH", "/rest/v1/zones"));
        var insert = Assert.Single(phone.Server.To("POST", "/rest/v1/zones")).Json;
        Assert.Equal(id, insert.GetProperty("id").GetGuid());
        Assert.Equal(phone.Me, insert.GetProperty("created_by").GetGuid());
        Assert.Equal("Casa", Crypto.DecryptOr(insert.GetProperty("name_enc").GetString(), key, "x"));
        var geo = Payloads.Open<Payloads.ZoneData>(insert.GetProperty("geo_enc").GetString(), key)!;
        Assert.Equal((40.0, -3.0, 100.0), (geo.Lat, geo.Lon, geo.R));
    }

    [Fact]
    public async Task GuardarZonaExistenteLaCambiaYSiNoExisteLaCreaConSuId()
    {
        var group = Guid.NewGuid();
        var phone = new Phone();
        await phone.WithKeyAsync(group);
        var existing = new Zone(Guid.NewGuid(), group, "Casa", 40, -3, 100, Guid.Empty);

        phone.Server.On("PATCH", "/rest/v1/zones", """[{"id":1}]""");
        await phone.Service.SaveZoneAsync(existing);
        Assert.Empty(phone.Server.To("POST", "/rest/v1/zones"));
        Assert.Equal($"id=eq.{existing.Id:D}", phone.Server.To("PATCH", "/rest/v1/zones").Single().Query);

        phone.Server.On("PATCH", "/rest/v1/zones", "[]");   // borrada en otro movil entretanto
        Assert.Equal(existing.Id, await phone.Service.SaveZoneReturningIdAsync(existing));
        Assert.Equal(existing.Id, Assert.Single(phone.Server.To("POST", "/rest/v1/zones")).Json.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task BorrarZona()
    {
        var phone = new Phone();
        var id = Guid.NewGuid();
        await phone.Service.DeleteZoneAsync(id);
        Assert.Equal($"id=eq.{id:D}", Assert.Single(phone.Server.To("DELETE", "/rest/v1/zones")).Query);
    }

    [Fact]
    public async Task AvisosDeZonaSeLeenSeActivanYSeBorranConLosDosApagados()
    {
        var group = Guid.NewGuid();
        var target = Guid.NewGuid();
        var zone = Guid.NewGuid();
        var phone = new Phone();
        phone.Server.Table("zone_subscriptions", Rows(new[] { new { group_id = group, target_id = target, zone_id = zone, on_enter = true, on_exit = false } }));

        Assert.Equal(new ZoneSubscription(target, zone, true, false), Assert.Single(await phone.Service.GetMySubscriptionsAsync(group)));
        Assert.Contains($"observer_id=eq.{phone.Me:D}", phone.Server.To("GET", "/rest/v1/zone_subscriptions").Single().Query);

        await phone.Service.SetSubscriptionAsync(group, new ZoneSubscription(target, zone, false, true));
        var upsert = Assert.Single(phone.Server.To("POST", "/rest/v1/zone_subscriptions"));
        Assert.Equal("on_conflict=observer_id,target_id,zone_id", upsert.Query);
        Assert.False(upsert.Json.GetProperty("on_enter").GetBoolean());
        Assert.Equal(phone.Me, upsert.Json.GetProperty("observer_id").GetGuid());

        await phone.Service.SetSubscriptionAsync(group, new ZoneSubscription(target, zone, false, false));
        var delete = Assert.Single(phone.Server.To("DELETE", "/rest/v1/zone_subscriptions"));
        Assert.Equal($"observer_id=eq.{phone.Me:D}&target_id=eq.{target:D}&zone_id=eq.{zone:D}", delete.Query);
    }

    [Fact]
    public async Task EventoDeZonaSoloEnterOExitYAvisa()
    {
        var phone = new Phone();
        await Assert.ThrowsAsync<ArgumentException>(() => phone.Service.ReportZoneEventAsync(Guid.NewGuid(), Guid.NewGuid(), "dentro"));
        Assert.Empty(phone.Server.Requests);

        await phone.Service.ReportZoneEventAsync(Guid.NewGuid(), Guid.NewGuid(), "exit");
        var call = phone.Server.RpcCall("report_zone_event");
        Assert.Equal("exit", call.Str("p_kind"));
        var notify = Assert.Single(phone.Server.Notifies);
        Assert.Equal(("zone_event", call.Str("p_id")), (notify.Str("type"), notify.Str("id")));
    }

    // -----------------------------------------------------------------------
    // Claves (§5): pedir, entregar y recoger
    // -----------------------------------------------------------------------

    [Fact]
    public async Task PedirEntregarYRecogerLaClaveDeUnGrupo()
    {
        var group = Guid.NewGuid();
        var holder = new Phone().MemberOf((group, true));
        var groupKey = await holder.WithKeyAsync(group);
        var lost = new Phone().MemberOf((group, false));
        lost.Store.Values["profile_name"] = "Blas";

        // 1. Al que le falta la pide.
        var shareId = Guid.NewGuid();
        lost.Server.Rpc("request_key_share", $"\"{shareId}\"");
        Assert.Equal(0, await lost.Service.RequestMissingKeysCountAsync());
        var devicePub = lost.Server.RpcCall("request_key_share").Str("p_device_pub");
        Assert.Equal("key_share", Assert.Single(lost.Server.Notifies).Str("type"));

        // 2. Mientras nadie la entregue, no se vuelve a pedir.
        lost.Server.Table("key_shares", Rows(new[] { new { id = shareId, group_id = group, user_id = lost.Me, device_pub = devicePub } }));
        Assert.Equal(0, await lost.Service.RequestMissingKeysCountAsync());
        Assert.Single(lost.Server.To("POST", "/rest/v1/rpc/request_key_share"));

        // 3. Quien la tiene la entrega.
        holder.Server.Table("key_shares", Rows(new object[]
        {
            new { id = shareId, group_id = group, user_id = lost.Me, device_pub = devicePub },
            new { id = Guid.NewGuid(), group_id = group, user_id = lost.Me, device_pub = (string?)null },   // sin clave publica: se salta
        }));
        Assert.Equal(1, await holder.Service.FulfillPendingKeySharesCountAsync());
        var fulfill = holder.Server.RpcCall("fulfill_key_share");
        Assert.Contains($"user_id=neq.{holder.Me:D}", holder.Server.To("GET", "/rest/v1/key_shares").Single().Query);

        // 4. El que la pidio la recoge y vuelve a publicar su nombre.
        lost.Server.Table("key_shares", Rows(new[]
        {
            new { id = shareId, group_id = group, user_id = lost.Me, device_pub = devicePub, fulfiller_pub = fulfill.Str("p_fulfiller_pub"), key_box = fulfill.Str("p_key_box") },
        }));
        Assert.Equal(1, await lost.Service.RequestMissingKeysCountAsync());
        Assert.Equal(groupKey, await lost.Keys.GetAsync(group));
        Assert.False(lost.Store.Values.ContainsKey($"share_priv_{shareId:D}"));
        Assert.Equal("Blas", Crypto.DecryptOr(lost.Server.RpcCall("update_my_member").Str("p_display_name_enc"), groupKey, "x"));
    }

    [Fact]
    public async Task ConTodasLasClavesNoPideNada()
    {
        var group = Guid.NewGuid();
        var phone = new Phone().MemberOf((group, false));
        await phone.WithKeyAsync(group);

        await phone.Service.RequestMissingKeysAsync();

        Assert.Empty(phone.Server.To("GET", "/rest/v1/key_shares"));
    }

    [Fact]
    public async Task PeticionDeOtroMovilNoSePuedeAbrirYSePideOtra()
    {
        var group = Guid.NewGuid();
        var phone = new Phone().MemberOf((group, false));
        phone.Server.Table("key_shares", Rows(new[] { new { id = Guid.NewGuid(), group_id = group, user_id = phone.Me, key_box = "enc1:x", fulfiller_pub = "y" } }));
        phone.Server.Rpc("request_key_share", $"\"{Guid.NewGuid()}\"");

        Assert.Equal(0, await phone.Service.RequestMissingKeysCountAsync());
        Assert.Single(phone.Server.To("POST", "/rest/v1/rpc/request_key_share"));
    }

    [Fact]
    public async Task EntregaSinGruposConClaveNoPreguntaYSiOtroSeAdelantoSigue()
    {
        var group = Guid.NewGuid();
        var phone = new Phone().MemberOf((group, true));
        Assert.Equal(0, await phone.Service.FulfillPendingKeySharesCountAsync());
        Assert.Empty(phone.Server.To("GET", "/rest/v1/key_shares"));

        await phone.WithKeyAsync(group);
        var (pub, _) = Crypto.NewEcdhKeyPair();
        phone.Server.Table("key_shares", Rows(new object[]
        {
            new { id = Guid.NewGuid(), group_id = group, user_id = Guid.NewGuid(), device_pub = pub },
            new { id = Guid.NewGuid(), group_id = group, user_id = Guid.NewGuid(), device_pub = "no es una clave" },
            new { id = Guid.NewGuid(), group_id = Guid.NewGuid(), user_id = Guid.NewGuid(), device_pub = pub },   // grupo sin clave aqui
        }));
        phone.Server.Rpc("fulfill_key_share", _ => FakeSupabase.Json("""{"code":"P0001","message":"not_found"}""", HttpStatusCode.BadRequest));

        await phone.Service.FulfillPendingKeySharesAsync();   // no lanza
        Assert.Single(phone.Server.To("POST", "/rest/v1/rpc/fulfill_key_share"));
    }

    [Fact]
    public async Task EntregaSinRedSiLanza()
    {
        var group = Guid.NewGuid();
        var phone = new Phone().MemberOf((group, true));
        await phone.WithKeyAsync(group);
        var (pub, _) = Crypto.NewEcdhKeyPair();
        phone.Server.Table("key_shares", Rows(new[] { new { id = Guid.NewGuid(), group_id = group, user_id = Guid.NewGuid(), device_pub = pub } }));
        phone.Server.Rpc("fulfill_key_share", _ => throw new HttpRequestException("sin red"));

        Assert.True((await Assert.ThrowsAsync<FamilyTogetherException>(() => phone.Service.FulfillPendingKeySharesAsync())).IsNetwork);
    }
}
