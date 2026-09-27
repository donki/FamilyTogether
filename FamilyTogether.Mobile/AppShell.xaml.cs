using FamilyTogether.Core;
using FamilyTogether.Mobile.Services;

namespace FamilyTogether.Mobile;

/// <summary>Una pagina que tiene algo abierto encima (una cuenta atras, un modo) y lo cierra con atras.</summary>
public interface IBackHandler
{
    /// <summary>Devuelve true si se ha encargado del boton de atras.</summary>
    bool HandleBack();
}

/// <summary>
/// Shell con el menu hamburguesa: Mapa (inicio), Grupos, Zonas, Historial, Guia, Ajustes, Novedades
/// y Acerca de. La primera vez (o sin servidor configurado) arranca en la bienvenida.
/// </summary>
public partial class AppShell : Shell
{
    public AppShell()
    {
        InitializeComponent();
        VersionLabel.Text = $"v{AppInfo.Current.VersionString}";

        CurrentItem = !FamilyTogetherConfig.IsServerConfigured || !AppState.WelcomeDone
            ? WelcomeItem
            : MapItem;
    }

    /// <summary>
    /// Atras (constitucion Mobile 7): primero se cierra lo que haya abierto (el menu, algo de la
    /// pagina); con una pagina apilada, vuelve a la anterior; en una seccion del menu que no es el
    /// inicio, vuelve al mapa; y en el mapa la app se oculta sin cerrarse (<c>MoveTaskToBack</c>).
    /// </summary>
    protected override bool OnBackButtonPressed()
    {
        if (FlyoutIsPresented)
        {
            FlyoutIsPresented = false;
            return true;
        }

        if (CurrentPage is IBackHandler handler && handler.HandleBack())
            return true;

        if (Navigation.NavigationStack.Count > 1 || Navigation.ModalStack.Count > 0)
            return base.OnBackButtonPressed();

        if (CurrentItem != MapItem && CurrentItem != WelcomeItem)
        {
            CurrentItem = MapItem;
            return true;
        }

#if ANDROID
        Microsoft.Maui.ApplicationModel.Platform.CurrentActivity?.MoveTaskToBack(true);
        return true;
#else
        return base.OnBackButtonPressed();
#endif
    }

    /// <summary>Tras la bienvenida o la guia: al mapa, como seccion de inicio.</summary>
    public void GoHome() => CurrentItem = MapItem;
}
