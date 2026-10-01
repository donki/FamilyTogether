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

/// <summary>
/// El primer tramo al echar a andar (2026-10-01). El sensor de movimiento significativo tarda en
/// saltar (Android lo deja a criterio del fabricante: de unos segundos a un par de minutos), y las
/// lecturas buenas del GPS que llegan antes se clasifican como «quieto» (<see cref="ReadingPolicy"/>):
/// el recorrido empezaba unas calles despues de salir de casa.
/// </summary>
/// <remarks>
/// Se guardan en memoria las ultimas lecturas GPS buenas (25 m o mejor) tomadas por quieto; cuando
/// el sensor salta, las de los <see cref="ReadingPolicy.MotionWindow"/> anteriores al aviso se
/// devuelven para que entren en el historial con su hora original. Las mas viejas eran deriva de
/// estar quieto y se tiran. Solo memoria: si el proceso muere, se pierde ese tramo, nada mas.
/// </remarks>
public sealed class MotionStartBuffer
{
    /// <summary>Maximo de lecturas guardadas (con una cada 30 s, cinco minutos son diez).</summary>
    public const int Capacity = 10;

    private readonly List<TrackPoint> _held = [];
    private readonly object _lock = new();

    public int Count
    {
        get { lock (_lock) return _held.Count; }
    }

    /// <summary>Guarda una lectura GPS buena tomada por quieto. Las de otros proveedores o peores de 25 m no valen.</summary>
    public void Hold(string? provider, TrackPoint reading)
    {
        if (!string.Equals(provider, ReadingPolicy.GpsProvider, StringComparison.OrdinalIgnoreCase) ||
            reading.Accuracy <= 0 || reading.Accuracy > LocationOutbox.MaxAccuracyMeters)
            return;

        lock (_lock)
        {
            _held.RemoveAll(p => reading.At - p.At > ReadingPolicy.MotionWindow);
            _held.Add(reading);
            if (_held.Count > Capacity)
                _held.RemoveRange(0, _held.Count - Capacity);
        }
    }

    /// <summary>
    /// El sensor ha saltado en <paramref name="motionAt"/>: devuelve, de la mas antigua a la mas
    /// nueva, las lecturas de los <see cref="ReadingPolicy.MotionWindow"/> anteriores, y vacia el
    /// buffer.
    /// </summary>
    public IReadOnlyList<TrackPoint> Release(DateTimeOffset motionAt)
    {
        lock (_lock)
        {
            var result = _held
                .Where(p => p.At <= motionAt && motionAt - p.At <= ReadingPolicy.MotionWindow)
                .OrderBy(p => p.At)
                .ToList();
            _held.Clear();
            return result;
        }
    }

    public void Clear()
    {
        lock (_lock) _held.Clear();
    }
}
