namespace FamilyLink.Mobile.Services;

/// <summary>
/// Avisa al servicio de ubicacion de que han cambiado los grupos con los que se comparte (pausa,
/// reanudar, unirse, abandonar, expulsion). Sin esto tarda hasta 60 s en enterarse y podria enviar
/// una posicion a un grupo en el que ya se esta en pausa.
/// </summary>
public static class SharingChanges
{
    public static void Notify()
    {
#if ANDROID
        try
        {
            Platforms.Android.LocationSharing.NotifySharingChanged();
        }
        catch (Exception ex)
        {
            CrashLog.Error("SharingChanges.Notify", ex);
        }
#endif
    }
}
