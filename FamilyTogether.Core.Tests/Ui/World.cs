using System.Net;

namespace FamilyTogether.Core.Tests.Ui;

/// <summary>
/// Un servidor falso con estado: grupos (con su clave), miembros, ultimas posiciones, recorridos y
/// zonas, que responde a las consultas del nucleo como lo haria Supabase con la RLS de cada grupo.
/// </summary>
internal sealed class World
{
    public sealed class GroupData
    {
        public Guid Id { get; } = Guid.NewGuid();
        public required string Name { get; set; }
        public required byte[] Key { get; init; }
        public List<MemberData> Members { get; } = [];
        public List<ZoneData> Zones { get; } = [];
        public List<(Guid User, double Lat, double Lon, double Acc, DateTimeOffset At, int? Battery)> Positions { get; } = [];
    }

    public sealed record MemberData(Guid User, string Name, bool Admin, bool Paused = false, DateTimeOffset? Until = null, string? Avatar = null);

    public sealed record ZoneData(Guid Id, string Name, double Lat, double Lon, double Radius, Guid CreatedBy);

    private readonly Phone _phone;

    public World(Phone phone)
    {
        _phone = phone;
        var s = phone.Server;
        s.Table("group_members", r =>
        {
            if (r.Query.Contains("group_id=eq."))
            {
                var g = Groups.FirstOrDefault(x => r.Query.Contains($"group_id=eq.{x.Id:D}"));
                return Rows(g?.Members.Select(m => Member(g, m)) ?? []);
            }

            return Rows(Groups.Where(g => g.Members.Any(m => m.User == phone.Me))
                .Select(g => Member(g, g.Members.First(m => m.User == phone.Me))));
        });
        s.Table("groups", r => Rows(Groups.Where(g => r.Query.Contains(g.Id.ToString("D")))
            .Select(g => new { id = g.Id, name_enc = Crypto.Encrypt(g.Name, g.Key) })));
        s.Table("zones", r => Rows(Groups.Where(g => r.Query.Contains(g.Id.ToString("D"))).SelectMany(g => g.Zones.Select(z => new
        {
            id = z.Id,
            group_id = g.Id,
            name_enc = Crypto.Encrypt(z.Name, g.Key),
            geo_enc = Crypto.Encrypt(Payloads.Zone(z.Lat, z.Lon, z.Radius), g.Key),
            created_by = z.CreatedBy,
        }))));
        s.Table("last_positions", r => Rows(Groups.Where(g => r.Query.Contains(g.Id.ToString("D"))).SelectMany(g =>
            g.Positions.GroupBy(p => p.User).Select(p => p.OrderBy(x => x.At).Last()).Select(p => Position(g, p)))));
        s.Table("positions", r => Rows(Groups.Where(g => r.Query.Contains(g.Id.ToString("D"))).SelectMany(g =>
            g.Positions.Where(p => r.Query.Contains(p.User.ToString("D"))).OrderBy(p => p.At).Select(p => Position(g, p)))));
    }

    public List<GroupData> Groups { get; } = [];

    /// <summary>Un grupo con su clave guardada en el movil, conmigo dentro y otros miembros.</summary>
    public async Task<GroupData> GroupAsync(string name, bool admin = true, params MemberData[] others)
    {
        var g = new GroupData { Name = name, Key = Crypto.NewGroupKey() };
        g.Members.Add(new MemberData(_phone.Me, "Yo", admin));
        g.Members.AddRange(others);
        await _phone.Keys.SetAsync(g.Id, g.Key);
        Groups.Add(g);
        return g;
    }

    private static object Member(GroupData g, MemberData m) => new
    {
        group_id = g.Id,
        user_id = m.User,
        role = m.Admin ? "admin" : "member",
        display_name_enc = Crypto.Encrypt(m.Name, g.Key),
        avatar_enc = m.Avatar is null ? null : Crypto.Encrypt(m.Avatar, g.Key),
        paused = m.Paused,
        pause_until = m.Until,
    };

    private static object Position(GroupData g, (Guid User, double Lat, double Lon, double Acc, DateTimeOffset At, int? Battery) p) => new
    {
        user_id = p.User,
        recorded_at = p.At,
        battery = p.Battery,
        payload_enc = Crypto.Encrypt(Payloads.Position(p.Lat, p.Lon, p.Acc), g.Key),
        coarse = false,
    };

    private static HttpResponseMessage Rows(IEnumerable<object> rows) => FakeSupabase.Json(FakeSupabase.Serialize(rows.ToArray()));

    public static HttpResponseMessage Fail(string code = "network") =>
        code == "network" ? throw new HttpRequestException("sin red") : FakeSupabase.Json($$"""{"code":"P0001","message":"{{code}}"}""", HttpStatusCode.BadRequest);
}
