using FamilyTogether.Core;

namespace FamilyTogether.Core.Tests;

/// <summary>Cuando sale la posicion: al moverse o cada hora (2026-10-12).</summary>
public class SendPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 18, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Start = Now.AddHours(-5);

    private static SentFix Sent(double minutesAgo, double accuracy = 8) => new(40, -3, accuracy, Now.AddMinutes(-minutesAgo));

    private static SendReason Fine(double? toFine, SentFix? last, double accuracy = 8, bool first = false) =>
        SendPolicy.Decide(ReadingKind.Fine, accuracy, toFine, toFine, last, first, stillBySensor: false, Start, Now);

    private static SendReason Coarse(double? toLast, SentFix? last, double accuracy = 30, bool still = false) =>
        SendPolicy.Decide(ReadingKind.Coarse, accuracy, null, toLast, last, firstFineOfRun: false, still, Start, Now);

    [Fact]
    public void Buena_movida_25_m_o_mas_sale()
    {
        Assert.Equal(SendReason.Moved, Fine(25, Sent(1)));
        Assert.Equal(SendReason.Moved, Fine(300, Sent(59)));
    }

    [Fact]
    public void Buena_sin_moverse_y_antes_de_la_hora_no_sale()
    {
        Assert.Equal(SendReason.Skip, Fine(24.9, Sent(1)));
        Assert.Equal(SendReason.Skip, Fine(0, Sent(59.9)));
    }

    [Fact]
    public void Quieto_una_hora_o_mas_sale_el_latido()
    {
        Assert.Equal(SendReason.Heartbeat, Fine(3, Sent(60)));
        Assert.Equal(SendReason.Heartbeat, Fine(3, Sent(200)));
        Assert.Equal(SendReason.Heartbeat, Coarse(5, Sent(60), still: true));
        Assert.Equal(SendReason.Heartbeat, Coarse(5, Sent(61)));
    }

    [Fact]
    public void La_primera_buena_del_arranque_o_sin_referencia_sale_siempre()
    {
        Assert.Equal(SendReason.First, Fine(2, Sent(1), first: true));
        Assert.Equal(SendReason.First, Fine(null, Sent(1)));
        Assert.Equal(SendReason.First, Fine(null, null));
        Assert.Equal(SendReason.First, Coarse(null, null));
    }

    [Fact]
    public void Aproximada_con_el_sensor_diciendo_quieto_nunca_es_movimiento()
    {
        // La red baila cientos de metros con el movil en la mesa: solo el latido.
        Assert.Equal(SendReason.Skip, Coarse(1000, Sent(30), accuracy: 20, still: true));
        Assert.Equal(SendReason.Skip, Coarse(150, Sent(59), still: true));
    }

    [Fact]
    public void Aproximada_cuenta_como_movimiento_desde_100_m_y_fuera_de_las_precisiones()
    {
        Assert.Equal(SendReason.Moved, Coarse(100, Sent(5, accuracy: 20), accuracy: 30));
        Assert.Equal(SendReason.Skip, Coarse(99, Sent(5, accuracy: 20), accuracy: 30));

        // Imprecisa: los circulos se tocan (90 + 60 = 150 m), no es movimiento aunque pase de 100.
        Assert.Equal(SendReason.Skip, Coarse(140, Sent(5, accuracy: 60), accuracy: 90));
        Assert.Equal(SendReason.Skip, Coarse(150, Sent(5, accuracy: 60), accuracy: 90));
        Assert.Equal(SendReason.Moved, Coarse(151, Sent(5, accuracy: 60), accuracy: 90));
    }

    [Fact]
    public void Descartada_o_sin_precision_nunca_sale()
    {
        Assert.Equal(SendReason.Skip, SendPolicy.Decide(ReadingKind.Discard, 8, null, null, null, true, false, Start, Now));
        Assert.Equal(SendReason.Skip, SendPolicy.Decide(ReadingKind.Fine, double.NaN, null, null, null, true, false, Start, Now));
        Assert.Equal(SendReason.Skip, SendPolicy.Decide(ReadingKind.Coarse, 0, null, null, null, false, false, Start, Now));
    }

    [Fact]
    public void Latido_sin_nada_enviado_cuenta_desde_el_arranque()
    {
        Assert.False(SendPolicy.HeartbeatDue(null, Now.AddMinutes(-59), Now));
        Assert.True(SendPolicy.HeartbeatDue(null, Now.AddMinutes(-60), Now));
        Assert.False(SendPolicy.HeartbeatDue(Sent(10), Now.AddHours(-3), Now));
        Assert.True(SendPolicy.HeartbeatDue(Sent(60), Now, Now));
    }

    [Fact]
    public void La_alarma_mira_a_la_hora_de_la_ultima_y_si_ya_toca_reintenta_a_los_diez_minutos()
    {
        Assert.Equal(TimeSpan.FromHours(1), SendPolicy.HeartbeatEvery);
        Assert.Equal(Now.AddMinutes(45), SendPolicy.NextCheck(Sent(15), Start, Now));
        Assert.Equal(Now.AddMinutes(20), SendPolicy.NextCheck(null, Now.AddMinutes(-40), Now));
        Assert.Equal(Now + SendPolicy.FixRetryEvery, SendPolicy.NextCheck(Sent(60), Start, Now));
        Assert.Equal(Now + SendPolicy.FixRetryEvery, SendPolicy.NextCheck(Sent(300), Start, Now));
    }
}
