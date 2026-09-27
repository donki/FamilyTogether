using Android.App;
using Android.Content;
using Android.Content.PM;
using FamilyLink.Core;

namespace FamilyLink.Mobile.Platforms.Android;

/// <summary>
/// Recoge la vuelta del navegador al vincular o recuperar la cuenta con Google o Microsoft
/// (ARQUITECTURA §2). Sin esta actividad y sus intent-filter la pestaña se queda con el código y la
/// app nunca se entera de que la entrada ha salido bien.
/// </summary>
/// <remarks>
/// <para>Dos esquemas, como en Task Manager:</para>
/// <list type="bullet">
/// <item><c>com.socratic.familylink</c>: el propio de la app (su ApplicationId). Lo usa Microsoft,
/// que acepta un esquema cualquiera.</item>
/// <item><see cref="FamilyLinkConfig.GoogleRedirectScheme"/>: el identificador invertido del
/// cliente de Google (<c>com.googleusercontent.apps.&lt;id&gt;</c>). Lo genera el núcleo al
/// compilar, porque los identificadores no viven en el repositorio. Si todavía está vacío se usa
/// uno de relleno para que el manifiesto sea válido: nadie vuelve nunca por él.</item>
/// </list>
/// </remarks>
[Activity(NoHistory = true, LaunchMode = LaunchMode.SingleTop, Exported = true)]
[IntentFilter(
    [Intent.ActionView],
    Categories = [Intent.CategoryDefault, Intent.CategoryBrowsable],
    DataScheme = AppRedirectScheme)]
[IntentFilter(
    [Intent.ActionView],
    Categories = [Intent.CategoryDefault, Intent.CategoryBrowsable],
    DataScheme = GoogleScheme)]
public class WebAuthenticationCallbackActivity : Microsoft.Maui.Authentication.WebAuthenticatorCallbackActivity
{
    /// <summary>Esquema propio de vuelta (Microsoft).</summary>
    public const string AppRedirectScheme = "com.socratic.familylink";

    /// <summary>Esquema de Google, o el de relleno si el cliente aún no está configurado.</summary>
    private const string GoogleScheme = FamilyLinkConfig.GoogleRedirectScheme == ""
        ? "com.socratic.familylink.googleplaceholder"
        : FamilyLinkConfig.GoogleRedirectScheme;
}
