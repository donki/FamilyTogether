using System.Globalization;

namespace FamilyTogether.Mobile.Platforms.Android;

/// <summary>
/// Textos visibles que solo existen en la parte nativa de Android: la guía de autoinicio.
/// </summary>
/// <remarks>
/// Van aquí y no en los recursos de la app porque dependen del fabricante del móvil y solo tienen
/// sentido en Android. Solo se nombran Xiaomi, Redmi y POCO (constitución General §6.13): el resto de
/// fabricantes recibe el texto genérico, aunque el botón siga abriendo su pantalla si se conoce. El idioma sale de <see cref="CultureInfo.CurrentUICulture"/>, que es lo que
/// fija la localización de la app (castellano si el dispositivo está en castellano; inglés en
/// cualquier otro caso, FR-023).
/// </remarks>
internal static class AndroidTexts
{
    private static readonly Dictionary<string, string> Es = new()
    {
        ["AutostartGeneric"] = "Muchos móviles traen su propio gestor de batería o de inicio automático, que cierra las apps en segundo plano. Búscalo en Ajustes (suele estar en Batería o en Aplicaciones) y permite que esta app se inicie sola y funcione en segundo plano sin restricciones.",
        ["AutostartXiaomi"] = "En Xiaomi, Redmi y POCO: abre Seguridad > Aplicaciones > Permisos > Inicio automático y activa esta app. Después, en Ajustes > Aplicaciones > esta app > Ahorro de batería, elige «Sin restricciones».",
    };

    private static readonly Dictionary<string, string> En = new()
    {
        ["AutostartGeneric"] = "Many phones come with their own battery or autostart manager, which closes apps in the background. Look for it in Settings (usually under Battery or Apps) and let this app start on its own and run in the background without restrictions.",
        ["AutostartXiaomi"] = "On Xiaomi, Redmi and POCO: open Security > Apps > Permissions > Autostart and turn this app on. Then, in Settings > Apps > this app > Battery saver, choose \"No restrictions\".",
    };

    /// <summary>Texto en el idioma de la interfaz; si falta la clave, la propia clave (se nota en pruebas).</summary>
    public static string Get(string key)
    {
        var table = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "es" ? Es : En;
        return table.TryGetValue(key, out var text) ? text : key;
    }
}
