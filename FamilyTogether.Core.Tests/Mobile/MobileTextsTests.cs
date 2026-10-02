using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using FamilyTogether.Core;
using FamilyTogether.Mobile.Localization;
using FamilyTogether.Mobile.Services;
using FamilyTogether.Core.Tests.Ui;

namespace FamilyTogether.Core.Tests.Mobile;

/// <summary>
/// La logica pura del proyecto MAUI (enlazada): tablas es/en, Loc, horas legibles y textos de los
/// avisos. Y que todo texto que pide la app exista en las dos tablas.
/// </summary>
public partial class MobileTextsTests : IDisposable
{
    private readonly CultureInfo _culture = CultureInfo.CurrentCulture;
    private readonly CultureInfo _uiCulture = CultureInfo.CurrentUICulture;

    public void Dispose()
    {
        // Loc.Apply cambia la cultura del proceso: se deja como estaba para las demas pruebas.
        MauiFakes.Preferences.Broken = false;
        MauiFakes.Preferences.Values.Clear();
        CultureInfo.DefaultThreadCurrentCulture = null;
        CultureInfo.DefaultThreadCurrentUICulture = null;
        CultureInfo.CurrentCulture = _culture;
        CultureInfo.CurrentUICulture = _uiCulture;
    }

    private static string MobileDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FamilyTogether.sln")))
            dir = dir.Parent;
        return Path.Combine(dir!.FullName, "FamilyTogether.Mobile");
    }

    /// <summary>Los .cs y .xaml de la app, sin entrar en bin ni obj (son enormes).</summary>
    private static IEnumerable<string> SourceFiles(DirectoryInfo dir)
    {
        foreach (var f in dir.EnumerateFiles())
        {
            if (f.Extension is ".cs" or ".xaml")
                yield return f.FullName;
        }

        foreach (var sub in dir.EnumerateDirectories())
        {
            if (sub.Name is "bin" or "obj")
                continue;
            foreach (var f in SourceFiles(sub))
                yield return f;
        }
    }

    [GeneratedRegex(@"\{(\d+)[^}]*\}")]
    private static partial Regex Placeholder();

    // -----------------------------------------------------------------------
    // Tablas
    // -----------------------------------------------------------------------

    [Fact]
    public void LasDosTablasTienenLasMismasClaves()
    {
        Assert.Empty(Strings.Es.Keys.Except(Strings.En.Keys));
        Assert.Empty(Strings.En.Keys.Except(Strings.Es.Keys));
    }

    [Fact]
    public void NingunTextoVacioYLosHuecosCoinciden()
    {
        foreach (var (key, es) in Strings.Es)
        {
            var en = Strings.En[key];
            Assert.False(string.IsNullOrWhiteSpace(es), key);
            Assert.False(string.IsNullOrWhiteSpace(en), key);
            var huecosEs = Placeholder().Matches(es).Select(m => m.Groups[1].Value).Distinct().Order();
            var huecosEn = Placeholder().Matches(en).Select(m => m.Groups[1].Value).Distinct().Order();
            Assert.True(huecosEs.SequenceEqual(huecosEn), $"{key}: {{}} distintos entre es y en");
            // Que string.Format no reviente con tantos argumentos como huecos.
            var args = Enumerable.Repeat((object)"x", 10).ToArray();
            _ = string.Format(CultureInfo.InvariantCulture, es, args);
            _ = string.Format(CultureInfo.InvariantCulture, en, args);
        }
    }

    [Fact]
    public void CadaErrorQuePuedeLanzarElNucleoTieneTexto()
    {
        string[] codes =
        [
            FamilyTogetherException.Network, FamilyTogetherException.Unauthorized, FamilyTogetherException.Server,
            FamilyTogetherException.NotConfigured, FamilyTogetherException.KeyMissing,
            // del servidor (P0001) y de las Edge Functions
            "not_member", "not_admin", "expired", "not_found", "already_member", "last_admin", "paused", "not_pending",
            "already_linked", "not_empty", "no_link", "sign_in_failed",
        ];
        foreach (var code in codes)
            Assert.True(Strings.Es.ContainsKey("Err_" + code), code);
    }

    [Fact]
    public void TodoTextoQuePideLaAppExiste()
    {
        var used = new HashSet<string>();
        foreach (var file in SourceFiles(new DirectoryInfo(MobileDir())))
        {
            var text = File.ReadAllText(file);
            foreach (Match m in Regex.Matches(text, @"Loc\.(?:Get|Format)\(""(\w+)"""))
                used.Add(m.Groups[1].Value);
            foreach (Match m in Regex.Matches(text, @"\{loc:T (\w+)\}"))
                used.Add(m.Groups[1].Value);
            foreach (Match m in Regex.Matches(text, @"Ui\.(?:AlertAsync|ConfirmAsync)\(\w+, ""(\w+)"""))
                used.Add(m.Groups[1].Value);
        }

        Assert.True(used.Count > 100, $"solo {used.Count} claves: ¿ha cambiado la forma de pedir textos?");
        Assert.DoesNotContain(used, k => !Strings.Es.ContainsKey(k));
    }

    [Fact]
    public void NovedadesEnLosDosIdiomasYDeLaMasNuevaALaMasVieja()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Mobile", "whatsnew.json")));
        var versions = new List<string>();
        foreach (var entry in doc.RootElement.EnumerateArray())
        {
            var version = entry.GetProperty("version").GetString()!;
            versions.Add(version);
            Assert.Matches(@"^\d{4}\.\d{2}\.\d{2}\.\d{2}$", version);
            Assert.True(DateOnly.TryParseExact(entry.GetProperty("date").GetString(), "yyyy-MM-dd", out _), version);
            var es = entry.GetProperty("es").EnumerateArray().ToList();
            var en = entry.GetProperty("en").EnumerateArray().ToList();
            Assert.NotEmpty(es);
            Assert.Equal(es.Count, en.Count);
        }

        Assert.Equal(versions.OrderDescending(StringComparer.Ordinal), versions);
        Assert.Equal(versions.Count, versions.Distinct().Count());
    }

    // -----------------------------------------------------------------------
    // Loc
    // -----------------------------------------------------------------------

    [Fact]
    public void ElegirIdiomaCambiaTextosYCultura()
    {
        Loc.SetPreference(Loc.Spanish);
        Assert.Equal(("es", "es"), (Loc.Preference, Loc.Language));
        Assert.Equal("Cancelar", Loc.Get("Cancel"));
        Assert.Equal("es-ES", CultureInfo.CurrentUICulture.Name);
        Assert.Equal("es-ES", Loc.Culture.Name);

        Loc.SetPreference(Loc.English);
        Assert.Equal("Cancel", Loc.Get("Cancel"));
        Assert.Equal("en-US", CultureInfo.CurrentCulture.Name);

        Loc.SetPreference(Loc.System);
        Assert.Contains(Loc.Language, new[] { "es", "en" });
    }

    [Fact]
    public void ClaveQueFaltaSaleEnElOtroIdiomaOComoClave()
    {
        Loc.SetPreference(Loc.English);
        Strings.Es["SoloEnCastellano"] = "solo aqui";
        try
        {
            Assert.Equal("solo aqui", Loc.Get("SoloEnCastellano"));
            Assert.Equal("NoExiste", Loc.Get("NoExiste"));
        }
        finally
        {
            Strings.Es.Remove("SoloEnCastellano");
        }
    }

    [Fact]
    public void FormatUsaLaCulturaYSiElTextoEstaMalDevuelveElTexto()
    {
        Loc.SetPreference(Loc.Spanish);
        Assert.Equal("SOS de Ana", Loc.Format("NotifSosTitle", "Ana"));
        Assert.Equal("hace 1,5 min", Loc.Format("AgoMinutes", 1.5));

        Strings.Es["Roto"] = "hueco {0";
        try
        {
            Assert.Equal("hueco {0", Loc.Format("Roto", 1));
        }
        finally
        {
            Strings.Es.Remove("Roto");
        }
    }

    [Fact]
    public void SinPreferenciasFuncionaIgual()
    {
        MauiFakes.Preferences.Broken = true;
        Assert.Equal(Loc.System, Loc.Preference);
        Loc.SetPreference(Loc.English);   // no lanza; esta sesion sigue con lo que habia
        Assert.Contains(Loc.Language, new[] { "es", "en" });
    }

    // -----------------------------------------------------------------------
    // Horas
    // -----------------------------------------------------------------------

    [Fact]
    public void HaceCuantoComoLoDiriaUnaPersona()
    {
        Loc.SetPreference(Loc.Spanish);
        var now = DateTimeOffset.Now;
        Assert.Equal("ahora mismo", TimeTexts.Ago(now.AddSeconds(-20)));
        Assert.Equal("hace 5 min", TimeTexts.Ago(now.AddMinutes(-5).AddSeconds(-10)));
        Assert.Equal("hace 3 h", TimeTexts.Ago(now.AddHours(-3).AddMinutes(-1)));
        var old = now.AddDays(-2);
        Assert.Equal("el " + old.ToLocalTime().ToString("g", CultureInfo.GetCultureInfo("es-ES")), TimeTexts.Ago(old));

        Loc.SetPreference(Loc.English);
        Assert.Equal("5 min ago", TimeTexts.Ago(now.AddMinutes(-5).AddSeconds(-10)));
    }

    [Fact]
    public void HastaCuandoHoyMananaOMasAdelante()
    {
        Loc.SetPreference(Loc.Spanish);
        var today = new DateTimeOffset(DateTime.Today.AddHours(18).AddMinutes(30));
        var tomorrow = new DateTimeOffset(DateTime.Today.AddDays(1).AddHours(8));
        var later = new DateTimeOffset(DateTime.Today.AddDays(5).AddHours(9));

        Assert.Equal("18:30", TimeTexts.Until(today));
        Assert.Equal("mañana 08:00", TimeTexts.Until(tomorrow));
        Assert.Equal(later.ToString("g", CultureInfo.GetCultureInfo("es-ES")), TimeTexts.Until(later));
        Assert.Equal("En pausa (hasta 18:30)", TimeTexts.Paused(today));
        Assert.Equal("En pausa", TimeTexts.Paused(null));
        Assert.Equal("batería 42 %", TimeTexts.Battery(42));
    }

    [Fact]
    public void HoraDelHistorialHoyAyerOFecha()
    {
        Loc.SetPreference(Loc.Spanish);
        var today = new DateTimeOffset(DateTime.Today.AddHours(9).AddMinutes(5));
        var yesterday = new DateTimeOffset(DateTime.Today.AddDays(-1).AddHours(23).AddMinutes(40));
        var before = new DateTimeOffset(DateTime.Today.AddDays(-3).AddHours(7));

        Assert.Equal("09:05", TimeTexts.Clock(today));
        Assert.Equal("ayer 23:40", TimeTexts.Clock(yesterday));
        Assert.Equal(before.ToString("g", CultureInfo.GetCultureInfo("es-ES")), TimeTexts.Clock(before));

        Loc.SetPreference(Loc.English);
        Assert.Equal("yesterday 23:40", TimeTexts.Clock(yesterday));
        Assert.Equal("Last 24 hours", Loc.Get("HistoryLast24h"));
        Loc.SetPreference(Loc.Spanish);
        Assert.Equal("Últimas 24 horas", Loc.Get("HistoryLast24h"));
    }

    // -----------------------------------------------------------------------
    // Avisos
    // -----------------------------------------------------------------------

    private static NotificationContent N(string type, string? kind = null, string actor = "Ana", string group = "Casa", string? zone = null) =>
        new(Channels.Sos, type, Guid.NewGuid(), Guid.NewGuid(), group, actor, zone, kind, null, null);

    [Fact]
    public void TextoDeCadaAviso()
    {
        Loc.SetPreference(Loc.Spanish);

        Assert.Equal(("SOS de Ana", "Casa: necesita ayuda. Toca para verle en el mapa."), NotificationTexts.Build(N(EventTypes.Sos)));
        Assert.Contains("última conocida", NotificationTexts.Build(N(EventTypes.Sos, "stale")).Body);
        Assert.Equal(("Ana ha llegado a Colegio", "Grupo Casa"), NotificationTexts.Build(N(EventTypes.ZoneEvent, "enter", zone: "Colegio")));
        Assert.Equal("Ana ha salido de Colegio", NotificationTexts.Build(N(EventTypes.ZoneEvent, "exit", zone: "Colegio")).Title);
        Assert.Equal(("Solicitud para unirse", "Ana quiere unirse a Casa. Toca para aprobar o rechazar."), NotificationTexts.Build(N(EventTypes.JoinRequest)));
        Assert.Equal(("Solicitud aprobada", "Ya eres miembro de Casa."), NotificationTexts.Build(N(EventTypes.RequestResolved, "approved")));
        Assert.Equal("Solicitud rechazada", NotificationTexts.Build(N(EventTypes.RequestResolved, "rejected")).Title);
        Assert.Equal(("Family Together", "Casa"), NotificationTexts.Build(N("otro")));
    }

    [Fact]
    public void NombresIlegiblesSeDicenConPalabras()
    {
        Loc.SetPreference(Loc.English);
        var (title, body) = NotificationTexts.Build(N(EventTypes.ZoneEvent, "enter", actor: "?", group: " ", zone: null));
        Assert.Equal($"{Strings.En["Unknown"]} arrived at {Strings.En["Unknown"]}", title);
        Assert.Equal($"Group {Strings.En["Unknown"]}", body);
    }
}
