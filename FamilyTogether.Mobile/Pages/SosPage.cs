using FamilyTogether.Core;
using FamilyTogether.Mobile.Controls;
using FamilyTogether.Mobile.Helpers;
using FamilyTogether.Mobile.Localization;
using FamilyTogether.Mobile.Services;

namespace FamilyTogether.Mobile.Pages;

/// <summary>
/// SOS (HU6, FR-019 a FR-021): cuenta atras de 3 s, grande, con Cancelar. Durante la cuenta atras se
/// pueden desmarcar grupos (por defecto, todos). Al llegar a cero se envia con la posicion de ahora
/// o, sin GPS, con la ultima conocida (y se dice). Sin conexion queda pendiente y se reintenta solo.
/// </summary>
public sealed class SosPage : ContentPage, IBackHandler
{
    private const int CountdownSeconds = 3;

    /// <summary>Lo que dura cada segundo de la cuenta atras (las pruebas lo acortan).</summary>
    internal static TimeSpan CountdownTick { get; set; } = TimeSpan.FromSeconds(1);

    private readonly FamilyService _family = ServiceHelper.Get<FamilyService>();
    private readonly SosService _sos = ServiceHelper.Get<SosService>();
    private readonly ILocationSharing? _sharing = ServiceHelper.TryGet<ILocationSharing>();

    private readonly Label _count = new() { FontSize = 96, FontAttributes = FontAttributes.Bold, HorizontalOptions = LayoutOptions.Center };
    private readonly Label _headline = new() { FontSize = 20, FontAttributes = FontAttributes.Bold, HorizontalTextAlignment = TextAlignment.Center };
    private readonly Label _detail = new() { HorizontalTextAlignment = TextAlignment.Center };
    private readonly VerticalStackLayout _groupsList = new() { Spacing = 0 };
    private readonly Button _cancel;
    private readonly Button _close;
    private readonly Dictionary<Guid, CheckBox> _checks = [];

    private IReadOnlyList<Group> _groups = [];
    private readonly TaskCompletionSource _groupsLoaded = new();
    private CancellationTokenSource? _countdown;
    private IDispatcherTimer? _retry;
    private bool _sent;

    public SosPage()
    {
        Title = Loc.Get("SosTitle");
        _count.TextColor = Ui.Color("Danger");
        _headline.TextColor = Ui.Color("Danger");
        _detail.Style = Ui.Style("BodyText");
        _detail.HorizontalTextAlignment = TextAlignment.Center;

        _cancel = UiKit.Outline(Loc.Get("SosCancel"), "ic_close.png", (_, _) => Cancel());
        _cancel.FontSize = 20;
        _cancel.MinimumHeightRequest = 64;
        _close = UiKit.Primary(Loc.Get("BackToMap"), "ic_map_w.png", async (_, _) => await Navigation.PopAsync());
        _close.IsVisible = false;

        Content = UiKit.Page(
            new VerticalStackLayout
            {
                Padding = 16,
                Spacing = 16,
                Children =
                {
                    _headline,
                    _count,
                    _detail,
                    UiKit.Card(new VerticalStackLayout
                    {
                        Spacing = 4,
                        Children = { UiKit.Title(Loc.Get("SosToGroups")), UiKit.Hint(Loc.Get("SosToGroupsHint")), _groupsList },
                    }),
                },
            },
            new VerticalStackLayout { Spacing = 8, Children = { _cancel, _close } });
    }

    public bool HandleBack()
    {
        // Atras durante la cuenta atras es Cancelar: nunca se envia un SOS por salir de la pantalla.
        if (_countdown is not null && !_sent)
            Cancel();
        return false;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_countdown is not null || _sent)
            return;

        _headline.Text = Loc.Get("SosSendingIn");
        _detail.Text = Loc.Get("SosCancelHint");
        _ = LoadGroupsAsync();
        await RunCountdownAsync();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _retry?.Stop();
        if (!_sent)
            _countdown?.Cancel();
    }

    private async Task LoadGroupsAsync()
    {
        try
        {
            _groups = await _family.GetGroupsAsync();
            _groupsList.Clear();
            _checks.Clear();
            foreach (var group in _groups)
            {
                var check = new CheckBox { IsChecked = !group.KeyMissing, IsEnabled = !group.KeyMissing };
                _checks[group.Id] = check;
                var name = group.KeyMissing ? Loc.Get("GroupNoKeyName") : group.Name;
                var label = UiKit.ItemTitle(name);
                var row = new Grid
                {
                    ColumnDefinitions = [new ColumnDefinition(new GridLength(48)), new ColumnDefinition(GridLength.Star)],
                    MinimumHeightRequest = 48,
                };
                row.Add(check, 0, 0);
                row.Add(label, 1, 0);
                label.VerticalOptions = LayoutOptions.Center;
                var tap = new TapGestureRecognizer();
                tap.Tapped += (_, _) => { if (check.IsEnabled && !_sent) check.IsChecked = !check.IsChecked; };
                label.GestureRecognizers.Add(tap);
                _groupsList.Add(row);
            }

            if (_groups.Count == 0)
                _groupsList.Add(UiKit.Body(Loc.Get("SosNoGroups")));
        }
        catch (Exception ex)
        {
            CrashLog.Error("SosPage.LoadGroups", ex);
            _groupsList.Clear();
            _groupsList.Add(UiKit.Body(ErrorTexts.Describe(ex)));
        }
        finally
        {
            _groupsLoaded.TrySetResult();
        }
    }

    private async Task RunCountdownAsync()
    {
        _countdown = new CancellationTokenSource();
        var token = _countdown.Token;
        try
        {
            for (var s = CountdownSeconds; s > 0; s--)
            {
                _count.Text = s.ToString(Loc.Culture);
                try
                {
                    HapticFeedback.Default.Perform(HapticFeedbackType.LongPress);
                }
                catch (Exception)
                {
                    // Sin vibrador no pasa nada.
                }

                await Task.Delay(CountdownTick, token);
            }

            // Si los grupos aun no han llegado, se espera a tenerlos: sin ellos no hay a quien enviar.
            await _groupsLoaded.Task.WaitAsync(token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        _count.Text = "0";
        await SendAsync();
    }

    private void Cancel()
    {
        if (_sent)
            return;

        _countdown?.Cancel();
        _headline.Text = Loc.Get("SosCancelled");
        _count.IsVisible = false;
        _detail.Text = Loc.Get("SosCancelledBody");
        _cancel.IsVisible = false;
        _close.IsVisible = true;
        foreach (var check in _checks.Values)
            check.IsEnabled = false;
    }

    private async Task SendAsync()
    {
        _sent = true;
        _cancel.IsVisible = false;
        _close.IsVisible = true;
        foreach (var check in _checks.Values)
            check.IsEnabled = false;

        var targets = _groups.Where(g => _checks.TryGetValue(g.Id, out var c) && c.IsChecked).Select(g => g.Id).ToList();
        if (targets.Count == 0)
        {
            _headline.Text = Loc.Get("SosNotSent");
            _count.IsVisible = false;
            _detail.Text = Loc.Get("SosNoTargets");
            return;
        }

        _headline.Text = Loc.Get("SosLocating");
        _count.IsVisible = false;
        _detail.Text = string.Empty;

        (double Lat, double Lon, double Accuracy, DateTimeOffset At, bool Stale)? position = null;
        try
        {
            if (_sharing is not null)
                position = await _sharing.GetCurrentOrLastAsync();
        }
        catch (Exception ex)
        {
            CrashLog.Error("SosPage.GetCurrentOrLast", ex);
        }

        if (position is not { } p)
        {
            _headline.Text = Loc.Get("SosNotSent");
            _detail.Text = Loc.Get("SosNoLocation");
            await Ui.AlertAsync(this, "SosTitle", Loc.Get("SosNoLocation"));
            return;
        }

        var (ok, _) = await Ui.RunAsync(this, () => _sos.SendAsync(targets, (p.Lat, p.Lon, p.Accuracy, p.At, p.Stale)));
        if (!ok)
        {
            _headline.Text = Loc.Get("SosNotSent");
            return;
        }

        ShowStatus(targets.Count, p.Stale, p.At);
        if (_sos.HasPending)
        {
            _retry ??= Dispatcher.CreateTimer();
            _retry.Interval = TimeSpan.FromSeconds(10);
            _retry.Tick += async (_, _) =>
            {
                try
                {
                    await _sos.RetryPendingAsync();
                }
                catch (Exception ex)
                {
                    CrashLog.Error("SosPage.Retry", ex);
                }

                ShowStatus(targets.Count, p.Stale, p.At);
                if (!_sos.HasPending)
                    _retry.Stop();
            };
            _retry.Start();
        }
    }

    private void ShowStatus(int groups, bool stale, DateTimeOffset at)
    {
        var pending = _sos.HasPending;
        _headline.Text = Loc.Get(pending ? "SosPending" : "SosSent");
        _headline.TextColor = Ui.Color(pending ? "Danger" : "Success");

        var text = pending ? Loc.Get("SosPendingBody") : Loc.Format("SosSentBody", groups);
        if (stale)
            text += Environment.NewLine + Environment.NewLine + Loc.Format("SosStale", TimeTexts.Ago(at));
        _detail.Text = text;
    }
}
