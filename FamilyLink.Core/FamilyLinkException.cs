namespace FamilyLink.Core;

/// <summary>
/// Fallo que la app tiene que poder contar. <see cref="Code"/> es una clave estable que se
/// traduce en la interfaz; el mensaje es para el registro.
/// </summary>
/// <remarks>
/// Claves: las de negocio que lanza el servidor (<c>not_member</c>, <c>not_admin</c>,
/// <c>expired</c>, <c>not_found</c>, <c>already_member</c>, <c>last_admin</c>, <c>paused</c>,
/// <c>not_pending</c>), las de las Edge Functions (<c>already_linked</c>, <c>not_empty</c>,
/// <c>no_link</c>…) y las del propio cliente: <see cref="Network"/>, <see cref="Unauthorized"/>,
/// <see cref="Server"/>, <see cref="NotConfigured"/> y <see cref="KeyMissing"/>.
/// </remarks>
public sealed class FamilyLinkException(string code, string message, Exception? inner = null)
    : Exception(message, inner)
{
    /// <summary>Sin red, o el servidor no contesta a tiempo. Se puede reintentar.</summary>
    public const string Network = "network";

    /// <summary>La sesion no vale (o la RLS no deja): p. ej. el usuario se recupero en otro movil.</summary>
    public const string Unauthorized = "unauthorized";

    /// <summary>Cualquier otro error del servidor.</summary>
    public const string Server = "server";

    /// <summary>La compilacion no lleva proyecto de Supabase (familylink.local.props vacio).</summary>
    public const string NotConfigured = "not_configured";

    /// <summary>Este movil no tiene la clave del grupo: no puede cifrar lo que haria falta.</summary>
    public const string KeyMissing = "key_missing";

    public string Code { get; } = code;

    public bool IsNetwork => Code == Network;
}
