namespace FamilyTogether.Mobile.Services;

/// <summary>
/// Preferencias de la interfaz (no secretas): si ya se paso por la bienvenida y la guia, la ultima
/// version vista para las novedades y el grupo elegido en cada pantalla.
/// </summary>
/// <remarks>El nombre y el avatar los guarda el nucleo en el almacen seguro (<c>ProfileAsync</c>).</remarks>
public static class AppState
{
    private const string KeyWelcomeDone = "welcome_done";
    private const string KeyGuideDone = "guide_done";
    private const string KeyLastVersion = "last_version_seen";
    private const string KeySelectedGroup = "selected_group";

    public static bool WelcomeDone
    {
        get => Get(KeyWelcomeDone, false);
        set => Set(KeyWelcomeDone, value);
    }

    public static bool GuideDone
    {
        get => Get(KeyGuideDone, false);
        set => Set(KeyGuideDone, value);
    }

    public static string LastVersionSeen
    {
        get => Get(KeyLastVersion, string.Empty);
        set => Set(KeyLastVersion, value);
    }

    /// <summary>El grupo que se eligio por ultima vez (mapa, zonas, historial comparten eleccion).</summary>
    public static Guid SelectedGroup
    {
        get => Guid.TryParse(Get(KeySelectedGroup, string.Empty), out var g) ? g : Guid.Empty;
        set => Set(KeySelectedGroup, value == Guid.Empty ? string.Empty : value.ToString("D"));
    }

    /// <summary>Hay una version instalada distinta de la ultima cuyas novedades se enseñaron.</summary>
    public static bool HasUnseenVersion => LastVersionSeen != AppInfo.Current.VersionString;

    public static void MarkVersionSeen() => LastVersionSeen = AppInfo.Current.VersionString;

    private static T Get<T>(string key, T fallback)
    {
        try
        {
            return fallback switch
            {
                bool b => (T)(object)Preferences.Default.Get(key, b),
                string s => (T)(object)Preferences.Default.Get(key, s),
                _ => fallback,
            };
        }
        catch (Exception)
        {
            return fallback;
        }
    }

    private static void Set<T>(string key, T value)
    {
        try
        {
            switch (value)
            {
                case bool b:
                    Preferences.Default.Set(key, b);
                    break;
                case string s:
                    Preferences.Default.Set(key, s);
                    break;
            }
        }
        catch (Exception ex)
        {
            CrashLog.Error($"Preferences.Set({key})", ex);
        }
    }
}
