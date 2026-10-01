using FamilyTogether.Core;
using FamilyTogether.Mobile.Helpers;
using FamilyTogether.Mobile.Localization;
using FamilyTogether.Mobile.Services;

namespace FamilyTogether.Mobile.Controls;

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

    /// <summary>
    /// Ultima lista que dio el servidor (solo en memoria, mientras viva el proceso): sin conexion,
    /// el Historial puede seguir pintando mi recorrido guardado en el movil.
    /// </summary>
    private static IReadOnlyList<Group>? s_lastGroups;

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
        IReadOnlyList<Group>? groups;
        if (s_lastGroups is { } cached)
        {
            // Ya se cargaron alguna vez: sin conexion se usa esa lista, sin dialogo.
            try
            {
                groups = await ServiceHelper.Get<FamilyService>().GetGroupsAsync();
            }
            catch (FamilyTogetherException ex) when (ex.IsNetwork || ex.Code == FamilyTogetherException.Server)
            {
                CrashLog.Info($"grupos: sin servidor ({ex.Code}); se usa la ultima lista");
                groups = cached;
            }
        }
        else
        {
            var (ok, loaded) = await Ui.RunAsync(page, () => ServiceHelper.Get<FamilyService>().GetGroupsAsync());
            if (!ok || loaded is null)
                return Selected;
            groups = loaded;
        }

        s_lastGroups = groups;

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
