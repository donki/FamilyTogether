using FamilyTogether.Core;
using FamilyTogether.Mobile.Controls;
using FamilyTogether.Mobile.Helpers;
using FamilyTogether.Mobile.Localization;
using FamilyTogether.Mobile.Services;

namespace FamilyTogether.Mobile.Pages;

/// <summary>
/// Inicio: el mapa del grupo elegido con un marcador por miembro y, abajo, la lista con la hora de
/// su ultima posicion, la bateria o «En pausa» (FR-012). Tocar a alguien centra el mapa. Se refresca
/// cada 30 s mientras se ve y al volver a la app. El boton rojo de SOS flota sobre el mapa.
/// </summary>
public sealed class MapPage : ContentPage
{
    private static readonly TimeSpan RefreshEvery = TimeSpan.FromSeconds(30);

    private readonly FamilyService _family = ServiceHelper.Get<FamilyService>();
    private readonly SosService _sos = ServiceHelper.Get<SosService>();
    private readonly ILocationSharing? _sharing = ServiceHelper.TryGet<ILocationSharing>();

    private readonly Picker _groupPicker = new() { HorizontalOptions = LayoutOptions.Fill };
    private readonly VerticalStackLayout _banners = new() { Spacing = 8, Padding = new Thickness(12, 0) };
    private readonly MapView _map = new();
    private readonly VerticalStackLayout _list = new() { Spacing = 0 };
    private readonly Label _listTitle = new();
    private readonly ActivityIndicator _busy = new() { IsRunning = false, IsVisible = false, HeightRequest = 24 };

    private IReadOnlyList<Group> _groups = [];
    private IDispatcherTimer? _timer;
    private bool _loading;
    private bool _fitted;
    private bool _pickerUpdating;

    public MapPage()
    {
        Title = Loc.Get("MenuMap");

        ToolbarItems.Add(new ToolbarItem { IconImageSource = "ic_refresh_w.png", Text = Loc.Get("Refresh"), Command = new Command(async () => await LoadAsync()) });

        _groupPicker.Title = Loc.Get("ChooseGroup");
        _groupPicker.SelectedIndexChanged += OnGroupChanged;
        _listTitle.Style = Ui.Style("CardTitle");
        _map.MemberTapped += (_, id) => _ = _map.RunAsync($"focusMember('{id:D}')");

        var sos = new Button
        {
            Text = Loc.Get("SosButton"),
            ImageSource = "ic_sos_w.png",
            ContentLayout = new Button.ButtonContentLayout(Button.ButtonContentLayout.ImagePosition.Left, 8),
            Style = Ui.Style("SosButton"),
            HorizontalOptions = LayoutOptions.End,
            VerticalOptions = LayoutOptions.End,
            Margin = new Thickness(16),
        };
        SemanticProperties.SetDescription(sos, Loc.Get("SosDescription"));
        sos.Clicked += async (_, _) => await Navigation.PushAsync(new SosPage());

        var fit = new ImageButton
        {
            Source = "ic_center_w.png",
            Style = Ui.Style("MapFabButton"),
            HorizontalOptions = LayoutOptions.Start,
            VerticalOptions = LayoutOptions.Start,
            Margin = new Thickness(12),
        };
        SemanticProperties.SetDescription(fit, Loc.Get("ShowEveryone"));
        fit.Clicked += (_, _) => _ = _map.RunAsync("fitMembers()");

        var mapArea = new Grid { Children = { _map, fit, sos } };

        var sheet = new Border
        {
            Style = Ui.Style("BottomSheet"),
            Content = new VerticalStackLayout
            {
                Padding = new Thickness(16, 12, 16, 8),
                Spacing = 6,
                Children =
                {
                    UiKit.Row(_listTitle, _busy),
                    new ScrollView { MaximumHeightRequest = 260, Content = _list },
                },
            },
        };

        var top = new Border
        {
            Style = Ui.Style("InputBox"),
            Margin = new Thickness(12, 8),
            Content = _groupPicker,
        };

        var root = new Grid
        {
            RowDefinitions =
            [
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Star),
                new RowDefinition(GridLength.Auto),
            ],
        };
        root.Add(top, 0, 0);
        root.Add(_banners, 0, 1);
        root.Add(mapArea, 0, 2);
        root.Add(sheet, 0, 3);
        Content = root;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        // La guia sale sola una vez; y las novedades, al abrir una version nueva (General 6.7).
        if (!AppState.GuideDone)
        {
            await Shell.Current.GoToAsync("//GuidePage");
            return;
        }

        if (AppState.HasUnseenVersion)
        {
            AppState.MarkVersionSeen();
            await Navigation.PushAsync(new WhatsNewPage());
            return;
        }

        _map.StartEvents();
        App.AppResumed += OnResumed;
        App.MapFocusRequested += OnResumed;
        _timer ??= CreateTimer();
        _timer.Start();
        // Al abrir, el mapa se centra en mi posicion (no en el grupo entero): se deja marcado como
        // ya encuadrado para que la carga no lo mueva, y ShowMeAsync lo centra en cuanto hay lectura.
        var centerOnMe = !_centeredOnMe && App.PendingMapFocus is null && !App.PendingMapFit;
        if (centerOnMe)
            _fitted = true;
        await LoadAsync();
        await ShowMeAsync(centerOnMe);
    }

    private bool _centeredOnMe;

    /// <summary>
    /// Mi posicion en el mapa: punto azul con su circulo de precision. Con <paramref name="center"/>
    /// (al abrir) se centra en ella; si no hay permiso o lectura, se encuadra al grupo como antes.
    /// </summary>
    private async Task ShowMeAsync(bool center)
    {
        (double Lat, double Lon, double Accuracy, DateTimeOffset At, bool Stale)? me = null;
        try
        {
            if (_sharing is not null && await _sharing.CheckPermissionAsync() != LocationPermissionState.Denied)
                me = await _sharing.GetCurrentOrLastAsync();
        }
        catch (Exception ex)
        {
            CrashLog.Info($"mapa: no se pudo leer la posicion propia: {ex.Message}");
        }

        if (me is not { } p)
        {
            if (center)
            {
                _fitted = false;
                await _map.RunAsync("fitMembers()");
            }
            return;
        }

        await _map.RunAsync($"setMe({MapView.Num(p.Lat)}, {MapView.Num(p.Lon)}, {MapView.Num(p.Accuracy)})");
        if (center)
        {
            _centeredOnMe = true;
            await _map.RunAsync($"center({MapView.Num(p.Lat)}, {MapView.Num(p.Lon)}, 15)");
        }
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _timer?.Stop();
        _map.StopEvents();
        App.AppResumed -= OnResumed;
        App.MapFocusRequested -= OnResumed;
    }

    private IDispatcherTimer CreateTimer()
    {
        var timer = Dispatcher.CreateTimer();
        timer.Interval = RefreshEvery;
        timer.Tick += async (_, _) =>
        {
            await LoadAsync(quiet: true);
            await ShowMeAsync(center: false);
        };
        return timer;
    }

    private void OnResumed(object? sender, EventArgs e) => MainThread.BeginInvokeOnMainThread(async () => await LoadAsync(quiet: true));

    private async void OnGroupChanged(object? sender, EventArgs e)
    {
        if (_pickerUpdating || _groupPicker.SelectedIndex < 0 || _groupPicker.SelectedIndex >= _groups.Count)
            return;

        AppState.SelectedGroup = _groups[_groupPicker.SelectedIndex].Id;
        _fitted = false;
        await LoadAsync();
    }

    /// <summary>
    /// Carga grupos, miembros y posiciones. En el refresco periodico (<paramref name="quiet"/>) un
    /// fallo de red no saca aviso: ya saldra si el usuario pide algo, y la lista sigue con lo ultimo.
    /// </summary>
    private async Task LoadAsync(bool quiet = false)
    {
        if (_loading)
            return;
        _loading = true;
        _busy.IsVisible = _busy.IsRunning = true;

        try
        {
            var groups = await _family.GetGroupsAsync();
            _groups = groups;
            FillPicker();
            await UpdateBannersAsync();

            if (_groups.Count == 0)
            {
                ShowNoGroups();
                await _map.RunAsync("setMembers([])");
                return;
            }

            var group = _groups.FirstOrDefault(g => g.Id == AppState.SelectedGroup) ?? _groups[0];
            AppState.SelectedGroup = group.Id;

            if (group.KeyMissing)
            {
                ShowList(group, [], []);
                await _map.RunAsync("setMembers([])");
                return;
            }

            var membersTask = _family.GetMembersAsync(group.Id);
            var positionsTask = _family.GetLastPositionsAsync(group.Id);
            await Task.WhenAll(membersTask, positionsTask);
            var members = membersTask.Result;
            var positions = positionsTask.Result;

            ShowList(group, members, positions);
            await DrawAsync(members, positions);

            _ = AppChores.EnsureSharingAsync(true);
        }
        catch (Exception ex)
        {
            if (quiet)
                CrashLog.Error("MapPage.LoadAsync", ex);
            else
                await Ui.ShowErrorAsync(this, ex);
        }
        finally
        {
            _loading = false;
            _busy.IsVisible = _busy.IsRunning = false;
        }
    }

    private void FillPicker()
    {
        _pickerUpdating = true;
        try
        {
            _groupPicker.ItemsSource = _groups.Select(g => g.KeyMissing ? Loc.Get("GroupNoKeyName") : g.Name).ToList();
            var index = _groups.ToList().FindIndex(g => g.Id == AppState.SelectedGroup);
            _groupPicker.SelectedIndex = _groups.Count == 0 ? -1 : Math.Max(0, index);
            _groupPicker.IsEnabled = _groups.Count > 1;
        }
        finally
        {
            _pickerUpdating = false;
        }
    }

    private async Task UpdateBannersAsync()
    {
        _banners.Clear();

        if (_sos.HasPending)
            _banners.Add(UiKit.Banner(Loc.Get("SosPendingBanner")));

        var selected = _groups.FirstOrDefault(g => g.Id == AppState.SelectedGroup) ?? _groups.FirstOrDefault();
        if (selected is { KeyMissing: true })
        {
            _banners.Add(UiKit.Banner(Loc.Get("KeyMissingBanner"),
                UiKit.Outline(Loc.Get("KeyMissingAction"), "ic_key.png", async (_, _) =>
                {
                    if (await Ui.RunAsync(this, () => _family.RequestMissingKeysAsync()))
                        await Ui.AlertAsync(this, "KeyMissingTitle", Loc.Get("KeyMissingRequested"));
                })));
        }

        if (_groups.Count > 0 && _sharing is { } sharing)
        {
            LocationPermissionState permission;
            try
            {
                permission = await sharing.CheckPermissionAsync();
            }
            catch (Exception ex)
            {
                CrashLog.Error("MapPage.CheckPermission", ex);
                return;
            }

            var text = permission switch
            {
                LocationPermissionState.Denied => Loc.Get("SharingDenied"),
                LocationPermissionState.WhileInUse => Loc.Get("SharingWhileInUse"),
                _ when !sharing.IsRunning => Loc.Get("SharingStopped"),
                _ => null,
            };

            if (text is not null)
            {
                _banners.Add(UiKit.Banner(text,
                    UiKit.Outline(Loc.Get("OpenGuide"), "ic_guide.png", async (_, _) => await Shell.Current.GoToAsync("//GuidePage"))));
            }
        }
    }

    private void ShowNoGroups()
    {
        _listTitle.Text = Loc.Get("NoGroupsTitle");
        _list.Clear();
        _list.Add(UiKit.Body(Loc.Get("NoGroupsBody")));
        _list.Add(UiKit.Primary(Loc.Get("GoToGroups"), "ic_group_w.png", async (_, _) => await Shell.Current.GoToAsync("//GroupsPage")));
    }

    private void ShowList(Group group, IReadOnlyList<Member> members, IReadOnlyList<MemberPosition> positions)
    {
        _listTitle.Text = group.KeyMissing ? Loc.Get("GroupNoKeyName") : group.Name;
        _list.Clear();

        if (group.KeyMissing)
        {
            _list.Add(UiKit.Body(Loc.Get("KeyMissingBanner")));
            return;
        }

        var byUser = positions.GroupBy(p => p.UserId).ToDictionary(g => g.Key, g => g.MaxBy(p => p.At)!);
        foreach (var member in members.OrderByDescending(m => m.IsMe).ThenBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            byUser.TryGetValue(member.UserId, out var position);
            _list.Add(MemberRow(member, position));
        }
    }

    private View MemberRow(Member member, MemberPosition? position)
    {
        var avatar = new AvatarView(40);
        avatar.Set(member.UserId, member.Name, member.AvatarBase64);

        string detail;
        if (member.Paused)
            detail = TimeTexts.Paused(member.PauseUntil);
        else if (position is null)
            detail = Loc.Get("NoPositionYet");
        else
            detail = $"{TimeTexts.Ago(position.At)} · {TimeTexts.Battery(position.Battery)}";

        var name = member.IsMe ? Loc.Format("MeSuffix", member.Name) : member.Name;
        var texts = new VerticalStackLayout
        {
            Spacing = 2,
            VerticalOptions = LayoutOptions.Center,
            Children = { UiKit.ItemTitle(name), UiKit.ItemSubtitle(detail) },
        };

        var row = new Grid
        {
            ColumnDefinitions = [new ColumnDefinition(new GridLength(48)), new ColumnDefinition(GridLength.Star)],
            ColumnSpacing = 12,
            Padding = new Thickness(0, 6),
            MinimumHeightRequest = 56,
        };
        row.Add(avatar, 0, 0);
        row.Add(texts, 1, 0);

        if (position is not null)
        {
            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) => _ = _map.RunAsync($"focusMember('{member.UserId:D}')");
            row.GestureRecognizers.Add(tap);
        }

        return row;
    }

    private async Task DrawAsync(IReadOnlyList<Member> members, IReadOnlyList<MemberPosition> positions)
    {
        var byUser = members.GroupBy(m => m.UserId).ToDictionary(g => g.Key, g => g.First());
        var items = positions
            .Where(p => byUser.TryGetValue(p.UserId, out var m) && !m.Paused)
            .Select(p =>
            {
                var m = byUser[p.UserId];
                return new
                {
                    id = p.UserId.ToString("D"),
                    name = m.Name,
                    initials = Avatars.Initials(m.Name),
                    color = Avatars.ColorFor(m.UserId),
                    avatar = m.AvatarBase64,
                    lat = p.Lat,
                    lon = p.Lon,
                    stale = DateTimeOffset.Now - p.At > TimeSpan.FromHours(1),
                };
            })
            .ToList();

        await _map.RunAsync($"setMembers({MapView.Json(items)})");

        // Abierto desde un aviso (SOS, zona): al sitio del evento, o a todo el grupo.
        if (App.PendingMapFocus is { } focus)
        {
            App.PendingMapFocus = null;
            App.PendingMapFit = false;
            _fitted = true;
            await _map.RunAsync($"center({MapView.Num(focus.Lat)}, {MapView.Num(focus.Lon)}, 16)");
            return;
        }

        if (App.PendingMapFit)
        {
            App.PendingMapFit = false;
            _fitted = false;
        }

        if (!_fitted && items.Count > 0)
        {
            _fitted = true;
            await _map.RunAsync("fitMembers()");
        }
    }
}
