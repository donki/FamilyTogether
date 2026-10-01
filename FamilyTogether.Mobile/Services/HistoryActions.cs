using FamilyTogether.Core;
using FamilyTogether.Mobile.Helpers;
using FamilyTogether.Mobile.Localization;

namespace FamilyTogether.Mobile.Services;

/// <summary>
/// Borrar mi historial, desde Ajustes o desde Historial: confirmacion, servidor (solo lo mio, RPC
/// <c>clear_my_history</c>) y cola local. La ultima posicion de cada grupo se conserva para que el
/// mapa me siga viendo.
/// </summary>
public static class HistoryActions
{
    /// <summary>Devuelve si se borro.</summary>
    public static async Task<bool> ClearMyHistoryAsync(Page page)
    {
        if (!await Ui.ConfirmAsync(page, "ClearHistory", Loc.Get("ClearHistoryConfirm"), "Delete"))
            return false;

        var outbox = ServiceHelper.Get<LocationOutbox>();
        var family = ServiceHelper.Get<FamilyService>();
        var (ok, deleted) = await Ui.RunAsync(page, () => outbox.ClearHistoryAsync(family));
        if (!ok)
            return false;

        // Y la copia local de mi recorrido de las ultimas 24 h.
        await Ui.RunAsync(page, () => ServiceHelper.Get<LocalTrack>().ClearAsync());

        await Ui.AlertAsync(page, "ClearHistory", Loc.Format("ClearHistoryDone", deleted));
        return true;
    }
}
