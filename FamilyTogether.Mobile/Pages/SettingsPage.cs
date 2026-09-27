using FamilyTogether.Core;
using FamilyTogether.Mobile.Controls;
using FamilyTogether.Mobile.Helpers;
using FamilyTogether.Mobile.Localization;
using FamilyTogether.Mobile.Services;

namespace FamilyTogether.Mobile.Pages;

/// <summary>
/// Ajustes: idioma (Sistema, Español, English, con su bandera dibujada), mi nombre y avatar en todos
/// mis grupos, la cuenta (vinculada o no, vincular Google o Microsoft y recuperar la cuenta en este
/// movil: HU8, FR-003), el estado de permisos y bateria, Novedades y Acerca de.
/// </summary>
public sealed class SettingsPage : ContentPage
{
    private readonly FamilyService _family = ServiceHelper.Get<FamilyService>();
    private readonly AccountService _account = ServiceHelper.Get<AccountService>();
    private readonly ILocationSharing? _sharing = ServiceHelper.TryGet<ILocationSharing>();
    private readonly INotifier? _notifier = ServiceHelper.TryGet<INotifier>();

    private readonly Button _system;
    private readonly Button _spanish;
    private readonly Button _english;

    private readonly Entry _name = new() { MaxLength = 40, ReturnType = ReturnType.Done };
    private readonly AvatarView _avatar = new(64);
    private readonly Button _removeAvatar;
    private string _savedName = string.Empty;
    private string? _avatarBase64;

    private readonly Label _accountState = new();
    private readonly VerticalStackLayout _linkButtons = new() { Spacing = 8 };

    private readonly Label _locationState = new();
    private readonly Label _notifState = new();
    private readonly Label _batteryState = new();

    public SettingsPage()
    {
        Title = Loc.Get("MenuSettings");

        // Idioma: los botones llevan la bandera dibujada (SVG plano), nunca el emoji ni las letras
        // del pais (General 6.2). «Sistema» lleva el globo.
        _system = LanguageButton(Loc.Get("LanguageSystem"), "ic_language.png", Loc.System);
        _spanish = LanguageButton("Español", "ic_flag_es.png", Loc.Spanish);
        _english = LanguageButton("English", "ic_flag_us.png", Loc.English);
        var languages = new VerticalStackLayout { Spacing = 8, Children = { _system, _spanish, _english } };

        _name.Placeholder = Loc.Get("DisplayNamePlaceholder");
        // Lo escrito se aplica al salir de la casilla, no solo al pulsar Guardar (General 6.8).
        _name.Unfocused += async (_, _) => await SaveProfileAsync(quietIfUnchanged: true);
        _name.Completed += (_, _) => _name.Unfocus();
        _removeAvatar = UiKit.Outline(Loc.Get("AvatarRemove"), "ic_close.png", async (_, _) =>
        {
            _avatarBase64 = null;
            ShowAvatar();
            await SaveProfileAsync(quietIfUnchanged: false);
        });

        _accountState.Style = Ui.Style("BodyText");
        foreach (var label in new[] { _locationState, _notifState, _batteryState })
            label.Style = Ui.Style("BodyText");

        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = 16,
                Spacing = 16,
                Children =
                {
                    UiKit.Card(new VerticalStackLayout
                    {
                        Spacing = 10,
                        Children = { UiKit.Title(Loc.Get("LanguageTitle")), languages, UiKit.Hint(Loc.Get("LanguageHint")) },
                    }),
                    UiKit.Card(new VerticalStackLayout
                    {
                        Spacing = 10,
                        Children =
                        {
                            UiKit.Title(Loc.Get("ProfileTitle")),
                            new HorizontalStackLayout { Spacing = 12, Children = { _avatar } },
                            UiKit.Body(Loc.Get("DisplayNameLabel")),
                            UiKit.Input(_name),
                            UiKit.Outline(Loc.Get("AvatarChoose"), "ic_camera.png", async (_, _) => await PickAvatarAsync()),
                            _removeAvatar,
                            UiKit.Hint(Loc.Get("ProfileHint")),
                        },
                    }),
                    UiKit.Card(new VerticalStackLayout
                    {
                        Spacing = 10,
                        Children =
                        {
                            UiKit.Title(Loc.Get("AccountTitle")),
                            _accountState,
                            _linkButtons,
                            UiKit.Separator(),
                            UiKit.Body(Loc.Get("RecoverExplain")),
                            UiKit.Outline(Loc.Get("RecoverAction"), "ic_recover.png", async (_, _) => await RecoverAsync()),
                        },
                    }),
                    UiKit.Card(new VerticalStackLayout
                    {
                        Spacing = 10,
                        Children =
                        {
                            UiKit.Title(Loc.Get("PermissionsTitle")),
                            _locationState,
                            _notifState,
                            _batteryState,
                            UiKit.Outline(Loc.Get("OpenGuide"), "ic_guide.png", async (_, _) => await Shell.Current.GoToAsync("//GuidePage")),
                        },
                    }),
                    UiKit.Card(new VerticalStackLayout
                    {
                        Spacing = 10,
                        Children =
                        {
                            UiKit.Outline(Loc.Get("MenuWhatsNew"), "ic_news.png", async (_, _) => await Shell.Current.GoToAsync("//WhatsNewPage")),
                            UiKit.Outline(Loc.Get("MenuAbout"), "ic_info.png", async (_, _) => await Shell.Current.GoToAsync("//AboutPage")),
                        },
                    }),
                },
            },
        };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        ShowLanguage();
        App.AppResumed += OnResumed;

        try
        {
            var (name, avatar) = await _family.ProfileAsync();
            _savedName = name;
            _name.Text = name;
            _avatarBase64 = avatar;
            ShowAvatar();
        }
        catch (Exception ex)
        {
            CrashLog.Error("SettingsPage.Profile", ex);
        }

        await ShowPermissionsAsync();
        await ShowAccountAsync();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        App.AppResumed -= OnResumed;
    }

    private void OnResumed(object? sender, EventArgs e) => MainThread.BeginInvokeOnMainThread(async () => await ShowPermissionsAsync());

    // ------------------------------------------------------------------ idioma

    private Button LanguageButton(string text, string icon, string preference)
    {
        var button = UiKit.Outline(text, icon, (_, _) => SetLanguage(preference));
        button.HorizontalOptions = LayoutOptions.Fill;
        return button;
    }

    /// <summary>El idioma elegido se resalta, para que se vea cual esta puesto sin adivinarlo.</summary>
    private void ShowLanguage()
    {
        var preference = Loc.Preference;
        foreach (var (button, value) in new[] { (_system, Loc.System), (_spanish, Loc.Spanish), (_english, Loc.English) })
        {
            var active = preference == value;
            button.BorderWidth = active ? 3 : 1;
            button.FontAttributes = active ? FontAttributes.Bold : FontAttributes.None;
            button.Opacity = active ? 1 : 0.75;
        }
    }

    /// <summary>
    /// Se reconstruye el Shell entero: los enlaces de MAUI no reevaluan los textos (comprobado en
    /// Task Manager el 2026-08-30). Despues se vuelve aqui, para que se vea el cambio donde se hizo.
    /// </summary>
    private void SetLanguage(string preference)
    {
        Loc.SetPreference(preference);
        if (Application.Current?.Windows.FirstOrDefault() is { } window)
        {
            var shell = new AppShell();
            window.Page = shell;
            MainThread.BeginInvokeOnMainThread(async () =>
            {
                try
                {
                    await shell.GoToAsync("//SettingsPage");
                }
                catch (Exception ex)
                {
                    CrashLog.Error("SettingsPage.SetLanguage", ex);
                }
            });
        }
    }

    // ------------------------------------------------------------------ perfil

    private void ShowAvatar()
    {
        _avatar.Set(ServiceHelper.Get<SupabaseClient>().UserGuid, _name.Text, _avatarBase64);
        _removeAvatar.IsVisible = _avatarBase64 is not null;
    }

    private async Task PickAvatarAsync()
    {
        var (ok, picked) = await Ui.RunAsync(this, Avatars.PickAsync);
        if (!ok || picked is null)
            return;

        _avatarBase64 = picked;
        ShowAvatar();
        await SaveProfileAsync(quietIfUnchanged: false);
    }

    private async Task SaveProfileAsync(bool quietIfUnchanged)
    {
        var name = _name.Text?.Trim() ?? string.Empty;
        if (quietIfUnchanged && name == _savedName)
            return;

        if (name.Length == 0)
        {
            // No se guarda un nombre vacio, y se dice: nunca se descarta en silencio.
            await Ui.AlertAsync(this, "ProfileTitle", Loc.Get("DisplayNameRequired"));
            _name.Text = _savedName;
            return;
        }

        if (await Ui.RunAsync(this, () => _family.UpdateMyProfileAsync(name, _avatarBase64)))
        {
            _savedName = name;
            ShowAvatar();
        }
    }

    // ------------------------------------------------------------------ cuenta

    private async Task ShowAccountAsync()
    {
        _linkButtons.Clear();
        _accountState.Text = Loc.Get("Loading");

        string? linked;
        try
        {
            linked = await _account.LinkedProviderAsync();
        }
        catch (Exception ex)
        {
            CrashLog.Error("SettingsPage.LinkedProvider", ex);
            _accountState.Text = ErrorTexts.Describe(ex);
            return;
        }

        _accountState.Text = linked switch
        {
            "google" => Loc.Format("AccountLinked", "Google"),
            "microsoft" => Loc.Format("AccountLinked", "Microsoft"),
            _ => Loc.Get("AccountNotLinked"),
        };

        var available = _account.Available;
        if (available.Count == 0)
        {
            _linkButtons.Add(UiKit.Hint(Loc.Get("AccountNoProviders")));
            return;
        }

        if (linked is not null)
            return;

        foreach (var provider in available)
        {
            var p = provider;
            _linkButtons.Add(UiKit.Outline(
                Loc.Format("LinkWith", Name(p)),
                p == IdentityProvider.Google ? "ic_google.png" : "ic_microsoft.png",
                async (_, _) => await LinkAsync(p)));
        }
    }

    private async Task LinkAsync(IdentityProvider provider)
    {
        if (await Ui.RunAsync(this, () => _account.LinkAsync(provider)))
        {
            await Ui.AlertAsync(this, "AccountTitle", Loc.Format("AccountLinkedDone", Name(provider)));
            await ShowAccountAsync();
        }
    }

    /// <summary>
    /// Recuperar: trae a este movil el usuario vinculado (grupos, historial, zonas) y el movil viejo
    /// deja de compartir. Solo en un movil sin grupos: nunca se fusionan dos usuarios.
    /// </summary>
    private async Task RecoverAsync()
    {
        var available = _account.Available;
        if (available.Count == 0)
        {
            await Ui.AlertAsync(this, "RecoverAction", Loc.Get("AccountNoProviders"));
            return;
        }

        if (!await Ui.ConfirmAsync(this, "RecoverAction", Loc.Get("RecoverConfirm"), "RecoverContinue"))
            return;

        var provider = available[0];
        if (available.Count > 1)
        {
            var google = Loc.Format("LinkWith", "Google");
            var microsoft = Loc.Format("LinkWith", "Microsoft");
            var choice = await SocShared.ModernDialog.ActionSheetAsync(this, Loc.Get("RecoverAction"), Loc.Get("Cancel"), google, microsoft);
            if (choice is null)
                return;
            provider = choice == microsoft ? IdentityProvider.Microsoft : IdentityProvider.Google;
        }

        if (!await Ui.RunAsync(this, () => _account.RecoverAsync(provider)))
            return;

        SharingChanges.Notify();
        AppState.SelectedGroup = Guid.Empty;
        _ = AppChores.RunAsync();
        await Ui.AlertAsync(this, "RecoverAction", Loc.Get("RecoverDone"));
        await ShowAccountAsync();
    }

    private static string Name(IdentityProvider provider) => provider == IdentityProvider.Microsoft ? "Microsoft" : "Google";

    // ------------------------------------------------------------------ permisos

    private async Task ShowPermissionsAsync()
    {
        try
        {
            if (_sharing is { } sharing)
            {
                var permission = await sharing.CheckPermissionAsync();
                _locationState.Text = Loc.Get(permission switch
                {
                    LocationPermissionState.Always => "PermLocationAlways",
                    LocationPermissionState.WhileInUse => "PermLocationWhileInUse",
                    _ => "PermLocationDenied",
                }) + " · " + Loc.Get(sharing.IsRunning ? "SharingRunning" : "SharingNotRunning");
                _batteryState.Text = Loc.Get(sharing.IsIgnoringBatteryOptimizations ? "PermBatteryOk" : "PermBatteryRestricted");
            }
            else
            {
                _locationState.Text = Loc.Get("PermUnavailable");
                _batteryState.Text = string.Empty;
            }

            _notifState.Text = Loc.Get(_notifier?.AreEnabled == true ? "PermNotifOn" : "PermNotifOff");
        }
        catch (Exception ex)
        {
            CrashLog.Error("SettingsPage.Permissions", ex);
        }
    }
}
