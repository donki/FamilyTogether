using FamilyTogether.Core;
using FamilyTogether.Mobile.Controls;
using FamilyTogether.Mobile.Helpers;
using FamilyTogether.Mobile.Localization;
using FamilyTogether.Mobile.Services;

namespace FamilyTogether.Mobile.Pages;

/// <summary>
/// Inicio: el mapa del grupo elegido, a pantalla completa, con un marcador por miembro. El boton de
/// la lupa abre la lista de personas con la hora de su ultima posicion, la bateria o «En pausa»
/// (FR-012); elegir a alguien cierra la lista y centra el mapa en esa persona. Atras cierra la lista
/// antes que nada (Mobile §7). Se refresca cada 30 s mientras se ve y al volver a la app. El boton
/// rojo de SOS flota sobre el mapa.
/// </summary>
public sealed class MapPage : ContentPage, IBackHandler
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
    private readonly Grid _people = new() { IsVisible = false };
    private readonly Border _noGroups = new() { IsVisible = false };
    private readonly ImageButton _search = new();
    private VerticalStackLayout _corner = new();

    private IReadOnlyList<Group> _groups = [];
    private IDispatcherTimer? _timer;
    private bool _loading;

    /// <summary>Se pidio otra carga mientras se cargaba (y si era en silencio).</summary>
    private bool? _loadAgain;
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

        // Lupa: abajo a la derecha, encima del SOS (que no se tapen); el «i» de la atribucion va abajo
        // a la izquierda y el zoom arriba a la derecha.
        _search.Source = "ic_search_w.png";
        _search.Style = Ui.Style("MapFabButton");
        _search.HorizontalOptions = LayoutOptions.End;
        SemanticProperties.SetDescription(_search, Loc.Get("FindPerson"));
        _search.Clicked += (_, _) => ShowPeople(true);

        sos.Margin = new Thickness(0);
        var corner = _corner = new VerticalStackLayout
        {
            Spacing = 12,
            Margin = new Thickness(16),
            HorizontalOptions = LayoutOptions.End,
            VerticalOptions = LayoutOptions.End,
            Children = { _search, sos },
        };

        // Sin grupos: una tarjeta abajo con el camino a Grupos (lo que antes decia la lista).
        _noGroups.Style = Ui.Style("BottomSheet");
        _noGroups.VerticalOptions = LayoutOptions.End;

        // La lista de personas: una hoja sobre el mapa; tocar fuera la cierra.
        var sheet = new Border
        {
            Style = Ui.Style("BottomSheet"),
            VerticalOptions = LayoutOptions.End,
            Content = new VerticalStackLayout
            {
                Padding = new Thickness(16, 12, 16, 8),
                Spacing = 6,
                Children =
                {
                    UiKit.Row(_listTitle, new HorizontalStackLayout
                    {
                        Spacing = 4,
                        Children =
                        {
                            _busy,
                            UiKit.Icon("ic_center.png", async (_, _) => { ShowPeople(false); await _map.RunAsync("fitMembers()"); }, Loc.Get("ShowEveryone")),
                            UiKit.Icon("ic_close.png", (_, _) => ShowPeople(false), Loc.Get("Close")),
                        },
                    }),
                    new ScrollView { MaximumHeightRequest = 360, Content = _list },
                },
            },
        };
        var shade = new BoxView { Color = Colors.Black, Opacity = 0.25 };
        var closeTap = new TapGestureRecognizer();
        closeTap.Tapped += (_, _) => ShowPeople(false);
        shade.GestureRecognizers.Add(closeTap);
        _people.Children.Add(shade);
        _people.Children.Add(sheet);

        var mapArea = new Grid { Children = { _map, fit, corner, _noGroups, _people } };

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
            ],
        };
        root.Add(top, 0, 0);
        root.Add(_banners, 0, 1);
        root.Add(mapArea, 0, 2);
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

    /// <summary>La ultima lectura de este movil: mi marca se pinta ahi, no en la del servidor.</summary>
    private (double Lat, double Lon, DateTimeOffset At)? _myFix;

    private IReadOnlyList<Member> _members = [];
    private IReadOnlyList<MemberPosition> _positions = [];

    /// <summary>
    /// Mi posicion en el mapa: mi propia marca (foto o inicial con mi nombre) se pinta en la lectura
    /// de este movil, que es mas reciente que la del servidor (la ReadingPolicy no envia las lecturas
    /// con el movil quieto). Nada de punto azul aparte: eran dos marcas para la misma persona. Con
    /// <paramref name="center"/> (al abrir) se centra en ella; si no hay permiso o lectura, mi marca
    /// va en la posicion del servidor y se encuadra al grupo como antes.
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

        _myFix = (p.Lat, p.Lon, p.At);
        if (_members.Count > 0 && _groups.FirstOrDefault(g => g.Id == AppState.SelectedGroup) is { KeyMissing: false } group)
        {
            ShowList(group, _members, _positions);
            await _map.RunAsync($"setMembers({MapView.Json(MarkerItems())})");
        }
        if (center)
        {
            _centeredOnMe = true;
            await _map.RunAsync($"center({MapView.Num(p.Lat)}, {MapView.Num(p.Lon)}, 15)");
        }
    }

    /// <summary>Atras con la lista abierta: se cierra la lista y el mapa se queda.</summary>
    public bool HandleBack()
    {
        if (!_people.IsVisible)
            return false;
        ShowPeople(false);
        return true;
    }

    private void ShowPeople(bool show) => _people.IsVisible = show;

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        ShowPeople(false);
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

    private void OnResumed(object? sender, EventArgs e) => UiThread.Post(async () => await LoadAsync(quiet: true));

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
        {
            // Se vuelve a cargar al acabar (p. ej. se eligio otro grupo mientras cargaba: si no, el
            // mapa seguia con el de antes hasta el siguiente refresco). Con aviso si alguna lo pedia.
            _loadAgain = (_loadAgain ?? true) && quiet;
            return;
        }
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
                _members = [];
                _positions = [];
                ShowNoGroups();
                await _map.RunAsync("setMembers([])");
                return;
            }

            var group = _groups.FirstOrDefault(g => g.Id == AppState.SelectedGroup) ?? _groups[0];
            AppState.SelectedGroup = group.Id;

            if (group.KeyMissing)
            {
                _members = [];
                _positions = [];
                ShowList(group, [], []);
                await _map.RunAsync("setMembers([])");
                return;
            }

            var membersTask = _family.GetMembersAsync(group.Id);
            var positionsTask = _family.GetLastPositionsAsync(group.Id);
            await Task.WhenAll(membersTask, positionsTask);
            var members = membersTask.Result;
            var positions = positionsTask.Result;

            _members = members;
            _positions = positions;
            ShowList(group, members, positions);
            await DrawAsync();

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
            if (_loadAgain is { } again)
            {
                _loadAgain = null;
                await LoadAsync(again);
            }
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
        _list.Clear();
        ShowPeople(false);
        // Sin grupos no hay a quien buscar ni a quien mandar un SOS, y la tarjeta ocupa ese sitio.
        _corner.IsVisible = false;
        _noGroups.Content = new VerticalStackLayout
        {
            Padding = new Thickness(16, 12, 16, 8),
            Spacing = 6,
            Children =
            {
                new Label { Text = Loc.Get("NoGroupsTitle"), Style = Ui.Style("CardTitle") },
                UiKit.Body(Loc.Get("NoGroupsBody")),
                UiKit.Primary(Loc.Get("GoToGroups"), "ic_group_w.png", async (_, _) => await Shell.Current.GoToAsync("//GroupsPage")),
            },
        };
        _noGroups.IsVisible = true;
    }

    private void ShowList(Group group, IReadOnlyList<Member> members, IReadOnlyList<MemberPosition> positions)
    {
        _noGroups.IsVisible = false;
        _corner.IsVisible = true;
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
            // Yo: la hora de la lectura de este movil si es mas nueva (la marca del mapa va ahi).
            if (member.IsMe && _myFix is { } fix && (position is null || fix.At > position.At))
                position = new MemberPosition(member.UserId, fix.Lat, fix.Lon, 0, MyBattery(position), fix.At);
            _list.Add(MemberRow(member, position));
        }
    }

    private static int MyBattery(MemberPosition? server)
    {
        try
        {
#if ANDROID
            // BatteryManager directamente: el Battery de MAUI exige BATTERY_STATS, que no se declara.
            var context = Android.App.Application.Context;
            var manager = (Android.OS.BatteryManager?)context.GetSystemService(Android.Content.Context.BatteryService);
            var level = manager?.GetIntProperty((int)Android.OS.BatteryProperty.Capacity) ?? -1;
            if (level is >= 0 and <= 100)
                return level;
#endif
        }
        catch (Exception ex)
        {
            CrashLog.Info($"mapa: no se pudo leer la bateria: {ex.Message}");
        }
        return server?.Battery ?? 0;
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
            tap.Tapped += (_, _) =>
            {
                ShowPeople(false);
                _ = _map.RunAsync($"focusMember('{member.UserId:D}')");
            };
            row.GestureRecognizers.Add(tap);
            SemanticProperties.SetDescription(row, $"{name}, {detail}");
        }

        return row;
    }

    /// <summary>
    /// Las marcas del mapa: cada miembro que no esta en pausa, en su ultima posicion del servidor; yo,
    /// en la lectura de este movil si la hay y es mas nueva.
    /// </summary>
    private List<object> MarkerItems()
    {
        var byUser = _positions.GroupBy(p => p.UserId).ToDictionary(g => g.Key, g => g.MaxBy(p => p.At)!);
        var items = new List<object>();
        foreach (var m in _members.Where(m => !m.Paused))
        {
            byUser.TryGetValue(m.UserId, out var server);
            double lat, lon;
            DateTimeOffset at;
            if (m.IsMe && _myFix is { } fix && (server is null || fix.At >= server.At))
                (lat, lon, at) = fix;
            else if (server is not null)
                (lat, lon, at) = (server.Lat, server.Lon, server.At);
            else
                continue;

            items.Add(new
            {
                id = m.UserId.ToString("D"),
                name = m.Name,
                initials = Avatars.Initials(m.Name),
                color = Avatars.ColorFor(m.UserId),
                avatar = m.AvatarBase64,
                lat,
                lon,
                stale = DateTimeOffset.Now - at > TimeSpan.FromHours(1),
            });
        }
        return items;
    }

    private async Task DrawAsync()
    {
        var items = MarkerItems();
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
