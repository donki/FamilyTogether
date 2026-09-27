using FamilyLink.Core;
using FamilyLink.Mobile.Controls;
using FamilyLink.Mobile.Helpers;
using FamilyLink.Mobile.Localization;
using FamilyLink.Mobile.Services;

namespace FamilyLink.Mobile.Pages;

/// <summary>
/// Primer arranque (FR-001, FR-002): que hace la app, que sale del movil y como va cifrado, el nombre
/// visible (obligatorio) y el avatar (opcional). Crea el usuario anonimo y sigue a la guia.
/// </summary>
/// <remarks>
/// Si la compilacion no lleva servidor (<see cref="FamilyLinkConfig.IsServerConfigured"/>) no hay
/// nada que hacer: se dice claro, con la razon y que hacer, en vez de reventar al primer uso.
/// </remarks>
public sealed class WelcomePage : ContentPage
{
    private readonly Entry _name = new() { MaxLength = 40, ReturnType = ReturnType.Done };
    private readonly AvatarView _avatar = new(72);
    private readonly Button _removeAvatar;
    private readonly Button _continue;
    private string? _avatarBase64;
    private bool _busy;

    public WelcomePage()
    {
        Title = Loc.Get("WelcomeTitle");
        Shell.SetFlyoutBehavior(this, FlyoutBehavior.Disabled);
        Shell.SetBackButtonBehavior(this, new BackButtonBehavior { IsVisible = false });

        _removeAvatar = UiKit.Outline(Loc.Get("AvatarRemove"), "ic_close.png", (_, _) => SetAvatar(null));
        _continue = UiKit.Primary(Loc.Get("WelcomeContinue"), "ic_next_w.png", OnContinue);

        if (!FamilyLinkConfig.IsServerConfigured)
        {
            Content = UiKit.Page(new VerticalStackLayout
            {
                Padding = 16,
                Spacing = 16,
                Children =
                {
                    Logo(),
                    UiKit.Card(new VerticalStackLayout
                    {
                        Spacing = 10,
                        Children =
                        {
                            UiKit.Title(Loc.Get("NoServerTitle")),
                            UiKit.Body(Loc.Get("NoServerBody")),
                            UiKit.Hint(Loc.Get("NoServerWhat")),
                        },
                    }),
                },
            });
            return;
        }

        _name.Placeholder = Loc.Get("DisplayNamePlaceholder");
        _name.Completed += (_, _) => _name.Unfocus();

        var pick = UiKit.Outline(Loc.Get("AvatarChoose"), "ic_camera.png", OnPickAvatar);

        Content = UiKit.Page(
            new VerticalStackLayout
            {
                Padding = 16,
                Spacing = 16,
                Children =
                {
                    Logo(),
                    UiKit.Card(new VerticalStackLayout
                    {
                        Spacing = 10,
                        Children = { UiKit.Title(Loc.Get("WelcomeWhatTitle")), UiKit.Body(Loc.Get("WelcomeWhatBody")) },
                    }),
                    UiKit.Card(new VerticalStackLayout
                    {
                        Spacing = 10,
                        Children = { UiKit.Title(Loc.Get("WelcomePrivacyTitle")), UiKit.Body(Loc.Get("WelcomePrivacyBody")) },
                    }),
                    UiKit.Card(new VerticalStackLayout
                    {
                        Spacing = 12,
                        Children =
                        {
                            UiKit.Title(Loc.Get("WelcomeYouTitle")),
                            UiKit.Body(Loc.Get("DisplayNameLabel")),
                            UiKit.Input(_name),
                            UiKit.Hint(Loc.Get("DisplayNameHint")),
                            UiKit.Separator(),
                            UiKit.Body(Loc.Get("AvatarLabel")),
                            new HorizontalStackLayout { Spacing = 12, Children = { _avatar } },
                            pick,
                            _removeAvatar,
                        },
                    }),
                },
            },
            _continue);

        SetAvatar(null);
    }

    private static Image Logo() => new()
    {
        Source = "about_logo.png",
        WidthRequest = 84,
        HeightRequest = 84,
        HorizontalOptions = LayoutOptions.Center,
        Margin = new Thickness(0, 8, 0, 0),
    };

    private void SetAvatar(string? base64)
    {
        _avatarBase64 = base64;
        _avatar.Set(Guid.Empty, string.IsNullOrWhiteSpace(_name.Text) ? "?" : _name.Text, base64);
        _removeAvatar.IsVisible = base64 is not null;
    }

    private async void OnPickAvatar(object? sender, EventArgs e)
    {
        var (ok, picked) = await Ui.RunAsync(this, Avatars.PickAsync);
        if (ok && picked is not null)
            SetAvatar(picked);
    }

    private async void OnContinue(object? sender, EventArgs e)
    {
        if (_busy)
            return;

        var name = _name.Text?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            // Nunca se sigue sin nombre y nunca en silencio (General 6.8).
            await Ui.AlertAsync(this, "WelcomeYouTitle", Loc.Get("DisplayNameRequired"));
            _name.Focus();
            return;
        }

        _busy = true;
        _continue.IsEnabled = false;
        try
        {
            var ok = await Ui.RunAsync(this, async () =>
            {
                await ServiceHelper.Get<SupabaseClient>().EnsureSignedInAsync();
                await ServiceHelper.Get<FamilyService>().UpdateMyProfileAsync(name, _avatarBase64);
            });
            if (!ok)
                return;

            AppState.WelcomeDone = true;
            // En una instalacion nueva las novedades no dicen nada: se dan por vistas.
            AppState.MarkVersionSeen();
            _ = AppChores.RunAsync();

            await Shell.Current.GoToAsync("//GuidePage");
        }
        finally
        {
            _busy = false;
            _continue.IsEnabled = true;
        }
    }
}
