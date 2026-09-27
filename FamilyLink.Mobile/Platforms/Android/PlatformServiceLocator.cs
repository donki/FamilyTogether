using Microsoft.Extensions.DependencyInjection;

namespace FamilyLink.Mobile.Platforms.Android;

/// <summary>
/// Acceso al contenedor de MAUI desde las piezas nativas (servicio, receptores, FCM), que Android
/// crea por su cuenta y no reciben inyección por constructor.
/// </summary>
/// <remarks>
/// <see cref="MainApplication"/> es la clase <c>Application</c> del proceso: Android la crea antes que
/// cualquier servicio o receptor, así que el contenedor existe también con la app cerrada. Aun así,
/// todo se pide como opcional: si algo falta, quien llama lo registra y sigue.
/// </remarks>
internal static class PlatformServiceLocator
{
    public static T? Get<T>() where T : class
    {
        try
        {
            return IPlatformApplication.Current?.Services.GetService<T>();
        }
        catch (Exception ex)
        {
            NativeLog.Warn($"No se pudo obtener {typeof(T).Name} del contenedor.", ex);
            return null;
        }
    }
}
