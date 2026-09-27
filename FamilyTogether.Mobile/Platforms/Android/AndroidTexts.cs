using System.Globalization;

namespace FamilyTogether.Mobile.Platforms.Android;

/// <summary>
/// Textos visibles que solo existen en la parte nativa de Android: la guía de autoinicio de cada
/// fabricante.
/// </summary>
/// <remarks>
/// Van aquí y no en los recursos de la app porque dependen del fabricante del móvil y solo tienen
/// sentido en Android. El idioma sale de <see cref="CultureInfo.CurrentUICulture"/>, que es lo que
/// fija la localización de la app (castellano si el dispositivo está en castellano; inglés en
/// cualquier otro caso, FR-023).
/// </remarks>
internal static class AndroidTexts
{
    private static readonly Dictionary<string, string> Es = new()
    {
        ["AutostartXiaomi"] = "En Xiaomi, Redmi y POCO: abre Seguridad > Aplicaciones > Permisos > Inicio automático y activa esta app. Después, en Ajustes > Aplicaciones > esta app > Ahorro de batería, elige «Sin restricciones».",
        ["AutostartHuawei"] = "En Huawei y Honor: abre Ajustes > Batería > Inicio de aplicaciones, desactiva «Gestionar automáticamente» para esta app y deja activados Inicio automático, Inicio secundario y Ejecutar en segundo plano.",
        ["AutostartOppo"] = "En OPPO, realme y OnePlus: abre Ajustes > Batería > esta app y permite la actividad en segundo plano; en Ajustes > Aplicaciones > Inicio automático, actívala.",
        ["AutostartVivo"] = "En vivo e iQOO: abre i Manager > Gestor de aplicaciones > Inicio automático y activa esta app; en Ajustes > Batería > Consumo en segundo plano, permítelo.",
        ["AutostartSamsung"] = "En Samsung: abre Ajustes > Batería > Límites de uso en segundo plano y añade esta app a «Aplicaciones que nunca se suspenden».",
        ["AutostartAsus"] = "En ASUS: abre Mobile Manager > Administrador de inicio automático y permite esta app.",
        ["AutostartMeizu"] = "En Meizu: abre Seguridad > Permisos > Inicio en segundo plano y activa esta app.",
    };

    private static readonly Dictionary<string, string> En = new()
    {
        ["AutostartXiaomi"] = "On Xiaomi, Redmi and POCO: open Security > Apps > Permissions > Autostart and turn this app on. Then, in Settings > Apps > this app > Battery saver, choose \"No restrictions\".",
        ["AutostartHuawei"] = "On Huawei and Honor: open Settings > Battery > App launch, turn off \"Manage automatically\" for this app and keep Auto-launch, Secondary launch and Run in background on.",
        ["AutostartOppo"] = "On OPPO, realme and OnePlus: open Settings > Battery > this app and allow background activity; in Settings > Apps > Auto launch, turn it on.",
        ["AutostartVivo"] = "On vivo and iQOO: open i Manager > App manager > Autostart and turn this app on; in Settings > Battery > Background power consumption, allow it.",
        ["AutostartSamsung"] = "On Samsung: open Settings > Battery > Background usage limits and add this app to \"Never sleeping apps\".",
        ["AutostartAsus"] = "On ASUS: open Mobile Manager > Auto-start manager and allow this app.",
        ["AutostartMeizu"] = "On Meizu: open Security > Permissions > Background launch and turn this app on.",
    };

    /// <summary>Texto en el idioma de la interfaz; si falta la clave, la propia clave (se nota en pruebas).</summary>
    public static string Get(string key)
    {
        var table = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "es" ? Es : En;
        return table.TryGetValue(key, out var text) ? text : key;
    }
}
