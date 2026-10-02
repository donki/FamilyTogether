namespace FamilyTogether.Mobile.Services;

/// <summary>
/// El hilo principal de MAUI, en un solo sitio. Las pruebas (la app compilada para net10.0, sin
/// Android ni hilo principal) ponen <see cref="Inline"/> y todo se ejecuta en el momento.
/// </summary>
public static class UiThread
{
    internal static bool Inline { get; set; }

    public static void Post(Action action)
    {
        if (Inline) action(); else MainThread.BeginInvokeOnMainThread(action);
    }

    public static Task<T> InvokeAsync<T>(Func<Task<T>> function) =>
        Inline ? function() : MainThread.InvokeOnMainThreadAsync(function);
}
