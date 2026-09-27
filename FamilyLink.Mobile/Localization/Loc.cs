using System.Globalization;

namespace FamilyLink.Mobile.Localization;

/// <summary>
/// Textos de la app en castellano e ingles (FR-023). Todo texto visible pasa por aqui: ninguno va
/// incrustado en el codigo (constitucion General 7).
/// </summary>
/// <remarks>
/// <para>Es estatico y no depende del contenedor de MAUI a proposito: lo usan tambien el servicio de
/// ubicacion, FCM y los avisos (<c>Platforms\Android</c>), que pueden correr con la app cerrada y sin
/// ninguna ventana. La preferencia de idioma se lee de <see cref="Preferences"/>, que funciona en ese
/// caso.</para>
///
/// <para>El cambio de idioma en caliente se hace reconstruyendo el Shell (ver
/// <see cref="Pages.SettingsPage"/>): los enlaces de MAUI no reevaluan un indexador, asi que los
/// textos del XAML se resuelven al construir la pagina (<see cref="TExtension"/>).</para>
/// </remarks>
public static class Loc
{
    /// <summary>Preferencia: <c>system</c>, <c>es</c> o <c>en</c>.</summary>
    public const string PreferenceKey = "language";

    public const string System = "system";
    public const string Spanish = "es";
    public const string English = "en";

    /// <summary>El idioma del dispositivo, leido antes de que la app toque la cultura.</summary>
    private static readonly string DeviceLanguage = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;

    private static string? _language;

    /// <summary>Idioma efectivo: <c>es</c> o <c>en</c>.</summary>
    public static string Language => _language ??= Resolve(Preference);

    /// <summary>Lo que eligio el usuario (Sistema, Español o English).</summary>
    public static string Preference
    {
        get
        {
            try
            {
                return Preferences.Default.Get(PreferenceKey, System);
            }
            catch (Exception)
            {
                return System;
            }
        }
    }

    public static CultureInfo Culture => CultureInfo.GetCultureInfo(Language == Spanish ? "es-ES" : "en-US");

    /// <summary>
    /// Fija la cultura del hilo a la del idioma elegido: asi las fechas y horas salen en ese idioma y
    /// la parte nativa (que mira <see cref="CultureInfo.CurrentUICulture"/>) habla igual que la app.
    /// </summary>
    public static void Apply()
    {
        _language = Resolve(Preference);
        try
        {
            CultureInfo.CurrentUICulture = Culture;
            CultureInfo.CurrentCulture = Culture;
            CultureInfo.DefaultThreadCurrentUICulture = Culture;
            CultureInfo.DefaultThreadCurrentCulture = Culture;
        }
        catch (Exception)
        {
            // Sin cultura no se pierde nada importante: los textos salen igual de las tablas.
        }
    }

    /// <summary>Guarda la eleccion y la aplica. Quien llama reconstruye el Shell.</summary>
    public static void SetPreference(string preference)
    {
        try
        {
            Preferences.Default.Set(PreferenceKey, preference);
        }
        catch (Exception)
        {
            // Sin preferencias se aplica igual para esta sesion.
        }

        _language = null;
        Apply();
    }

    /// <summary>Texto de una clave. Si falta, la propia clave: se nota en las pruebas.</summary>
    public static string Get(string key)
    {
        var table = Language == Spanish ? Strings.Es : Strings.En;
        if (table.TryGetValue(key, out var text))
            return text;

        // Una clave que solo este en un idioma sale en el otro antes que como clave.
        return (Language == Spanish ? Strings.En : Strings.Es).TryGetValue(key, out var other) ? other : key;
    }

    /// <summary>Texto con huecos (<c>{0}</c>…), con la cultura del idioma de la app.</summary>
    public static string Format(string key, params object?[] args)
    {
        try
        {
            return string.Format(Culture, Get(key), args);
        }
        catch (FormatException)
        {
            return Get(key);
        }
    }

    private static string Resolve(string preference) => preference switch
    {
        Spanish => Spanish,
        English => English,
        // Castellano si el dispositivo esta en castellano (o en otra lengua de España); ingles en
        // cualquier otro caso.
        _ => DeviceLanguage is "es" or "ca" or "gl" or "eu" ? Spanish : English,
    };
}
