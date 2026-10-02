using FamilyTogether.Core;
using FamilyTogether.Mobile.Controls;
using FamilyTogether.Mobile.Helpers;
using FamilyTogether.Mobile.Localization;
using FamilyTogether.Mobile.Services;

namespace FamilyTogether.Mobile.Pages;

/// <summary>
/// Crear o editar una zona en el mapa: se toca para poner el centro, el deslizador da el radio
/// (50 a 2000 m) y se le pone nombre. El nombre y la geometria viajan cifrados con la clave del grupo.
/// </summary>
public sealed class ZoneEditPage : ContentPage
{
    private const double MinRadius = 50;
    private const double MaxRadius = 2000;

    private readonly FamilyService _family = ServiceHelper.Get<FamilyService>();
    private readonly Group _group;
    private readonly Zone? _zone;
    private readonly MapView _map = new();
    private readonly Entry _name = new() { MaxLength = 40 };
    private readonly Slider _radius = new() { Minimum = MinRadius, Maximum = MaxRadius };
    private readonly Label _radiusLabel = new();
    private readonly Button _save;
    private double _lat;
    private double _lon;
    private bool _centerChosen;
    private bool _saving;

    public ZoneEditPage(Group group, Zone? zone)
    {
        _group = group;
        _zone = zone;
        Title = Loc.Get(zone is null ? "NewZone" : "EditZone");

        _lat = zone?.Lat ?? 40.4168;
        _lon = zone?.Lon ?? -3.7038;
        _centerChosen = zone is not null;
        _name.Text = zone?.Name ?? string.Empty;
        _name.Placeholder = Loc.Get("ZoneNamePlaceholder");
        _radius.Value = Math.Clamp(zone?.Radius ?? 150, MinRadius, MaxRadius);
        _radiusLabel.Style = Ui.Style("BodyText");
        UpdateRadiusLabel();

        _radius.ValueChanged += (_, e) =>
        {
            // De 10 en 10 metros: nadie necesita mas precision y el numero se lee mejor.
            var rounded = Math.Round(e.NewValue / 10) * 10;
            if (Math.Abs(rounded - e.NewValue) > 0.01)
            {
                _radius.Value = rounded;
                return;
            }

            UpdateRadiusLabel();
            _ = _map.RunAsync($"setEditRadius({MapView.Num(rounded)})");
        };

        _map.MapTapped += (_, p) =>
        {
            _lat = p.Lat;
            _lon = p.Lon;
            _centerChosen = true;
        };

        _save = UiKit.Primary(Loc.Get("Save"), "ic_save_w.png", async (_, _) => await SaveAsync());

        var panel = new Border
        {
            Style = Ui.Style("BottomSheet"),
            Content = new ScrollView
            {
                MaximumHeightRequest = 360,
                Content = new VerticalStackLayout
                {
                    Padding = new Thickness(16, 12, 16, 16),
                    Spacing = 8,
                    Children =
                    {
                        UiKit.Hint(Loc.Get("ZoneTapHint")),
                        UiKit.Body(Loc.Get("ZoneName")),
                        UiKit.Input(_name),
                        _radiusLabel,
                        _radius,
                        _save,
                    },
                },
            },
        };

        var root = new Grid { RowDefinitions = [new RowDefinition(GridLength.Star), new RowDefinition(GridLength.Auto)] };
        root.Add(_map, 0, 0);
        root.Add(panel, 0, 1);
        Content = root;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        _map.StartEvents();
        await _map.RunAsync($"editZone({MapView.Num(_lat)}, {MapView.Num(_lon)}, {MapView.Num(_radius.Value)})");

        // Zona nueva: se empieza donde esta uno, si se sabe; mientras, en el centro por defecto.
        if (!_centerChosen && ServiceHelper.TryGet<ILocationSharing>() is { } sharing)
        {
            try
            {
                if (await sharing.GetCurrentOrLastAsync() is { } here && !_centerChosen)
                {
                    _lat = here.Lat;
                    _lon = here.Lon;
                    await _map.RunAsync($"editZone({MapView.Num(_lat)}, {MapView.Num(_lon)}, {MapView.Num(_radius.Value)})");
                }
            }
            catch (Exception ex)
            {
                CrashLog.Error("ZoneEditPage.Locate", ex);
            }
        }
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _map.StopEvents();
    }

    private void UpdateRadiusLabel() => _radiusLabel.Text = Loc.Format("RadiusMeters", (int)_radius.Value);

    private async Task SaveAsync()
    {
        if (_saving)
            return;

        var name = _name.Text?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            await Ui.AlertAsync(this, "ZoneName", Loc.Get("ZoneNameRequired"));
            _name.Focus();
            return;
        }

        _saving = true;
        _save.IsEnabled = false;
        try
        {
            // La sesion, dentro de RunAsync: sin ella (aun no cargada) es un aviso, no un cierre de la app.
            if (await Ui.RunAsync(this, async () =>
            {
                var client = ServiceHelper.Get<SupabaseClient>();
                await client.EnsureSignedInAsync();
                var zone = new Zone(_zone?.Id ?? Guid.Empty, _group.Id, name, _lat, _lon, _radius.Value, _zone?.CreatedBy ?? client.UserGuid);
                await _family.SaveZoneAsync(zone);
            }))
                await Navigation.PopAsync();
        }
        finally
        {
            _saving = false;
            _save.IsEnabled = true;
        }
    }
}
