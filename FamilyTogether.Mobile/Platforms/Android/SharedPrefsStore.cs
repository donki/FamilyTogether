using Android.Content;
using FamilyTogether.Mobile.Services.Native;

namespace FamilyTogether.Mobile.Platforms.Android;

/// <summary>El almacén de <see cref="SharingState"/>: unas <c>SharedPreferences</c> propias.</summary>
internal sealed class SharedPrefsStore(ISharedPreferences prefs) : IKeyValueStore
{
    public static IKeyValueStore? Open() =>
        global::Android.App.Application.Context.GetSharedPreferences(SharingState.PrefsName, FileCreationMode.Private) is { } p
            ? new SharedPrefsStore(p)
            : null;

    public bool GetBool(string key, bool fallback) => prefs.GetBoolean(key, fallback);

    public string? GetString(string key) => prefs.GetString(key, null);

    public void Apply(IReadOnlyDictionary<string, object?> changes)
    {
        var edit = prefs.Edit();
        if (edit is null)
            return;

        foreach (var (key, value) in changes)
        {
            switch (value)
            {
                case null: edit.Remove(key); break;
                case bool b: edit.PutBoolean(key, b); break;
                case long l: edit.PutLong(key, l); break;
                default: edit.PutString(key, value.ToString()); break;
            }
        }

        edit.Apply();
    }
}
