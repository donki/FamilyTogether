using System.Reflection;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.ApplicationModel.DataTransfer;
using Microsoft.Maui.Dispatching;
using Microsoft.Maui.Storage;

namespace FamilyTogether.Core.Tests.Ui;

/// <summary>
/// Dobles de las piezas de MAUI que la app usa fuera de la interfaz (preferencias, version, carpetas,
/// portapapeles, compartir, hilo principal y temporizadores), para ejecutar la app compilada para
/// net10.0 sin Android. MAUI deja cambiar su implementacion por defecto con SetDefault/SetCurrent
/// (internos): se llaman por reflexion.
/// </summary>
internal static class MauiFakes
{
    public static FakePreferences Preferences { get; } = new();
    public static FakeAppInfo AppInfo { get; } = new();
    public static FakeFileSystem FileSystem { get; } = new();
    public static FakeClipboard Clipboard { get; } = new();
    public static FakeShare Share { get; } = new();
    public static TestDispatcher Dispatcher { get; } = new();
    public static FakeSecureStorage SecureStorage { get; } = new();
    public static FakeAuthenticator Authenticator { get; } = new();
    public static FakeDeviceInfo DeviceInfo { get; } = new();
    public static FakeMediaPicker MediaPicker { get; } = new();

    private static bool _installed;

    public static void Install()
    {
        if (_installed)
            return;
        _installed = true;

        Swap(typeof(Microsoft.Maui.Storage.Preferences), Preferences);
        Swap(typeof(Microsoft.Maui.ApplicationModel.AppInfo), AppInfo);
        Swap(typeof(Microsoft.Maui.Storage.FileSystem), FileSystem);
        Swap(typeof(Microsoft.Maui.ApplicationModel.DataTransfer.Clipboard), Clipboard);
        Swap(typeof(Microsoft.Maui.ApplicationModel.DataTransfer.Share), Share);
        Swap(typeof(Microsoft.Maui.Storage.SecureStorage), SecureStorage);
        Swap(typeof(Microsoft.Maui.Authentication.WebAuthenticator), Authenticator);
        Swap(typeof(Microsoft.Maui.Devices.DeviceInfo), DeviceInfo);
        Swap(typeof(Microsoft.Maui.Media.MediaPicker), MediaPicker);
        DispatcherProvider.SetCurrent(new TestDispatcherProvider(Dispatcher));
    }

    private static void Swap(Type type, object implementation)
    {
        var method = type.GetMethod("SetDefault", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            ?? type.GetMethod("SetCurrent", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new InvalidOperationException($"{type.Name} no tiene SetDefault/SetCurrent");
        method.Invoke(null, [implementation]);
    }
}

internal sealed class FakePreferences : IPreferences
{
    public Dictionary<string, object?> Values { get; } = [];

    /// <summary>Para probar que la app sobrevive a un almacen de preferencias que falla.</summary>
    public bool Broken { get; set; }

    private void Check()
    {
        if (Broken)
            throw new InvalidOperationException("preferencias rotas");
    }

    public bool ContainsKey(string key, string? sharedName = null) { Check(); return Values.ContainsKey(key); }

    public void Remove(string key, string? sharedName = null) { Check(); Values.Remove(key); }

    public void Clear(string? sharedName = null) { Check(); Values.Clear(); }

    public void Set<T>(string key, T value, string? sharedName = null) { Check(); Values[key] = value; }

    public T Get<T>(string key, T defaultValue, string? sharedName = null)
    {
        Check();
        return Values.TryGetValue(key, out var v) && v is T t ? t : defaultValue;
    }
}

internal sealed class FakeAppInfo : IAppInfo
{
    public string PackageName => "com.socratic.familytogether";
    public string Name => "Family Together";
    public string VersionString { get; set; } = "2026.10.02.00";
    public Version Version => new(2026, 10, 2, 0);
    public string BuildString => "2026100200";
    public void ShowSettingsUI() => SettingsShown++;
    public int SettingsShown { get; set; }
    public AppTheme RequestedTheme => AppTheme.Light;
    public AppPackagingModel PackagingModel => AppPackagingModel.Packaged;
    public LayoutDirection RequestedLayoutDirection => LayoutDirection.LeftToRight;
}

internal sealed class FakeFileSystem : IFileSystem
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "ft-tests-" + Guid.NewGuid().ToString("N"));

    /// <summary>Ficheros del paquete de la app (Resources\Raw): nombre logico -> contenido.</summary>
    public Dictionary<string, string> Package { get; } = [];

    public string CacheDirectory => Directory.CreateDirectory(Path.Combine(Root, "cache")).FullName;
    public string AppDataDirectory => Directory.CreateDirectory(Path.Combine(Root, "data")).FullName;

    public Task<Stream> OpenAppPackageFileAsync(string filename) =>
        Package.TryGetValue(filename, out var text)
            ? Task.FromResult<Stream>(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text)))
            : Task.FromException<Stream>(new FileNotFoundException(filename));

    public Task<bool> AppPackageFileExistsAsync(string filename) => Task.FromResult(Package.ContainsKey(filename));
}

internal sealed class FakeClipboard : IClipboard
{
    public string? Text { get; private set; }
    public bool HasText => Text is not null;
    public event EventHandler<EventArgs>? ClipboardContentChanged;
    public Task SetTextAsync(string? text) { Text = text; ClipboardContentChanged?.Invoke(this, EventArgs.Empty); return Task.CompletedTask; }
    public Task<string?> GetTextAsync() => Task.FromResult(Text);
}

internal sealed class FakeShare : IShare
{
    public List<object> Requests { get; } = [];
    public bool Fail { get; set; }
    public Task RequestAsync(ShareTextRequest request) { if (Fail) throw new InvalidOperationException("sin compartir"); Requests.Add(request); return Task.CompletedTask; }
    public Task RequestAsync(ShareFileRequest request) { Requests.Add(request); return Task.CompletedTask; }
    public Task RequestAsync(ShareMultipleFilesRequest request) { Requests.Add(request); return Task.CompletedTask; }
}

/// <summary>Hilo principal de mentira: lo que se despacha se ejecuta en el momento.</summary>
internal sealed class TestDispatcher : IDispatcher
{
    public List<TestTimer> Timers { get; } = [];
    public List<Action> Delayed { get; } = [];
    public bool IsDispatchRequired => false;
    public bool Dispatch(Action action) { action(); return true; }
    public bool DispatchDelayed(TimeSpan delay, Action action) { Delayed.Add(action); return true; }

    public void RunDelayed()
    {
        var pending = Delayed.ToList();
        Delayed.Clear();
        foreach (var a in pending)
            a();
    }

    public IDispatcherTimer CreateTimer()
    {
        var t = new TestTimer();
        Timers.Add(t);
        return t;
    }
}

/// <summary>Temporizador que solo avanza cuando la prueba lo pide (<see cref="Fire"/>).</summary>
internal sealed class TestTimer : IDispatcherTimer
{
    public TimeSpan Interval { get; set; }
    public bool IsRepeating { get; set; } = true;
    public bool IsRunning { get; private set; }
    public event EventHandler? Tick;
    public void Start() => IsRunning = true;
    public void Stop() => IsRunning = false;
    public void Fire() => Tick?.Invoke(this, EventArgs.Empty);
}

internal sealed class TestDispatcherProvider(TestDispatcher dispatcher) : IDispatcherProvider
{
    public IDispatcher? GetForCurrentThread() => dispatcher;
}

/// <summary>
/// Animaciones al instante: sin pantalla no hay reloj que las haga avanzar, y los dialogos esperan a
/// que acabe la de salida para devolver la respuesta.
/// </summary>
internal sealed class InstantAnimations : Microsoft.Maui.Animations.IAnimationManager
{
    public double SpeedModifier { get; set; } = 1;
    public bool AutoStartTicker { get; set; } = true;
    public Microsoft.Maui.Animations.ITicker Ticker { get; set; } = new Microsoft.Maui.Animations.Ticker();

    public void Add(Microsoft.Maui.Animations.Animation animation)
    {
        for (var i = 0; i < 1000 && !animation.HasFinished; i++)
            animation.Tick(1000);
    }

    public void Remove(Microsoft.Maui.Animations.Animation animation) { }
}

/// <summary>Un «handler» de la aplicacion solo para dar a MAUI su contexto (servicios y animaciones).</summary>
internal sealed class FakeAppHandler : IElementHandler
{
    public FakeAppHandler()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton<Microsoft.Maui.Animations.IAnimationManager>(services, new InstantAnimations());
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton<IDispatcher>(services, MauiFakes.Dispatcher);
        MauiContext = new MauiContext(Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services));
    }

    public object? PlatformView => null;
    public IElement? VirtualView { get; private set; }
    public IMauiContext? MauiContext { get; private set; }
    public void DisconnectHandler() => VirtualView = null;
    public void Invoke(string command, object? args = null) { }
    public void SetMauiContext(IMauiContext mauiContext) => MauiContext = mauiContext;
    public void SetVirtualView(IElement view) => VirtualView = view;
    public void UpdateValue(string property) { }
}

internal sealed class FakeSecureStorage : ISecureStorage
{
    public Dictionary<string, string> Values { get; } = [];
    public bool Broken { get; set; }

    private void Check()
    {
        if (Broken)
            throw new InvalidOperationException("almacen roto");
    }

    public Task<string?> GetAsync(string key) { Check(); return Task.FromResult(Values.TryGetValue(key, out var v) ? v : null); }
    public Task SetAsync(string key, string value) { Check(); Values[key] = value; return Task.CompletedTask; }
    public bool Remove(string key) { Check(); return Values.Remove(key); }
    public void RemoveAll() { Check(); Values.Clear(); }
}

internal sealed class FakeAuthenticator : Microsoft.Maui.Authentication.IWebAuthenticator
{
    public Microsoft.Maui.Authentication.WebAuthenticatorResult Result { get; set; } = new(new Dictionary<string, string>());
    public Microsoft.Maui.Authentication.WebAuthenticatorOptions? Last { get; private set; }

    public Task<Microsoft.Maui.Authentication.WebAuthenticatorResult> AuthenticateAsync(Microsoft.Maui.Authentication.WebAuthenticatorOptions webAuthenticatorOptions)
    {
        Last = webAuthenticatorOptions;
        return Task.FromResult(Result);
    }

    public Task<Microsoft.Maui.Authentication.WebAuthenticatorResult> AuthenticateAsync(Microsoft.Maui.Authentication.WebAuthenticatorOptions webAuthenticatorOptions, CancellationToken cancellationToken) =>
        AuthenticateAsync(webAuthenticatorOptions);
}

/// <summary>
/// Un movil Android: el WebView de MAUI mira la plataforma antes de evaluar JavaScript (en Android
/// manda el guion tal cual).
/// </summary>
internal sealed class FakeDeviceInfo : Microsoft.Maui.Devices.IDeviceInfo
{
    public string Model => "Prueba";
    public string Manufacturer => "Prueba";
    public string Name => "Prueba";
    public string VersionString => "16";
    public Version Version => new(16, 0);
    public Microsoft.Maui.Devices.DevicePlatform Platform => Microsoft.Maui.Devices.DevicePlatform.Android;
    public Microsoft.Maui.Devices.DeviceIdiom Idiom => Microsoft.Maui.Devices.DeviceIdiom.Phone;
    public Microsoft.Maui.Devices.DeviceType DeviceType => Microsoft.Maui.Devices.DeviceType.Virtual;
}

/// <summary>La galeria de fotos: devuelve lo que la prueba ponga en <see cref="Photos"/> (o lanza).</summary>
internal sealed class FakeMediaPicker : Microsoft.Maui.Media.IMediaPicker
{
    public Func<List<FileResult>>? Photos { get; set; }
    public List<Microsoft.Maui.Media.MediaPickerOptions?> Requests { get; } = [];
    public bool IsCaptureSupported => false;

    public Task<List<FileResult>> PickPhotosAsync(Microsoft.Maui.Media.MediaPickerOptions? options = null)
    {
        Requests.Add(options);
        return Task.FromResult(Photos?.Invoke() ?? []);
    }

    public Task<FileResult?> PickPhotoAsync(Microsoft.Maui.Media.MediaPickerOptions? options = null) => throw new NotSupportedException();
    public Task<FileResult?> CapturePhotoAsync(Microsoft.Maui.Media.MediaPickerOptions? options = null) => throw new NotSupportedException();
    public Task<FileResult?> PickVideoAsync(Microsoft.Maui.Media.MediaPickerOptions? options = null) => throw new NotSupportedException();
    public Task<List<FileResult>> PickVideosAsync(Microsoft.Maui.Media.MediaPickerOptions? options = null) => throw new NotSupportedException();
    public Task<FileResult?> CaptureVideoAsync(Microsoft.Maui.Media.MediaPickerOptions? options = null) => throw new NotSupportedException();
}
