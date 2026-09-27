using System.Security.Cryptography;
using System.Text;

namespace FamilyLink.Core;

/// <summary>
/// Cifrado de extremo a extremo (ARQUITECTURA §3): AES-256-GCM con la clave del grupo y ECDH P-256
/// para entregarla de movil a movil.
/// </summary>
/// <remarks>
/// <para><b>El sobre.</b> <c>"enc1:" + base64(nonce[12] ‖ cifrado ‖ tag[16])</c>. Cada valor lleva
/// su propio nonce aleatorio de 96 bits, que es lo que exige GCM para no repetir nunca
/// (nonce, clave). GCM ademas autentica: un byte cambiado hace fallar el descifrado en vez de
/// devolver basura. Un texto vacio se queda vacio.</para>
///
/// <para><b>ECDH.</b> Publicas en base64 de SubjectPublicKeyInfo y privadas en base64 de PKCS#8,
/// que son los formatos que exportan e importan igual Windows, Linux y Android. La clave de sobre
/// es <c>SHA-256(secreto compartido)</c> (<c>DeriveKeyFromHash</c>): 32 bytes, el mismo sobre.</para>
/// </remarks>
public static class Crypto
{
    public const string Prefix = "enc1:";

    private const int KeySize = 32;     // AES-256
    private const int NonceSize = 12;   // 96 bits, lo estandar en GCM
    private const int TagSize = 16;

    /// <summary>Clave nueva de grupo: 32 bytes aleatorios. Solo la genera quien crea el grupo.</summary>
    public static byte[] NewGroupKey() => RandomNumberGenerator.GetBytes(KeySize);

    /// <summary>Cifra un texto con una clave de 32 bytes. Vacio → vacio.</summary>
    public static string Encrypt(string plain, byte[] key)
    {
        if (string.IsNullOrEmpty(plain))
            return string.Empty;

        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var data = Encoding.UTF8.GetBytes(plain);
        var box = new byte[NonceSize + data.Length + TagSize];

        using (var aes = new AesGcm(key, TagSize))
        {
            aes.Encrypt(
                nonce,
                data,
                box.AsSpan(NonceSize, data.Length),
                box.AsSpan(NonceSize + data.Length, TagSize));
        }

        nonce.CopyTo(box, 0);
        return Prefix + Convert.ToBase64String(box);
    }

    /// <summary>
    /// Descifra un sobre <c>enc1:</c>. Devuelve false si el sobre no cuadra, la clave no es la buena
    /// o el contenido esta manipulado: nunca lanza. Vacio (o null) → true con texto vacio.
    /// </summary>
    public static bool TryDecrypt(string? value, byte[]? key, out string plain)
    {
        plain = string.Empty;

        if (string.IsNullOrEmpty(value))
            return true;

        if (key is null || key.Length != KeySize || !value.StartsWith(Prefix, StringComparison.Ordinal))
            return false;

        try
        {
            var box = Convert.FromBase64String(value[Prefix.Length..]);
            if (box.Length < NonceSize + TagSize)
                return false;

            var cipherLength = box.Length - NonceSize - TagSize;
            var data = new byte[cipherLength];

            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(
                box.AsSpan(0, NonceSize),
                box.AsSpan(NonceSize, cipherLength),
                box.AsSpan(NonceSize + cipherLength, TagSize),
                data);

            plain = Encoding.UTF8.GetString(data);
            return true;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
            // Clave equivocada o dato manipulado: es exactamente lo que AES-GCM tiene que hacer.
            return false;
        }
    }

    /// <summary>Descifra o devuelve <paramref name="fallback"/>. Para pintar nombres sin excepciones.</summary>
    public static string DecryptOr(string? value, byte[]? key, string fallback) =>
        TryDecrypt(value, key, out var plain) ? plain : fallback;

    /// <summary>Par ECDH P-256 nuevo: publica (SPKI) y privada (PKCS#8), en base64.</summary>
    public static (string PublicKey, string PrivateKey) NewEcdhKeyPair()
    {
        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        return (
            Convert.ToBase64String(ecdh.ExportSubjectPublicKeyInfo()),
            Convert.ToBase64String(ecdh.ExportPkcs8PrivateKey()));
    }

    /// <summary>
    /// Clave de sobre compartida entre mi privada y la publica del otro: SHA-256 del secreto ECDH.
    /// Los dos lados obtienen la misma. Lanza <see cref="CryptographicException"/> si alguna de las
    /// dos claves no es valida.
    /// </summary>
    public static byte[] SharedKey(string myPrivate, string otherPublic)
    {
        try
        {
            using var mine = ECDiffieHellman.Create();
            mine.ImportPkcs8PrivateKey(Convert.FromBase64String(myPrivate), out _);

            using var other = ECDiffieHellman.Create();
            other.ImportSubjectPublicKeyInfo(Convert.FromBase64String(otherPublic), out _);

            return mine.DeriveKeyFromHash(other.PublicKey, HashAlgorithmName.SHA256);
        }
        catch (FormatException ex)
        {
            throw new CryptographicException("Clave ECDH mal formada.", ex);
        }
    }

    /// <summary>Como <see cref="SharedKey"/>, pero null si alguna clave no vale.</summary>
    public static byte[]? TrySharedKey(string? myPrivate, string? otherPublic)
    {
        if (string.IsNullOrEmpty(myPrivate) || string.IsNullOrEmpty(otherPublic))
            return null;

        try
        {
            return SharedKey(myPrivate, otherPublic);
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            return null;
        }
    }
}
