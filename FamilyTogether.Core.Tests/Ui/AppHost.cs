using System.Runtime.CompilerServices;
using FamilyTogether.Mobile;
using FamilyTogether.Mobile.Helpers;
using FamilyTogether.Mobile.Localization;
using FamilyTogether.Mobile.Services;
using Microsoft.Extensions.DependencyInjection;

namespace FamilyTogether.Core.Tests.Ui;

internal static class TestModule
{
    [ModuleInitializer]
    internal static void Init() => MauiFakes.Install();
}

/// <summary>
/// La app montada para las pruebas: el nucleo con el servidor falso (<see cref="Phone"/>), una base
/// SQLite temporal, los dobles de la parte nativa y una <see cref="Application"/> con los estilos de
/// la app. Las paginas se crean con su constructor, como hace el Shell.
/// </summary>
internal sealed class AppHost : IDisposable
{
    public AppHost(string language = Loc.Spanish)
    {
        UiThread.Inline = true;
        // Paginas de pruebas anteriores que siguen suscritas a los avisos estaticos de la app.
        foreach (var name in new[] { "AppResumed", "MapFocusRequested" })
            typeof(App).GetField(name, System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)?.SetValue(null, null);
        MauiFakes.Preferences.Broken = false;
        MauiFakes.Preferences.Values.Clear();
        MauiFakes.Dispatcher.Timers.Clear();
        MauiFakes.Dispatcher.Delayed.Clear();
        Loc.SetPreference(language);
        Loc.Apply();

        Directory.CreateDirectory(Folder);
        var db = Path.Combine(Folder, "ft.db3");
        Outbox = new LocationOutbox(db);
        Track = new LocalTrack(db);
        Sos = new SosService(db, Phone.Service);
        Zones = new ZoneWatcher(db, Phone.Service);
        Feed = new EventFeed(db, Phone.Service);
        Roads = new OsmRoadSource(new HttpClient(Overpass), Path.Combine(Folder, "roads"));
        Account = new AccountService(Phone.Client, Phone.Service, Phone.Store, new HttpClient(Phone.Server), Browser);

        var services = new ServiceCollection();
        services.AddSingleton(Phone.Client);
        services.AddSingleton(Phone.Keys);
        services.AddSingleton(Phone.Service);
        services.AddSingleton<ISecureStore>(Phone.Store);
        services.AddSingleton(Account);
        services.AddSingleton(Outbox);
        services.AddSingleton(Track);
        services.AddSingleton(Sos);
        services.AddSingleton(Zones);
        services.AddSingleton(Feed);
        services.AddSingleton(Roads);
        services.AddSingleton(new TrackSnapper(Roads));
        services.AddSingleton<ILocationSharing>(Location);
        services.AddSingleton<IPushService>(Push);
        services.AddSingleton<INotifier>(Notifier);
        Services = services.BuildServiceProvider();
        ServiceHelper.Initialize(Services);

        Application = new App();
        Microsoft.Maui.Controls.Application.Current = Application;
        Application.Handler = new FakeAppHandler();
    }

    public string Folder { get; } = Path.Combine(Path.GetTempPath(), "ft-app-" + Guid.NewGuid().ToString("N"));
    public Phone Phone { get; } = new();
    public FakeSupabase Server => Phone.Server;
    public FakeSupabase Overpass { get; } = new();
    public FakeBrowser Browser { get; } = new();
    public FakeLocationSharing Location { get; } = new();
    public FakePush Push { get; } = new();
    public FakeNotifier Notifier { get; } = new();
    public LocationOutbox Outbox { get; }
    public LocalTrack Track { get; }
    public SosService Sos { get; }
    public ZoneWatcher Zones { get; }
    public EventFeed Feed { get; }
    public OsmRoadSource Roads { get; }
    public AccountService Account { get; }
    public ServiceProvider Services { get; }
    public App Application { get; }

    public void Dispose()
    {
        Microsoft.Maui.Controls.Application.Current = null;
        Services.Dispose();
        SQLite.SQLiteAsyncConnection.ResetPool();
        try { Directory.Delete(Folder, true); } catch { }
    }
}

internal sealed class FakeBrowser : IOAuthBrowser
{
    public string RedirectUri => "familytogether://auth";
    public Func<Uri, Task<Uri>>? Respond { get; set; }
    public List<Uri> Opened { get; } = [];

    public Task<Uri> AuthenticateAsync(Uri authorizeUrl, CancellationToken cancellationToken = default)
    {
        Opened.Add(authorizeUrl);
        return Respond?.Invoke(authorizeUrl) ?? Task.FromException<Uri>(new TaskCanceledException());
    }
}

internal sealed class FakeLocationSharing : ILocationSharing
{
    public LocationPermissionState Permission { get; set; } = LocationPermissionState.Always;
    public LocationPermissionState AfterRequest { get; set; } = LocationPermissionState.Always;
    public bool IsRunning { get; set; }
    public int Starts { get; private set; }
    public int Stops { get; private set; }
    public (double Lat, double Lon, double Accuracy, DateTimeOffset At, bool Stale)? Position { get; set; } = (41.39, 2.17, 8, DateTimeOffset.UtcNow, false);
    public bool IsIgnoringBatteryOptimizations { get; set; }
    public string? ManufacturerAutostartHint { get; set; } = "Inicio automatico";
    public List<string> Opened { get; } = [];

    public bool ThrowOnCheck { get; set; }
    public Task<LocationPermissionState> CheckPermissionAsync() =>
        ThrowOnCheck ? throw new InvalidOperationException("sin permisos") : Task.FromResult(Permission);

    public Task<LocationPermissionState> RequestPermissionAsync()
    {
        Permission = AfterRequest;
        return Task.FromResult(Permission);
    }

    public void Start() { Starts++; IsRunning = true; }
    public void Stop() { Stops++; IsRunning = false; }
    public bool Throw { get; set; }
    public Task<(double Lat, double Lon, double Accuracy, DateTimeOffset At, bool Stale)?> GetCurrentOrLastAsync() =>
        Throw ? throw new InvalidOperationException("sin GPS") : Task.FromResult(Position);
    public void OpenBatteryOptimizationSettings() => Opened.Add("battery");
    public void OpenAppSettings() => Opened.Add("app");
    public void OpenManufacturerAutostartSettings() => Opened.Add("autostart");
}

internal sealed class FakePush : IPushService
{
    public bool IsConfigured { get; set; } = true;
    public string? Token { get; set; } = "token-fcm";
    public Task<string?> GetTokenAsync() => Task.FromResult(Token);
}

internal sealed class FakeNotifier : INotifier
{
    public bool AreEnabled { get; set; } = true;
    public bool GrantOnRequest { get; set; } = true;
    public List<NotificationContent> Shown { get; } = [];
    public List<Guid> Cancelled { get; } = [];

    public Task<bool> RequestPermissionAsync()
    {
        AreEnabled = GrantOnRequest;
        return Task.FromResult(AreEnabled);
    }

    public void Show(NotificationContent content) => Shown.Add(content);
    public void Cancel(Guid eventId) => Cancelled.Add(eventId);
}
