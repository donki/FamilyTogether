using FamilyTogether.Core;
using FamilyTogether.Mobile.Localization;

namespace FamilyTogether.Mobile.Services;

/// <summary>
/// Ayudas comunes de la interfaz: llamar al nucleo sin que un fallo se quede en silencio, traducir
/// los errores (constitucion General 6.9) y leer recursos del tema.
/// </summary>
public static class Ui
{
    /// <summary>
    /// Ejecuta una llamada al nucleo. Si falla, lo apunta en el registro y enseña un aviso con la
    /// razon y que hacer, en el idioma del usuario. Devuelve si salio bien.
    /// </summary>
    public static async Task<bool> RunAsync(Page page, Func<Task> action)
    {
        try
        {
            await action();
            return true;
        }
        catch (Exception ex)
        {
            await ShowErrorAsync(page, ex);
            return false;
        }
    }

    /// <summary>Como <see cref="RunAsync(Page, Func{Task})"/>, con resultado.</summary>
    public static async Task<(bool Ok, T? Value)> RunAsync<T>(Page page, Func<Task<T>> action)
    {
        try
        {
            return (true, await action());
        }
        catch (Exception ex)
        {
            await ShowErrorAsync(page, ex);
            return (false, default);
        }
    }

    /// <summary>Aviso de error: titulo, razon y que hacer. Lo tecnico, al registro.</summary>
    public static async Task ShowErrorAsync(Page page, Exception ex)
    {
        CrashLog.Error($"Ui({page.GetType().Name})", ex);
        try
        {
            await SocShared.ModernDialog.AlertAsync(page, Loc.Get("ErrorTitle"), ErrorTexts.Describe(ex), Loc.Get("Ok"));
        }
        catch (Exception dialogError)
        {
            CrashLog.Error("Ui.ShowErrorAsync", dialogError);
        }
    }

    public static Task AlertAsync(Page page, string titleKey, string message) =>
        SocShared.ModernDialog.AlertAsync(page, Loc.Get(titleKey), message, Loc.Get("Ok"));

    public static Task<bool> ConfirmAsync(Page page, string titleKey, string message, string acceptKey) =>
        SocShared.ModernDialog.AlertAsync(page, Loc.Get(titleKey), message, Loc.Get(acceptKey), Loc.Get("Cancel"));

    /// <summary>La pagina que se ve ahora (la de arriba de la pila del Shell), para los avisos globales.</summary>
    public static Page? CurrentPage()
    {
        if (Shell.Current is { } shell)
            return shell.CurrentPage;

        return Application.Current?.Windows.FirstOrDefault()?.Page;
    }

    public static Style? Style(string key) =>
        Application.Current?.Resources.TryGetValue(key, out var value) == true ? value as Style : null;

    public static Color Color(string key) =>
        Application.Current?.Resources.TryGetValue(key, out var value) == true && value is Color c ? c : Colors.Gray;

    /// <summary>Un color de par claro/oscuro, segun el tema que se ve ahora.</summary>
    public static Color ThemeColor(string baseKey) =>
        Color(baseKey + (Application.Current?.RequestedTheme == AppTheme.Dark ? "Dark" : "Light"));

    public static void SetThemeColor(BindableObject target, BindableProperty property, string baseKey) =>
        target.SetAppThemeColor(property, Color(baseKey + "Light"), Color(baseKey + "Dark"));
}

/// <summary>
/// Cada error previsible tiene su texto: la razon en una frase y que hacer (General 6.9). El usuario
/// nunca ve un mensaje tecnico, un nombre de parametro ni un JSON del servidor.
/// </summary>
public static class ErrorTexts
{
    public static string Describe(Exception ex) => ex switch
    {
        FamilyTogetherException fl => ForCode(fl.Code),
        TaskCanceledException or OperationCanceledException => Loc.Get("Err_cancelled"),
        HttpRequestException or TimeoutException => Loc.Get("Err_network"),
        PermissionException => Loc.Get("Err_permission"),
        _ => Loc.Get("ErrUnexpected"),
    };

    public static string ForCode(string code)
    {
        var key = "Err_" + code;
        var text = Loc.Get(key);
        if (text != key)
            return text;

        CrashLog.Info($"Codigo de error sin texto propio: {code}");
        return Loc.Get("Err_server");
    }
}
