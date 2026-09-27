using FamilyTogether.Core;

namespace FamilyTogether.Core.Tests;

public class ReadingPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 10, 0, 0, TimeSpan.Zero);

    private static ReadingKind C(string provider, double acc, double? speed = null, bool sensor = true, double? motionMinutesAgo = 1, bool first = false) =>
        ReadingPolicy.Classify(provider, acc, speed, sensor, motionMinutesAgo is { } m ? Now.AddMinutes(-m) : null, Now, first);

    [Fact]
    public void La_red_nunca_entra_en_el_historial_aunque_diga_buena_precision()
    {
        Assert.Equal(ReadingKind.Coarse, C("network", 8));
        Assert.Equal(ReadingKind.Coarse, C("passive", 5));
        Assert.Equal(ReadingKind.Coarse, C("network", 8, first: true));
    }

    [Fact]
    public void Gps_bueno_moviendose_entra()
    {
        Assert.Equal(ReadingKind.Fine, C("gps", 10));
    }

    [Fact]
    public void Gps_con_el_movil_quieto_es_aproximada()
    {
        Assert.Equal(ReadingKind.Coarse, C("gps", 10, motionMinutesAgo: null));
        Assert.Equal(ReadingKind.Coarse, C("gps", 10, motionMinutesAgo: 6));
        Assert.Equal(ReadingKind.Coarse, C("gps", 10, speed: 0.3, motionMinutesAgo: null));
    }

    [Fact]
    public void Gps_quieto_pero_con_velocidad_de_andar_entra()
    {
        Assert.Equal(ReadingKind.Fine, C("gps", 10, speed: 1.6, motionMinutesAgo: null));
    }

    [Fact]
    public void Sin_sensor_de_movimiento_como_antes()
    {
        Assert.Equal(ReadingKind.Fine, C("gps", 10, sensor: false, motionMinutesAgo: null));
    }

    [Fact]
    public void La_primera_del_arranque_entra_si_es_gps_buena()
    {
        Assert.Equal(ReadingKind.Fine, C("gps", 10, motionMinutesAgo: null, first: true));
        Assert.Equal(ReadingKind.Coarse, C("gps", 40, first: true));
    }

    [Fact]
    public void Precision_mala_aproximada_o_fuera()
    {
        Assert.Equal(ReadingKind.Coarse, C("gps", 40));
        Assert.Equal(ReadingKind.Discard, C("gps", 150));
        Assert.Equal(ReadingKind.Discard, C("network", 0));
    }
}
