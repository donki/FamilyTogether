using System.Text.Json;
using FamilyLink.Mobile.Localization;

namespace FamilyLink.Mobile.Services;

/// <summary>
/// Novedades de las ultimas versiones (constitucion General 6.7), sacadas de
/// <c>Resources\Raw\whatsnew.json</c>: lo que nota el usuario, en los dos idiomas.
/// </summary>
public static class WhatsNew
{
    public const int MaxVersions = 5;

    public sealed record Release(string Version, string Date, IReadOnlyList<string> Items);

    public static async Task<IReadOnlyList<Release>> LoadAsync()
    {
        try
        {
            await using var stream = await FileSystem.OpenAppPackageFileAsync("whatsnew.json");
            using var doc = await JsonDocument.ParseAsync(stream);
            var lang = Loc.Language;
            var list = new List<Release>();
            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                var items = entry.TryGetProperty(lang, out var arr) || entry.TryGetProperty("en", out arr)
                    ? arr.EnumerateArray().Select(i => i.GetString() ?? string.Empty).ToList()
                    : [];
                list.Add(new Release(
                    entry.GetProperty("version").GetString() ?? string.Empty,
                    entry.TryGetProperty("date", out var d) ? d.GetString() ?? string.Empty : string.Empty,
                    items));
            }

            return [.. list.Take(MaxVersions)];
        }
        catch (Exception ex)
        {
            CrashLog.Error("WhatsNew.LoadAsync", ex);
            return [];
        }
    }
}
