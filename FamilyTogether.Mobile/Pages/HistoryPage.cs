using FamilyTogether.Core;
using FamilyTogether.Mobile.Controls;
using FamilyTogether.Mobile.Helpers;
using FamilyTogether.Mobile.Localization;
using FamilyTogether.Mobile.Services;

namespace FamilyTogether.Mobile.Pages;

/// <summary>
/// Historial (HU4, FR-014): el recorrido de un miembro en un dia de los ultimos 30 (la retencion),
/// como una linea en el mapa con su inicio y su fin.
/// </summary>
/// <remarks>
/// <para><b>Por las calles.</b> Primero se dibuja recto, al momento; si el ajuste esta encendido
/// (Ajustes, por defecto si), se pide la red de calles de las teselas que toca el recorrido
/// (<see cref="TrackSnapper"/>: a Overpass solo van rectangulos fijos, nunca el recorrido), se
/// ajusta en el movil y se redibuja. Si falla, se queda recto con una linea que lo dice, sin
/// dialogos. El ajuste no se guarda en ningun sitio.</para>
/// <para>El boton de la papelera borra <b>mi</b> historial (<see cref="HistoryActions"/>).</para>
/// </remarks>
public sealed class HistoryPage : ContentPage
{
    private const int RetentionDays = 30;
    private const string StartColor = "#27AE60";
    private const string EndColor = "#BA1A1A";

    private readonly FamilyService _family = ServiceHelper.Get<FamilyService>();
    private readonly TrackSnapper _snapper = ServiceHelper.Get<TrackSnapper>();
    private readonly Label _snapStatus = new();
    private CancellationTokenSource? _snapCancel;
    private readonly GroupSelector _selector = new();
    private readonly Picker _member = new();
    private readonly DatePicker _day = new() { Format = "D" };
    private readonly MapView _map = new();
    private readonly Label _summary = new();
    private IReadOnlyList<Member> _members = [];
    private bool _updating;
    private bool _loading;

    public HistoryPage()
    {
        Title = Loc.Get("MenuHistory");
        ToolbarItems.Add(new ToolbarItem
        {
            IconImageSource = "ic_delete_w.png",
            Text = Loc.Get("ClearHistory"),
            Command = new Command(async () =>
            {
                if (await HistoryActions.ClearMyHistoryAsync(this))
                    await LoadTrackAsync();
            }),
        });
        _snapStatus.Style = Ui.Style("HintText");
        _snapStatus.LineBreakMode = LineBreakMode.WordWrap;
        _snapStatus.IsVisible = false;
        _member.Title = Loc.Get("ChooseMember");
        _summary.Style = Ui.Style("BodyText");

        _day.MinimumDate = DateTime.Today.AddDays(-(RetentionDays - 1));
        _day.MaximumDate = DateTime.Today;
        _day.Date = DateTime.Today;

        _selector.SelectionChanged += async (_, _) => await LoadMembersAsync();
        _member.SelectedIndexChanged += async (_, _) => { if (!_updating) await LoadTrackAsync(); };
        _day.DateSelected += async (_, _) => await LoadTrackAsync();

        var legend = new HorizontalStackLayout
        {
            Spacing = 16,
            Children =
            {
                Dot(StartColor, Loc.Get("TrackStart")),
                Dot(EndColor, Loc.Get("TrackEnd")),
            },
        };

        var controls = new VerticalStackLayout
        {
            Padding = new Thickness(12, 8),
            Spacing = 8,
            Children =
            {
                _selector,
                new Grid
                {
                    ColumnDefinitions = [new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Star)],
                    ColumnSpacing = 8,
                }.Also(g =>
                {
                    g.Add(UiKit.Input(_member), 0, 0);
                    g.Add(UiKit.Input(_day), 1, 0);
                }),
            },
        };

        var bottom = new Border
        {
            Style = Ui.Style("BottomSheet"),
            Content = new VerticalStackLayout
            {
                Padding = new Thickness(16, 12),
                Spacing = 6,
                Children = { _summary, legend, _snapStatus, UiKit.Hint(Loc.Format("HistoryRetention", RetentionDays)) },
            },
        };

        var root = new Grid
        {
            RowDefinitions = [new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star), new RowDefinition(GridLength.Auto)],
        };
        root.Add(controls, 0, 0);
        root.Add(_map, 0, 1);
        root.Add(bottom, 0, 2);
        Content = root;
    }

    private static View Dot(string color, string text) => new HorizontalStackLayout
    {
        Spacing = 6,
        Children =
        {
            new BoxView { Color = Color.FromArgb(color), WidthRequest = 14, HeightRequest = 14, CornerRadius = 7, VerticalOptions = LayoutOptions.Center },
            UiKit.Hint(text),
        },
    };

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        // El dia de hoy cambia si la app se queda abierta de un dia para otro.
        _day.MinimumDate = DateTime.Today.AddDays(-(RetentionDays - 1));
        _day.MaximumDate = DateTime.Today;

        if (await _selector.LoadAsync(this) is null)
        {
            _summary.Text = Loc.Get("NoGroupsWithKey");
            return;
        }

        await LoadMembersAsync();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _snapCancel?.Cancel();
    }

    private async Task LoadMembersAsync()
    {
        if (_selector.Selected is not { } group)
            return;

        var (ok, members) = await Ui.RunAsync(this, () => _family.GetMembersAsync(group.Id));
        if (!ok || members is null)
            return;

        var previous = _member.SelectedIndex >= 0 && _member.SelectedIndex < _members.Count ? _members[_member.SelectedIndex].UserId : Guid.Empty;
        _members = [.. members.OrderByDescending(m => m.IsMe).ThenBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase)];

        _updating = true;
        try
        {
            _member.ItemsSource = _members.Select(m => m.IsMe ? Loc.Format("MeSuffix", m.Name) : m.Name).ToList();
            var index = _members.ToList().FindIndex(m => m.UserId == previous);
            _member.SelectedIndex = _members.Count == 0 ? -1 : Math.Max(0, index);
        }
        finally
        {
            _updating = false;
        }

        await LoadTrackAsync();
    }

    private async Task LoadTrackAsync()
    {
        if (_loading || _selector.Selected is not { } group || _member.SelectedIndex < 0 || _member.SelectedIndex >= _members.Count)
            return;

        _loading = true;
        try
        {
            var member = _members[_member.SelectedIndex];
            var day = DateOnly.FromDateTime(_day.Date ?? DateTime.Today);
            _summary.Text = Loc.Get("Loading");
            _snapCancel?.Cancel();
            _snapStatus.IsVisible = false;

            var (ok, points) = await Ui.RunAsync(this, () => _family.GetHistoryAsync(group.Id, member.UserId, day, TimeZoneInfo.Local));
            if (!ok || points is null)
            {
                _summary.Text = string.Empty;
                return;
            }

            var ordered = points.OrderBy(p => p.At).ToList();
            if (ordered.Count == 0)
            {
                _summary.Text = Loc.Get("NoPositionsThatDay");
                await _map.RunAsync("clearTrack()");
                return;
            }

            var first = ordered[0].At.ToLocalTime().ToString("HH:mm", Loc.Culture);
            var last = ordered[^1].At.ToLocalTime().ToString("HH:mm", Loc.Culture);
            _summary.Text = Loc.Format("TrackSummary", ordered.Count, first, last);

            var coords = ordered.Select(p => new[] { p.Lat, p.Lon }).ToList();
            await _map.RunAsync($"setTrack({MapView.Json(coords)}, '{StartColor}', '{EndColor}')");

            if (AppState.SnapTracks && ordered.Count >= 2)
            {
                _snapCancel = new CancellationTokenSource();
                _ = SnapAsync([.. ordered.Select(p => new TrackPoint(p.Lat, p.Lon, p.Accuracy))], _snapCancel.Token);
            }
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>
    /// Ajusta el recorrido ya dibujado y lo redibuja. Nunca molesta: si no hay mapa de calles, se
    /// queda recto y lo dice en una linea.
    /// </summary>
    private async Task SnapAsync(IReadOnlyList<TrackPoint> points, CancellationToken cancellationToken)
    {
        try
        {
            _snapStatus.Text = Loc.Get("SnapWorking");
            _snapStatus.IsVisible = true;

            var result = await _snapper.SnapAsync(points, cancellationToken);
            if (cancellationToken.IsCancellationRequested)
                return;

            if (result.Track.MatchedPoints > 0)
            {
                var coords = result.Track.Line.Select(p => new[] { p.Lat, p.Lon }).ToList();
                await _map.RunAsync($"setTrack({MapView.Json(coords)}, '{StartColor}', '{EndColor}')");
            }

            _snapStatus.Text = result switch
            {
                { Track.MatchedPoints: 0, TilesMissing: > 0 } => Loc.Get("SnapUnavailable"),
                { TilesMissing: > 0 } => Loc.Format("SnapPartial", result.TilesMissing, result.TilesWanted),
                { Track.MatchedPoints: > 0 } => Loc.Get("SnapDone"),
                _ => string.Empty,
            };
            _snapStatus.IsVisible = _snapStatus.Text.Length > 0;
        }
        catch (OperationCanceledException)
        {
            // Se cambio de persona o de dia, o se salio de la pagina.
        }
        catch (Exception ex)
        {
            // Es solo el dibujo: se queda recto, sin dialogo.
            CrashLog.Error("HistoryPage.Snap", ex);
            if (!cancellationToken.IsCancellationRequested)
                _snapStatus.Text = Loc.Get("SnapUnavailable");
        }
    }
}
