using FamilyLink.Core;
using FamilyLink.Mobile.Helpers;

namespace FamilyLink.Mobile.Services;

/// <summary>
/// Lo que se hace al abrir y al volver a la app, sin molestar: renovar la sesion, registrar el token
/// de avisos, entregar claves de grupo a quien las pida, reintentar un SOS pendiente y asegurarse de
/// que el servicio de ubicacion esta vivo si hay grupos con los que compartir.
/// </summary>
/// <remarks>Nada de esto enseña errores: se registran y se reintenta en la siguiente vuelta.</remarks>
public static class AppChores
{
    private static int _running;

    public static async Task RunAsync()
    {
        if (!FamilyLinkConfig.IsServerConfigured || !AppState.WelcomeDone)
            return;
        if (Interlocked.Exchange(ref _running, 1) == 1)
            return;

        try
        {
            await ServiceHelper.Get<SupabaseClient>().EnsureSignedInAsync();
            var family = ServiceHelper.Get<FamilyService>();

            await Step("push", async () =>
            {
                if (ServiceHelper.TryGet<IPushService>() is { IsConfigured: true } push
                    && await push.GetTokenAsync() is { } token)
                {
                    await family.RegisterPushTokenAsync(token);
                }
            });

            await Step("key_shares", () => family.FulfillPendingKeySharesAsync());
            await Step("sos", () => ServiceHelper.Get<SosService>().RetryPendingAsync());
            await Step("sharing", async () =>
            {
                var groups = await family.GetGroupsAsync();
                await EnsureSharingAsync(groups.Count > 0);
            });
        }
        catch (Exception ex)
        {
            CrashLog.Error("AppChores", ex);
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    /// <summary>
    /// Arranca el servicio de ubicacion si hay permiso y algun grupo. Sin grupos no hay con quien
    /// compartir y la notificacion fija solo confundiria.
    /// </summary>
    public static async Task EnsureSharingAsync(bool hasGroups)
    {
        if (!hasGroups || ServiceHelper.TryGet<ILocationSharing>() is not { } sharing || sharing.IsRunning)
            return;

        if (await sharing.CheckPermissionAsync() != LocationPermissionState.Denied)
            sharing.Start();
    }

    private static async Task Step(string name, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            CrashLog.Error($"AppChores.{name}", ex);
        }
    }
}
