using Android.App;
using Android.Runtime;
using FamilyTogether.Mobile.Platforms.Android;
using FamilyTogether.Mobile.Services;

namespace FamilyTogether.Mobile;

[Application]
public class MainApplication : MauiApplication
{
    public MainApplication(IntPtr handle, JniHandleOwnership ownership)
        : base(handle, ownership)
    {
        // Gestor global de excepciones (constitución General §6.12): se engancha lo antes posible,
        // antes de que se abra ninguna ventana y antes de que arranque el servicio o FCM, que
        // también viven en este proceso. Los de .NET (AppDomain y TaskScheduler) los engancha
        // CrashLog.Hook desde MauiProgram, común a todas las plataformas.
        AndroidEnvironment.UnhandledExceptionRaiser += OnUnhandledException;
    }

    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

    public override void OnCreate()
    {
        base.OnCreate();

        // FCM con inicialización manual (sin google-services.json). Si el proyecto de Firebase no
        // está configurado, no se hace nada y los avisos llegan por consulta cada 60 s.
        FirebaseSetup.Initialize(this);

        // Los canales se crean una vez al arrancar el proceso: así existen aunque el primer aviso
        // llegue por FCM con la app cerrada.
        Notifier.EnsureChannels(this);
    }

    /// <summary>
    /// Un error que no se esperaba nunca cierra la app: se registra con la traza completa, se avisa
    /// en el idioma del usuario (si hay ventana; desde el servicio con la app cerrada no hay nadie
    /// mirando y solo se registra) y se sigue.
    /// </summary>
    private static void OnUnhandledException(object? sender, RaiseThrowableEventArgs e)
    {
        e.Handled = true;

        try
        {
            global::Android.Util.Log.Error("FamilyTogether", $"AndroidEnvironment.UnhandledExceptionRaiser: {e.Exception}");
            CrashLog.Error("AndroidEnvironment.UnhandledExceptionRaiser", e.Exception);
            CrashLog.NotifyUser();
        }
        catch
        {
            // El registro y el aviso nunca lanzan, pero aquí no se arriesga nada.
        }
    }
}
