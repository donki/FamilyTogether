using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Media;
using Android.OS;
using AndroidX.Core.App;
using FamilyTogether.Mobile.Services.Native;

namespace FamilyTogether.Mobile.Platforms.Android;

/// <summary>
/// La alarma de un SOS recibido cuando el usuario quiere que suene aunque el móvil esté en
/// silencio (<see cref="SharingState.SosLoud"/>).
/// </summary>
/// <remarks>
/// <para>El sonido de un canal de notificación lo calla el modo silencio o vibración (y en MIUI,
/// también con el uso de alarma). Por eso la app reproduce ella misma el tono de alarma por el flujo
/// de <b>alarma</b>, que ni el silencio ni la vibración tocan (igual que el despertador), subiendo
/// ese volumen al máximo mientras suena y dejándolo después como estaba. Vibra a la vez.</para>
/// <para>Suena hasta <see cref="Duration"/>, hasta tocar «Silenciar» (en el aviso o en la
/// notificación fija), descartar el aviso o abrir la app. No salta «No molestar» si el usuario
/// lo ha configurado para callar también las alarmas: eso no lo decide la app.</para>
/// <para>Mientras suena, <see cref="SosAlarmService"/> (servicio en primer plano de tipo
/// <c>shortService</c>, sin permiso propio ni declaración en Play) mantiene vivo el proceso:
/// el servicio de FCM vuelve en cuanto ha montado el aviso. Si Android no deja arrancarlo, la
/// alarma suena igual mientras el proceso siga vivo (el servicio de ubicación lo suele mantener).</para>
/// </remarks>
public static class SosAlarm
{
    /// <summary>Lo que suena como mucho; un SOS nuevo vuelve a empezar la cuenta.</summary>
    public static readonly TimeSpan Duration = TimeSpan.FromMinutes(1);

    private static readonly long[] Vibration = [0, 800, 400, 800, 400, 800, 1200];

    private static readonly object Gate = new();
    private static readonly Handler MainHandler = new(Looper.MainLooper!);
    private static readonly Java.Lang.IRunnable StopRunnable = new Java.Lang.Runnable(() => Stop(global::Android.App.Application.Context));

    private static MediaPlayer? _player;
    private static Vibrator? _vibrator;
    private static int? _volumeBefore;
    private static int _volumeSet = -1;

    public static bool IsRinging
    {
        get { lock (Gate) return _player is not null; }
    }

    /// <summary>
    /// Empieza a sonar (o, si ya sonaba, alarga la cuenta). Devuelve si suena: si no, el aviso
    /// debe ir por el canal SOS normal, con su propio sonido.
    /// </summary>
    public static bool Start(Context context)
    {
        lock (Gate)
        {
            try
            {
                if (_player is null)
                {
                    RaiseAlarmVolume(context);
                    _player = Play(context);
                    if (_player is null)
                    {
                        RestoreAlarmVolume(context);
                        return false;
                    }

                    Vibrate(context);
                    StartKeepAlive(context);
                }

                MainHandler.RemoveCallbacks(StopRunnable);
                MainHandler.PostDelayed(StopRunnable, (long)Duration.TotalMilliseconds);
                return true;
            }
            catch (Exception ex)
            {
                NativeLog.Error("No se pudo hacer sonar la alarma SOS.", ex);
                StopLocked(context);
                return false;
            }
        }
    }

    public static void Stop(Context context)
    {
        lock (Gate)
            StopLocked(context);
    }

    /// <summary>Intención de «Silenciar» (botón del aviso y de la notificación fija) y de descartar el aviso.</summary>
    public static PendingIntent? SilenceIntent(Context context)
    {
        var intent = new Intent(context, typeof(SosAlarmStopReceiver));
        return PendingIntent.GetBroadcast(context, 0, intent, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
    }

    private static void StopLocked(Context context)
    {
        MainHandler.RemoveCallbacks(StopRunnable);
        var wasRinging = _player is not null;

        try
        {
            _player?.Stop();
        }
        catch (Exception ex)
        {
            NativeLog.Warn("No se pudo parar la alarma SOS.", ex);
        }

        _player?.Release();
        _player = null;

        try
        {
            _vibrator?.Cancel();
        }
        catch (Exception ex)
        {
            NativeLog.Warn("No se pudo parar la vibración del SOS.", ex);
        }

        _vibrator = null;
        RestoreAlarmVolume(context);

        if (wasRinging)
        {
            try
            {
                context.StopService(new Intent(context, typeof(SosAlarmService)));
            }
            catch (Exception ex)
            {
                NativeLog.Warn("No se pudo parar el servicio de la alarma SOS.", ex);
            }
        }
    }

    private static MediaPlayer? Play(Context context)
    {
        var uri = RingtoneManager.GetDefaultUri(RingtoneType.Alarm)
                  ?? RingtoneManager.GetDefaultUri(RingtoneType.Ringtone)
                  ?? RingtoneManager.GetDefaultUri(RingtoneType.Notification);
        if (uri is null)
            return null;

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
            return player;
        }
        catch
        {
            player.Release();
            throw;
        }
    }

    /// <summary>
    /// El volumen de alarma al máximo: el silencio no lo toca, pero el usuario puede tenerlo bajo
    /// o a cero. Se apunta el de antes para devolverlo al parar.
    /// </summary>
    private static void RaiseAlarmVolume(Context context)
    {
        try
        {
            if (context.GetSystemService(Context.AudioService) is not AudioManager audio)
                return;

            var max = audio.GetStreamMaxVolume(global::Android.Media.Stream.Alarm);
            var now = audio.GetStreamVolume(global::Android.Media.Stream.Alarm);
            if (now >= max)
                return;

            audio.SetStreamVolume(global::Android.Media.Stream.Alarm, max, (VolumeNotificationFlags)0);
            _volumeBefore = now;
            _volumeSet = max;
        }
        catch (Exception ex)
        {
            // Con «No molestar» algunos móviles no dejan tocar el volumen: suena al que tenga.
            NativeLog.Warn("No se pudo subir el volumen de alarma.", ex);
        }
    }

    private static void RestoreAlarmVolume(Context context)
    {
        if (_volumeBefore is not { } before)
            return;

        try
        {
            // Si el usuario lo ha cambiado mientras sonaba, se respeta lo suyo.
            if (context.GetSystemService(Context.AudioService) is AudioManager audio &&
                audio.GetStreamVolume(global::Android.Media.Stream.Alarm) == _volumeSet)
                audio.SetStreamVolume(global::Android.Media.Stream.Alarm, before, (VolumeNotificationFlags)0);
        }
        catch (Exception ex)
        {
            NativeLog.Warn("No se pudo devolver el volumen de alarma.", ex);
        }

        _volumeBefore = null;
        _volumeSet = -1;
    }

    private static void Vibrate(Context context)
    {
        try
        {
            var vibrator = OperatingSystem.IsAndroidVersionAtLeast(31)
                ? (context.GetSystemService(Context.VibratorManagerService) as VibratorManager)?.DefaultVibrator
                : context.GetSystemService(Context.VibratorService) as Vibrator;
            if (vibrator is null || !vibrator.HasVibrator)
                return;

            if (OperatingSystem.IsAndroidVersionAtLeast(26))
            {
                var alarm = new AudioAttributes.Builder().SetUsage(AudioUsageKind.Alarm)!.Build();
#pragma warning disable CA1422 // Vibrate(effect, AudioAttributes) sigue funcionando en Android 13+.
                vibrator.Vibrate(VibrationEffect.CreateWaveform(Vibration, 0), alarm);
#pragma warning restore CA1422
            }
            else
            {
#pragma warning disable CA1422
                vibrator.Vibrate(Vibration, 0);
#pragma warning restore CA1422
            }

            _vibrator = vibrator;
        }
        catch (Exception ex)
        {
            NativeLog.Warn("No se pudo vibrar con el SOS.", ex);
        }
    }

    private static void StartKeepAlive(Context context)
    {
        try
        {
            var intent = new Intent(context, typeof(SosAlarmService));
            if (OperatingSystem.IsAndroidVersionAtLeast(26))
                context.StartForegroundService(intent);
            else
                context.StartService(intent);
        }
        catch (Exception ex)
        {
            // ForegroundServiceStartNotAllowedException: suena igual mientras viva el proceso.
            NativeLog.Warn("No se pudo arrancar el servicio de la alarma SOS.", ex);
        }
    }
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
