// Sustituto de Microsoft.Maui.Storage.Preferences para compilar fuera de MAUI los ficheros de la app
// que se enlazan a las pruebas (Loc.cs). Solo lo que usan: Get y Set con valor por defecto.
namespace Microsoft.Maui.Storage;

public sealed class Preferences
{
    public static Preferences Default { get; } = new();

    public Dictionary<string, object?> Values { get; } = [];

    /// <summary>Para probar que la app sobrevive a un almacen de preferencias que falla.</summary>
    public bool Broken { get; set; }

    public T Get<T>(string key, T defaultValue)
    {
        if (Broken)
            throw new InvalidOperationException("preferencias rotas");
        return Values.TryGetValue(key, out var v) && v is T t ? t : defaultValue;
    }

    public void Set<T>(string key, T value)
    {
        if (Broken)
            throw new InvalidOperationException("preferencias rotas");
        Values[key] = value;
    }
}
