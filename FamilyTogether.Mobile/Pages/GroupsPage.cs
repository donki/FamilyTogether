using FamilyTogether.Core;
using FamilyTogether.Mobile.Controls;
using FamilyTogether.Mobile.Helpers;
using FamilyTogether.Mobile.Localization;
using FamilyTogether.Mobile.Services;

namespace FamilyTogether.Mobile.Pages;

/// <summary>
/// Mis grupos (FR-004, FR-005): cada uno con mi papel y si estoy en pausa; crear un grupo, unirse con
/// el codigo escrito o escaneando el QR, y el estado de mis solicitudes pendientes, que se mira al
/// abrir y al volver a la app.
/// </summary>
public sealed class GroupsPage : ContentPage
{
    private readonly FamilyService _family = ServiceHelper.Get<FamilyService>();

    private readonly VerticalStackLayout _requests = new() { Spacing = 6 };
    private readonly Border _requestsCard;
    private readonly VerticalStackLayout _groupsList = new() { Spacing = 0 };
    private readonly ActivityIndicator _busy = new() { HeightRequest = 24 };
    private bool _loading;
    private string? _lastGroupIds;

    public GroupsPage()
    {
        Title = Loc.Get("MenuGroups");

        ToolbarItems.Add(new ToolbarItem { IconImageSource = "ic_add_w.png", Text = Loc.Get("CreateGroup"), Command = new Command(async () => await CreateAsync()) });
        ToolbarItems.Add(new ToolbarItem { IconImageSource = "ic_scan_w.png", Text = Loc.Get("ScanQr"), Command = new Command(async () => await ScanAsync()) });

        _requestsCard = UiKit.Card(new VerticalStackLayout
        {
            Spacing = 8,
            Children = { UiKit.Title(Loc.Get("MyRequestsTitle")), _requests },
        });
        _requestsCard.IsVisible = false;

        var actions = UiKit.Card(new VerticalStackLayout
        {
            Spacing = 10,
            Children =
            {
                UiKit.Primary(Loc.Get("CreateGroup"), "ic_add_w.png", async (_, _) => await CreateAsync()),
                UiKit.Outline(Loc.Get("JoinWithCode"), "ic_join.png", async (_, _) => await JoinByCodeAsync()),
                UiKit.Outline(Loc.Get("ScanQr"), "ic_scan.png", async (_, _) => await ScanAsync()),
                UiKit.Hint(Loc.Get("JoinHint")),
            },
        });

        Content = UiKit.Page(new VerticalStackLayout
        {
            Padding = 16,
            Spacing = 16,
            Children =
            {
                _requestsCard,
                UiKit.Card(new VerticalStackLayout
                {
                    Spacing = 6,
                    Children = { UiKit.Row(UiKit.Title(Loc.Get("MyGroupsTitle")), _busy), _groupsList },
                }),
                actions,
            },
        });
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        App.AppResumed += OnResumed;
        await LoadAsync();

        // Un QR escaneado con la camara del sistema llega como enlace: se atiende aqui.
        if (App.PendingJoinCode is { } code)
        {
            App.PendingJoinCode = null;
            if (await Ui.ConfirmAsync(this, "JoinTitle", Loc.Format("JoinConfirmCode", Pretty(code)), "Join"))
                await JoinAsync(code);
        }
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        App.AppResumed -= OnResumed;
    }

    private void OnResumed(object? sender, EventArgs e) => MainThread.BeginInvokeOnMainThread(async () => await LoadAsync());

    private async Task LoadAsync()
    {
        if (_loading)
            return;
        _loading = true;
        _busy.IsRunning = _busy.IsVisible = true;
        try
        {
            await CheckRequestsAsync();

            var (ok, groups) = await Ui.RunAsync(this, () => _family.GetGroupsAsync());
            if (!ok || groups is null)
                return;

            // Mi fila en cada grupo, para enseñar si estoy en pausa. En paralelo: pocos grupos.
            var mine = await Task.WhenAll(groups.Select(async g =>
            {
                if (g.KeyMissing)
                    return null;
                try
                {
                    return (await _family.GetMembersAsync(g.Id)).FirstOrDefault(m => m.IsMe);
                }
                catch (Exception ex)
                {
                    CrashLog.Error("GroupsPage.Members", ex);
                    return null;
                }
            }));

            // Si el conjunto de grupos cambio (me aprobaron, me expulsaron), el servicio lo sabe ya.
            var ids = string.Join(',', groups.Select(g => g.Id).Order());
            if (ids != _lastGroupIds)
            {
                if (_lastGroupIds is not null)
                    SharingChanges.Notify();
                _lastGroupIds = ids;
            }

            _groupsList.Clear();
            if (groups.Count == 0)
                _groupsList.Add(UiKit.Body(Loc.Get("NoGroupsBody")));

            for (var i = 0; i < groups.Count; i++)
                _groupsList.Add(GroupRow(groups[i], mine[i]));
        }
        finally
        {
            _loading = false;
            _busy.IsRunning = _busy.IsVisible = false;
        }
    }

    /// <summary>
    /// Mira como van mis solicitudes. Las aprobadas ya han guardado la clave y publicado mi nombre (lo
    /// hace el nucleo); las rechazadas se dicen una vez y se olvidan.
    /// </summary>
    private async Task CheckRequestsAsync()
    {
        IReadOnlyList<Guid> ids;
        try
        {
            ids = await _family.GetMyPendingRequestIdsAsync();
        }
        catch (Exception ex)
        {
            CrashLog.Error("GroupsPage.PendingIds", ex);
            return;
        }

        _requests.Clear();
        var approved = 0;
        var rejected = 0;
        foreach (var id in ids)
        {
            try
            {
                switch (await _family.CheckMyRequestAsync(id))
                {
                    case JoinState.Approved:
                        approved++;
                        break;
                    case JoinState.Rejected:
                        rejected++;
                        break;
                    default:
                        _requests.Add(UiKit.Row(UiKit.Body(Loc.Get("RequestPending")), new Image { Source = "ic_history.png", WidthRequest = 22, HeightRequest = 22 }));
                        break;
                }
            }
            catch (Exception ex)
            {
                // Sin red se queda como pendiente: se volvera a mirar.
                CrashLog.Error("GroupsPage.CheckMyRequest", ex);
                _requests.Add(UiKit.Body(Loc.Get("RequestPending")));
            }
        }

        _requestsCard.IsVisible = _requests.Children.Count > 0;

        if (approved > 0)
        {
            SharingChanges.Notify();
            _ = AppChores.EnsureSharingAsync(true);
            await Ui.AlertAsync(this, "RequestApprovedTitle", Loc.Get("RequestApprovedBody"));
        }

        if (rejected > 0)
            await Ui.AlertAsync(this, "RequestRejectedTitle", Loc.Get("RequestRejectedBody"));
    }

    private View GroupRow(Group group, Member? me)
    {
        var name = group.KeyMissing ? Loc.Get("GroupNoKeyName") : group.Name;
        var role = Loc.Get(group.IAmAdmin ? "RoleAdmin" : "RoleMember");
        var detail = me is { Paused: true } ? $"{role} · {TimeTexts.Paused(me.PauseUntil)}" : role;
        if (group.KeyMissing)
            detail = $"{role} · {Loc.Get("KeyMissingShort")}";

        var icon = new Image { Source = group.IAmAdmin ? "ic_admin.png" : "ic_group.png", WidthRequest = 24, HeightRequest = 24, VerticalOptions = LayoutOptions.Center };
        var texts = new VerticalStackLayout
        {
            Spacing = 2,
            VerticalOptions = LayoutOptions.Center,
            Children = { UiKit.ItemTitle(name), UiKit.ItemSubtitle(detail) },
        };

        var row = new Grid
        {
            ColumnDefinitions = [new ColumnDefinition(new GridLength(32)), new ColumnDefinition(GridLength.Star), new ColumnDefinition(new GridLength(24))],
            ColumnSpacing = 12,
            Padding = new Thickness(0, 8),
            MinimumHeightRequest = 56,
        };
        row.Add(icon, 0, 0);
        row.Add(texts, 1, 0);
        row.Add(new Image { Source = "ic_chevron_right.png", WidthRequest = 20, HeightRequest = 20, VerticalOptions = LayoutOptions.Center }, 2, 0);

        var tap = new TapGestureRecognizer();
        tap.Tapped += async (_, _) => await Navigation.PushAsync(new GroupDetailPage(group));
        row.GestureRecognizers.Add(tap);
        return row;
    }

    private async Task CreateAsync()
    {
        var name = await SocShared.ModernDialog.PromptAsync(this, Loc.Get("CreateGroup"), Loc.Get("GroupNamePrompt"),
            Loc.Get("Create"), Loc.Get("Cancel"), placeholder: Loc.Get("GroupNamePlaceholder"));
        name = name?.Trim();
        if (name is null)
            return;
        if (name.Length == 0)
        {
            await Ui.AlertAsync(this, "CreateGroup", Loc.Get("GroupNameRequired"));
            return;
        }

        var (ok, group) = await Ui.RunAsync(this, async () =>
        {
            var (myName, avatar) = await _family.ProfileAsync();
            return await _family.CreateGroupAsync(name, myName, avatar);
        });
        if (!ok || group is null)
            return;

        AppState.SelectedGroup = group.Id;
        SharingChanges.Notify();
        _ = AppChores.EnsureSharingAsync(true);
        await LoadAsync();

        // Lo siguiente que se quiere hacer con un grupo nuevo es invitar (SC-001).
        await Navigation.PushAsync(new GroupDetailPage(group));
    }

    private async Task JoinByCodeAsync()
    {
        var text = await SocShared.ModernDialog.PromptAsync(this, Loc.Get("JoinTitle"), Loc.Get("JoinCodePrompt"),
            Loc.Get("Join"), Loc.Get("Cancel"), placeholder: "ABCD-EFGH");
        if (text is null)
            return;

        if (!InviteCodes.TryParse(text, out var code))
        {
            await Ui.AlertAsync(this, "JoinTitle", Loc.Get("JoinCodeInvalid"));
            return;
        }

        await JoinAsync(code);
    }

    private async Task ScanAsync()
    {
        var code = await ScanQrPage.RequestAsync(this);
        if (code is not null)
            await JoinAsync(code);
    }

    private async Task JoinAsync(string code)
    {
        var ok = await Ui.RunAsync(this, async () =>
        {
            var (myName, _) = await _family.ProfileAsync();
            await _family.RequestJoinAsync(code, myName);
        });
        if (!ok)
            return;

        await Ui.AlertAsync(this, "JoinTitle", Loc.Get("JoinSent"));
        await LoadAsync();
    }

    /// <summary>El codigo en dos bloques de cuatro, que se lee y se dicta mejor.</summary>
    public static string Pretty(string code) => code.Length == 8 ? $"{code[..4]}-{code[4..]}" : code;
}
