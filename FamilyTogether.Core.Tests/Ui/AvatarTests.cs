using FamilyTogether.Mobile.Controls;
using FamilyTogether.Mobile.Localization;
using FamilyTogether.Mobile.Services;
using Microsoft.Maui.Controls.Shapes;

namespace FamilyTogether.Core.Tests.Ui;

/// <summary>Avatares: color e iniciales de cada persona, la foto en base64 y el circulo que los pinta.</summary>
public class AvatarTests : IDisposable
{
    private readonly AppHost _host = new();
    private readonly Func<FileResult, Task<Stream>> _openPhoto = Avatars.OpenPhoto;
    private readonly string _photo = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"ft-foto-{Guid.NewGuid():N}.png");

    public void Dispose()
    {
        Avatars.OpenPhoto = _openPhoto;
        MauiFakes.MediaPicker.Photos = null;
        MauiFakes.MediaPicker.Requests.Clear();
        File.Delete(_photo);
        _host.Dispose();
    }

    private static readonly string[] Palette =
        ["#3525CD", "#C2185B", "#00897B", "#EF6C00", "#6A1B9A", "#2E7D32", "#1565C0", "#AD1457", "#5D4037", "#00838F"];

    private static async Task<byte[]> ReadAsync(ImageSource source)
    {
        var stream = await Assert.IsType<StreamImageSource>(source).Stream(CancellationToken.None);
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy);
        return copy.ToArray();
    }

    [Fact]
    public void ElColorSaleDelIdYEsSiempreElMismo()
    {
        Assert.Equal("#3525CD", Avatars.ColorFor(Guid.Empty));   // suma 0: el primero

        // 0x01 en un solo byte: suma 1, el segundo color.
        Assert.Equal("#C2185B", Avatars.ColorFor(new Guid([1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0])));

        var colors = Enumerable.Range(0, 200).Select(_ => Guid.NewGuid()).Select(id => (id, color: Avatars.ColorFor(id))).ToList();
        Assert.All(colors, c => Assert.Contains(c.color, Palette));
        Assert.All(colors, c => Assert.Equal(c.color, Avatars.ColorFor(c.id)));
        Assert.True(colors.Select(c => c.color).Distinct().Count() > 5, "los colores deberian repartirse");
    }

    [Theory]
    [InlineData(null, "?")]
    [InlineData("", "?")]
    [InlineData("   ", "?")]
    [InlineData("?", "?")]
    [InlineData("ana", "A")]
    [InlineData("  ana   maría  lópez ", "AL")]
    [InlineData("élodie Ñúñez", "ÉÑ")]
    [InlineData("José", "J")]
    public void Iniciales(string? name, string expected) => Assert.Equal(expected, Avatars.Initials(name));

    [Fact]
    public async Task LaFotoEnBase64SeConvierteEnImagen()
    {
        Assert.Null(Avatars.ToImage(null));
        Assert.Null(Avatars.ToImage(""));
        Assert.Null(Avatars.ToImage("esto no es base64!"));

        var bytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 };
        var image = Avatars.ToImage(Convert.ToBase64String(bytes));
        Assert.Equal(bytes, await ReadAsync(image!));
        Assert.Equal(bytes, await ReadAsync(image!));   // se puede leer mas de una vez
    }

    [Fact]
    public async Task ElegirFotoDevuelveSuBase64()
    {
        var bytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 9, 8, 7, 6 };
        await File.WriteAllBytesAsync(_photo, bytes);
        var opened = new List<string>();
        Avatars.OpenPhoto = p =>
        {
            opened.Add(p.FullPath);
            return Task.FromResult<Stream>(File.OpenRead(p.FullPath));
        };
        MauiFakes.MediaPicker.Photos = () => [new FileResult(_photo)];

        var avatar = await Avatars.PickAsync();

        Assert.Equal(Convert.ToBase64String(bytes), avatar);
        Assert.Equal([_photo], opened);
        var options = Assert.Single(MauiFakes.MediaPicker.Requests);
        Assert.Equal(Loc.Get("AvatarPick"), options!.Title);
        Assert.Equal(1, options.SelectionLimit);
    }

    [Fact]
    public async Task SiSeCancelaLaGaleriaNoHayFoto()
    {
        MauiFakes.MediaPicker.Photos = () => [];
        Assert.Null(await Avatars.PickAsync());

        MauiFakes.MediaPicker.Photos = () => null!;
        Assert.Null(await Avatars.PickAsync());
    }

    [Fact]
    public async Task SiLaFotoNoSePuedeLeerLanzaParaQueSeAvise()
    {
        MauiFakes.MediaPicker.Photos = () => [new FileResult(_photo)];
        Avatars.OpenPhoto = _ => throw new IOException("ilegible");

        await Assert.ThrowsAsync<IOException>(Avatars.PickAsync);
    }

    [Fact]
    public async Task ElAvatarPintaLaFotoOLasInicialesEnSuColor()
    {
        var user = Guid.NewGuid();
        var view = new AvatarView(50);
        var image = view.All<Image>().Single();
        var initials = view.All<Label>().Single();

        Assert.Equal(50, view.WidthRequest);
        Assert.Equal(50, view.HeightRequest);
        Assert.Equal(new CornerRadius(25), Assert.IsType<RoundRectangle>(view.StrokeShape).CornerRadius);
        Assert.Equal(19, initials.FontSize, 6);   // 50 × 0,38

        view.Set(user, "Ana López", null);
        Assert.Equal(Color.FromArgb(Avatars.ColorFor(user)), view.BackgroundColor);
        Assert.Equal("AL", initials.Text);
        Assert.False(image.IsVisible);
        Assert.Null(image.Source);

        var bytes = new byte[] { 1, 2, 3 };
        view.Set(user, null, Convert.ToBase64String(bytes));
        Assert.Equal("?", initials.Text);
        Assert.True(image.IsVisible);
        Assert.Equal(bytes, await ReadAsync(image.Source));

        // Una foto estropeada: las iniciales.
        view.Set(user, "Bea", "%%%");
        Assert.False(image.IsVisible);
        Assert.Equal("B", initials.Text);
    }

    [Fact]
    public void ElAvatarPequenoNoBajaDeDoceDeLetra()
    {
        var initials = new AvatarView(20).All<Label>().Single();
        Assert.Equal(12, initials.FontSize);
        Assert.Equal(40, new AvatarView().WidthRequest);
    }
}
