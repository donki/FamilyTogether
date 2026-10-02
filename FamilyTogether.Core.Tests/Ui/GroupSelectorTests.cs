using System.Net;
using System.Reflection;
using FamilyTogether.Core;
using FamilyTogether.Mobile.Controls;
using FamilyTogether.Mobile.Localization;
using FamilyTogether.Mobile.Services;

namespace FamilyTogether.Core.Tests.Ui;

/// <summary>El selector de grupo de Zonas e Historial.</summary>
public class GroupSelectorTests : IDisposable
{
    private readonly AppHost _host = new();
    private readonly World _world;
    private readonly ContentPage _page;
    private readonly GroupSelector _selector = new();

    public GroupSelectorTests()
    {
        ForgetLastGroups();
        _world = new World(_host.Phone);
        _page = new ContentPage { Content = new Grid { Children = { _selector } } };
        _page.Host();
    }

    public void Dispose()
    {
        ForgetLastGroups();
        _host.Dispose();
    }

    /// <summary>La lista guardada en memoria vive lo que el proceso: cada prueba empieza sin ella.</summary>
    internal static void ForgetLastGroups() =>
        typeof(GroupSelector).GetField("s_lastGroups", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, null);

    private Picker Picker => _selector.All<Picker>().Single();

    private static List<string> Items(Picker picker) => [.. ((IEnumerable<string>)picker.ItemsSource).Cast<string>()];

    [Fact]
    public async Task SoloOfreceLosGruposConClaveYRecuerdaElElegido()
    {
        var casa = await _world.GroupAsync("Casa");
        var abuelos = await _world.GroupAsync("Abuelos");
        var sinClave = await _world.GroupAsync("Primos");
        await _host.Phone.Keys.RemoveAsync(sinClave.Id);
        AppState.SelectedGroup = casa.Id;
        var changes = new List<Group>();
        _selector.SelectionChanged += (_, g) => changes.Add(g);

        var selected = await _selector.LoadAsync(_page);

        Assert.Equal(casa.Id, selected!.Id);
        Assert.Equal(["Abuelos", "Casa"], Items(Picker));
        Assert.Equal(1, Picker.SelectedIndex);
        Assert.True(Picker.IsEnabled);
        Assert.Equal([abuelos.Id, casa.Id], _selector.Groups.Select(g => g.Id));
        Assert.Empty(changes);   // cargar no cuenta como elegir

        Picker.SelectedIndex = 0;
        Assert.Equal(abuelos.Id, AppState.SelectedGroup);
        Assert.Equal([abuelos.Id], changes.Select(g => g.Id));
        Assert.Equal(abuelos.Id, _selector.Selected!.Id);
        Assert.Equal(Loc.Get("ChooseGroup"), Picker.Title);
    }

    [Fact]
    public async Task UnSoloGrupoNoSeDejaCambiarYElElegidoQueYaNoEstaPasaAlPrimero()
    {
        var casa = await _world.GroupAsync("Casa");
        AppState.SelectedGroup = Guid.NewGuid();

        Assert.Equal(casa.Id, (await _selector.LoadAsync(_page))!.Id);
        Assert.False(Picker.IsEnabled);
        Assert.Equal(0, Picker.SelectedIndex);
    }

    [Fact]
    public async Task SinGruposConClaveNoHayElegido()
    {
        var g = await _world.GroupAsync("Sin clave");
        await _host.Phone.Keys.RemoveAsync(g.Id);
        var changes = 0;
        _selector.SelectionChanged += (_, _) => changes++;

        Assert.Null(await _selector.LoadAsync(_page));
        Assert.Empty(Items(Picker));
        Assert.Equal(-1, Picker.SelectedIndex);
        Assert.False(Picker.IsEnabled);
        Assert.Null(_selector.Selected);

        // Elegir fuera de la lista no hace nada.
        Picker.SelectedIndex = -1;
        Assert.Equal(0, changes);
    }

    [Fact]
    public async Task LaPrimeraVezSinServidorAvisaEnSuIdioma()
    {
        _host.Server.Table("group_members", _ => World.Fail());

        var load = _selector.LoadAsync(_page);
        var text = await _page.DialogTextAsync();
        Assert.Contains(Loc.Get("Err_network"), text);
        await _page.AnswerKeyAsync("Ok");

        Assert.Null(await load);
        Assert.Empty(_selector.Groups);

        // No se ha guardado nada: la siguiente vez vuelve a avisar.
        var again = _selector.LoadAsync(_page);
        await _page.AnswerKeyAsync("Ok");
        Assert.Null(await again);
    }

    [Fact]
    public async Task ConLaListaYaCargadaSinServidorSigueConEllaSinAvisar()
    {
        var casa = await _world.GroupAsync("Casa");
        await _selector.LoadAsync(_page);

        // Sin red, y luego con el servidor caido (500): la ultima lista, sin dialogo.
        _host.Server.Table("group_members", _ => World.Fail());
        var other = new GroupSelector();
        Assert.Equal(casa.Id, (await other.LoadAsync(_page))!.Id);
        Assert.Null(_page.Dialog());

        _host.Server.Table("group_members", _ => FakeSupabase.Status(HttpStatusCode.InternalServerError, "caido"));
        Assert.Equal(casa.Id, (await other.LoadAsync(_page))!.Id);
        Assert.Equal(["Casa"], Items(other.All<Picker>().Single()));
        Assert.Null(_page.Dialog());
    }

    [Fact]
    public async Task ConLaListaCargadaUnErrorQueNoEsDeRedSeAvisaYSeQuedaComoEstaba()
    {
        var casa = await _world.GroupAsync("Casa");
        await _selector.LoadAsync(_page);

        // Antes se escapaba la excepcion (y en OnAppearing, async void, salia el aviso generico).
        _host.Server.Table("group_members", _ => World.Fail("not_member"));
        var load = _selector.LoadAsync(_page);
        Assert.Contains(Loc.Get("Err_not_member"), await _page.DialogTextAsync());
        await _page.AnswerKeyAsync("Ok");

        Assert.Equal(casa.Id, (await load)!.Id);
        Assert.Equal(["Casa"], Items(Picker));
    }

    [Fact]
    public async Task ConLaListaCargadaLosCambiosDelServidorSeVen()
    {
        await _world.GroupAsync("Casa");
        await _selector.LoadAsync(_page);
        var nuevo = await _world.GroupAsync("Nuevo");

        await _selector.LoadAsync(_page);

        Assert.Equal(["Casa", "Nuevo"], Items(Picker));
        Assert.True(Picker.IsEnabled);
        Assert.Contains(_selector.Groups, g => g.Id == nuevo.Id);
    }
}
