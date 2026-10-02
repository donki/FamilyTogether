namespace FamilyTogether.Mobile.Services.Native;

/// <summary>
/// Registro de la parte nativa: al logcat y al registro común de la app
/// (<see cref="CrashLog"/>, <c>AppDataDirectory/familytogether.log</c>), con la traza completa
/// (constitución General §6.12). Nunca lanza: un registro que falla no puede tumbar nada.
/// </summary>
public static class NativeLog
{
    /// <summary>
    /// Salida al logcat (nivel, línea). La pone <c>MainApplication</c> con <c>Android.Util.Log</c>;
    /// sin ella, solo el fichero.
    /// </summary>
    public static Action<string, string>? Logcat { get; set; }

    public static void Info(string message) => Write("INFO", message, null);

    public static void Warn(string message, Exception? ex = null) => Write("WARN", message, ex);

    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    private static void Write(string level, string message, Exception? ex)
    {
        var line = ex is null ? message : $"{message}\n{ex}";

        try
        {
            Logcat?.Invoke(level, line);
        }
        catch
        {
            // Sin logcat seguimos con el fichero.
        }

        try
        {
            if (level == "ERROR" && ex is not null)
                CrashLog.Error("android: " + message, ex);
            else
                CrashLog.Info($"android {level}: {line}");
        }
        catch
        {
            // CrashLog tampoco lanza, pero aquí no se arriesga nada.
        }
    }
}
