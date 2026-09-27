using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FamilyTogether.Core;

/// <summary>
/// Las cargas JSON que se cifran (ARQUITECTURA §3): claves cortas e invariantes. System.Text.Json
/// escribe los numeros siempre con punto, sea cual sea la cultura del movil.
/// </summary>
internal static class Payloads
{
    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    internal sealed record PositionData(
        [property: JsonPropertyName("lat")] double Lat,
        [property: JsonPropertyName("lon")] double Lon,
        [property: JsonPropertyName("acc")] double Acc);

    internal sealed record ZoneData(
        [property: JsonPropertyName("lat")] double Lat,
        [property: JsonPropertyName("lon")] double Lon,
        [property: JsonPropertyName("r")] double R);

    internal sealed record SosData(
        [property: JsonPropertyName("lat")] double Lat,
        [property: JsonPropertyName("lon")] double Lon,
        [property: JsonPropertyName("acc")] double Acc,
        [property: JsonPropertyName("at")] string At,
        [property: JsonPropertyName("stale")] bool Stale);

    public static string Position(double lat, double lon, double acc) =>
        JsonSerializer.Serialize(new PositionData(lat, lon, acc), Options);

    public static string Zone(double lat, double lon, double radius) =>
        JsonSerializer.Serialize(new ZoneData(lat, lon, radius), Options);

    public static string Sos(double lat, double lon, double acc, DateTimeOffset at, bool stale) =>
        JsonSerializer.Serialize(new SosData(lat, lon, acc, FormatUtc(at), stale), Options);

    /// <summary><c>2026-09-27T10:00:00Z</c>: UTC, sin fracciones.</summary>
    public static string FormatUtc(DateTimeOffset at) =>
        at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    public static T? Read<T>(string json) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Descifra y lee una carga; null si no se puede (sin clave, manipulada o mal formada).</summary>
    public static T? Open<T>(string? envelope, byte[]? key) where T : class =>
        !string.IsNullOrEmpty(envelope) && Crypto.TryDecrypt(envelope, key, out var json) && json.Length > 0
            ? Read<T>(json)
            : null;
}
