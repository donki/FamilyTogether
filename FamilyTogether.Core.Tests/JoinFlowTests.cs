using FamilyTogether.Core;

namespace FamilyTogether.Core.Tests;

/// <summary>El flujo de §4 entero, con las mismas piezas que usa FamilyService, sin servidor.</summary>
public class JoinFlowTests
{
    private sealed record Request(string ReqPub, string ReqPriv, string InvPub, string InvPrivEnc, string NameBox);

    /// <summary>Lo que hace el solicitante con la informacion de la invitacion; request_join copia inv_pub e inv_priv_enc en la fila.</summary>
    private static Request Join(string invPub, string invPrivEnc, string name)
    {
        var (reqPub, reqPriv) = Crypto.NewEcdhKeyPair();
        var nameBox = Crypto.Encrypt(name, Crypto.SharedKey(reqPriv, invPub));
        return new Request(reqPub, reqPriv, invPub, invPrivEnc, nameBox);
    }

    [Fact]
    public void InvitacionSolicitudAprobacionYApertura()
    {
        // 1. El admin crea el grupo y la invitacion: privada cifrada con la clave del grupo.
        var groupKey = Crypto.NewGroupKey();
        var (invPub, invPriv) = Crypto.NewEcdhKeyPair();
        var invPrivEnc = Crypto.Encrypt(invPriv, groupKey);

        // 2. El solicitante pide entrar.
        var request = Join(invPub, invPrivEnc, "Marta");

        // 3. Otro admin (solo tiene la clave del grupo y la fila) lee el nombre y aprueba.
        Assert.Equal("Marta", FamilyService.OpenRequestName(groupKey, request.InvPrivEnc, request.ReqPub, request.NameBox));
        var shared = FamilyService.RequestSecret(groupKey, request.InvPrivEnc, request.ReqPub);
        Assert.NotNull(shared);
        var keyBox = FamilyService.SealKeyBox(groupKey, shared);

        // 4. El solicitante abre key_box con su privada y la inv_pub de la fila.
        Assert.Equal(groupKey, FamilyService.OpenKeyBox(request.ReqPriv, request.InvPub, keyBox));
    }

    [Fact]
    public void SinLaClaveDelGrupoNoSeLeeNiSeAprueba()
    {
        var groupKey = Crypto.NewGroupKey();
        var (invPub, invPriv) = Crypto.NewEcdhKeyPair();
        var request = Join(invPub, Crypto.Encrypt(invPriv, groupKey), "Marta");

        Assert.Equal("?", FamilyService.OpenRequestName(null, request.InvPrivEnc, request.ReqPub, request.NameBox));
        Assert.Equal("?", FamilyService.OpenRequestName(Crypto.NewGroupKey(), request.InvPrivEnc, request.ReqPub, request.NameBox));
        Assert.Null(FamilyService.RequestSecret(Crypto.NewGroupKey(), request.InvPrivEnc, request.ReqPub));
    }

    [Fact]
    public void SolicitudRepetidaActualizadaSeAbreConLaPrivadaNueva()
    {
        var groupKey = Crypto.NewGroupKey();
        var (invPub, invPriv) = Crypto.NewEcdhKeyPair();
        var invPrivEnc = Crypto.Encrypt(invPriv, groupKey);

        var first = Join(invPub, invPrivEnc, "Marta");
        var second = Join(invPub, invPrivEnc, "Marta");   // el servidor actualiza la fila con esta

        var keyBox = FamilyService.SealKeyBox(groupKey, FamilyService.RequestSecret(groupKey, second.InvPrivEnc, second.ReqPub)!);

        Assert.Equal(groupKey, FamilyService.OpenKeyBox(second.ReqPriv, second.InvPub, keyBox));
        Assert.Null(FamilyService.OpenKeyBox(first.ReqPriv, first.InvPub, keyBox));
    }

    [Fact]
    public void KeyBoxManipuladoNoAbre()
    {
        var groupKey = Crypto.NewGroupKey();
        var (invPub, invPriv) = Crypto.NewEcdhKeyPair();
        var request = Join(invPub, Crypto.Encrypt(invPriv, groupKey), "Marta");
        var keyBox = FamilyService.SealKeyBox(groupKey, FamilyService.RequestSecret(groupKey, request.InvPrivEnc, request.ReqPub)!);

        var box = Convert.FromBase64String(keyBox["enc1:".Length..]);
        box[20] ^= 0x01;
        Assert.Null(FamilyService.OpenKeyBox(request.ReqPriv, request.InvPub, "enc1:" + Convert.ToBase64String(box)));
        Assert.Null(FamilyService.OpenKeyBox(null, request.InvPub, keyBox));
    }
}
