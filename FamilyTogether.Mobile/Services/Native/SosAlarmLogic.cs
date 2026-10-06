namespace FamilyTogether.Mobile.Services.Native;

/// <summary>
/// Lo que sabe hacer el móvil para la alarma SOS (en Android, <c>AndroidAlarmDevice</c>; en las
/// pruebas, un doble). Cada método es una llamada al sistema, sin decisiones.
/// </summary>
public interface IAlarmDevice
{
    /// <summary>Empieza el tono de alarma en bucle por el flujo de alarma. False si no hay tono.</summary>
    bool PlaySound();

    void StopSound();

    /// <summary>Vibración de alarma en bucle (sin vibrador, no hace nada).</summary>
    void Vibrate();

    void StopVibration();

    /// <summary>Volumen de alarma actual y máximo.</summary>
    (int Now, int Max) AlarmVolume();

    void SetAlarmVolume(int volume);

    /// <summary>El servicio en primer plano que mantiene vivo el proceso mientras suena.</summary>
    void StartKeepAlive();

    void StopKeepAlive();

    /// <summary>Programa la parada (sustituye la que hubiera).</summary>
    void ScheduleStop(TimeSpan after);

    void CancelScheduledStop();
}

/// <summary>
/// La alarma de un SOS recibido cuando el usuario quiere que suene aunque el móvil esté en
/// silencio (<see cref="SharingState.SosLoud"/>): cuándo suena, cuánto, y cómo deja el volumen.
/// </summary>
/// <remarks>
/// <para>El sonido de un canal de notificación lo calla el modo silencio o vibración (y en MIUI,
/// también con el uso de alarma). Por eso la app reproduce ella misma el tono por el flujo de
/// <b>alarma</b>, que ni el silencio ni la vibración tocan (igual que el despertador), con el
/// volumen de alarma al máximo mientras suena; al parar lo deja como estaba, salvo que el usuario
/// lo haya cambiado mientras tanto. Vibra a la vez.</para>
/// <para>Suena hasta <see cref="Duration"/> (un SOS nuevo vuelve a empezar la cuenta), hasta tocar
/// «Silenciar», descartar el aviso o abrir la app. Un fallo del sistema nunca rompe el aviso: si
/// no llega a sonar, <see cref="Start"/> devuelve false y el aviso va por el canal SOS normal.</para>
/// </remarks>
public sealed class SosAlarmLogic(IAlarmDevice device)
{
    /// <summary>Lo que suena como mucho.</summary>
    public static readonly TimeSpan Duration = TimeSpan.FromMinutes(1);

    private readonly object _gate = new();
    private bool _ringing;
    private int? _volumeBefore;
    private int _volumeSet = -1;

    public bool IsRinging
    {
        get { lock (_gate) return _ringing; }
    }

    /// <summary>Empieza a sonar (o, si ya sonaba, alarga la cuenta). Devuelve si suena.</summary>
    public bool Start()
    {
        lock (_gate)
        {
            try
            {
                if (!_ringing)
                {
                    RaiseAlarmVolume();
                    if (!device.PlaySound())
                    {
                        RestoreAlarmVolume();
                        return false;
                    }

                    _ringing = true;
                    Try(device.Vibrate, "No se pudo vibrar con el SOS.");
                    // ForegroundServiceStartNotAllowedException: suena igual mientras viva el proceso.
                    Try(device.StartKeepAlive, "No se pudo arrancar el servicio de la alarma SOS.");
                }

                device.ScheduleStop(Duration);
                return true;
            }
            catch (Exception ex)
            {
                NativeLog.Error("No se pudo hacer sonar la alarma SOS.", ex);
                StopLocked();
                return false;
            }
        }
    }

    public void Stop()
    {
        lock (_gate)
            StopLocked();
    }

    private void StopLocked()
    {
        Try(device.CancelScheduledStop, "No se pudo anular la parada de la alarma SOS.");
        var wasRinging = _ringing;
        _ringing = false;
        Try(device.StopSound, "No se pudo parar la alarma SOS.");
        Try(device.StopVibration, "No se pudo parar la vibración del SOS.");
        RestoreAlarmVolume();
        if (wasRinging)
            Try(device.StopKeepAlive, "No se pudo parar el servicio de la alarma SOS.");
    }

    /// <summary>
    /// El volumen de alarma al máximo: el silencio no lo toca, pero el usuario puede tenerlo bajo
    /// o a cero. Se apunta el de antes para devolverlo al parar.
    /// </summary>
    private void RaiseAlarmVolume()
    {
        try
        {
            var (now, max) = device.AlarmVolume();
            if (now >= max)
                return;

            device.SetAlarmVolume(max);
            _volumeBefore = now;
            _volumeSet = max;
        }
        catch (Exception ex)
        {
            // Con «No molestar» algunos móviles no dejan tocar el volumen: suena al que tenga.
            NativeLog.Warn("No se pudo subir el volumen de alarma.", ex);
        }
    }

    private void RestoreAlarmVolume()
    {
        if (_volumeBefore is not { } before)
            return;

        try
        {
            // Si el usuario lo ha cambiado mientras sonaba, se respeta lo suyo.
            if (device.AlarmVolume().Now == _volumeSet)
                device.SetAlarmVolume(before);
        }
        catch (Exception ex)
        {
            NativeLog.Warn("No se pudo devolver el volumen de alarma.", ex);
        }

        _volumeBefore = null;
        _volumeSet = -1;
    }

    private static void Try(Action action, string error)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            NativeLog.Warn(error, ex);
        }
    }
}
