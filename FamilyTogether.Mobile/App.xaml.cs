using FamilyTogether.Core;
using FamilyTogether.Mobile.Localization;
using FamilyTogether.Mobile.Services;

namespace FamilyTogether.Mobile;

public partial class App : Application
{
    /// <summary>Codigo de invitacion que llego por enlace y aun no se ha atendido (lo recoge Grupos).</summary>
    public static string? PendingJoinCode { get; set; }

    /// <summary>Enlace que llego antes de que hubiera ventana (arranque desde un QR o un aviso).</summary>
    private static string? _pendingLink;

    /// <summary>La app vuelve a primer plano: las pantallas con datos vivos se refrescan.</summary>
    public static event EventHandler? AppResumed;

    /// <summary>
    /// La app pasa a segundo plano (Inicio, otra app, pantalla apagada). MAUI no llama a
    /// <c>OnDisappearing</c> en ese caso y el proceso sigue vivo por el servicio de ubicación: sin
    /// este aviso, el mapa seguía refrescando cada 30 s (red y una lectura GPS de precisión máxima)
    /// y preguntando a la página cada 400 ms con la app cerrada (batería, SC-005, 2026-10-06).
    /// </summary>
    public static event EventHandler? AppStopped;

    public App()
    {
        InitializeComponent();
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        // El idioma, antes de construir el Shell: los textos de cada pagina se resuelven al crearla.
        Loc.Apply();

        var window = new Window(new AppShell());

        window.Created += (_, _) =>
        {
            DeliverPendingLink();
            _ = AppChores.RunAsync();
        };
        window.Resumed += (_, _) =>
        {
            DeliverPendingLink();
            _ = AppChores.RunAsync();
            AppResumed?.Invoke(this, EventArgs.Empty);
        };
        window.Stopped += (_, _) => AppStopped?.Invoke(this, EventArgs.Empty);

        return window;
    }

    /// <summary>
    /// Enlaces propios que entrega <c>MainActivity</c>: el QR de una invitacion
    /// (<c>familytogether://join?c=XXXX</c>) abre la pantalla de unirse con el codigo; un aviso tocado
    /// (<c>familytogether://event?type=…&amp;group=…&amp;event=…</c>) abre su grupo en el mapa, o Grupos
    /// si es una solicitud; el widget SOS (<c>familytogether://sos</c>) abre la cuenta atras del SOS.
    /// </summary>
    public static void HandleDeepLink(string uri)
    {
        _pendingLink = uri;
        if (Current?.Windows.FirstOrDefault()?.Page is AppShell)
            UiThread.Post(DeliverPendingLink);
    }

    private static void DeliverPendingLink()
    {
        var link = Interlocked.Exchange(ref _pendingLink, null);
        if (string.IsNullOrWhiteSpace(link) || Shell.Current is not AppShell shell)
            return;

        try
        {
            var uri = new Uri(link);
            var query = ParseQuery(uri.Query);

            if (uri.Host.Equals("join", StringComparison.OrdinalIgnoreCase))
            {
                if (InviteCodes.TryParse(link, out var code))
                {
                    PendingJoinCode = code;
                    // Sin bienvenida hecha, primero el nombre: Grupos lo recogera despues.
                    if (AppState.WelcomeDone)
                        _ = shell.GoToAsync("//GroupsPage");
                }
                return;
            }

            if (uri.Host.Equals("sos", StringComparison.OrdinalIgnoreCase))
            {
                if (AppState.WelcomeDone)
                    _ = OpenSosAsync(shell);
                return;
            }

            if (uri.Host.Equals("event", StringComparison.OrdinalIgnoreCase) && AppState.WelcomeDone)
            {
                var group = query.TryGetValue("group", out var g) && Guid.TryParse(g, out var id) ? id : Guid.Empty;
                if (group != Guid.Empty)
                    AppState.SelectedGroup = group;

                _ = OpenEventAsync(shell, query.GetValueOrDefault("type"), group, query);
            }
        }
        catch (Exception ex)
        {
            CrashLog.Error("App.DeliverPendingLink", ex);
        }
    }

    /// <summary>
    /// Un aviso tocado: SOS y zonas abren el mapa de su grupo (centrado en el sitio si el enlace lo
    /// trae; si no, con todo el grupo a la vista); una solicitud abre el detalle del grupo, donde
    /// estan las pendientes; una solicitud resuelta abre Grupos.
    /// </summary>
    private static async Task OpenEventAsync(AppShell shell, string? type, Guid group, Dictionary<string, string> query)
    {
        try
        {
            switch (type)
            {
                case EventTypes.JoinRequest when group != Guid.Empty:
                    await shell.GoToAsync("//GroupsPage");
                    await shell.Navigation.PushAsync(new Pages.GroupDetailPage(new Group(group, string.Empty, true, false)));
                    break;

                case EventTypes.RequestResolved or EventTypes.JoinRequest:
                    await shell.GoToAsync("//GroupsPage");
                    break;

                default:
                    if (double.TryParse(query.GetValueOrDefault("lat"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var lat)
                        && double.TryParse(query.GetValueOrDefault("lon"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var lon))
                    {
                        PendingMapFocus = (lat, lon);
                    }
                    else
                    {
                        PendingMapFit = true;
                    }

                    await shell.GoToAsync("//MapPage");
                    // Con el mapa ya en pantalla no hay OnAppearing: que lo aplique ya, no en la
                    // siguiente recarga (hasta 30 s).
                    MapFocusRequested?.Invoke(null, EventArgs.Empty);
                    break;
            }
        }
        catch (Exception ex)
        {
            CrashLog.Error("App.OpenEventAsync", ex);
        }
    }

    /// <summary>
    /// El widget SOS: el mapa y encima la cuenta atras de 3 s con Cancelar, como el boton del mapa
    /// (un toque sin querer en la pantalla de inicio nunca envia nada solo). Si ya hay una cuenta
    /// atras en pantalla, no se abre otra.
    /// </summary>
    private static async Task OpenSosAsync(AppShell shell)
    {
        try
        {
            if (shell.Navigation.NavigationStack.LastOrDefault() is Pages.SosPage)
                return;

            await shell.GoToAsync("//MapPage");
            await shell.Navigation.PushAsync(new Pages.SosPage());
        }
        catch (Exception ex)
        {
            CrashLog.Error("App.OpenSosAsync", ex);
        }
    }

    /// <summary>Sitio en el que centrar el mapa al abrirlo desde un aviso.</summary>
    public static (double Lat, double Lon)? PendingMapFocus { get; set; }

    /// <summary>Al abrir el mapa desde un aviso sin sitio: encuadrar a todo el grupo.</summary>
    public static bool PendingMapFit { get; set; }

    /// <summary>Hay un <see cref="PendingMapFocus"/> o <see cref="PendingMapFit"/> nuevo para el mapa abierto.</summary>
    public static event EventHandler? MapFocusRequested;

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length == 2)
                result[parts[0]] = Uri.UnescapeDataString(parts[1]);
        }

        return result;
    }
}
