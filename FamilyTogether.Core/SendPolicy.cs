namespace FamilyTogether.Core;

/// <summary>Por que se envia (o no) una lectura ya clasificada por <see cref="ReadingPolicy"/>.</summary>
public enum SendReason
{
    /// <summary>No se envia: no se ha movido de verdad y aun no toca el latido.</summary>
    Skip,

    /// <summary>No hay referencia (la primera buena del arranque, o nunca se ha enviado nada).</summary>
    First,

    /// <summary>Se ha movido de verdad desde la ultima enviada.</summary>
    Moved,

    /// <summary>Quieto, pero hace <see cref="SendPolicy.HeartbeatEvery"/> o mas que no sale nada.</summary>
    Heartbeat,
}

/// <summary>Una posicion enviada (puesta en la cola), de referencia para la siguiente.</summary>
public readonly record struct SentFix(double Lat, double Lon, double Accuracy, DateTimeOffset At);

/// <summary>
/// Cuando se envia la posicion al servidor: <b>al moverse o cada hora</b> (Josep, 2026-10-12).
/// </summary>
/// <remarks>
/// <para><b>Antes.</b> Una lectura buena (GPS de 25 m o mejor con el movil moviendose) salia a 25 m
/// de la ultima buena; una aproximada (red, o GPS con el movil quieto) cada 10 min como mucho, se
/// moviera o no: con el movil en casa toda la tarde, la red (que baila decenas de metros) mandaba
/// una posicion cada 10 min. Y con el movil quieto del todo, si el sistema no entregaba ninguna
/// lectura (el <c>minDistance</c> de 25 m), el grupo podia pasar horas sin saber si seguia vivo.</para>
/// <para><b>Ahora.</b></para>
/// <list type="number">
/// <item><b>Buena</b> (<see cref="ReadingKind.Fine"/>): ya es movimiento de verdad (sensor o
/// velocidad, <see cref="ReadingPolicy"/>); sale si esta a <see cref="FineMoveMeters"/> o mas de la
/// ultima buena (FR-009, el detalle del recorrido). La primera de cada arranque sale siempre.</item>
/// <item><b>Aproximada</b> (<see cref="ReadingKind.Coarse"/>): con el sensor diciendo que esta
/// quieto, nunca cuenta como movimiento (es el baile de la red). Si no, solo si esta a
/// <see cref="CoarseMoveMeters"/> o mas de la ultima enviada <b>y</b> los dos circulos de
/// precision no se tocan (distancia &gt; precision de esta + precision de aquella).</item>
/// <item><b>Latido</b>: pasada <see cref="HeartbeatEvery"/> desde la ultima enviada (de cualquier
/// clase), sale la siguiente lectura valida aunque no se haya movido.</item>
/// </list>
/// <para>Lo que decide aqui pasa ademas por los filtros de la cola (<see cref="LocationOutbox"/>:
/// una aproximada no sale si en 10 min ha salido otra o una buena), salvo el latido, que va
/// forzado.</para>
/// </remarks>
public static class SendPolicy
{
    /// <summary>Latido: como mucho este tiempo sin enviar nada aunque el movil no se mueva.</summary>
    public static readonly TimeSpan HeartbeatEvery = TimeSpan.FromHours(1);

    /// <summary>
    /// Si toca el latido y no llega ninguna lectura (sin cobertura, proveedor apagado), cada cuanto
    /// se vuelve a pedir una.
    /// </summary>
    public static readonly TimeSpan FixRetryEvery = TimeSpan.FromMinutes(10);

    /// <summary>Desplazamiento minimo de una lectura buena (FR-009).</summary>
    public const double FineMoveMeters = 25;

    /// <summary>Desplazamiento minimo de una aproximada para contar como movimiento.</summary>
    public const double CoarseMoveMeters = 100;

    /// <param name="kind">Lo que dijo <see cref="ReadingPolicy.Classify"/> (con la antiguedad ya mirada).</param>
    /// <param name="accuracy">Precision de la lectura, en metros.</param>
    /// <param name="distanceToLastFine">Distancia a la ultima buena enviada, o <c>null</c> si no hay.</param>
    /// <param name="distanceToLast">Distancia a la ultima enviada de cualquier clase (<paramref name="last"/>), o <c>null</c>.</param>
    /// <param name="last">La ultima enviada de cualquier clase (para el latido y las aproximadas).</param>
    /// <param name="firstFineOfRun">Aun no ha salido ninguna buena desde que arranco el servicio.</param>
    /// <param name="stillBySensor">El sensor de movimiento significativo dice que esta quieto.</param>
    /// <param name="since">Si no hay <paramref name="last"/>, desde cuando se cuenta el latido (el arranque).</param>
    public static SendReason Decide(
        ReadingKind kind, double accuracy, double? distanceToLastFine, double? distanceToLast,
        SentFix? last, bool firstFineOfRun, bool stillBySensor, DateTimeOffset since, DateTimeOffset now)
    {
        if (kind == ReadingKind.Discard || double.IsNaN(accuracy) || accuracy <= 0)
            return SendReason.Skip;

        if (kind == ReadingKind.Fine)
        {
            if (firstFineOfRun || distanceToLastFine is null)
                return SendReason.First;
            if (distanceToLastFine >= FineMoveMeters)
                return SendReason.Moved;
            return HeartbeatDue(last, since, now) ? SendReason.Heartbeat : SendReason.Skip;
        }

        // Aproximada.
        if (last is not { } previous || distanceToLast is not { } distance)
            return SendReason.First;

        if (!stillBySensor && distance >= CoarseMoveMeters && distance > accuracy + previous.Accuracy)
            return SendReason.Moved;

        return HeartbeatDue(last, since, now) ? SendReason.Heartbeat : SendReason.Skip;
    }

    /// <summary>Toca el latido: hace <see cref="HeartbeatEvery"/> o mas de la ultima enviada (o del arranque, si nunca).</summary>
    public static bool HeartbeatDue(SentFix? last, DateTimeOffset since, DateTimeOffset now) =>
        now - (last?.At ?? since) >= HeartbeatEvery;

    /// <summary>
    /// Cuando volver a mirar el latido (la alarma del servicio): a la hora de la ultima enviada; si
    /// ya toca (se ha pedido una lectura y aun no ha llegado), dentro de <see cref="FixRetryEvery"/>.
    /// </summary>
    public static DateTimeOffset NextCheck(SentFix? last, DateTimeOffset since, DateTimeOffset now)
    {
        var due = (last?.At ?? since) + HeartbeatEvery;
        return due <= now ? now + FixRetryEvery : due;
    }
}
