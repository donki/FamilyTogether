using System.Globalization;
using FamilyTogether.Core;

namespace FamilyTogether.Mobile.Services.Native;

/// <summary>
/// Almacén clave-valor de la parte nativa. En Android, unas <c>SharedPreferences</c> propias
/// (<c>SharedPrefsStore</c>); en las pruebas, un diccionario.
/// </summary>
public interface IKeyValueStore
{
    bool GetBool(string key, bool fallback);

    string? GetString(string key);

    /// <summary>
    /// Escribe varios valores de una vez (<c>string</c>, <c>bool</c> o <c>long</c>; <c>null</c> lo
    /// quita), como un solo <c>Edit().Apply()</c>.
    /// </summary>
    void Apply(IReadOnlyDictionary<string, object?> changes);
}

/// <summary>
/// Estado persistido de la parte nativa: si el usuario comparte su ubicación, la última posición
/// enviada y la caché de los grupos donde comparte.
/// </summary>
/// <remarks>
/// Va en <c>SharedPreferences</c> propias y no en <c>Preferences</c> de MAUI porque lo leen el
/// receptor de arranque y el servicio con la app cerrada, y porque no debe mezclarse con los
/// ajustes de la interfaz. El almacén lo pone <c>MainApplication</c> al crear el proceso
/// (<see cref="Store"/>).
/// </remarks>
public static class SharingState
{
    public const string PrefsName = "familytogether_location";
    private const string KeyEnabled = "enabled";
    private const string KeyLastLat = "last_lat";
    private const string KeyLastLon = "last_lon";

    /// <summary>El almacén (se pide en cada acceso, como las <c>SharedPreferences</c> de Android).</summary>
    public static Func<IKeyValueStore?> Store { get; set; } = () => null;

    private static IKeyValueStore? Prefs => Store();

    /// <summary>El usuario ha activado «compartir mi ubicación» y no lo ha desactivado.</summary>
    public static bool Enabled
    {
        get
        {
            try { return Prefs?.GetBool(KeyEnabled, false) ?? false; }
            catch { return false; }
        }
        set
        {
            try { Prefs?.Apply(new Dictionary<string, object?> { [KeyEnabled] = value }); }
            catch (Exception ex) { NativeLog.Warn("No se pudo guardar si se comparte la ubicación.", ex); }
        }
    }

    private const string KeySosLoud = "sos_loud";

    /// <summary>
    /// Un SOS recibido suena como alarma aunque el móvil esté en silencio o en vibración (ajuste
    /// del usuario, encendido por defecto). Va aquí y no en <c>Preferences</c> porque lo lee el
    /// servicio de FCM con la app cerrada.
    /// </summary>
    public static bool SosLoud
    {
        get
        {
            try { return Prefs?.GetBool(KeySosLoud, true) ?? true; }
            catch { return true; }
        }
        set
        {
            try { Prefs?.Apply(new Dictionary<string, object?> { [KeySosLoud] = value }); }
            catch (Exception ex) { NativeLog.Warn("No se pudo guardar el ajuste de la alarma SOS.", ex); }
        }
    }

    /// <summary>Última posición puesta en la cola, para el umbral de 25 m entre envíos.</summary>
    public static (double Lat, double Lon)? LastSent
    {
        get
        {
            try
            {
                var prefs = Prefs;
                var lat = prefs?.GetString(KeyLastLat);
                var lon = prefs?.GetString(KeyLastLon);
                if (double.TryParse(lat, NumberStyles.Float, CultureInfo.InvariantCulture, out var la) &&
                    double.TryParse(lon, NumberStyles.Float, CultureInfo.InvariantCulture, out var lo))
                    return (la, lo);
            }
            catch
            {
                // Sin dato: la próxima lectura válida se envía.
            }
            return null;
        }
        set
        {
            try
            {
                Prefs?.Apply(new Dictionary<string, object?>
                {
                    [KeyLastLat] = value?.Lat.ToString("R", CultureInfo.InvariantCulture),
                    [KeyLastLon] = value?.Lon.ToString("R", CultureInfo.InvariantCulture),
                });
            }
            catch (Exception ex)
            {
                NativeLog.Warn("No se pudo guardar la última posición enviada.", ex);
            }
        }
    }

    // ==================================================================================
    //  Grupos donde comparto
    // ==================================================================================

    private const string KeyGroups = "sharing_groups";
    private const string KeyGroupsAt = "sharing_groups_at";

    private sealed record Entry(Guid Group, bool Paused, DateTimeOffset? Until);

    private static readonly SemaphoreSlim RefreshGate = new(1, 1);
    private static List<Entry>? _entries;
    private static DateTimeOffset _refreshedAt = DateTimeOffset.MinValue;

    /// <summary>
    /// Olvida la caché: la próxima lectura vuelve a preguntar al servidor. Lo llama
    /// <c>LocationSharing.NotifySharingChanged</c> cuando la interfaz pausa, reanuda, entra o
    /// sale de un grupo, para que la pausa se cumpla desde la siguiente posición (FR-018).
    /// </summary>
    public static void InvalidateGroups() => _refreshedAt = DateTimeOffset.MinValue;

    /// <summary>Olvida también lo cargado del almacén (al cambiar de almacén, en las pruebas).</summary>
    internal static void Reset()
    {
        _entries = null;
        _refreshedAt = DateTimeOffset.MinValue;
    }

    /// <summary>
    /// Grupos donde comparto en este momento: soy miembro, tengo la clave y no estoy en pausa
    /// efectiva (<c>not paused or pause_until &lt;= now()</c>, ARQUITECTURA §6).
    /// </summary>
    /// <remarks>
    /// Con red, se refresca si la caché tiene más de <paramref name="maxAge"/>. Sin red, o si el
    /// servidor falla, vale la última guardada: la hora de fin de cada pausa se evalúa en el
    /// momento, así que una pausa con fin se levanta sola aunque no haya conexión.
    /// </remarks>
    public static async Task<IReadOnlyList<Guid>> GetSharingGroupsAsync(FamilyService? family, bool online, TimeSpan maxAge)
    {
        _entries ??= Load();

        if (family is not null && online && DateTimeOffset.UtcNow - _refreshedAt > maxAge)
        {
            await RefreshGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (DateTimeOffset.UtcNow - _refreshedAt > maxAge)
                    await RefreshAsync(family).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                NativeLog.Warn("No se pudieron refrescar los grupos donde se comparte; se usa la caché.", ex);
            }
            finally
            {
                RefreshGate.Release();
            }
        }

        var now = DateTimeOffset.UtcNow;
        return (_entries ?? [])
            .Where(e => !e.Paused || (e.Until is { } until && until <= now))
            .Select(e => e.Group)
            .ToList();
    }

    private static async Task RefreshAsync(FamilyService family)
    {
        var fresh = new List<Entry>();
        var groups = await family.GetGroupsAsync().ConfigureAwait(false);

        foreach (var group in groups)
        {
            // Sin la clave del grupo no se puede cifrar la posición (se está recuperando la cuenta).
            if (group.KeyMissing)
                continue;

            var members = await family.GetMembersAsync(group.Id).ConfigureAwait(false);
            var me = members.FirstOrDefault(m => m.IsMe);
            if (me is null)
                continue;

            fresh.Add(new Entry(group.Id, me.Paused, me.PauseUntil));
        }

        _entries = fresh;
        _refreshedAt = DateTimeOffset.UtcNow;
        Save(fresh, _refreshedAt);
    }

    private static List<Entry> Load()
    {
        try
        {
            var prefs = Prefs;
            var raw = prefs?.GetString(KeyGroups);
            if (string.IsNullOrEmpty(raw))
                return [];

            var list = new List<Entry>();
            foreach (var item in raw.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                // grupo|pausado(0/1)|fin en ms Unix o vacío
                var parts = item.Split('|');
                if (parts.Length != 3 || !Guid.TryParse(parts[0], out var id))
                    continue;

                DateTimeOffset? until = long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms)
                    ? DateTimeOffset.FromUnixTimeMilliseconds(ms)
                    : null;
                list.Add(new Entry(id, parts[1] == "1", until));
            }
            return list;
        }
        catch (Exception ex)
        {
            NativeLog.Warn("Caché de grupos ilegible; se empieza vacía.", ex);
            return [];
        }
    }

    private static void Save(List<Entry> entries, DateTimeOffset at)
    {
        try
        {
            var raw = string.Join(';', entries.Select(e =>
                $"{e.Group:D}|{(e.Paused ? "1" : "0")}|{e.Until?.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) ?? string.Empty}"));

            Prefs?.Apply(new Dictionary<string, object?>
            {
                [KeyGroups] = raw,
                [KeyGroupsAt] = at.ToUnixTimeMilliseconds(),
            });
        }
        catch (Exception ex)
        {
            NativeLog.Warn("No se pudo guardar la caché de grupos.", ex);
        }
    }
}
