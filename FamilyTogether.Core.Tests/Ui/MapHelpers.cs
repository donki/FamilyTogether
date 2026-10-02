using System.Reflection;
using FamilyTogether.Mobile.Controls;
using FamilyTogether.Mobile.Helpers;
using FamilyTogether.Mobile.Localization;
using FamilyTogether.Mobile.Services;

namespace FamilyTogether.Core.Tests.Ui;

/// <summary>
/// El «handler» del WebView del mapa: MAUI le pasa la orden <c>EvaluateJavaScriptAsync</c> con un
/// <see cref="EvaluateJavaScriptAsyncRequest"/> (un TaskCompletionSource) y aqui se contesta como lo
/// haria map.html. Apunta cada guion que se ejecuta.
/// </summary>
internal sealed class FakeWebViewHandler : IViewHandler
{
    private readonly List<string> _scripts = [];

    public FakeWebViewHandler()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        MauiContext = new MauiContext(Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services));
    }

    /// <summary>
    /// La respuesta de la pagina a cada guion. Por defecto: lista a la primera, sin eventos en la
    /// cola y nada para lo demas. Si lanza, la evaluacion falla con esa excepcion.
    /// </summary>
    public Func<string, string?> Answer { get; set; } = script => script.Contains("mapReady", StringComparison.Ordinal) ? "yes" : null;

    /// <summary>Lo que se ha ejecutado, sin las preguntas de «listo» ni las de la cola de toques.</summary>
    public List<string> Calls
    {
        get
        {
            lock (_scripts)
                return [.. _scripts.Where(s => !s.Contains("mapReady", StringComparison.Ordinal) && s != "takeEvents()")];
        }
    }

    /// <summary>Todos los guiones, tambien los de «listo» y los de la cola.</summary>
    public List<string> All
    {
        get
        {
            lock (_scripts)
                return [.. _scripts];
        }
    }

    public void Clear()
    {
        lock (_scripts)
            _scripts.Clear();
    }

    public bool HasContainer { get; set; }
    public object? ContainerView => null;
    public object? PlatformView => null;
    public IView? VirtualView { get; private set; }
    IElement? IElementHandler.VirtualView => VirtualView;
    public IMauiContext? MauiContext { get; private set; }

    public void Invoke(string command, object? args = null)
    {
        if (command != nameof(IWebView.EvaluateJavaScriptAsync) || args is not EvaluateJavaScriptAsyncRequest request)
            return;

        lock (_scripts)
            _scripts.Add(request.Script);
        if (Hold?.Invoke(request.Script) == true)
        {
            lock (_held)
                _held.Add(request);
            return;
        }

        Complete(request);
    }

    /// <summary>Los guiones que cumplan esto se quedan sin respuesta hasta <see cref="Release"/>.</summary>
    public Func<string, bool>? Hold { get; set; }

    private readonly List<EvaluateJavaScriptAsyncRequest> _held = [];

    public int HeldCount
    {
        get
        {
            lock (_held)
                return _held.Count;
        }
    }

    /// <summary>Contesta (con <see cref="Answer"/>) a los guiones retenidos.</summary>
    public void Release()
    {
        Hold = null;
        List<EvaluateJavaScriptAsyncRequest> held;
        lock (_held)
        {
            held = [.. _held];
            _held.Clear();
        }
        foreach (var request in held)
            Complete(request);
    }

    private void Complete(EvaluateJavaScriptAsyncRequest request)
    {
        try
        {
            request.TrySetResult(Answer(request.Script)!);
        }
        catch (Exception ex)
        {
            request.TrySetException(ex);
        }
    }

    public Size GetDesiredSize(double widthConstraint, double heightConstraint) => Size.Zero;
    public void PlatformArrange(Rect frame) { }
    public void DisconnectHandler() => VirtualView = null;
    public void SetMauiContext(IMauiContext mauiContext) => MauiContext = mauiContext;
    public void SetVirtualView(IElement view) => VirtualView = (IView)view;
    public void UpdateValue(string property) { }
}

/// <summary>
/// map.html en el paquete de la app mientras dure la prueba (sin el, el mapa no carga y no se ejecuta
/// nada), y el WebView de cada <see cref="MapView"/> con un <see cref="FakeWebViewHandler"/>.
/// </summary>
/// <remarks>
/// Hay que quitarlo al acabar: un MapView sin handler y con map.html esperaria su respuesta para
/// siempre, y colgaria las pruebas de otras paginas.
/// </remarks>
internal sealed class FakeMap : IDisposable
{
    public const string Html = "<html><body><script>var T = /*MAP_TEXT*/{};</script></body></html>";

    public FakeMap(bool withHtml = true)
    {
        if (withHtml)
            MauiFakes.FileSystem.Package["map.html"] = Html;
    }

    public void Dispose() => MauiFakes.FileSystem.Package.Remove("map.html");

    public static WebView Web(MapView map) => (WebView)map.Content;

    public static FakeWebViewHandler Attach(MapView map)
    {
        var handler = new FakeWebViewHandler();
        Web(map).Handler = handler;
        return handler;
    }

    /// <summary>El mapa (unico) de una pagina, ya con su handler falso.</summary>
    public static FakeWebViewHandler Attach(Page page) => Attach(page.All<MapView>().Single());
}

/// <summary>Ayudas de las pruebas del mapa, el historial y los controles.</summary>
internal static class MapKit
{
    /// <summary>La app ya pasada por la guia y con las novedades de esta version vistas.</summary>
    public static void GuideAndNewsSeen()
    {
        AppState.GuideDone = true;
        AppState.MarkVersionSeen();
    }

    /// <summary>El boton de icono con esta descripcion (lector de pantalla) localizada.</summary>
    public static ImageButton Described(this Element root, string key)
    {
        var text = Loc.Get(key);
        return root.All<ImageButton>().Where(b => SemanticProperties.GetDescription(b) == text).OrderBy(b => b.Visible()).LastOrDefault()
            ?? throw new InvalidOperationException($"No hay icono «{text}»");
    }

    public static void ClickDescribed(this Element root, string key) => ((IButtonController)root.Described(key)).SendClicked();

    public static void SendLoaded(this VisualElement element) =>
        typeof(VisualElement).GetMethod("SendLoaded", BindingFlags.Instance | BindingFlags.NonPublic, Type.EmptyTypes)!.Invoke(element, null);

    /// <summary>
    /// Mete la pagina en un Shell con rutas de mentira (GuidePage, GroupsPage...) para las paginas que
    /// navegan con <c>Shell.Current.GoToAsync("//Ruta")</c>.
    /// </summary>
    public static Shell InShell(this Page page, params string[] routes)
    {
        var shell = new Shell();
        shell.Items.Add(new ShellContent { Route = "Home", Content = page });
        foreach (var route in routes)
            shell.Items.Add(new ShellContent { Route = route, Content = new ContentPage { Title = route } });
        var window = new Window(shell);
        typeof(Application).GetMethod("AddWindow", BindingFlags.Instance | BindingFlags.NonPublic, [typeof(Window)])!
            .Invoke(Application.Current, [window]);
        return shell;
    }

    public static string? Route(this Shell shell) => shell.CurrentState?.Location?.OriginalString;

    /// <summary>Cambia un servicio de la app por otro (el resto, los del <see cref="AppHost"/>).</summary>
    public static void Replace<T>(this AppHost host, T replacement) where T : class =>
        ServiceHelper.Initialize(new OverrideProvider(host.Services, typeof(T), replacement));

    private sealed class OverrideProvider(IServiceProvider inner, Type type, object replacement) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == type ? replacement : inner.GetService(serviceType);
    }
}

/// <summary>Un permiso de ubicacion que no se puede consultar (el sistema lanza).</summary>
internal sealed class BrokenLocationSharing : ILocationSharing
{
    public bool IsRunning => true;
    public Task<LocationPermissionState> CheckPermissionAsync() => throw new InvalidOperationException("permiso ilegible");
    public Task<LocationPermissionState> RequestPermissionAsync() => throw new InvalidOperationException("permiso ilegible");
    public void Start() { }
    public void Stop() { }
    public Task<(double Lat, double Lon, double Accuracy, DateTimeOffset At, bool Stale)?> GetCurrentOrLastAsync() => throw new InvalidOperationException("sin lectura");
    public bool IsIgnoringBatteryOptimizations => true;
    public string? ManufacturerAutostartHint => null;
    public void OpenBatteryOptimizationSettings() { }
    public void OpenAppSettings() { }
    public void OpenManufacturerAutostartSettings() { }
}
