using Android.Gms.Tasks;
using Firebase.Messaging;
using FamilyTogether.Mobile.Services;
using GmsTask = Android.Gms.Tasks.Task;
using Task = System.Threading.Tasks.Task;

namespace FamilyTogether.Mobile.Platforms.Android;

/// <inheritdoc cref="IPushService"/>
/// <remarks>
/// El token es <b>del móvil</b> (Mobile §10): la app lo registra con
/// <c>FamilyService.RegisterPushTokenAsync</c> al arrancar y <see cref="FamilyTogetherMessagingService"/>
/// lo vuelve a registrar cada vez que Firebase lo renueva.
/// </remarks>
public sealed class PushService : IPushService
{
    private static readonly TimeSpan TokenTimeout = TimeSpan.FromSeconds(20);

    public bool IsConfigured
    {
        get
        {
            if (!FirebaseSetup.IsInitialized)
                FirebaseSetup.Initialize(global::Android.App.Application.Context);
            return FirebaseSetup.IsInitialized;
        }
    }

    public async Task<string?> GetTokenAsync()
    {
        if (!IsConfigured)
            return null;

        try
        {
            // El binding marca GetToken como «deprecated», pero en el SDK de Firebase es la forma
            // vigente de pedir el token de registro. Se silencia solo aquí.
#pragma warning disable CS0618
            var tokenTask = FirebaseMessaging.Instance.GetToken();
#pragma warning restore CS0618
            var result = await AwaitGms(tokenTask).WaitAsync(TokenTimeout).ConfigureAwait(false);
            var token = result?.ToString();
            return string.IsNullOrWhiteSpace(token) ? null : token;
        }
        catch (Exception ex)
        {
            // Sin red, sin Play Services o valores de Firebase erróneos: sin push, con consulta.
            NativeLog.Warn("No se pudo obtener el token de FCM.", ex);
            return null;
        }
    }

    /// <summary>Convierte una tarea de Play Services en una de .NET.</summary>
    internal static Task<Java.Lang.Object?> AwaitGms(GmsTask task)
    {
        var tcs = new TaskCompletionSource<Java.Lang.Object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        task.AddOnCompleteListener(new CompleteListener(tcs));
        return tcs.Task;
    }

    private sealed class CompleteListener(TaskCompletionSource<Java.Lang.Object?> tcs) : Java.Lang.Object, IOnCompleteListener
    {
        public void OnComplete(GmsTask task)
        {
            try
            {
                if (task.IsCanceled)
                    tcs.TrySetCanceled();
                else if (task.IsSuccessful)
                    tcs.TrySetResult(task.Result);
                else
                    tcs.TrySetException((Exception?)task.Exception ?? new InvalidOperationException("FCM task failed"));
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }
    }
}
