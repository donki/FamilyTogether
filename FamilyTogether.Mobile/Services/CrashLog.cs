using System.Text;
using FamilyTogether.Mobile.Localization;

namespace FamilyTogether.Mobile.Services;

/// <summary>
/// Gestor global de excepciones (constitucion General 6.12) y registro de la app: un error que no se
/// esperaba nunca cierra la aplicacion; se apunta con la traza completa en
/// <c>AppDataDirectory/familytogether.log</c> y se avisa al usuario en su idioma si hay ventana.
/// </summary>
/// <remarks>
/// Android (<c>AndroidEnvironment.UnhandledExceptionRaiser</c>) lo engancha <c>MainApplication</c>;
/// aqui van los de .NET, comunes a todas las plataformas, y se enganchan en <c>MauiProgram</c> antes
/// de crear ninguna ventana.
/// </remarks>
public static class CrashLog
{
    private const long MaxBytes = 512 * 1024;
    private static readonly object Gate = new();
    private static int _hooked;
    private static DateTime _lastNotice;

    public static string FilePath
    {
        get
        {
            try
            {
                return Path.Combine(FileSystem.AppDataDirectory, "familytogether.log");
            }
            catch (Exception)
            {
                return Path.Combine(Path.GetTempPath(), "familytogether.log");
            }
        }
    }

    public static void Hook()
    {
        if (Interlocked.Exchange(ref _hooked, 1) == 1)
            return;

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            // Este no se puede frenar: se registra, que es lo unico que queda.
            if (e.ExceptionObject is Exception ex)
                Error("AppDomain.UnhandledException", ex);
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            e.SetObserved();
            Error("TaskScheduler.UnobservedTaskException", e.Exception);
            NotifyUser();
        };

        // Lo que el nucleo cuenta sin romper nada (un aviso que no sale, una fila descartada).
        FamilyTogether.Core.CoreLog.Written += message => Info("core: " + message);
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string source, Exception ex) => Write("ERROR", $"{source}: {ex}");

    /// <summary>
    /// Avisa de que algo fallo sin que la app se cierre. Una sola vez cada pocos segundos: una
    /// rafaga de errores no puede convertirse en una rafaga de dialogos.
    /// </summary>
    public static void NotifyUser()
    {
        if (DateTime.UtcNow - _lastNotice < TimeSpan.FromSeconds(10))
            return;
        _lastNotice = DateTime.UtcNow;

        try
        {
            MainThread.BeginInvokeOnMainThread(async () =>
            {
                try
                {
                    if (Ui.CurrentPage() is { } page)
                        await SocShared.ModernDialog.AlertAsync(page, Loc.Get("ErrorTitle"), Loc.Get("ErrUnexpected"), Loc.Get("Ok"));
                }
                catch (Exception)
                {
                    // Sin ventana a la que avisar: ya esta en el registro.
                }
            });
        }
        catch (Exception)
        {
            // Idem.
        }
    }

    private static void Write(string level, string message)
    {
        System.Diagnostics.Debug.WriteLine($"[Family Together] {level} {message}");
#if ANDROID
        // Tambien al logcat (etiqueta FamilyTogether); lo de NativeLog ya fue.
        if (!message.StartsWith("android", StringComparison.Ordinal))
        {
            try
            {
                if (level == "ERROR")
                    global::Android.Util.Log.Error("FamilyTogether", message);
                else
                    global::Android.Util.Log.Info("FamilyTogether", message);
            }
            catch
            {
                // Sin logcat, queda el fichero.
            }
        }
#endif
        try
        {
            lock (Gate)
            {
                var path = FilePath;
                var info = new FileInfo(path);
                if (info.Exists && info.Length > MaxBytes)
                {
                    // El lleno se aparta a .old (pisando el anterior) y se empieza otro: nunca crece sin fin.
                    File.Move(path, path + ".old", overwrite: true);
                }

                File.AppendAllText(path, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} {level} {message}{Environment.NewLine}", Encoding.UTF8);
            }
        }
        catch (Exception)
        {
            // El registro nunca puede tumbar lo que se estaba registrando.
        }
    }
}
