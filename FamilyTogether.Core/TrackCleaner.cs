namespace FamilyTogether.Core;

/// <summary>
/// Limpia un recorrido antes de dibujarlo (y de ajustarlo a calles): quita los saltos de ida y
/// vuelta imposibles y junta las paradas. Solo para dibujar; lo guardado no cambia.
/// </summary>
/// <remarks>
/// <para><b>Saltos.</b> La ubicacion por red (y a veces el GPS entre edificios) da de vez en cuando
/// una posicion a cientos de metros que enseguida «vuelve»: en el dibujo es una punta de 1 km que
/// cruza un rio. Se reconoce como una excursion: desde la ultima posicion buena se salta mas de
/// <see cref="MinJumpMeters"/> y, en las <see cref="MaxExcursionPoints"/> siguientes, se vuelve de
/// otro salto igual cerca del punto de partida. No hace falta mirar la velocidad (en el Xiaomi las
/// puntas llegaban con minutos entre medias, en casa): el movil envia una posicion cada 25 m, asi
/// que un viaje real de ida y vuelta de mas de 300 m deja muchas lecturas por el camino.
/// Ademas se descarta cualquier posicion que exija mas de <see cref="MaxSpeedKmh"/> desde la ultima
/// buena (ni en autopista), siempre que el salto sea mayor que las dos precisiones juntas.</para>
///
/// <para><b>Paradas.</b> Posiciones seguidas a menos de su precision de la primera de la parada son
/// la misma: se juntan en una (media ponderada por 1/precision², con la mejor precision y la hora de
/// la primera). Asi una hora en casa no dibuja una maraña.</para>
/// </remarks>
public static class TrackCleaner
{
    internal const double MinJumpMeters = 300;
    internal const double MaxRoundTripSpeedKmh = 50;
    internal const double MaxSpeedKmh = 200;
    internal const int MaxExcursionPoints = 5;

    /// <summary>Radio minimo de una parada, en metros.</summary>
    internal const double MinStopRadius = 15;

    public static IReadOnlyList<TrackPoint> Clean(IReadOnlyList<TrackPoint> points)
    {
        if (points.Count < 3)
            return points;

        return MergeStops(DropLoneEnds(DropJumps(points)));
    }

    /// <summary>
    /// Una punta al principio o al final no tiene vuelta que la delate: se quita si llega o sale
    /// con un salto rapido y largo mientras sus vecinas estan quietas entre si.
    /// </summary>
    internal static List<TrackPoint> DropLoneEnds(List<TrackPoint> points)
    {
        bool Lone(TrackPoint end, TrackPoint next, TrackPoint after) =>
            Distance(end, next) > Math.Max(MinJumpMeters, Margin(end, next)) &&
            Speed(end, next) > MaxRoundTripSpeedKmh &&
            Distance(next, after) < Distance(end, next) * 0.3;

        if (points.Count >= 3 && Lone(points[0], points[1], points[2]))
            points.RemoveAt(0);
        if (points.Count >= 3 && Lone(points[^1], points[^2], points[^3]))
            points.RemoveAt(points.Count - 1);
        return points;
    }

    /// <summary>Quita excursiones de ida y vuelta imposibles y saltos a velocidad imposible.</summary>
    internal static List<TrackPoint> DropJumps(IReadOnlyList<TrackPoint> points)
    {
        var kept = new List<TrackPoint> { points[0] };
        var i = 1;
        while (i < points.Count)
        {
            var from = kept[^1];
            var here = points[i];
            var jump = Distance(from, here);
            var margin = Margin(from, here);

            // Sin mirar la velocidad: las posiciones salen cada 25 m, asi que ir y volver de verdad a
            // mas de 300 m deja muchas por el camino. Una o pocas lecturas sueltas alla y vuelta es
            // ruido de la ubicacion aunque pasen minutos (en casa no hay lecturas entre medias).
            if (jump > Math.Max(MinJumpMeters, margin))
            {
                // ¿Vuelve pronto y deprisa cerca de donde estaba?
                var back = -1;
                for (var k = i + 1; k < points.Count && k <= i + MaxExcursionPoints; k++)
                {
                    if (Distance(from, points[k]) < Math.Max(jump * 0.3, Margin(from, points[k])) &&
                        Distance(points[k - 1], points[k]) > Math.Max(MinJumpMeters, Margin(points[k - 1], points[k])))
                    {
                        back = k;
                        break;
                    }
                }

                if (back > 0)
                {
                    i = back;   // se salta la excursion entera
                    continue;
                }
            }

            if (jump > margin && Speed(from, here) > MaxSpeedKmh)
            {
                i++;
                continue;
            }

            kept.Add(here);
            i++;
        }

        return kept;
    }

    /// <summary>Junta en una sola las posiciones seguidas de una parada.</summary>
    internal static List<TrackPoint> MergeStops(IReadOnlyList<TrackPoint> points)
    {
        var result = new List<TrackPoint>();
        var i = 0;
        while (i < points.Count)
        {
            var anchor = points[i];
            var j = i + 1;
            while (j < points.Count && Distance(anchor, points[j]) <= Math.Max(MinStopRadius, Math.Max(Acc(anchor), Acc(points[j]))))
                j++;

            if (j - i == 1)
            {
                result.Add(anchor);
            }
            else
            {
                double sw = 0, lat = 0, lon = 0, best = double.MaxValue;
                for (var k = i; k < j; k++)
                {
                    var w = 1 / Math.Pow(Math.Max(Acc(points[k]), 1), 2);
                    sw += w;
                    lat += points[k].Lat * w;
                    lon += points[k].Lon * w;
                    best = Math.Min(best, Acc(points[k]));
                }
                result.Add(new TrackPoint(lat / sw, lon / sw, best, anchor.At));
            }

            i = j;
        }

        return result;
    }

    private static double Acc(TrackPoint p) => p.Accuracy is > 0 and < 1000 ? p.Accuracy : 25;

    private static double Margin(TrackPoint a, TrackPoint b) => Acc(a) + Acc(b);

    private static double Distance(TrackPoint a, TrackPoint b) => Geo.DistanceMeters(a.Lat, a.Lon, b.Lat, b.Lon);

    /// <summary>Velocidad en km/h entre dos posiciones; sin horas, 0 (no se juzga).</summary>
    private static double Speed(TrackPoint a, TrackPoint b)
    {
        if (a.At == default || b.At == default)
            return 0;

        var seconds = Math.Abs((b.At - a.At).TotalSeconds);
        return Distance(a, b) / Math.Max(seconds, 1) * 3.6;
    }
}
