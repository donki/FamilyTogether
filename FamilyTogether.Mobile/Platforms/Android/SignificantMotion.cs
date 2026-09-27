using Android.Content;
using Android.Hardware;

namespace FamilyTogether.Mobile.Platforms.Android;

/// <summary>
/// Sensor de movimiento significativo (<c>TYPE_SIGNIFICANT_MOTION</c>) para saber si el móvil se
/// ha movido de verdad, sin Google Play Services (ReadingPolicy, FR-010 del 2026-09-28).
/// </summary>
/// <remarks>
/// <para>Es un sensor de disparo único y de activación: lo vigila el concentrador de sensores del
/// propio chip, no la app, y solo despierta al procesador cuando detecta que el usuario cambia de
/// sitio (andar, bici, coche). Android lo exige de bajo consumo (en la práctica, décimas de mA; nada
/// comparado con el GPS). Tras cada disparo se vuelve a armar.</para>
/// <para>Si el dispositivo no lo tiene (o falla), <see cref="Available"/> es falso y todo sigue como
/// antes: el GPS bueno entra en el historial sin mirar el movimiento.</para>
/// </remarks>
internal sealed class SignificantMotion : TriggerEventListener
{
    private SensorManager? _manager;
    private Sensor? _sensor;
    private DateTimeOffset _lastLogged = DateTimeOffset.MinValue;

    public bool Available { get; private set; }

    /// <summary>Último disparo del sensor.</summary>
    public DateTimeOffset? LastMotion { get; private set; }

    public void Start(Context context)
    {
        try
        {
            _manager = (SensorManager?)context.GetSystemService(Context.SensorService);
            _sensor = _manager?.GetDefaultSensor(SensorType.SignificantMotion);
            if (_sensor is null)
            {
                NativeLog.Info("Sin sensor de movimiento significativo: el GPS bueno entra en el historial como antes.");
                return;
            }

            Available = Arm();
            NativeLog.Info($"Sensor de movimiento significativo: {(Available ? "activo" : "no se pudo armar")} ({_sensor.Name}, {_sensor.Power} mA).");
        }
        catch (Exception ex)
        {
            Available = false;
            NativeLog.Warn("No se pudo usar el sensor de movimiento significativo.", ex);
        }
    }

    public void Stop()
    {
        try
        {
            if (_manager is not null && _sensor is not null)
                _manager.CancelTriggerSensor(this, _sensor);
        }
        catch (Exception)
        {
            // Nada que cancelar.
        }

        Available = false;
    }

    public override void OnTrigger(TriggerEvent? e)
    {
        var now = DateTimeOffset.UtcNow;
        LastMotion = now;
        if (now - _lastLogged > TimeSpan.FromMinutes(10))
        {
            _lastLogged = now;
            NativeLog.Info("Movimiento significativo: el móvil se está moviendo.");
        }

        // De un solo disparo: hay que volver a armarlo.
        if (!Arm())
            Available = false;
    }

    private bool Arm()
    {
        try
        {
            return _manager is not null && _sensor is not null && _manager.RequestTriggerSensor(this, _sensor);
        }
        catch (Exception ex)
        {
            NativeLog.Warn("No se pudo armar el sensor de movimiento significativo.", ex);
            return false;
        }
    }
}
