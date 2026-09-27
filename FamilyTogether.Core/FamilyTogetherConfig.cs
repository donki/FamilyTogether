namespace FamilyTogether.Core;

/// <summary>
/// Datos del proyecto de Supabase, de los clientes OAuth y de Firebase.
/// </summary>
/// <remarks>
/// <para>Las constantes las genera <c>familytogether.props</c> en <c>obj/</c> a partir de
/// <c>familytogether.local.props</c> o de variables de entorno: los valores reales no viven en el
/// repositorio. Aqui solo va lo que se calcula a partir de ellas.</para>
///
/// <para><b>La clave publicable puede viajar en la app.</b> Es la antigua <c>anon key</c>: por si
/// sola no da acceso a ningun dato. Lo que protege las filas es la RLS y, en el contenido, el
/// cifrado con la clave del grupo, que el servidor no tiene.</para>
/// </remarks>
public static partial class FamilyTogetherConfig
{
    /// <summary>Hay proyecto de Supabase con el que hablar. Sin el, la app no puede hacer nada remoto.</summary>
    public static bool IsServerConfigured => SupabaseUrl.Length > 0 && PublishableKey.Length > 0;

    /// <summary>Hay Firebase para los avisos. Sin el, el servicio de ubicacion consulta cada 60 s.</summary>
    public static bool IsPushConfigured =>
        FcmProjectId.Length > 0 && FcmApplicationId.Length > 0 && FcmApiKey.Length > 0 && FcmSenderId.Length > 0;
}
