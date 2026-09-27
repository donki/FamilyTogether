using FamilyLink.Core;
using FamilyLink.Mobile.Helpers;
using FamilyLink.Mobile.Localization;
using FamilyLink.Mobile.Services;

namespace FamilyLink.Mobile.Controls;

/// <summary>
/// Selector de grupo de Zonas e Historial. Comparte la eleccion con el mapa
/// (<see cref="AppState.SelectedGroup"/>). Solo ofrece los grupos cuya clave tiene este movil: sin
/// ella no hay zonas ni recorridos que se puedan leer.
/// </summary>
public sealed class GroupSelector : ContentView
{
    private readonly Picker _picker = new() { HorizontalOptions = LayoutOptions.Fill };
    private IReadOnlyList<Group> _groups = [];
    private bool _updating;

    public GroupSelector()
    {
        _picker.Title = Loc.Get("ChooseGroup");
        _picker.SelectedIndexChanged += (_, _) =>
        {
            if (_updating || Selected is not { } group)
                return;
            AppState.SelectedGroup = group.Id;
            SelectionChanged?.Invoke(this, group);
        };
        Content = UiKit.Input(_picker);
    }

    public event EventHandler<Group>? SelectionChanged;

    public IReadOnlyList<Group> Groups => _groups;

    public Group? Selected =>
        _picker.SelectedIndex >= 0 && _picker.SelectedIndex < _groups.Count ? _groups[_picker.SelectedIndex] : null;

    /// <summary>Carga los grupos. Devuelve el elegido, o null si no hay ninguno con clave.</summary>
    public async Task<Group?> LoadAsync(Page page)
    {
        var (ok, groups) = await Ui.RunAsync(page, () => ServiceHelper.Get<FamilyService>().GetGroupsAsync());
        if (!ok || groups is null)
            return Selected;

        _updating = true;
        try
        {
            _groups = [.. groups.Where(g => !g.KeyMissing)];
            _picker.ItemsSource = _groups.Select(g => g.Name).ToList();
            var index = _groups.ToList().FindIndex(g => g.Id == AppState.SelectedGroup);
            _picker.SelectedIndex = _groups.Count == 0 ? -1 : Math.Max(0, index);
            _picker.IsEnabled = _groups.Count > 1;
        }
        finally
        {
            _updating = false;
        }

        return Selected;
    }
}
