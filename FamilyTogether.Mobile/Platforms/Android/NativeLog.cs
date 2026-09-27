using FamilyTogether.Mobile.Services;

namespace FamilyTogether.Mobile.Platforms.Android;

/// <summary>
/// Registro de la parte nativa: al logcat y al registro común de la app
/// (<see cref="CrashLog"/>, <c>AppDataDirectory/familytogether.log</c>), con la traza completa
/// (constitución General §6.12). Nunca lanza: un registro que falla no puede tumbar nada.
/// </summary>
internal static class NativeLog
{
    private const string Tag = "FamilyTogether";

    public static void Info(string message) => Write("INFO", message, null);

    public static void Warn(string message, Exception? ex = null) => Write("WARN", message, ex);

    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    private static void Write(string level, string message, Exception? ex)
    {
        var line = ex is null ? message : $"{message}\n{ex}";

        try
        {
            switch (level)
            {
                case "ERROR": global::Android.Util.Log.Error(Tag, line); break;
                case "WARN": global::Android.Util.Log.Warn(Tag, line); break;
                default: global::Android.Util.Log.Info(Tag, line); break;
            }
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
