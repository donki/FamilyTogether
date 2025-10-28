using Android.App;
using Android.Content;
using Android.OS;
using AndroidX.Core.App;
using FamilyTogether.App.Services;

namespace FamilyTogether.App.Platforms.Android;

[Service(Enabled = true, Exported = false)]
public class LocationForegroundService : Service
{
    private const int NotificationId = 1001;
    private const string ChannelId = "FamilyTogetherLocationChannel";
    private const string ChannelName = "Ubicación FamilyTogether";
    
    private PollingService? _pollingService;
    private bool _isRunning = false;

    public override void OnCreate()
    {
        base.OnCreate();
        CreateNotificationChannel();
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        if (_isRunning)
            return StartCommandResult.Sticky;

        _isRunning = true;

        var notification = CreateNotification();
        StartForeground(NotificationId, notification);

        StartLocationTracking();

        return StartCommandResult.Sticky;
    }

    public override IBinder? OnBind(Intent? intent)
    {
        return null;
    }

    public override void OnDestroy()
    {
        _isRunning = false;
        _pollingService?.StopPolling();
        StopForeground(true);
        base.OnDestroy();
    }

    private void CreateNotificationChannel()
    {
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
        {
            var channel = new NotificationChannel(ChannelId, ChannelName, NotificationImportance.Low)
            {
                Description = "Notificación para el servicio de ubicación de FamilyTogether"
            };

            var notificationManager = GetSystemService(NotificationService) as NotificationManager;
            notificationManager?.CreateNotificationChannel(channel);
        }
    }

    private Notification CreateNotification()
    {
        var intent = new Intent(this, typeof(MainActivity));
        var pendingIntent = PendingIntent.GetActivity(this, 0, intent, PendingIntentFlags.Immutable);

        var builder = new NotificationCompat.Builder(this, ChannelId)
            .SetContentTitle("FamilyTogether")
            .SetContentText("Compartiendo ubicación con tu familia")
            .SetSmallIcon(Resource.Drawable.notification_icon_background) // Usar un ícono apropiado
            .SetContentIntent(pendingIntent)
            .SetOngoing(true)
            .SetPriority(NotificationCompat.PriorityLow)
            .SetCategory(NotificationCompat.CategoryService);

        return builder.Build();
    }

    private void StartLocationTracking()
    {
        try
        {
            // Obtener servicios desde el contenedor de dependencias de MAUI
            var serviceProvider = MauiApplication.Current?.Services;
            if (serviceProvider != null)
            {
                var apiService = serviceProvider.GetService<ApiService>();
                var locationService = serviceProvider.GetService<LocationService>();

                if (apiService != null && locationService != null)
                {
                    _pollingService = new PollingService(apiService, locationService);
                    _pollingService.StartPolling();
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error starting location tracking: {ex.Message}");
        }
    }

    public static void StartService(Context context)
    {
        var intent = new Intent(context, typeof(LocationForegroundService));
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
        {
            context.StartForegroundService(intent);
        }
        else
        {
            context.StartService(intent);
        }
    }

    public static void StopService(Context context)
    {
        var intent = new Intent(context, typeof(LocationForegroundService));
        context.StopService(intent);
    }
}