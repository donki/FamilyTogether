using FamilyLink.Core;
using FamilyLink.Mobile.Controls;
using FamilyLink.Mobile.Helpers;
using FamilyLink.Mobile.Localization;
using FamilyLink.Mobile.Services;

namespace FamilyLink.Mobile.Pages;

/// <summary>
/// Un grupo: miembros (nombrar o quitar administradores y expulsar, solo admins: HU9, FR-008),
/// solicitudes pendientes con Aprobar / Rechazar (FR-007), Invitar (QR y codigo, FR-006), mi pausa en
/// este grupo (HU7, FR-017) y Abandonar (FR-024).
/// </summary>
public sealed class GroupDetailPage : ContentPage
{
    private readonly FamilyService _family = ServiceHelper.Get<FamilyService>();
    private Group _group;

    private readonly Label _header = new() { FontSize = 22, FontAttributes = FontAttributes.Bold };
    private readonly Label _role = new();
    private readonly VerticalStackLayout _banner = new();
    private readonly VerticalStackLayout _requests = new() { Spacing = 8 };
    private readonly Border _requestsCard;
    private readonly VerticalStackLayout _members = new() { Spacing = 0 };
    private readonly Label _pauseState = new();
    private readonly Button _pause;
    private readonly Button _resume;
    private readonly TimePicker _pauseTime = new() { Format = "HH:mm", HorizontalOptions = LayoutOptions.Start };
    private readonly VerticalStackLayout _pauseTimeRow;
    private readonly Button _invite;
    private readonly ActivityIndicator _busy = new() { HeightRequest = 24 };
    private bool _loading;

    public GroupDetailPage(Group group)
    {
        _group = group;
        Title = Loc.Get("GroupTitle");
        Ui.SetThemeColor(_header, Label.TextColorProperty, "TextPrimary");
        _role.Style = Ui.Style("HintText");
        _pauseState.Style = Ui.Style("BodyText");

        _invite = UiKit.Primary(Loc.Get("Invite"), "ic_qr_w.png", async (_, _) => await Navigation.PushAsync(new InvitePage(_group)));
        _pause = UiKit.Outline(Loc.Get("Pause"), "ic_pause.png", async (_, _) => await ChoosePauseAsync());
        _resume = UiKit.Primary(Loc.Get("Resume"), "ic_play_w.png", async (_, _) => await SetPauseAsync(false, null));

        _pauseTime.Time = new TimeSpan(DateTime.Now.Hour + 1 < 24 ? DateTime.Now.Hour + 1 : 23, 0, 0);
        _pauseTimeRow = new VerticalStackLayout
        {
            Spacing = 8,
            IsVisible = false,
            Children =
            {
                UiKit.Body(Loc.Get("PauseUntilChosen")),
                UiKit.Input(_pauseTime),
                UiKit.Primary(Loc.Get("PauseConfirm"), "ic_pause_w.png", async (_, _) => await PauseUntilChosenAsync()),
            },
        };

        _requestsCard = UiKit.Card(new VerticalStackLayout
        {
            Spacing = 8,
            Children = { UiKit.Title(Loc.Get("RequestsTitle")), _requests },
        });
        _requestsCard.IsVisible = false;

        Content = UiKit.Page(new VerticalStackLayout
        {
            Padding = 16,
            Spacing = 16,
            Children =
            {
                new VerticalStackLayout { Spacing = 2, Children = { UiKit.Row(_header, _busy), _role } },
                _banner,
                _invite,
                _requestsCard,
                UiKit.Card(new VerticalStackLayout
                {
                    Spacing = 6,
                    Children = { UiKit.Title(Loc.Get("MembersTitle")), _members },
                }),
                UiKit.Card(new VerticalStackLayout
                {
                    Spacing = 10,
                    Children = { UiKit.Title(Loc.Get("MySharingTitle")), _pauseState, _pause, _resume, _pauseTimeRow, UiKit.Hint(Loc.Get("PauseHint")) },
                }),
                UiKit.Danger(Loc.Get("LeaveGroup"), "ic_leave_danger.png", async (_, _) => await LeaveAsync()),
            },
        });
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        if (_loading)
            return;
        _loading = true;
        _busy.IsRunning = _busy.IsVisible = true;
        try
        {
            // El grupo puede haber cambiado (rol, clave recibida): se vuelve a leer.
            var (ok, groups) = await Ui.RunAsync(this, () => _family.GetGroupsAsync());
            if (!ok || groups is null)
                return;

            if (groups.FirstOrDefault(g => g.Id == _group.Id) is not { } fresh)
            {
                // Ya no soy miembro (expulsado, o el grupo se borro): no hay nada que enseñar.
                SharingChanges.Notify();
                await Ui.AlertAsync(this, "GroupTitle", Loc.Get("Err_not_member"));
                await Navigation.PopAsync();
                return;
            }

            _group = fresh;
            _header.Text = _group.KeyMissing ? Loc.Get("GroupNoKeyName") : _group.Name;
            _role.Text = Loc.Get(_group.IAmAdmin ? "RoleAdmin" : "RoleMember");
            _invite.IsVisible = _group.IAmAdmin && !_group.KeyMissing;

            _banner.Clear();
            if (_group.KeyMissing)
                _banner.Add(UiKit.Banner(Loc.Get("KeyMissingBanner")));

            var (membersOk, members) = await Ui.RunAsync(this, () => _family.GetMembersAsync(_group.Id));
            if (membersOk && members is not null)
                ShowMembers(members);

            if (_group.IAmAdmin && !_group.KeyMissing)
            {
                var (requestsOk, requests) = await Ui.RunAsync(this, () => _family.GetPendingRequestsAsync(_group.Id));
                if (requestsOk && requests is not null)
                    ShowRequests(requests);
            }
            else
            {
                _requestsCard.IsVisible = false;
            }
        }
        finally
        {
            _loading = false;
            _busy.IsRunning = _busy.IsVisible = false;
        }
    }

    private void ShowMembers(IReadOnlyList<Member> members)
    {
        _members.Clear();
        foreach (var member in members.OrderByDescending(m => m.IsMe).ThenByDescending(m => m.IsAdmin).ThenBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase))
            _members.Add(MemberRow(member));

        var me = members.FirstOrDefault(m => m.IsMe);
        var paused = me?.Paused == true;
        _pauseState.Text = paused ? TimeTexts.Paused(me!.PauseUntil) : Loc.Get("SharingInGroup");
        _pause.IsVisible = !paused;
        _resume.IsVisible = paused;
        _pauseTimeRow.IsVisible = false;
    }

    private View MemberRow(Member member)
    {
        var avatar = new AvatarView(40);
        avatar.Set(member.UserId, member.Name, member.AvatarBase64);

        var role = Loc.Get(member.IsAdmin ? "RoleAdmin" : "RoleMember");
        var detail = member.Paused ? $"{role} · {TimeTexts.Paused(member.PauseUntil)}" : role;
        var texts = new VerticalStackLayout
        {
            Spacing = 2,
            VerticalOptions = LayoutOptions.Center,
            Children =
            {
                UiKit.ItemTitle(member.IsMe ? Loc.Format("MeSuffix", member.Name) : member.Name),
                UiKit.ItemSubtitle(detail),
            },
        };

        var row = new Grid
        {
            ColumnDefinitions =
            [
                new ColumnDefinition(new GridLength(48)),
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Auto),
            ],
            ColumnSpacing = 4,
            Padding = new Thickness(0, 4),
            MinimumHeightRequest = 56,
        };
        row.Add(avatar, 0, 0);
        row.Add(texts, 1, 0);

        // Solo los administradores gestionan a los demas; a uno mismo no se le toca desde aqui.
        if (_group.IAmAdmin && !member.IsMe)
        {
            row.Add(UiKit.Icon(member.IsAdmin ? "ic_admin_off.png" : "ic_admin.png",
                async (_, _) => await ToggleAdminAsync(member),
                Loc.Get(member.IsAdmin ? "RemoveAdmin" : "MakeAdmin")), 2, 0);
            row.Add(UiKit.Icon("ic_kick_danger.png", async (_, _) => await KickAsync(member), Loc.Get("Kick")), 3, 0);
        }

        return row;
    }

    private void ShowRequests(IReadOnlyList<JoinRequest> requests)
    {
        _requests.Clear();
        foreach (var request in requests.OrderBy(r => r.CreatedAt))
        {
            var buttons = new Grid
            {
                ColumnDefinitions = [new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Star)],
                ColumnSpacing = 8,
            };
            buttons.Add(UiKit.Primary(Loc.Get("Approve"), "ic_approve_w.png", async (_, _) => await ResolveAsync(request, true)), 0, 0);
            buttons.Add(UiKit.Danger(Loc.Get("Reject"), "ic_reject_danger.png", async (_, _) => await ResolveAsync(request, false)), 1, 0);

            _requests.Add(new VerticalStackLayout
            {
                Spacing = 6,
                Padding = new Thickness(0, 4),
                Children =
                {
                    UiKit.ItemTitle(request.Name == "?" ? Loc.Get("Unknown") : request.Name),
                    UiKit.ItemSubtitle(Loc.Format("RequestedAgo", TimeTexts.Ago(request.CreatedAt))),
                    buttons,
                },
            });
        }

        _requestsCard.IsVisible = requests.Count > 0;
    }

    private async Task ResolveAsync(JoinRequest request, bool approve)
    {
        if (await Ui.RunAsync(this, () => approve ? _family.ApproveAsync(request) : _family.RejectAsync(request)))
            await LoadAsync();
    }

    private async Task ToggleAdminAsync(Member member)
    {
        var message = Loc.Format(member.IsAdmin ? "RemoveAdminConfirm" : "MakeAdminConfirm", member.Name);
        if (!await Ui.ConfirmAsync(this, member.IsAdmin ? "RemoveAdmin" : "MakeAdmin", message, member.IsAdmin ? "RemoveAdmin" : "MakeAdmin"))
            return;

        if (await Ui.RunAsync(this, () => _family.SetRoleAsync(_group.Id, member.UserId, !member.IsAdmin)))
            await LoadAsync();
    }

    private async Task KickAsync(Member member)
    {
        if (!await Ui.ConfirmAsync(this, "Kick", Loc.Format("KickConfirm", member.Name), "Kick"))
            return;

        if (await Ui.RunAsync(this, () => _family.RemoveMemberAsync(_group.Id, member.UserId)))
            await LoadAsync();
    }

    private async Task ChoosePauseAsync()
    {
        var oneHour = Loc.Get("PauseOneHour");
        var eightHours = Loc.Get("PauseEightHours");
        var tomorrow = Loc.Get("PauseTomorrow");
        var forever = Loc.Get("PauseIndefinite");
        var chosen = Loc.Get("PauseChooseTime");

        var choice = await SocShared.ModernDialog.ActionSheetAsync(this, Loc.Get("Pause"), Loc.Get("Cancel"),
            oneHour, eightHours, tomorrow, forever, chosen);

        var now = DateTimeOffset.Now;
        if (choice == oneHour)
            await SetPauseAsync(true, now.AddHours(1));
        else if (choice == eightHours)
            await SetPauseAsync(true, now.AddHours(8));
        else if (choice == tomorrow)
            await SetPauseAsync(true, new DateTimeOffset(DateTime.Today.AddDays(1).AddHours(8)));
        else if (choice == forever)
            await SetPauseAsync(true, null);
        else if (choice == chosen)
            _pauseTimeRow.IsVisible = true;
    }

    /// <summary>La hora elegida, hoy si aun no ha pasado; si ya paso, mañana a esa hora.</summary>
    private async Task PauseUntilChosenAsync()
    {
        var time = _pauseTime.Time ?? TimeSpan.FromHours(20);
        var until = DateTime.Today.Add(time);
        if (until <= DateTime.Now)
            until = until.AddDays(1);
        await SetPauseAsync(true, new DateTimeOffset(until));
    }

    private async Task SetPauseAsync(bool paused, DateTimeOffset? until)
    {
        if (await Ui.RunAsync(this, () => _family.SetPauseAsync(_group.Id, paused, until)))
        {
            SharingChanges.Notify();
            await LoadAsync();
        }
    }

    private async Task LeaveAsync()
    {
        if (!await Ui.ConfirmAsync(this, "LeaveGroup", Loc.Get("LeaveConfirm"), "LeaveGroup"))
            return;

        if (!await Ui.RunAsync(this, () => _family.LeaveGroupAsync(_group.Id)))
            return;

        SharingChanges.Notify();
        if (AppState.SelectedGroup == _group.Id)
            AppState.SelectedGroup = Guid.Empty;
        await Navigation.PopAsync();
    }
}
