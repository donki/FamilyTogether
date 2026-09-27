using FamilyTogether.Core;

namespace FamilyTogether.Core.Tests;

/// <summary>Limpieza de la traza con recorridos sinteticos en metros alrededor de un origen.</summary>
public class TrackCleanerTests
{
    private const double MetersPerDegree = 111_195.0;
    private static readonly DateTimeOffset T0 = new(2026, 9, 27, 19, 0, 0, TimeSpan.Zero);

    private static TrackPoint P(double x, double y, double minutes, double accuracy = 10) =>
        new(41.43 + y / MetersPerDegree, 2.22 + x / (MetersPerDegree * Math.Cos(41.43 * Math.PI / 180)), accuracy, T0.AddMinutes(minutes));

    private static double X(TrackPoint p) => (p.Lon - 2.22) * MetersPerDegree * Math.Cos(41.43 * Math.PI / 180);

    [Fact]
    public void Ida_y_vuelta_de_1_km_en_un_minuto_se_quita()
    {
        // Andando hacia el este a 80 m/min; en el minuto 3 salta 1 km al norte y al 4 vuelve.
        var track = new[] { P(0, 0, 0), P(80, 0, 1), P(160, 0, 2), P(200, 1000, 3, 25), P(240, 0, 4), P(320, 0, 5) };

        var clean = TrackCleaner.Clean(track);

        Assert.Equal(5, clean.Count);
        Assert.DoesNotContain(clean, p => (p.Lat - 41.43) * MetersPerDegree > 100);
    }

    [Fact]
    public void Excursion_de_varios_puntos_tambien_se_quita()
    {
        var track = new[]
        {
            P(0, 0, 0), P(80, 0, 1),
            P(100, 900, 2, 60), P(120, 950, 2.5, 60), P(110, 920, 3, 60),   // tres lecturas al otro lado del rio
            P(160, 0, 4), P(240, 0, 5),
        };

        var clean = TrackCleaner.Clean(track);

        Assert.Equal(4, clean.Count);
    }

    [Fact]
    public void Un_viaje_de_verdad_en_coche_se_respeta()
    {
        // 1 km cada minuto (60 km/h) en linea recta, sin volver: es un desplazamiento real.
        var track = Enumerable.Range(0, 8).Select(i => P(i * 1000, 0, i)).ToList();

        Assert.Equal(8, TrackCleaner.Clean(track).Count);
    }

    [Fact]
    public void Salto_a_velocidad_imposible_se_quita_aunque_no_vuelva_a_su_sitio()
    {
        // 5 km en 30 s (600 km/h) y luego sigue desde donde estaba.
        var track = new[] { P(0, 0, 0), P(80, 0, 1), P(5080, 0, 1.5), P(160, 0, 2), P(240, 0, 3) };

        var clean = TrackCleaner.Clean(track);

        Assert.DoesNotContain(clean, p => X(p) > 1000);
        Assert.Equal(4, clean.Count);
    }

    [Fact]
    public void Punta_al_final_se_quita()
    {
        var track = new[] { P(0, 0, 0), P(80, 0, 1), P(160, 0, 2), P(170, 5, 3), P(1200, 800, 4, 30) };

        var clean = TrackCleaner.Clean(track);

        Assert.DoesNotContain(clean, p => X(p) > 500);
    }

    [Fact]
    public void Parada_se_junta_en_un_punto()
    {
        // Diez lecturas en casa dentro de su precision (20 m), y luego se pone en marcha.
        var random = new Random(1);
        var track = Enumerable.Range(0, 10).Select(i => P(random.NextDouble() * 20 - 10, random.NextDouble() * 20 - 10, i * 5, 20))
            .Concat([P(200, 0, 55), P(400, 0, 58)])
            .ToList();

        var clean = TrackCleaner.Clean(track);

        Assert.Equal(3, clean.Count);
        Assert.True(Math.Abs(X(clean[0])) < 10);
        Assert.Equal(T0, clean[0].At);
    }

    [Fact]
    public void Sin_horas_no_se_juzgan_velocidades()
    {
        var track = new[] { new TrackPoint(41.43, 2.22, 10), new TrackPoint(41.44, 2.22, 10), new TrackPoint(41.45, 2.22, 10) };   // saltos de 1 km sin hora: no se puede saber si es imposible

        Assert.Equal(3, TrackCleaner.Clean(track).Count);
    }

    [Fact]
    public void Punta_lenta_desde_casa_tambien_se_quita()
    {
        // En casa (sin lecturas entre medias) salta 900 m a los 10 min y vuelve a los 20: 5 km/h.
        var track = new[] { P(0, 0, 0), P(30, 0, 5), P(600, 700, 15, 20), P(10, 5, 25), P(40, 0, 30) };

        var clean = TrackCleaner.Clean(track);

        Assert.DoesNotContain(clean, p => X(p) > 100);
    }

    [Fact]
    public void Ida_y_vuelta_real_con_lecturas_por_el_camino_se_respeta()
    {
        // A la tienda, 600 m, y vuelta, con una lectura cada 30 m (andando).
        var go = Enumerable.Range(0, 21).Select(i => P(i * 30, 0, i * 0.4));
        var back = Enumerable.Range(1, 20).Select(i => P(600 - i * 30, 3, 20 + i * 0.4));
        var track = go.Concat(back).ToList();

        var clean = TrackCleaner.Clean(track);

        Assert.Contains(clean, p => X(p) > 550);
    }
}
