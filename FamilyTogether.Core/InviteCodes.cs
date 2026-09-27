using QRCoder;

namespace FamilyTogether.Core;

/// <summary>
/// Codigos de invitacion: 8 caracteres de un alfabeto sin los que se confunden (0/O, 1/I).
/// </summary>
public static class InviteCodes
{
    public const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    public const int Length = 8;
    public const string QrPrefix = "familytogether://join?c=";

    /// <summary>Contenido del QR para un codigo.</summary>
    public static string ToQrPayload(string code) => QrPrefix + code;

    /// <summary>
    /// Saca el codigo de lo que haya escrito o escaneado el usuario: el codigo solo o el contenido
    /// del QR (<c>familytogether://join?c=XXXX</c>). Normaliza a mayusculas y quita espacios y guiones.
    /// Devuelve false si lo que queda no es un codigo posible.
    /// </summary>
    public static bool TryParse(string? input, out string code)
    {
        code = string.Empty;
        if (string.IsNullOrWhiteSpace(input))
            return false;

        var text = input.Trim();

        if (text.StartsWith("familytogether:", StringComparison.OrdinalIgnoreCase))
        {
            var query = text.IndexOf('?');
            if (query < 0)
                return false;

            string? value = null;
            foreach (var pair in text[(query + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = pair.Split('=', 2);
                if (parts.Length == 2 && parts[0].Equals("c", StringComparison.OrdinalIgnoreCase))
                {
                    value = Uri.UnescapeDataString(parts[1]);
                    break;
                }
            }

            if (value is null)
                return false;

            text = value;
        }

        var normalized = new string(text
            .Where(c => !char.IsWhiteSpace(c) && c != '-')
            .Select(char.ToUpperInvariant)
            .ToArray());

        if (normalized.Length != Length || normalized.Any(c => !Alphabet.Contains(c)))
            return false;

        code = normalized;
        return true;
    }

    /// <summary>Como <see cref="TryParse"/>, pero lanza <c>not_found</c> si no es un codigo.</summary>
    public static string Parse(string? input) =>
        TryParse(input, out var code)
            ? code
            : throw new FamilyTogetherException("not_found", "not_found");
}

/// <summary>El PNG del QR, para que la app lo pinte sin otra biblioteca.</summary>
public static class QrCodes
{
    /// <summary>PNG en blanco y negro, 10 px por modulo, correccion de errores media.</summary>
    public static byte[] QrPng(string payload)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.M);
        return new PngByteQRCode(data).GetGraphic(10);
    }
}
