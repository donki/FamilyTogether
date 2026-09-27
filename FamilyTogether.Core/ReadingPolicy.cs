namespace FamilyTogether.Core;

/// <summary>Que se hace con una lectura de ubicacion.</summary>
public enum ReadingKind
{
    /// <summary>No vale para nada (precision peor de 100 m).</summary>
    Discard,

    /// <summary>Aproximada: solo actualiza la ultima posicion del mapa, ni historial ni zonas.</summary>
    Coarse,

    /// <summary>Buena: historial, zonas y mapa.</summary>
    Fine,
}

/// <summary>
/// Decide si una lectura entra en el historial (FR-010, decision de Josep del 2026-09-28).
/// </summary>
/// <remarks>
/// <para><b>El problema.</b> Con el Xiaomi quieto en casa toda la tarde, el historial dibujo cuatro
/// horas de «paseos» de hasta 1 km cruzando el Besos: lecturas del proveedor de red (wifi y
/// antenas) que dicen 15-25 m de precision y aciertan de lejos, y derivas del GPS.</para>
///
/// <para><b>Reglas.</b></para>
/// <list type="number">
/// <item>Precision peor de 100 m: fuera.</item>
/// <item><b>Solo el GPS entra en el historial.</b> Una lectura de otro proveedor (red, pasiva) es
/// siempre aproximada, diga lo que diga su precision.</item>
/// <item>GPS peor de 25 m: aproximada.</item>
/// <item><b>GPS con el movil quieto</b>: si el dispositivo tiene sensor de movimiento significativo
/// y no ha saltado en los ultimos <see cref="MotionWindow"/>, el cambio de sitio es deriva, no
/// movimiento: aproximada. Salvo que el propio GPS mida velocidad de ir andando o mas
/// (<see cref="MovingSpeed"/>), que tambien cuenta como movimiento. Sin ese sensor, como antes.</item>
/// <item>La primera buena de cada arranque del servicio entra siempre (el grupo tiene que ver una
/// posicion reciente), si es del GPS y de 25 m o mejor.</item>
/// </list>
/// </remarks>
public static class ReadingPolicy
{
    public const string GpsProvider = "gps";

    /// <summary>Tras un aviso del sensor, se considera que el movil se mueve durante este tiempo.</summary>
    public static readonly TimeSpan MotionWindow = TimeSpan.FromMinutes(5);

    /// <summary>Velocidad medida por el GPS (m/s) a partir de la cual el movil se mueve seguro (~5 km/h).</summary>
    public const double MovingSpeed = 1.4;

    /// <param name="provider">Proveedor de Android (<c>gps</c>, <c>network</c>, <c>fused</c>...).</param>
    /// <param name="speed">Velocidad del GPS en m/s, si la trae.</param>
    /// <param name="motionSensor">El dispositivo tiene sensor de movimiento significativo funcionando.</param>
    /// <param name="lastMotion">Ultimo aviso del sensor (null si no ha saltado nunca).</param>
    /// <param name="firstOfRun">Aun no ha salido ninguna buena desde que arranco el servicio.</param>
    public static ReadingKind Classify(
        string? provider, double accuracy, double? speed,
        bool motionSensor, DateTimeOffset? lastMotion, DateTimeOffset now, bool firstOfRun)
    {
        if (double.IsNaN(accuracy) || accuracy <= 0 || accuracy > LocationOutbox.CoarseMaxAccuracyMeters)
            return ReadingKind.Discard;

        if (!string.Equals(provider, GpsProvider, StringComparison.OrdinalIgnoreCase))
            return ReadingKind.Coarse;

        if (accuracy > LocationOutbox.MaxAccuracyMeters)
            return ReadingKind.Coarse;

        if (firstOfRun || !motionSensor)
            return ReadingKind.Fine;

        var moving = (lastMotion is { } m && now - m < MotionWindow) || speed is >= MovingSpeed;
        return moving ? ReadingKind.Fine : ReadingKind.Coarse;
    }
}
