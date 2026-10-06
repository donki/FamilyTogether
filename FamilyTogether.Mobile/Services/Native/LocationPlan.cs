using FamilyTogether.Core;

namespace FamilyTogether.Mobile.Services.Native;

/// <summary>Cómo se escucha la ubicación en cada momento.</summary>
public enum TrackingMode
{
    /// <summary>El móvil se mueve (o no se sabe): GPS y red cada 30 s, como siempre.</summary>
    Moving,

    /// <summary>El móvil está quieto: GPS apagado; solo la red, de vez en cuando, y la pasiva.</summary>
    Still,
}

/// <summary>Una petición a <c>LocationManager.requestLocationUpdates</c>.</summary>
public sealed record ProviderRequest(string Provider, TimeSpan MinTime, float MinDistanceMeters);

/// <summary>
/// Qué proveedores se piden y cada cuánto, según si el móvil se mueve (batería, SC-005 &lt; 5 % en
/// 24 h, 2026-10-06). Sin Android: lo aplica <c>LocationSharingForegroundService</c>.
/// </summary>
/// <remarks>
/// <para><b>El problema.</b> Hasta la 2026.10.05.00 el GPS estaba pedido las 24 h cada 30 s. El
/// <c>minDistance = 25 m</c> solo filtra lo que se entrega a la app: el chip GPS sigue encendido
/// para saber si se ha movido, y dentro de casa, sin cielo, busca satélites sin parar (Android no
/// le pone tiempo máximo con intervalos de menos de 60 s). Era, con diferencia, lo que más gastaba,
/// y no servía de nada: con el móvil quieto el GPS no entra en el historial (FR-010,
/// <see cref="ReadingPolicy"/>).</para>
/// <para><b>Quieto</b> = el dispositivo tiene sensor de movimiento significativo y ni ha saltado,
/// ni el GPS ha medido velocidad de ir andando, ni ha arrancado el servicio en los últimos
/// <see cref="StillAfter"/> (la misma ventana de <see cref="ReadingPolicy.MotionWindow"/>: pasada
/// esa ventana el GPS ya cuenta como deriva). Entonces el GPS se apaga; queda la red cada
/// <see cref="StillNetworkEvery"/> (el mapa del grupo sigue viendo dónde estoy, aproximada, y si
/// el sensor fallara se notaría el cambio de sitio) y la pasiva (lo que pidan otras apps, gratis).
/// Al saltar el sensor se vuelve a <see cref="TrackingMode.Moving"/> en el momento.</para>
/// <para><b>Sin sensor</b> no hay forma barata de saber cuándo vuelve a moverse: siempre
/// <see cref="TrackingMode.Moving"/>, como antes.</para>
/// <para><b>Sin lotes</b> (<c>maxUpdateDelay</c>): moviéndose, juntar lecturas retrasaría la posición
/// en el mapa de los demás por encima de los 60 s de SC-002; quieto, con la red cada 5 min, no hay
/// nada que juntar.</para>
/// </remarks>
public static class LocationPlan
{
    public const string GpsProvider = "gps";
    public const string NetworkProvider = "network";
    public const string PassiveProvider = "passive";

    /// <summary>Intervalo mínimo entre lecturas de un mismo proveedor moviéndose (el de siempre).</summary>
    public static readonly TimeSpan MovingEvery = TimeSpan.FromSeconds(30);

    /// <summary>Quieto: la red cada tanto (un escaneo de wifi, nada de GPS).</summary>
    public static readonly TimeSpan StillNetworkEvery = TimeSpan.FromMinutes(5);

    /// <summary>Quieto: lo que otras apps pidan, como mucho una por minuto.</summary>
    public static readonly TimeSpan StillPassiveEvery = TimeSpan.FromMinutes(1);

    /// <summary>Sin movimiento durante este tiempo, el móvil está quieto.</summary>
    public static TimeSpan StillAfter => ReadingPolicy.MotionWindow;

    /// <param name="motionSensor">El sensor de movimiento significativo está armado.</param>
    /// <param name="lastMotion">Último disparo del sensor.</param>
    /// <param name="lastFastGps">Última lectura GPS con velocidad de ir andando o más.</param>
    /// <param name="startedAt">Cuándo empezó a escuchar el servicio (la primera posición sale con el GPS).</param>
    public static TrackingMode ModeFor(bool motionSensor, DateTimeOffset? lastMotion, DateTimeOffset? lastFastGps,
        DateTimeOffset startedAt, DateTimeOffset now)
    {
        if (!motionSensor)
            return TrackingMode.Moving;

        var latest = startedAt;
        if (lastMotion is { } m && m > latest)
            latest = m;
        if (lastFastGps is { } f && f > latest)
            latest = f;

        return now - latest < StillAfter ? TrackingMode.Moving : TrackingMode.Still;
    }

    /// <summary>Cuándo pasaría a quieto si no pasa nada más (para programar la comprobación).</summary>
    public static DateTimeOffset StillAt(DateTimeOffset? lastMotion, DateTimeOffset? lastFastGps, DateTimeOffset startedAt)
    {
        var latest = startedAt;
        if (lastMotion is { } m && m > latest)
            latest = m;
        if (lastFastGps is { } f && f > latest)
            latest = f;
        return latest + StillAfter;
    }

    /// <summary>
    /// Las peticiones para un modo, solo de los proveedores que hay. Siempre con la distancia de
    /// 25 m (FR-009): el sistema no despierta a la app si no ha cambiado de sitio.
    /// </summary>
    public static IReadOnlyList<ProviderRequest> Requests(TrackingMode mode, IReadOnlyCollection<string> available)
    {
        var wanted = mode == TrackingMode.Moving
            ? new[]
            {
                new ProviderRequest(GpsProvider, MovingEvery, SharingEngine.ThresholdMeters),
                new ProviderRequest(NetworkProvider, MovingEvery, SharingEngine.ThresholdMeters),
            }
            : new[]
            {
                new ProviderRequest(NetworkProvider, StillNetworkEvery, SharingEngine.ThresholdMeters),
                // La pasiva no enciende nada: recibe lo que el sistema ya ha calculado para otras
                // apps (un navegador, el tiempo). Si alguna trae GPS con velocidad, se pasa a moverse.
                new ProviderRequest(PassiveProvider, StillPassiveEvery, SharingEngine.ThresholdMeters),
            };

        return [.. wanted.Where(r => available.Contains(r.Provider))];
    }
}
