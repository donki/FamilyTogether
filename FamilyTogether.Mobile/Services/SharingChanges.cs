using FamilyTogether.Mobile.Services.Native;

namespace FamilyTogether.Mobile.Services;

/// <summary>
/// Avisa a la parte nativa de que ha cambiado dónde se comparte (pausa, reanudar, entrar o salir de
/// un grupo): la siguiente posición vuelve a preguntar al servidor en vez de usar la caché (FR-018).
/// </summary>
public static class SharingChanges
{
    public static void Notify() => SharingState.InvalidateGroups();
}
