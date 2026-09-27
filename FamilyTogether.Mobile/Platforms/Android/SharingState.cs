using System.Globalization;
using Android.Content;
using FamilyTogether.Core;

namespace FamilyTogether.Mobile.Platforms.Android;

/// <summary>
/// Estado persistido de la parte nativa: si el usuario comparte su ubicación, la última posición
/// enviada y la caché de los grupos donde comparte.
/// </summary>
/// <remarks>
/// Va en <c>SharedPreferences</c> propias y no en <c>Preferences</c> de MAUI porque lo leen el
/// receptor de arranque y el servicio con la app cerrada, y porque no debe mezclarse con los
/// ajustes de la interfaz.
/// </remarks>
internal static class SharingState
{
    private const string PrefsName = "familytogether_location";
    private const string KeyEnabled = "enabled";
    private const string KeyLastLat = "last_lat";
    private const string KeyLastLon = "last_lon";

    private static ISharedPreferences? Prefs =>
        global::Android.App.Application.Context.GetSharedPreferences(PrefsName, FileCreationMode.Private);

    /// <summary>El usuario ha activado «compartir mi ubicación» y no lo ha desactivado.</summary>
    public static bool Enabled
    {
        get
        {
            try { return Prefs?.GetBoolean(KeyEnabled, false) ?? false; }
            catch { return false; }
        }
        set
        {
            try { Prefs?.Edit()?.PutBoolean(KeyEnabled, value)?.Apply(); }
            catch (Exception ex) { NativeLog.Warn("No se pudo guardar si se comparte la ubicación.", ex); }
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
                var lat = prefs?.GetString(KeyLastLat, null);
                var lon = prefs?.GetString(KeyLastLon, null);
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
                var edit = Prefs?.Edit();
                if (edit is null)
                    return;
                if (value is { } v)
                {
                    edit.PutString(KeyLastLat, v.Lat.ToString("R", CultureInfo.InvariantCulture));
                    edit.PutString(KeyLastLon, v.Lon.ToString("R", CultureInfo.InvariantCulture));
                }
                else
                {
                    edit.Remove(KeyLastLat);
                    edit.Remove(KeyLastLon);
                }
                edit.Apply();
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
    /// <see cref="LocationSharing.NotifySharingChanged"/> cuando la interfaz pausa, reanuda, entra o
    /// sale de un grupo, para que la pausa se cumpla desde la siguiente posición (FR-018).
    /// </summary>
    public static void InvalidateGroups() => _refreshedAt = DateTimeOffset.MinValue;

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
            var raw = prefs?.GetString(KeyGroups, null);
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

            Prefs?.Edit()?
                .PutString(KeyGroups, raw)?
                .PutLong(KeyGroupsAt, at.ToUnixTimeMilliseconds())?
                .Apply();
        }
        catch (Exception ex)
        {
            NativeLog.Warn("No se pudo guardar la caché de grupos.", ex);
        }
    }
}
