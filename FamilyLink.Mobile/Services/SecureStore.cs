using FamilyLink.Core;

namespace FamilyLink.Mobile.Services;

/// <summary>
/// Almacen seguro del movil (Android Keystore por debajo de <see cref="SecureStorage"/>): claves de
/// los grupos, privadas ECDH de las solicitudes en curso, perfil y tokens de la sesion de Supabase.
/// Nada de esto sale del movil.
/// </summary>
/// <remarks>
/// Si el Keystore del dispositivo esta roto —pasa en algunos fabricantes tras restaurar una copia—
/// no se cae a un almacen en claro: las claves de grupo no pueden quedar sin proteger. Se registra y
/// la operacion falla con una excepcion que la interfaz traduce.
/// </remarks>
public sealed class SecureStore : ISecureStore, ITokenStore
{
    public async Task<string?> GetAsync(string key)
    {
        try
        {
            return await SecureStorage.Default.GetAsync(key).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            CrashLog.Error($"SecureStorage.Get({key})", ex);
            return null;
        }
    }

    public async Task SetAsync(string key, string value) => await StoreAsync(key, value).ConfigureAwait(false);

    async Task ITokenStore.SetAsync(string key, string? value) => await StoreAsync(key, value).ConfigureAwait(false);

    public void Remove(string key)
    {
        try
        {
            SecureStorage.Default.Remove(key);
        }
        catch (Exception ex)
        {
            CrashLog.Error($"SecureStorage.Remove({key})", ex);
        }
    }

    private async Task StoreAsync(string key, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            Remove(key);
            return;
        }

        try
        {
            await SecureStorage.Default.SetAsync(key, value).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            CrashLog.Error($"SecureStorage.Set({key})", ex);
            throw new FamilyLinkException("secure_storage", "No se pudo guardar en el almacen seguro.", ex);
        }
    }
}
