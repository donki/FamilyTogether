using FamilyLink.Core;

namespace FamilyLink.Core.Tests;

public class InviteCodeTests
{
    [Theory]
    [InlineData("ABCD2345", "ABCD2345")]
    [InlineData("abcd2345", "ABCD2345")]
    [InlineData("  abcd-2345 ", "ABCD2345")]
    [InlineData("AB CD 23 45", "ABCD2345")]
    [InlineData("familylink://join?c=ABCD2345", "ABCD2345")]
    [InlineData("FAMILYLINK://join?c=abcd-2345", "ABCD2345")]
    [InlineData("familylink://join?x=1&c=QRST6789", "QRST6789")]
    public void CodigosValidos(string input, string expected)
    {
        Assert.True(InviteCodes.TryParse(input, out var code));
        Assert.Equal(expected, code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ABC")]
    [InlineData("ABCD23456")]
    [InlineData("ABCD0O1I")]              // letras y cifras que no estan en el alfabeto
    [InlineData("familylink://join")]
    [InlineData("familylink://join?d=ABCD2345")]
    public void CodigosInvalidos(string? input)
    {
        Assert.False(InviteCodes.TryParse(input, out _));
    }

    [Fact]
    public void ParseLanzaNotFound()
    {
        var ex = Assert.Throws<FamilyLinkException>(() => InviteCodes.Parse("xx"));
        Assert.Equal("not_found", ex.Code);
    }

    [Fact]
    public void QrDeIdaYVuelta()
    {
        var payload = InviteCodes.ToQrPayload("ABCD2345");
        Assert.Equal("familylink://join?c=ABCD2345", payload);
        Assert.True(InviteCodes.TryParse(payload, out var code));
        Assert.Equal("ABCD2345", code);
    }

    [Fact]
    public void QrPngEsUnPng()
    {
        var png = QrCodes.QrPng("familylink://join?c=ABCD2345");
        Assert.True(png.Length > 100);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, png[..4]);
    }

    [Fact]
    public void ErrorP0001TraeLaClave()
    {
        var ex = SupabaseClient.ErrorFrom(System.Net.HttpStatusCode.BadRequest,
            "{\"code\":\"P0001\",\"details\":null,\"hint\":null,\"message\":\"expired\"}");
        Assert.Equal("expired", ex.Code);

        Assert.Equal("unauthorized", SupabaseClient.ErrorFrom(System.Net.HttpStatusCode.Unauthorized, "{}").Code);
        Assert.Equal("server", SupabaseClient.ErrorFrom(System.Net.HttpStatusCode.InternalServerError, "boom").Code);
    }

    [Fact]
    public void DiaEnLaZonaHoraria()
    {
        var madrid = TimeZoneInfo.FindSystemTimeZoneById("Europe/Madrid");
        var (from, to) = FamilyService.DayRange(new DateOnly(2026, 9, 27), madrid);

        Assert.Equal(new DateTimeOffset(2026, 9, 26, 22, 0, 0, TimeSpan.Zero), from);
        Assert.Equal(new DateTimeOffset(2026, 9, 27, 22, 0, 0, TimeSpan.Zero), to);

        // El dia del cambio de hora tiene 25 horas.
        var (f2, t2) = FamilyService.DayRange(new DateOnly(2026, 10, 25), madrid);
        Assert.Equal(TimeSpan.FromHours(25), t2 - f2);
    }
}
