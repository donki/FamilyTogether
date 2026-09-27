using FamilyTogether.Core;

namespace FamilyTogether.Core.Tests;

public class CryptoTests
{
    [Fact]
    public void IdaYVuelta()
    {
        var key = Crypto.NewGroupKey();
        var sealedText = Crypto.Encrypt("Casa de la abuela — ñandú", key);

        Assert.StartsWith("enc1:", sealedText);
        Assert.True(Crypto.TryDecrypt(sealedText, key, out var plain));
        Assert.Equal("Casa de la abuela — ñandú", plain);
    }

    [Fact]
    public void FormatoDelSobre()
    {
        var key = Crypto.NewGroupKey();
        var box = Convert.FromBase64String(Crypto.Encrypt("abc", key)["enc1:".Length..]);

        // nonce[12] + cifrado[3] + tag[16]
        Assert.Equal(12 + 3 + 16, box.Length);
    }

    [Fact]
    public void VacioSeQuedaVacio()
    {
        var key = Crypto.NewGroupKey();
        Assert.Equal(string.Empty, Crypto.Encrypt("", key));
        Assert.True(Crypto.TryDecrypt("", key, out var plain));
        Assert.Equal(string.Empty, plain);
    }

    [Fact]
    public void CadaCifradoLlevaSuNonce()
    {
        var key = Crypto.NewGroupKey();
        Assert.NotEqual(Crypto.Encrypt("igual", key), Crypto.Encrypt("igual", key));
    }

    [Fact]
    public void ClaveEquivocadaFallaSinExcepcion()
    {
        var sealedText = Crypto.Encrypt("secreto", Crypto.NewGroupKey());
        Assert.False(Crypto.TryDecrypt(sealedText, Crypto.NewGroupKey(), out _));
        Assert.False(Crypto.TryDecrypt(sealedText, null, out _));
        Assert.Equal("?", Crypto.DecryptOr(sealedText, Crypto.NewGroupKey(), "?"));
    }

    [Fact]
    public void SobreManipuladoFalla()
    {
        var key = Crypto.NewGroupKey();
        var box = Convert.FromBase64String(Crypto.Encrypt("mensaje largo de prueba", key)["enc1:".Length..]);
        box[15] ^= 0x01;   // un bit del texto cifrado

        Assert.False(Crypto.TryDecrypt("enc1:" + Convert.ToBase64String(box), key, out _));

        // Y el tag tambien.
        box[15] ^= 0x01;
        box[^1] ^= 0x80;
        Assert.False(Crypto.TryDecrypt("enc1:" + Convert.ToBase64String(box), key, out _));
    }

    [Theory]
    [InlineData("enc1:")]
    [InlineData("enc1:no-es-base64!!")]
    [InlineData("enc1:AAAA")]
    [InlineData("texto en claro")]
    public void SobresMalFormadosFallan(string value)
    {
        Assert.False(Crypto.TryDecrypt(value, Crypto.NewGroupKey(), out _));
    }

    [Fact]
    public void EcdhEntreDosParesDaLaMismaClave()
    {
        var a = Crypto.NewEcdhKeyPair();
        var b = Crypto.NewEcdhKeyPair();

        var ab = Crypto.SharedKey(a.PrivateKey, b.PublicKey);
        var ba = Crypto.SharedKey(b.PrivateKey, a.PublicKey);

        Assert.Equal(32, ab.Length);
        Assert.Equal(ab, ba);

        // Y un tercero no llega a ella.
        var c = Crypto.NewEcdhKeyPair();
        Assert.NotEqual(ab, Crypto.SharedKey(c.PrivateKey, b.PublicKey));
    }

    [Fact]
    public void EntregaDeLaClaveDelGrupoPorEcdh()
    {
        // §4: el admin cifra la clave del grupo con ECDH(inv_priv, req_pub); el solicitante la
        // abre con ECDH(req_priv, inv_pub).
        var groupKey = Crypto.NewGroupKey();
        var inv = Crypto.NewEcdhKeyPair();
        var req = Crypto.NewEcdhKeyPair();

        var keyBox = Crypto.Encrypt(Convert.ToBase64String(groupKey), Crypto.SharedKey(inv.PrivateKey, req.PublicKey));

        Assert.True(Crypto.TryDecrypt(keyBox, Crypto.SharedKey(req.PrivateKey, inv.PublicKey), out var opened));
        Assert.Equal(groupKey, Convert.FromBase64String(opened));
    }

    [Fact]
    public void ClavesMalFormadas()
    {
        var a = Crypto.NewEcdhKeyPair();
        Assert.Null(Crypto.TrySharedKey(a.PrivateKey, "no es una clave"));
        Assert.Null(Crypto.TrySharedKey(null, a.PublicKey));
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => Crypto.SharedKey(a.PrivateKey, "AAAA"));
    }

    [Fact]
    public void CargasJsonInvariantes()
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("es-ES");
            Assert.Equal("{\"lat\":40.1,\"lon\":-3.2,\"acc\":8.5}", Payloads.Position(40.1, -3.2, 8.5));
            Assert.Equal("{\"lat\":40.1,\"lon\":-3.2,\"r\":150}", Payloads.Zone(40.1, -3.2, 150));
            Assert.Equal(
                "{\"lat\":40.1,\"lon\":-3.2,\"acc\":8.5,\"at\":\"2026-09-27T10:00:00Z\",\"stale\":false}",
                Payloads.Sos(40.1, -3.2, 8.5, new DateTimeOffset(2026, 9, 27, 12, 0, 0, TimeSpan.FromHours(2)), false));

            var key = Crypto.NewGroupKey();
            var opened = Payloads.Open<Payloads.PositionData>(Crypto.Encrypt(Payloads.Position(40.1, -3.2, 8.5), key), key);
            Assert.Equal(new Payloads.PositionData(40.1, -3.2, 8.5), opened);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    [Fact]
    public async Task GroupKeyStoreGuardaEnBase64()
    {
        var store = new MemoryStore();
        var keys = new GroupKeyStore(store);
        var group = Guid.NewGuid();
        var key = Crypto.NewGroupKey();

        await keys.SetAsync(group, key);
        Assert.Equal(Convert.ToBase64String(key), await store.GetAsync($"group_key_{group:D}"));
        Assert.Equal(key, await new GroupKeyStore(store).GetAsync(group));

        await keys.RemoveAsync(group);
        Assert.Null(await keys.GetAsync(group));
    }
}

internal sealed class MemoryStore : ISecureStore
{
    private readonly Dictionary<string, string> _values = [];

    public Task<string?> GetAsync(string key) => Task.FromResult(_values.TryGetValue(key, out var v) ? v : null);

    public Task SetAsync(string key, string value)
    {
        _values[key] = value;
        return Task.CompletedTask;
    }

    public void Remove(string key) => _values.Remove(key);
}
