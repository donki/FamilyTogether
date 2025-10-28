using Microsoft.Extensions.Logging;
using FamilyTogether.App.Services;

namespace FamilyTogether.App;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
				fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
			});

		// Register services
		builder.Services.AddSingleton<NetworkService>();
		builder.Services.AddSingleton<OfflineStorageService>();
		builder.Services.AddSingleton<ApiService>();
		builder.Services.AddSingleton<LocationService>();
		builder.Services.AddSingleton<PermissionService>();
		builder.Services.AddSingleton<PollingService>();
		builder.Services.AddSingleton<NotificationService>();
		builder.Services.AddSingleton<BackgroundService>();
		builder.Services.AddSingleton<BatteryOptimizationService>();
		builder.Services.AddSingleton<ErrorHandlingTestService>();

#if DEBUG
		builder.Logging.AddDebug();
#endif

		return builder.Build();
	}
}
