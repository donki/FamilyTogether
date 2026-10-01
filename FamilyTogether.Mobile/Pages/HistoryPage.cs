using FamilyTogether.Core;
using FamilyTogether.Mobile.Controls;
using FamilyTogether.Mobile.Helpers;
using FamilyTogether.Mobile.Localization;
using FamilyTogether.Mobile.Services;

namespace FamilyTogether.Mobile.Pages;

/// <summary>
/// Historial (HU4, FR-014): el recorrido de un miembro en las <b>ultimas 24 horas</b> (lo que se
/// ve al abrir; cruza la medianoche) o en un dia de los ultimos 30 (la retencion), como una linea en
/// el mapa con su inicio y su fin.
/// </summary>
/// <remarks>
/// <para><b>Mi recorrido</b> se junta con la copia local de las ultimas 24 h
/// (<see cref="LocalTrack"/>): se ve aunque el servidor falle o la cola aun no se haya enviado.</para>
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
    private const string StopColor = "#3525CD";

    private readonly FamilyService _family = ServiceHelper.Get<FamilyService>();
    private readonly TrackSnapper _snapper = ServiceHelper.Get<TrackSnapper>();
    private readonly Label _snapStatus = new();
    private CancellationTokenSource? _snapCancel;
    private List<object[]> _stops = [];
    private static readonly TimeSpan SnapLimit = TimeSpan.FromSeconds(75);
    private readonly GroupSelector _selector = new();
    private readonly Picker _member = new();
    private readonly Picker _period = new();
    private readonly DatePicker _day = new() { Format = "d" };   // fecha corta (27/09/2026)
    private readonly Border _dayBox;
    private readonly LocalTrack _localTrack = ServiceHelper.Get<LocalTrack>();
    private readonly MapView _map = new();
    private readonly Label _summary = new();
    private IReadOnlyList<Member> _members = [];
    private bool _updating;
    private bool _loading;

    /// <summary>Ultimos miembros de cada grupo (en memoria): sin conexion se sigue viendo mi recorrido local.</summary>
    private static readonly Dictionary<Guid, IReadOnlyList<Member>> s_lastMembers = [];

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

        // Ultimas 24 horas (por defecto) o un dia concreto.
        _period.Title = Loc.Get("HistoryPeriod");
        _period.ItemsSource = new List<string> { Loc.Get("HistoryLast24h"), Loc.Get("HistoryOneDay") };
        _period.SelectedIndex = 0;
        _dayBox = UiKit.Input(_day);
        _dayBox.IsVisible = false;
        _period.SelectedIndexChanged += async (_, _) =>
        {
            _dayBox.IsVisible = !Last24h;
            await LoadTrackAsync();
        };

        var legend = new HorizontalStackLayout
        {
            Spacing = 16,
            Children =
            {
                Dot(StartColor, Loc.Get("TrackStart")),
                Dot(EndColor, Loc.Get("TrackEnd")),
                Dot(StopColor, Loc.Get("TrackStop")),
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
                    g.Add(UiKit.Input(_period), 1, 0);
                }),
                _dayBox,
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

    private bool Last24h => _period.SelectedIndex != 1;

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

        IReadOnlyList<Member>? members;
        if (s_lastMembers.TryGetValue(group.Id, out var cached))
        {
            try
            {
                members = await _family.GetMembersAsync(group.Id);
            }
            catch (FamilyTogetherException ex) when (ex.IsNetwork || ex.Code == FamilyTogetherException.Server)
            {
                members = cached;
            }
        }
        else
        {
            var (ok, loaded) = await Ui.RunAsync(this, () => _family.GetMembersAsync(group.Id));
            if (!ok || loaded is null)
                return;
            members = loaded;
        }

        s_lastMembers[group.Id] = members;

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
            var last24h = Last24h;
            var day = DateOnly.FromDateTime(_day.Date ?? DateTime.Today);
            var (from, to) = last24h ? FamilyService.RecentRange(DateTimeOffset.UtcNow) : FamilyService.DayRange(day, TimeZoneInfo.Local);
            _summary.Text = Loc.Get("Loading");
            _snapCancel?.Cancel();
            _snapStatus.IsVisible = false;

            // Mi recorrido sin conexion: lo del movil, sin dialogo y con una linea que lo dice.
            var offline = false;
            bool ok;
            IReadOnlyList<MemberPosition>? points;
            if (member.IsMe)
            {
                try
                {
                    points = await _family.GetHistoryAsync(group.Id, member.UserId, from, to);
                    ok = true;
                }
                catch (FamilyTogetherException ex) when (ex.IsNetwork || ex.Code == FamilyTogetherException.Server)
                {
                    (ok, points, offline) = (false, null, true);
                }
            }
            else
            {
                (ok, points) = await Ui.RunAsync(this, () => _family.GetHistoryAsync(group.Id, member.UserId, from, to));
            }

            // Mi recorrido: tambien lo guardado en el movil (las ultimas 24 h).
            IReadOnlyList<MemberPosition> local = [];
            if (member.IsMe)
            {
                try
                {
                    local = await _localTrack.GetAsync(member.UserId, group.Id, from, to);
                }
                catch (Exception ex)
                {
                    CrashLog.Error("HistoryPage.LocalTrack", ex);
                }
            }

            if ((!ok || points is null) && local.Count == 0)
            {
                _summary.Text = offline ? Loc.Get("Err_network") : string.Empty;
                return;
            }

            var ordered = LocalTrack.Merge(points ?? [], local).ToList();
            if (ordered.Count == 0)
            {
                _summary.Text = Loc.Get(last24h ? "NoPositionsLast24h" : "NoPositionsThatDay");
                _stops = [];
                await _map.RunAsync("clearTrack()");
                return;
            }

            // En un dia, solo la hora; en las ultimas 24 h, «ayer 18:30» si cruza la medianoche.
            string Clock(DateTimeOffset at) => last24h ? TimeTexts.Clock(at) : at.ToLocalTime().ToString("HH:mm", Loc.Culture);
            var first = Clock(ordered[0].At);
            var last = Clock(ordered[^1].At);
            _summary.Text = Loc.Format("TrackSummary", ordered.Count, first, last);
            if (offline)
                _summary.Text += Environment.NewLine + Loc.Get("HistoryOfflineLocal");

            // Sin saltos de ida y vuelta imposibles ni marañas de las paradas (solo el dibujo).
            // Las paradas largas (10 min o mas en ~150 m) se pintan como un punto, no como lineas.
            var cleaned = TrackCleaner.CleanWithStops([.. ordered.Select(p => new TrackPoint(p.Lat, p.Lon, p.Accuracy, p.At))]);
            var clean = cleaned.Points;
            _stops = [.. cleaned.Stops.Select(s => new object[]
            {
                s.Center.Lat, s.Center.Lon,
                Loc.Format("StopLabel", Clock(s.From), Clock(s.To)),
            })];
            CrashLog.Info($"historial{(last24h ? " 24 h" : "")}: {ordered.Count} posiciones ({local.Count} del movil), {clean.Count} tras limpiar, {cleaned.Stops.Count} paradas");
            var coords = clean.Select(p => new[] { p.Lat, p.Lon }).ToList();
            await _map.RunAsync($"setTrack({MapView.Json(coords)}, '{StartColor}', '{EndColor}')");
            await _map.RunAsync($"addStops({MapView.Json(_stops)})");

            if (AppState.SnapTracks && clean.Count >= 2)
            {
                _snapCancel = new CancellationTokenSource();
                _ = SnapAsync(clean, _snapCancel.Token);
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
        // Tope total: pase lo que pase con la red, en este tiempo se acaba (recto si hace falta).
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(SnapLimit);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            _snapStatus.Text = Loc.Get("SnapWorking");
            _snapStatus.IsVisible = true;

            var result = await _snapper.SnapAsync(points, limit.Token);
            if (cancellationToken.IsCancellationRequested)
                return;
            CrashLog.Info($"historial: ajuste {result.Track.MatchedPoints}/{result.Track.PointCount} posiciones, " +
                          $"faltan {result.TilesMissing} de {result.TilesWanted} teselas, {watch.Elapsed.TotalSeconds:F1} s");

            if (result.Track.MatchedPoints > 0)
            {
                var coords = result.Track.Line.Select(p => new[] { p.Lat, p.Lon }).ToList();
                await _map.RunAsync($"setTrack({MapView.Json(coords)}, '{StartColor}', '{EndColor}')");
                await _map.RunAsync($"addStops({MapView.Json(_stops)})");
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
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Tope total agotado: se queda recto y se dice.
            CrashLog.Info($"historial: ajuste sin terminar en {watch.Elapsed.TotalSeconds:F0} s; queda recto");
            _snapStatus.Text = Loc.Get("SnapUnavailable");
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
