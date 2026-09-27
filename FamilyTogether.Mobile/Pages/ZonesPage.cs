using FamilyTogether.Core;
using FamilyTogether.Mobile.Controls;
using FamilyTogether.Mobile.Helpers;
using FamilyTogether.Mobile.Localization;
using FamilyTogether.Mobile.Services;

namespace FamilyTogether.Mobile.Pages;

/// <summary>
/// Zonas del grupo (HU5, FR-015): cualquier miembro las crea, edita y borra, y todo el grupo las ve.
/// Desde aqui se abren los avisos de entrada y salida por persona y zona (FR-016).
/// </summary>
public sealed class ZonesPage : ContentPage
{
    private readonly FamilyService _family = ServiceHelper.Get<FamilyService>();
    private readonly GroupSelector _selector = new();
    private readonly MapView _map = new();
    private readonly VerticalStackLayout _list = new() { Spacing = 0 };
    private readonly ActivityIndicator _busy = new() { HeightRequest = 24 };
    private IReadOnlyList<Zone> _zones = [];
    private bool _loading;

    public ZonesPage()
    {
        Title = Loc.Get("MenuZones");
        ToolbarItems.Add(new ToolbarItem { IconImageSource = "ic_add_w.png", Text = Loc.Get("NewZone"), Command = new Command(async () => await EditAsync(null)) });
        ToolbarItems.Add(new ToolbarItem { IconImageSource = "ic_bell_w.png", Text = Loc.Get("ZoneAlerts"), Command = new Command(async () => await OpenAlertsAsync()) });

        _selector.Margin = new Thickness(12, 8);
        _selector.SelectionChanged += async (_, _) => await LoadZonesAsync();

        var sheet = new Border
        {
            Style = Ui.Style("BottomSheet"),
            Content = new VerticalStackLayout
            {
                Padding = new Thickness(16, 12, 16, 8),
                Spacing = 8,
                Children =
                {
                    UiKit.Row(UiKit.Title(Loc.Get("ZonesTitle")), _busy),
                    new ScrollView { MaximumHeightRequest = 260, Content = _list },
                    new Grid
                    {
                        ColumnDefinitions = [new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Star)],
                        ColumnSpacing = 8,
                        Children =
                        {
                            UiKit.Primary(Loc.Get("NewZone"), "ic_add_w.png", async (_, _) => await EditAsync(null)),
                        },
                    }.Also(g => g.Add(UiKit.Outline(Loc.Get("ZoneAlerts"), "ic_bell.png", async (_, _) => await OpenAlertsAsync()), 1, 0)),
                },
            },
        };

        var root = new Grid
        {
            RowDefinitions = [new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star), new RowDefinition(GridLength.Auto)],
        };
        root.Add(_selector, 0, 0);
        root.Add(_map, 0, 1);
        root.Add(sheet, 0, 2);
        Content = root;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_loading)
            return;

        var group = await _selector.LoadAsync(this);
        if (group is null)
        {
            _list.Clear();
            _list.Add(UiKit.Body(Loc.Get("NoGroupsWithKey")));
            return;
        }

        await LoadZonesAsync();
    }

    private async Task LoadZonesAsync()
    {
        if (_selector.Selected is not { } group || _loading)
            return;

        _loading = true;
        _busy.IsRunning = _busy.IsVisible = true;
        try
        {
            var (ok, zones) = await Ui.RunAsync(this, () => _family.GetZonesAsync(group.Id));
            if (!ok || zones is null)
                return;

            _zones = zones;
            _list.Clear();
            if (zones.Count == 0)
                _list.Add(UiKit.Body(Loc.Get("NoZones")));

            foreach (var zone in zones.OrderBy(z => z.Name, StringComparer.CurrentCultureIgnoreCase))
                _list.Add(ZoneRow(zone));

            var items = zones.Select(z => new { id = z.Id.ToString("D"), name = z.Name, lat = z.Lat, lon = z.Lon, r = z.Radius }).ToList();
            var json = MapView.Json(items);
            await _map.RunAsync($"setZones({json})");
            await _map.RunAsync($"fitZones({json})");
        }
        finally
        {
            _loading = false;
            _busy.IsRunning = _busy.IsVisible = false;
        }
    }

    private View ZoneRow(Zone zone)
    {
        var texts = new VerticalStackLayout
        {
            Spacing = 2,
            VerticalOptions = LayoutOptions.Center,
            Children = { UiKit.ItemTitle(zone.Name), UiKit.ItemSubtitle(Loc.Format("RadiusMeters", (int)zone.Radius)) },
        };

        var row = new Grid
        {
            ColumnDefinitions =
            [
                new ColumnDefinition(new GridLength(32)),
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Auto),
            ],
            ColumnSpacing = 4,
            MinimumHeightRequest = 56,
        };
        row.Add(new Image { Source = "ic_zone.png", WidthRequest = 24, HeightRequest = 24, VerticalOptions = LayoutOptions.Center }, 0, 0);
        row.Add(texts, 1, 0);
        row.Add(UiKit.Icon("ic_edit.png", async (_, _) => await EditAsync(zone), Loc.Get("Edit")), 2, 0);
        row.Add(UiKit.Icon("ic_delete_danger.png", async (_, _) => await DeleteAsync(zone), Loc.Get("Delete")), 3, 0);

        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) => _ = _map.RunAsync($"center({MapView.Num(zone.Lat)}, {MapView.Num(zone.Lon)}, 16)");
        texts.GestureRecognizers.Add(tap);
        return row;
    }

    private async Task EditAsync(Zone? zone)
    {
        if (_selector.Selected is not { } group)
        {
            await Ui.AlertAsync(this, "MenuZones", Loc.Get("NoGroupsWithKey"));
            return;
        }

        await Navigation.PushAsync(new ZoneEditPage(group, zone));
    }

    private async Task DeleteAsync(Zone zone)
    {
        if (!await Ui.ConfirmAsync(this, "Delete", Loc.Format("DeleteZoneConfirm", zone.Name), "Delete"))
            return;

        if (await Ui.RunAsync(this, () => _family.DeleteZoneAsync(zone.Id)))
            await LoadZonesAsync();
    }

    private async Task OpenAlertsAsync()
    {
        if (_selector.Selected is not { } group)
        {
            await Ui.AlertAsync(this, "MenuZones", Loc.Get("NoGroupsWithKey"));
            return;
        }

        await Navigation.PushAsync(new ZoneAlertsPage(group));
    }
}

internal static class ViewExtensions
{
    /// <summary>Para montar una vista y seguir configurandola en la misma expresion.</summary>
    public static T Also<T>(this T view, Action<T> configure)
    {
        configure(view);
        return view;
    }
}
