using FamilyLink.Core;
using FamilyLink.Mobile.Controls;
using FamilyLink.Mobile.Helpers;
using FamilyLink.Mobile.Localization;
using FamilyLink.Mobile.Services;

namespace FamilyLink.Mobile.Pages;

/// <summary>
/// Avisos de zonas (FR-016): la matriz persona × zona con un interruptor de Entrada y otro de
/// Salida. Solo avisa de las combinaciones activadas, y cada uno elige las suyas.
/// </summary>
public sealed class ZoneAlertsPage : ContentPage
{
    private readonly FamilyService _family = ServiceHelper.Get<FamilyService>();
    private readonly Group _group;
    private readonly VerticalStackLayout _content = new() { Padding = 16, Spacing = 16 };
    private readonly Dictionary<(Guid Target, Guid Zone), ZoneSubscription> _subs = [];
    private bool _filling;

    public ZoneAlertsPage(Group group)
    {
        _group = group;
        Title = Loc.Get("ZoneAlerts");
        Content = new ScrollView { Content = _content };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        _content.Clear();
        _content.Add(UiKit.Hint(Loc.Format("ZoneAlertsHint", _group.Name)));

        var (ok, data) = await Ui.RunAsync(this, async () =>
        {
            var members = _family.GetMembersAsync(_group.Id);
            var zones = _family.GetZonesAsync(_group.Id);
            var subs = _family.GetMySubscriptionsAsync(_group.Id);
            await Task.WhenAll(members, zones, subs);
            return (Members: members.Result, Zones: zones.Result, Subs: subs.Result);
        });
        if (!ok)
            return;

        _subs.Clear();
        foreach (var s in data.Subs)
            _subs[(s.TargetId, s.ZoneId)] = s;

        if (data.Zones.Count == 0)
        {
            _content.Add(UiKit.Body(Loc.Get("NoZones")));
            return;
        }

        var others = data.Members.Where(m => !m.IsMe).OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        if (others.Count == 0)
        {
            _content.Add(UiKit.Body(Loc.Get("ZoneAlertsNobody")));
            return;
        }

        _filling = true;
        try
        {
            foreach (var member in others)
                _content.Add(MemberCard(member, data.Zones));
        }
        finally
        {
            _filling = false;
        }
    }

    private View MemberCard(Member member, IReadOnlyList<Zone> zones)
    {
        var avatar = new AvatarView(36);
        avatar.Set(member.UserId, member.Name, member.AvatarBase64);
        var head = new HorizontalStackLayout { Spacing = 12, Children = { avatar, UiKit.Title(member.Name) } };

        var stack = new VerticalStackLayout { Spacing = 4, Children = { head } };
        foreach (var zone in zones.OrderBy(z => z.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            _subs.TryGetValue((member.UserId, zone.Id), out var current);
            var enter = new Switch { IsToggled = current?.OnEnter == true };
            var exit = new Switch { IsToggled = current?.OnExit == true };

            enter.Toggled += async (_, _) => await SaveAsync(member.UserId, zone.Id, enter, exit);
            exit.Toggled += async (_, _) => await SaveAsync(member.UserId, zone.Id, enter, exit);

            stack.Add(UiKit.Separator());
            stack.Add(UiKit.ItemTitle(zone.Name));
            stack.Add(UiKit.Row(UiKit.Body(Loc.Get("AlertOnEnter")), enter));
            stack.Add(UiKit.Row(UiKit.Body(Loc.Get("AlertOnExit")), exit));
        }

        return UiKit.Card(stack);
    }

    private async Task SaveAsync(Guid target, Guid zone, Switch enter, Switch exit)
    {
        if (_filling)
            return;

        var subscription = new ZoneSubscription(target, zone, enter.IsToggled, exit.IsToggled);
        if (await Ui.RunAsync(this, () => _family.SetSubscriptionAsync(_group.Id, subscription)))
        {
            _subs[(target, zone)] = subscription;
            return;
        }

        // No se guardo: los interruptores vuelven a lo que habia, en vez de mentir.
        _filling = true;
        _subs.TryGetValue((target, zone), out var previous);
        enter.IsToggled = previous?.OnEnter == true;
        exit.IsToggled = previous?.OnExit == true;
        _filling = false;
    }
}
