using System.Collections.Concurrent;

namespace FamilyLink.Core;

/// <summary>
/// Las claves de los grupos, en el almacen seguro del movil (<c>group_key_&lt;id&gt;</c>, base64).
/// </summary>
/// <remarks>
/// La clave de un grupo <b>nunca pasa en claro por el servidor</b>: se genera al crear el grupo y
/// llega a los demas moviles por ECDH (§4, §5). Se guarda una copia en memoria porque se usa en
/// cada posicion que se cifra y el almacen seguro de Android es lento.
/// </remarks>
public sealed class GroupKeyStore
{
    private readonly ISecureStore _store;
    private readonly ConcurrentDictionary<Guid, byte[]> _cache = new();

    public GroupKeyStore(ISecureStore store) => _store = store;

    public static string StorageKey(Guid group) => $"group_key_{group:D}";

    public async Task<byte[]?> GetAsync(Guid group)
    {
        if (_cache.TryGetValue(group, out var cached))
            return cached;

        var stored = await _store.GetAsync(StorageKey(group)).ConfigureAwait(false);
        if (string.IsNullOrEmpty(stored))
            return null;

        try
        {
            var key = Convert.FromBase64String(stored);
            if (key.Length != 32)
                return null;

            _cache[group] = key;
            return key;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public async Task SetAsync(Guid group, byte[] key)
    {
        if (key.Length != 32)
            throw new ArgumentException("La clave de grupo tiene que ser de 32 bytes.", nameof(key));

        await _store.SetAsync(StorageKey(group), Convert.ToBase64String(key)).ConfigureAwait(false);
        _cache[group] = key;
    }

    public Task RemoveAsync(Guid group)
    {
        _cache.TryRemove(group, out _);
        _store.Remove(StorageKey(group));
        return Task.CompletedTask;
    }
}
