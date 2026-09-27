using FamilyTogether.Core;
using FamilyTogether.Mobile.Controls;
using FamilyTogether.Mobile.Helpers;
using FamilyTogether.Mobile.Localization;
using FamilyTogether.Mobile.Services;

namespace FamilyTogether.Mobile.Pages;

/// <summary>
/// Invitar a un grupo (HU1, FR-006): el QR grande, el codigo en letra grande debajo y la cuenta
/// atras de sus 5 minutos. Al caducar se pide otro con un toque. Cada codigo es independiente: se
/// pueden tener varios vigentes a la vez.
/// </summary>
/// <remarks>
/// Es una pagina y no un aviso porque hay que enseñarla: se deja abierta mientras el otro apunta con
/// su movil. El QR va sobre blanco: sobre oscuro, la camara no lo pilla.
/// </remarks>
public sealed class InvitePage : ContentPage
{
    private readonly FamilyService _family = ServiceHelper.Get<FamilyService>();
    private readonly Group _group;

    private readonly Image _qr = new() { WidthRequest = 260, HeightRequest = 260, Margin = 16 };
    private readonly Label _code = new() { FontSize = 36, FontAttributes = FontAttributes.Bold, HorizontalOptions = LayoutOptions.Center, FontFamily = "monospace", CharacterSpacing = 2 };
    private readonly Label _countdown = new() { FontSize = 20, HorizontalOptions = LayoutOptions.Center };
    private readonly Button _share;
    private IDispatcherTimer? _timer;
    private Invitation? _invitation;
    private bool _creating;

    public InvitePage(Group group)
    {
        _group = group;
        Title = Loc.Get("InviteTitle");
        Ui.SetThemeColor(_code, Label.TextColorProperty, "TextPrimary");
        Ui.SetThemeColor(_countdown, Label.TextColorProperty, "TextSecondary");

        ToolbarItems.Add(new ToolbarItem { IconImageSource = "ic_close_w.png", Text = Loc.Get("Close"), Command = new Command(async () => await Navigation.PopAsync()) });

        _share = UiKit.Outline(Loc.Get("ShareCode"), "ic_share.png", async (_, _) => await ShareAsync());

        Content = UiKit.Page(
            new VerticalStackLayout
            {
                Padding = 16,
                Spacing = 14,
                Children =
                {
                    UiKit.Hint(Loc.Format("InviteHint", group.Name)),
                    new Border
                    {
                        Style = Ui.Style("Card"),
                        BackgroundColor = Colors.White,
                        HorizontalOptions = LayoutOptions.Center,
                        Content = _qr,
                    },
                    _code,
                    _countdown,
                    UiKit.Hint(Loc.Get("InviteHowTo")),
                },
            },
            new VerticalStackLayout
            {
                Spacing = 8,
                Children =
                {
                    UiKit.Primary(Loc.Get("InviteNewCode"), "ic_refresh_w.png", async (_, _) => await CreateAsync()),
                    _share,
                },
            });
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        _timer ??= CreateTimer();
        _timer.Start();
        if (_invitation is null)
            await CreateAsync();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _timer?.Stop();
    }

    private IDispatcherTimer CreateTimer()
    {
        var timer = Dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(1);
        timer.Tick += (_, _) => UpdateCountdown();
        return timer;
    }

    private async Task CreateAsync()
    {
        if (_creating)
            return;
        _creating = true;
        try
        {
            var (ok, invitation) = await Ui.RunAsync(this, () => _family.CreateInvitationAsync(_group.Id));
            if (!ok || invitation is null)
                return;

            _invitation = invitation;
            var png = QrCodes.QrPng(invitation.QrPayload);
            _qr.Source = ImageSource.FromStream(() => new MemoryStream(png));
            _qr.Opacity = 1;
            _code.Text = GroupsPage.Pretty(invitation.Code);
            UpdateCountdown();
        }
        finally
        {
            _creating = false;
        }
    }

    private void UpdateCountdown()
    {
        if (_invitation is null)
            return;

        var left = _invitation.ExpiresAt - DateTimeOffset.Now;
        if (left <= TimeSpan.Zero)
        {
            // Caducado: se atenua para que nadie intente escanearlo, y se dice que hacer.
            _countdown.Text = Loc.Get("InviteExpired");
            _countdown.TextColor = Ui.Color("Danger");
            _qr.Opacity = 0.15;
            _code.Opacity = 0.3;
            _share.IsEnabled = false;
            return;
        }

        _countdown.Text = Loc.Format("InviteExpiresIn", $"{(int)left.TotalMinutes}:{left.Seconds:00}");
        Ui.SetThemeColor(_countdown, Label.TextColorProperty, "TextSecondary");
        _code.Opacity = 1;
        _share.IsEnabled = true;
    }

    private async Task ShareAsync()
    {
        if (_invitation is null)
            return;

        try
        {
            await Share.Default.RequestAsync(new ShareTextRequest
            {
                Title = Loc.Get("InviteTitle"),
                Text = Loc.Format("InviteShareText", _group.Name, GroupsPage.Pretty(_invitation.Code), _invitation.QrPayload),
            });
        }
        catch (Exception ex)
        {
            await Ui.ShowErrorAsync(this, ex);
        }
    }
}
