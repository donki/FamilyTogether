using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Media;
using Android.OS;
using AndroidX.Core.App;
using FamilyTogether.Mobile.Services.Native;

namespace FamilyTogether.Mobile.Platforms.Android;

/// <summary>
/// La alarma de un SOS recibido aunque el móvil esté en silencio: las decisiones están en
/// <see cref="SosAlarmLogic"/>; aquí, solo el aparato Android (<see cref="AndroidAlarmDevice"/>).
/// </summary>
public static class SosAlarm
{
    private static readonly SosAlarmLogic Logic = new(new AndroidAlarmDevice(global::Android.App.Application.Context));

    public static bool IsRinging => Logic.IsRinging;

    /// <summary>Empieza a sonar (o alarga la cuenta). Si devuelve false, el aviso va por el canal SOS normal.</summary>
    public static bool Start(Context context) => Logic.Start();

    public static void Stop(Context context) => Logic.Stop();

    /// <summary>Intención de «Silenciar» (botón del aviso y de la notificación fija) y de descartar el aviso.</summary>
    public static PendingIntent? SilenceIntent(Context context)
    {
        var intent = new Intent(context, typeof(SosAlarmStopReceiver));
        return PendingIntent.GetBroadcast(context, 0, intent, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
    }
}

/// <summary>
/// <see cref="IAlarmDevice"/> en Android: tono de alarma con <c>MediaPlayer</c> por el flujo de
/// alarma, vibrador, volumen del flujo de alarma, <see cref="SosAlarmService"/> y un
/// <c>Handler</c> del hilo principal para la parada.
/// </summary>
internal sealed class AndroidAlarmDevice(Context context) : IAlarmDevice
{
    private static readonly long[] Vibration = [0, 800, 400, 800, 400, 800, 1200];

    private readonly Handler _handler = new(Looper.MainLooper!);
    private readonly Java.Lang.IRunnable _stop = new Java.Lang.Runnable(() => SosAlarm.Stop(context));
    private MediaPlayer? _player;
    private Vibrator? _vibrator;

    private AudioManager? Audio => context.GetSystemService(Context.AudioService) as AudioManager;

    public bool PlaySound()
    {
        var uri = RingtoneManager.GetDefaultUri(RingtoneType.Alarm)
                  ?? RingtoneManager.GetDefaultUri(RingtoneType.Ringtone)
                  ?? RingtoneManager.GetDefaultUri(RingtoneType.Notification);
        if (uri is null)
            return false;

        var player = new MediaPlayer();
        try
        {
            player.SetAudioAttributes(new AudioAttributes.Builder()
                .SetUsage(AudioUsageKind.Alarm)!
                .SetContentType(AudioContentType.Sonification)!
                .Build());
            player.SetDataSource(context, uri);
            player.Looping = true;
            player.Prepare();
            player.Start();
            _player = player;
            return true;
        }
        catch
        {
            player.Release();
            throw;
        }
    }

    public void StopSound()
    {
        var player = _player;
        _player = null;
        if (player is null)
            return;
        try { player.Stop(); }
        finally { player.Release(); }
    }

    public void Vibrate()
    {
        var vibrator = OperatingSystem.IsAndroidVersionAtLeast(31)
            ? (context.GetSystemService(Context.VibratorManagerService) as VibratorManager)?.DefaultVibrator
            : context.GetSystemService(Context.VibratorService) as Vibrator;
        if (vibrator is null || !vibrator.HasVibrator)
            return;

#pragma warning disable CA1422 // Vibrate(effect, AudioAttributes) sigue funcionando en Android 13+.
        if (OperatingSystem.IsAndroidVersionAtLeast(26))
            vibrator.Vibrate(VibrationEffect.CreateWaveform(Vibration, 0), new AudioAttributes.Builder().SetUsage(AudioUsageKind.Alarm)!.Build());
        else
            vibrator.Vibrate(Vibration, 0);
#pragma warning restore CA1422
        _vibrator = vibrator;
    }

    public void StopVibration()
    {
        _vibrator?.Cancel();
        _vibrator = null;
    }

    public (int Now, int Max) AlarmVolume() =>
        Audio is { } audio
            ? (audio.GetStreamVolume(global::Android.Media.Stream.Alarm), audio.GetStreamMaxVolume(global::Android.Media.Stream.Alarm))
            : (0, 0);

    public void SetAlarmVolume(int volume) =>
        Audio?.SetStreamVolume(global::Android.Media.Stream.Alarm, volume, (VolumeNotificationFlags)0);

    public void StartKeepAlive()
    {
        var intent = new Intent(context, typeof(SosAlarmService));
        if (OperatingSystem.IsAndroidVersionAtLeast(26))
            context.StartForegroundService(intent);
        else
            context.StartService(intent);
    }

    public void StopKeepAlive() => context.StopService(new Intent(context, typeof(SosAlarmService)));

    public void ScheduleStop(TimeSpan after)
    {
        _handler.RemoveCallbacks(_stop);
        _handler.PostDelayed(_stop, (long)after.TotalMilliseconds);
    }

    public void CancelScheduledStop() => _handler.RemoveCallbacks(_stop);
}

/// <summary>
/// Mantiene vivo el proceso mientras suena <see cref="SosAlarm"/>, con una notificación fija con
/// «Silenciar». Tipo <c>shortService</c> (Android 14+): no pide permiso ni declaración en Play y
/// Android lo para solo a los 3 minutos, de sobra para el minuto de alarma.
/// </summary>
[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeShortService)]
public class SosAlarmService : Service
{
    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        try
        {
            Notifier.EnsureChannels(this);
            var silence = SosAlarm.SilenceIntent(this);

            var builder = new NotificationCompat.Builder(this, NotificationRules.ChannelService);
            builder.SetSmallIcon(Resource.Drawable.ic_notification);
            builder.SetContentTitle(NotificationRules.Text("SosAlarmRinging", "SOS alarm ringing"));
            builder.SetContentText(NotificationRules.Text("SosAlarmRingingText", "Tap Silence to stop it."));
            builder.SetOngoing(true);
            builder.SetContentIntent(silence);
            builder.AddAction(0, NotificationRules.Text("SosAlarmSilence", "Silence"), silence);
            var notification = builder.Build()!;

            if (OperatingSystem.IsAndroidVersionAtLeast(34))
                StartForeground(NotificationRules.SosAlarmNotificationId, notification, ForegroundService.TypeShortService);
            else
                StartForeground(NotificationRules.SosAlarmNotificationId, notification);

            if (!SosAlarm.IsRinging)
                StopSelf();
        }
        catch (Exception ex)
        {
            NativeLog.Warn("El servicio de la alarma SOS no pudo ponerse en primer plano.", ex);
            StopSelf();
        }

        return StartCommandResult.NotSticky;
    }

    /// <summary>Android 14+: se acabó el tiempo de un <c>shortService</c>.</summary>
    public override void OnTimeout(int startId)
    {
        SosAlarm.Stop(this);
        StopSelf();
    }
}

/// <summary>«Silenciar» y descartar el aviso SOS: paran la alarma.</summary>
[BroadcastReceiver(Exported = false)]
public class SosAlarmStopReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is not null)
            SosAlarm.Stop(context);
    }
}
