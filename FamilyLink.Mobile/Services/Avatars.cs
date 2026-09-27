using FamilyLink.Mobile.Localization;

namespace FamilyLink.Mobile.Services;

/// <summary>
/// Avatares: elegir una foto, recortarla al cuadrado y escalarla a 96×96 PNG en base64 (lo que viaja
/// cifrado con la clave del grupo), y pintar el de cada miembro o sus iniciales.
/// </summary>
public static class Avatars
{
    public const int Size = 96;

    /// <summary>Colores de los marcadores: un tono por persona, estable (sale de su id).</summary>
    private static readonly string[] Palette =
    [
        "#3525CD", "#C2185B", "#00897B", "#EF6C00", "#6A1B9A", "#2E7D32", "#1565C0", "#AD1457", "#5D4037", "#00838F",
    ];

    public static string ColorFor(Guid user)
    {
        var bytes = user.ToByteArray();
        var sum = 0;
        foreach (var b in bytes)
            sum += b;
        return Palette[sum % Palette.Length];
    }

    public static string Initials(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name == "?")
            return "?";

        var parts = name.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var first = char.ToUpperInvariant(parts[0][0]);
        return parts.Length > 1 ? $"{first}{char.ToUpperInvariant(parts[^1][0])}" : first.ToString();
    }

    public static ImageSource? ToImage(string? base64)
    {
        if (string.IsNullOrEmpty(base64))
            return null;

        try
        {
            var bytes = Convert.FromBase64String(base64);
            return ImageSource.FromStream(() => new MemoryStream(bytes));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// Deja elegir una foto de la galeria y la devuelve ya recortada a 96×96 PNG en base64. Devuelve
    /// <c>null</c> si se cancela; si la foto no se puede leer, lanza para que se avise.
    /// </summary>
    public static async Task<string?> PickAsync()
    {
        var photos = await MediaPicker.Default.PickPhotosAsync(new MediaPickerOptions { Title = Loc.Get("AvatarPick"), SelectionLimit = 1 });
        var photo = photos?.FirstOrDefault();
        if (photo is null)
            return null;

        await using var stream = await photo.OpenReadAsync();
        return await ToAvatarAsync(stream);
    }

    private static async Task<string> ToAvatarAsync(Stream stream)
    {
#if ANDROID
        // Microsoft.Maui.Graphics sabe decodificar, recortar (Bleed = llenar y cortar lo que sobra) y
        // guardar en PNG sin otra biblioteca.
        using var image = Microsoft.Maui.Graphics.Platform.PlatformImage.FromStream(stream);
        using var square = image.Resize(Size, Size, ResizeMode.Bleed, disposeOriginal: false);
        using var output = new MemoryStream();
        await square.SaveAsync(output, ImageFormat.Png);
        return Convert.ToBase64String(output.ToArray());
#else
        using var output = new MemoryStream();
        await stream.CopyToAsync(output);
        return Convert.ToBase64String(output.ToArray());
#endif
    }
}
