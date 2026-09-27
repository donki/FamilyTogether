using FamilyTogether.Mobile.Controls;
using FamilyTogether.Mobile.Localization;
using FamilyTogether.Mobile.Services;

namespace FamilyTogether.Mobile.Pages;

/// <summary>
/// Novedades de las cinco ultimas versiones, de la mas nueva a la mas antigua (General 6.7). Sale
/// sola la primera vez que se abre una version nueva (desde el mapa) y se puede abrir desde el menu,
/// desde Ajustes y desde Acerca de.
/// </summary>
public sealed class WhatsNewPage : ContentPage
{
    private readonly VerticalStackLayout _list = new() { Padding = 16, Spacing = 16 };

    public WhatsNewPage()
    {
        Title = Loc.Get("MenuWhatsNew");
        Content = new ScrollView { Content = _list };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        AppState.MarkVersionSeen();

        var releases = await WhatsNew.LoadAsync();
        _list.Clear();
        if (releases.Count == 0)
        {
            _list.Add(UiKit.Body(Loc.Get("WhatsNewEmpty")));
            return;
        }

        foreach (var release in releases)
        {
            var stack = new VerticalStackLayout { Spacing = 8 };
            var current = release.Version == AppInfo.Current.VersionString;
            stack.Add(UiKit.Title(current ? Loc.Format("WhatsNewCurrent", release.Version) : release.Version));
            if (release.Date.Length > 0 && DateTime.TryParse(release.Date, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var date))
                stack.Add(UiKit.Hint(date.ToString("D", Loc.Culture)));

            foreach (var item in release.Items)
            {
                var row = new Grid
                {
                    ColumnDefinitions = [new ColumnDefinition(new GridLength(14)), new ColumnDefinition(GridLength.Star)],
                    ColumnSpacing = 8,
                };
                row.Add(new BoxView { Color = Ui.Color("Primary"), WidthRequest = 6, HeightRequest = 6, CornerRadius = 3, VerticalOptions = LayoutOptions.Start, Margin = new Thickness(0, 8, 0, 0) }, 0, 0);
                row.Add(UiKit.Body(item), 1, 0);
                stack.Add(row);
            }

            _list.Add(UiKit.Card(stack));
        }
    }
}
