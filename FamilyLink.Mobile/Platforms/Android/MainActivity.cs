using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.View;
using FamilyLink.Mobile.Platforms.Android;
using AndroidView = Android.Views.View;

namespace FamilyLink.Mobile;

/// <remarks>
/// <para>El <c>intent-filter</c> de <c>familylink://join</c> es lo que hace útil el QR de una
/// invitación (ARQUITECTURA §4): la cámara del sistema ve el enlace, Android sabe que esta app lo
/// entiende y la abre con el código dentro. Los avisos abren también esta actividad con
/// <c>familylink://event?…</c> (ver <see cref="Notifier"/>); los dos van a
/// <c>App.HandleDeepLink</c>.</para>
/// <para>No sobrescribe <c>OnBackPressed</c> (constitución Mobile §7): el botón de atrás lo
/// resuelve el Shell, que es quien sabe si hay pantalla anterior.</para>
/// </remarks>
[Activity(
    Theme = "@style/Maui.SplashTheme",
    MainLauncher = true,
    LaunchMode = LaunchMode.SingleTop,
    Exported = true,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode
        | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
[IntentFilter(
    [Intent.ActionView],
    Categories = [Intent.CategoryDefault, Intent.CategoryBrowsable],
    DataScheme = DeepLinkScheme,
    DataHost = "join")]
public class MainActivity : MauiAppCompatActivity
{
    /// <summary>Esquema propio de los enlaces de la app (QR de invitación y avisos).</summary>
    public const string DeepLinkScheme = "familylink";

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        ApplySystemBarInsets();

        // Si al girar la pantalla se recrea la actividad, el enlace ya se entregó la primera vez.
        if (savedInstanceState is null)
            Deliver(Intent);

        // Si el usuario compartía su ubicación y el servicio no está vivo (forzar detención, un
        // fabricante agresivo), abrir la app lo reanuda: aquí la app está en pantalla y Android
        // deja arrancar un servicio de tipo location aunque no haya permiso de segundo plano.
        LocationSharing.ResumeIfEnabled();
    }

    /// <summary>
    /// La app ya estaba abierta y llega otro enlace (QR escaneado con la app detrás, aviso
    /// tocado). Con <c>SingleTop</c> no se vuelve a pasar por <c>OnCreate</c>.
    /// </summary>
    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        Intent = intent;
        Deliver(intent);
    }

    private static void Deliver(Intent? intent)
    {
        try
        {
            var data = intent?.Data?.ToString();
            if (string.IsNullOrWhiteSpace(data))
                return;

            if (!data.StartsWith(DeepLinkScheme + "://", StringComparison.OrdinalIgnoreCase))
                return;

            App.HandleDeepLink(data);
        }
        catch (Exception ex)
        {
            // Un enlace mal formado no puede tumbar la app.
            NativeLog.Warn("No se pudo entregar el enlace a la app.", ex);
        }
    }

    /// <summary>
    /// Desde Android 15 el sistema dibuja de borde a borde: se separa el contenido del reloj y de
    /// la barra inferior y se pinta el hueco con el índigo de la marca (como File Manager).
    /// </summary>
    private void ApplySystemBarInsets()
    {
        var content = FindViewById(global::Android.Resource.Id.Content);
        if (content is null)
            return;

        content.SetBackgroundColor(global::Android.Graphics.Color.ParseColor("#2A1CB8"));
        ViewCompat.SetOnApplyWindowInsetsListener(content, new SystemBarInsetsListener());

        // Iconos claros sobre el índigo.
        var controller = Window is not null ? WindowCompat.GetInsetsController(Window, Window.DecorView) : null;
        if (controller is not null)
        {
            controller.AppearanceLightStatusBars = false;
            controller.AppearanceLightNavigationBars = false;
        }
    }

    private sealed class SystemBarInsetsListener : Java.Lang.Object, IOnApplyWindowInsetsListener
    {
        public WindowInsetsCompat OnApplyWindowInsets(AndroidView? view, WindowInsetsCompat? insets)
        {
            // Se consumen siempre: ninguna vista hija debe volver a aplicarlos.
            var consumed = WindowInsetsCompat.Consumed!;
            if (view is null || insets is null)
                return consumed;

            var bars = insets.GetInsets(WindowInsetsCompat.Type.SystemBars() | WindowInsetsCompat.Type.DisplayCutout());
            if (bars is not null)
                view.SetPadding(bars.Left, bars.Top, bars.Right, bars.Bottom);

            return consumed;
        }
    }
}
