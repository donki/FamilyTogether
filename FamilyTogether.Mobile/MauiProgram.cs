using FamilyTogether.Core;
using FamilyTogether.Mobile.Helpers;
using FamilyTogether.Mobile.Localization;
using FamilyTogether.Mobile.Services;
using Microsoft.Extensions.Logging;
using ZXing.Net.Maui.Controls;

namespace FamilyTogether.Mobile;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        // Gestor global de excepciones (General 6.12): lo primero, antes de crear nada. El de
        // Android (AndroidEnvironment) lo engancha MainApplication.
        CrashLog.Hook();

        // El idioma, antes de cualquier texto (tambien los de la parte nativa).
        Loc.Apply();

        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();

        // Lector de QR de las invitaciones (ZXing.Net.Maui, MIT).
        builder.UseBarcodeReader();

        // Todo el nucleo en un solo fichero SQLite (cola de posiciones, SOS pendientes, estado de
        // zonas y eventos ya vistos), en la carpeta privada de la app.
        var database = Path.Combine(FileSystem.AppDataDirectory, "familytogether.db3");

        builder.Services.AddSingleton(_ => new HttpClient { Timeout = TimeSpan.FromSeconds(20) });

        // Almacen seguro: claves de grupo, privadas ECDH, perfil y tokens de la sesion.
        builder.Services.AddSingleton<SecureStore>();
        builder.Services.AddSingleton<ISecureStore>(sp => sp.GetRequiredService<SecureStore>());
        builder.Services.AddSingleton<ITokenStore>(sp => sp.GetRequiredService<SecureStore>());

        // Navegador del sistema para vincular y recuperar la cuenta.
        builder.Services.AddSingleton<IOAuthBrowser, MauiOAuthBrowser>();

        builder.Services.AddSingleton(sp => new SupabaseClient(
            sp.GetRequiredService<HttpClient>(), sp.GetRequiredService<ITokenStore>()));
        builder.Services.AddSingleton(sp => new GroupKeyStore(sp.GetRequiredService<ISecureStore>()));
        builder.Services.AddSingleton(sp => new FamilyService(
            sp.GetRequiredService<SupabaseClient>(), sp.GetRequiredService<GroupKeyStore>(), sp.GetRequiredService<ISecureStore>()));
        builder.Services.AddSingleton(sp => new AccountService(
            sp.GetRequiredService<SupabaseClient>(), sp.GetRequiredService<FamilyService>(),
            sp.GetRequiredService<ISecureStore>(), sp.GetRequiredService<HttpClient>(), sp.GetRequiredService<IOAuthBrowser>()));
        builder.Services.AddSingleton(_ => new LocationOutbox(database));
        builder.Services.AddSingleton(sp => new SosService(database, sp.GetRequiredService<FamilyService>()));
        builder.Services.AddSingleton(sp => new ZoneWatcher(database, sp.GetRequiredService<FamilyService>()));
        builder.Services.AddSingleton(sp => new EventFeed(database, sp.GetRequiredService<FamilyService>()));

#if ANDROID
        // La parte nativa: servicio de ubicacion en primer plano, FCM y avisos.
        builder.Services.AddSingleton<ILocationSharing, Platforms.Android.LocationSharing>();
        builder.Services.AddSingleton<IPushService, Platforms.Android.PushService>();
        builder.Services.AddSingleton<INotifier, Platforms.Android.Notifier>();
#endif

#if DEBUG
        builder.Services.AddLogging(logging => logging.AddDebug());
#endif

        var app = builder.Build();
        ServiceHelper.Initialize(app.Services);
        return app;
    }
}
