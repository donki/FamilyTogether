namespace FamilyLink.Mobile.Helpers;

/// <summary>
/// Acceso al contenedor de dependencias desde las paginas, que el Shell crea con su constructor sin
/// parametros.
/// </summary>
public static class ServiceHelper
{
    private static IServiceProvider? _services;

    public static void Initialize(IServiceProvider services) => _services = services;

    public static IServiceProvider? Services => _services ??= IPlatformApplication.Current?.Services;

    public static T Get<T>() where T : notnull
    {
        var services = Services
            ?? throw new InvalidOperationException("ServiceHelper se uso antes de que MauiProgram lo inicializara.");

        return services.GetRequiredService<T>();
    }

    /// <summary>El servicio si esta registrado; <c>null</c> si no (p. ej. la parte nativa aun no existe).</summary>
    public static T? TryGet<T>() where T : class
    {
        try
        {
            return Services?.GetService<T>();
        }
        catch (Exception ex)
        {
            global::FamilyLink.Mobile.Services.CrashLog.Info($"No se pudo obtener {typeof(T).Name}: {ex.Message}");
            return null;
        }
    }
}
