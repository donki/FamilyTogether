using FamilyTogether.Mobile.Helpers;
using FamilyTogether.Mobile.Localization;
using FamilyTogether.Mobile.Services;

namespace FamilyTogether.Mobile.Pages;

/// <summary>
/// Guia de configuracion paso a paso (constitucion General 6.10, referencia
/// <c>Mobile/Credentials/Pages/TutorialPage.cs</c>): ubicacion precisa, «Permitir siempre»,
/// notificaciones, ahorro de bateria e inicio automatico del fabricante. Cada paso explica que hace,
/// tiene un boton que lo hace o abre la pantalla del sistema, y su estado se vuelve a mirar solo
/// cada dos segundos y al volver a la app. Sale sola una vez (tras la bienvenida) y luego desde el menu.
/// </summary>
public sealed class GuidePage : ContentPage
{
    private sealed record Step(
        string Icon,
        string Title,
        string Body,
        Func<Task<bool>>? IsDone = null,
        string? ActionText = null,
        Func<Task>? Action = null,
        bool Optional = false);

    private readonly ILocationSharing? _sharing = ServiceHelper.TryGet<ILocationSharing>();
    private readonly INotifier? _notifier = ServiceHelper.TryGet<INotifier>();
    private List<Step> _steps = [];
    private int _index;
    private IDispatcherTimer? _timer;
    private bool _checking;

    private readonly Label _progress = new() { HorizontalOptions = LayoutOptions.Center };
    private readonly HorizontalStackLayout _dots = new() { Spacing = 6, HorizontalOptions = LayoutOptions.Center };
    private readonly Image _icon = new() { WidthRequest = 36, HeightRequest = 36, Aspect = Aspect.AspectFit, HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center };
    private readonly Label _title = new() { FontSize = 22, FontAttributes = FontAttributes.Bold, HorizontalTextAlignment = TextAlignment.Center };
    private readonly Label _body = new();
    private readonly Image _stateIcon = new() { WidthRequest = 22, HeightRequest = 22, VerticalOptions = LayoutOptions.Center };
    private readonly Label _stateText = new() { FontSize = 15, FontAttributes = FontAttributes.Bold, VerticalOptions = LayoutOptions.Center };
    private readonly HorizontalStackLayout _state;
    private readonly Button _action = new() { ContentLayout = new Button.ButtonContentLayout(Button.ButtonContentLayout.ImagePosition.Left, 8) };
    private readonly Button _back = new() { ImageSource = "ic_back.png", ContentLayout = new Button.ButtonContentLayout(Button.ButtonContentLayout.ImagePosition.Left, 6) };
    private readonly Button _next = new() { ContentLayout = new Button.ButtonContentLayout(Button.ButtonContentLayout.ImagePosition.Right, 6) };

    public GuidePage()
    {
        Title = Loc.Get("MenuGuide");

        _action.Style = Ui.Style("PrimaryButton");
        _back.Style = Ui.Style("OutlineButton");
        _next.Style = Ui.Style("PrimaryButton");
        _progress.Style = Ui.Style("HintText");
        _body.Style = Ui.Style("BodyText");
        Ui.SetThemeColor(_title, Label.TextColorProperty, "TextPrimary");
        _state = new HorizontalStackLayout { Spacing = 8, HorizontalOptions = LayoutOptions.Center, Children = { _stateIcon, _stateText } };

        _action.Clicked += OnActionClicked;
        _back.Clicked += (_, _) => Go(_index - 1);
        _next.Clicked += OnNextClicked;
        _back.Text = Loc.Get("GuideBack");

        var card = new Border
        {
            Style = Ui.Style("Card"),
            Content = new VerticalStackLayout
            {
                Padding = new Thickness(20, 24),
                Spacing = 16,
                Children =
                {
                    new Border
                    {
                        WidthRequest = 72,
                        HeightRequest = 72,
                        HorizontalOptions = LayoutOptions.Center,
                        StrokeThickness = 0,
                        StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 36 },
                        BackgroundColor = Color.FromArgb("#263525CD"),
                        Content = _icon,
                    },
                    _title, _body, _state, _action,
                },
            },
        };

        var nav = new Grid
        {
            ColumnDefinitions = [new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Star)],
            ColumnSpacing = 12,
            Padding = new Thickness(16, 8, 16, 16),
        };
        nav.Add(_back, 0, 0);
        nav.Add(_next, 1, 0);

        // Anterior / Siguiente fijos abajo: con la letra grande el texto se desplaza, pero los
        // botones para avanzar siempre se ven.
        var root = new Grid { RowDefinitions = [new RowDefinition(GridLength.Star), new RowDefinition(GridLength.Auto)] };
        root.Add(new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = new Thickness(16, 16, 16, 8),
                Spacing = 14,
                Children = { _progress, _dots, card },
            },
        }, 0, 0);
        root.Add(nav, 0, 1);
        Content = root;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();

        // Sale sola una vez: vista una vez, ya no se vuelve a abrir sola.
        AppState.GuideDone = true;

        _steps = BuildSteps();
        if (_index >= _steps.Count)
            _index = 0;
        Show();

        _timer ??= Dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(2);
        _timer.Tick -= OnTick;
        _timer.Tick += OnTick;
        _timer.Start();
        App.AppResumed += OnResumed;
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _timer?.Stop();
        App.AppResumed -= OnResumed;
    }

    private void OnTick(object? sender, EventArgs e) => _ = RefreshStateAsync();

    // Al volver de una pantalla del sistema (permisos, bateria), el paso se vuelve a mirar al momento.
    private void OnResumed(object? sender, EventArgs e) => UiThread.Post(() => _ = RefreshStateAsync());

    private List<Step> BuildSteps()
    {
        var steps = new List<Step>
        {
            new("ic_guide.png", Loc.Get("GuideIntroTitle"), Loc.Get("GuideIntroBody")),
        };

        if (_sharing is { } sharing)
        {
            steps.Add(new("ic_location.png", Loc.Get("GuidePreciseTitle"), Loc.Get("GuidePreciseBody"),
                async () => await sharing.CheckPermissionAsync() != LocationPermissionState.Denied,
                Loc.Get("GuidePreciseAction"),
                async () =>
                {
                    if (await sharing.RequestPermissionAsync() == LocationPermissionState.Denied)
                        sharing.OpenAppSettings();
                }));

            steps.Add(new("ic_location.png", Loc.Get("GuideAlwaysTitle"), Loc.Get("GuideAlwaysBody"),
                async () => await sharing.CheckPermissionAsync() == LocationPermissionState.Always,
                Loc.Get("GuideAlwaysAction"),
                async () =>
                {
                    // Android 11+ no enseña «Permitir siempre» en un dialogo: lleva a Ajustes. Si
                    // tras pedirlo no esta, se abre la ficha de la app para elegirlo a mano.
                    if (await sharing.RequestPermissionAsync() != LocationPermissionState.Always)
                        sharing.OpenAppSettings();
                }));
        }

        if (_notifier is { } notifier)
        {
            steps.Add(new("ic_bell.png", Loc.Get("GuideNotifTitle"), Loc.Get("GuideNotifBody"),
                () => Task.FromResult(notifier.AreEnabled),
                Loc.Get("GuideNotifAction"),
                async () =>
                {
                    if (!await notifier.RequestPermissionAsync())
                        _sharing?.OpenAppSettings();
                }));
        }

        if (_sharing is { } s)
        {
            steps.Add(new("ic_battery_saver.png", Loc.Get("GuideBatteryTitle"), Loc.Get("GuideBatteryBody"),
                () => Task.FromResult(s.IsIgnoringBatteryOptimizations),
                Loc.Get("GuideBatteryAction"),
                () => { s.OpenBatteryOptimizationSettings(); return Task.CompletedTask; }));

            if (s.ManufacturerAutostartHint is { Length: > 0 } hint)
            {
                steps.Add(new("ic_autostart.png", Loc.Get("GuideAutostartTitle"), hint,
                    null,
                    Loc.Get("GuideAutostartAction"),
                    () => { s.OpenManufacturerAutostartSettings(); return Task.CompletedTask; },
                    Optional: true));
            }
        }

        steps.Add(new("ic_check.png", Loc.Get("GuideEndTitle"), Loc.Get("GuideEndBody")));
        return steps;
    }

    private void Go(int index)
    {
        if (index < 0 || index >= _steps.Count)
            return;
        _index = index;
        Show();
    }

    private void Show()
    {
        var step = _steps[_index];
        var last = _index == _steps.Count - 1;
        _progress.Text = Loc.Format("GuideStepOf", _index + 1, _steps.Count);
        _icon.Source = step.Icon;
        _title.Text = step.Title;
        _body.Text = step.Body;
        _action.IsVisible = step.Action is not null;
        _action.Text = step.ActionText;
        _back.IsVisible = _index > 0;
        _next.Text = last ? Loc.Get("GuideFinish") : Loc.Get("GuideNext");
        _next.ImageSource = last ? "ic_check_w.png" : "ic_next_w.png";
        _dots.Clear();
        for (var i = 0; i < _steps.Count; i++)
        {
            _dots.Add(new BoxView
            {
                WidthRequest = i == _index ? 20 : 8,
                HeightRequest = 8,
                CornerRadius = 4,
                Color = Ui.Color(i <= _index ? "Primary" : "SeparatorLight"),
            });
        }

        _state.IsVisible = false;
        _ = RefreshStateAsync();
    }

    /// <summary>El estado del paso actual: hecho (verde), pendiente (rojo) u opcional.</summary>
    private async Task RefreshStateAsync()
    {
        if (_steps.Count == 0 || _checking)
            return;

        _checking = true;
        try
        {
            var step = _steps[_index];
            if (step.IsDone is null)
            {
                _state.IsVisible = step.Optional;
                _stateIcon.Source = "ic_optional.png";
                _stateText.Text = Loc.Get("GuideOptional");
                _stateText.TextColor = Ui.Color("Primary");
                _action.Style = Ui.Style("PrimaryButton");
                _action.ImageSource = "ic_open_w.png";
                return;
            }

            bool done;
            try
            {
                done = await step.IsDone();
            }
            catch (Exception ex)
            {
                CrashLog.Error("GuidePage.IsDone", ex);
                done = false;
            }

            if (!ReferenceEquals(step, _steps[_index]))
                return;

            _state.IsVisible = true;
            _stateIcon.Source = done ? "ic_done_ok.png" : "ic_pending.png";
            _stateText.Text = Loc.Get(done ? "GuideDone" : "GuidePending");
            _stateText.TextColor = Ui.Color(done ? "Success" : "Danger");

            // Hecho el paso, su boton pasa a secundario: lo importante ya es seguir.
            _action.Style = Ui.Style(done ? "OutlineButton" : "PrimaryButton");
            _action.ImageSource = done ? "ic_open.png" : "ic_open_w.png";
        }
        finally
        {
            _checking = false;
        }
    }

    private async void OnActionClicked(object? sender, EventArgs e)
    {
        var step = _steps[_index];
        if (step.Action is null)
            return;

        await Ui.RunAsync(this, step.Action);
        await RefreshStateAsync();
    }

    private async void OnNextClicked(object? sender, EventArgs e)
    {
        if (_index < _steps.Count - 1)
        {
            Go(_index + 1);
            return;
        }

        _index = 0;

        // Con permiso, se empieza a compartir ya (si hay grupos; si no, al crear o unirse a uno).
        try
        {
            var groups = await ServiceHelper.Get<FamilyTogether.Core.FamilyService>().GetGroupsAsync();
            await AppChores.EnsureSharingAsync(groups.Count > 0);
        }
        catch (Exception ex)
        {
            CrashLog.Error("GuidePage.Finish", ex);
        }

        // Si se llego desde el QR de una invitacion, se sigue por ahi.
        await Shell.Current.GoToAsync(App.PendingJoinCode is not null ? "//GroupsPage" : "//MapPage");
    }
}
