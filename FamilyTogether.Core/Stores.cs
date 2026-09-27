namespace FamilyTogether.Core;

/// <summary>
/// Almacen seguro del movil (en Android, <c>SecureStorage</c>). Lo implementa la app.
/// </summary>
/// <remarks>
/// Aqui viven las claves de los grupos (<c>group_key_&lt;id&gt;</c>), las privadas ECDH de las
/// solicitudes y peticiones de clave en curso y el perfil propio. Nada de esto sale del movil.
/// </remarks>
public interface ISecureStore
{
    Task<string?> GetAsync(string key);

    Task SetAsync(string key, string value);

    void Remove(string key);
}

/// <summary>
/// Donde se guardan los tokens de la sesion de Supabase. Lo implementa la app (en Android,
/// <c>SecureStorage</c>; puede ser la misma clase que <see cref="ISecureStore"/>). Un valor
/// <c>null</c> borra la clave.
/// </summary>
public interface ITokenStore
{
    Task<string?> GetAsync(string key);

    Task SetAsync(string key, string? value);
}

/// <summary>
/// Registro minimo del nucleo. Lo que falla sin romper nada —un aviso que no sale, una fila que
/// se descarta— se cuenta aqui; la app decide a donde lo manda.
/// </summary>
public static class CoreLog
{
    public static event Action<string>? Written;

    internal static void Write(string message)
    {
        System.Diagnostics.Debug.WriteLine($"[Family Together] {message}");
        try
        {
            Written?.Invoke(message);
        }
        catch
        {
            // Un registro que falla no puede tumbar lo que se estaba registrando.
        }
    }
}
