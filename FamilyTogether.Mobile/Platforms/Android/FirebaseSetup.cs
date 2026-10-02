using Android.Content;
using Firebase;
using FamilyTogether.Core;
using FamilyTogether.Mobile.Services.Native;

namespace FamilyTogether.Mobile.Platforms.Android;

/// <summary>
/// Arranque manual de Firebase, <b>solo Messaging</b> (constitución General §1.2 y Mobile §10).
/// </summary>
/// <remarks>
/// <para>No hay <c>google-services.json</c> en el repositorio: los cuatro valores del proyecto de
/// Firebase los genera el núcleo en <see cref="FamilyTogetherConfig"/> a partir de
/// <c>familytogether.local.props</c>, igual que la URL de Supabase. Si están vacíos
/// (<see cref="FamilyTogetherConfig.IsPushConfigured"/> falso) no se inicializa nada y los avisos
/// llegan por la consulta cada 60 s del servicio de ubicación.</para>
/// <para>El <c>FirebaseInitProvider</c> de la biblioteca intenta arrancar solo al abrir el proceso,
/// no encuentra los recursos del JSON y lo deja estar: la app por defecto la crea este método.</para>
/// </remarks>
internal static class FirebaseSetup
{
    private static readonly object Gate = new();

    /// <summary>Verdadero si Firebase quedó inicializado en este proceso.</summary>
    public static bool IsInitialized { get; private set; }

    public static void Initialize(Context context)
    {
        if (!FamilyTogetherConfig.IsPushConfigured)
            return;

        lock (Gate)
        {
            if (IsInitialized)
                return;

            try
            {
                // Si ya existe (por ejemplo, porque alguien añadió el JSON), se reutiliza.
                if (FirebaseApp.GetApps(context).Count > 0)
                {
                    IsInitialized = true;
                    return;
                }

                var options = new FirebaseOptions.Builder()
                    .SetProjectId(FamilyTogetherConfig.FcmProjectId)
                    .SetApplicationId(FamilyTogetherConfig.FcmApplicationId)
                    .SetApiKey(FamilyTogetherConfig.FcmApiKey)
                    .SetGcmSenderId(FamilyTogetherConfig.FcmSenderId)
                    .Build();

                FirebaseApp.InitializeApp(context, options);
                IsInitialized = true;
            }
            catch (Exception ex)
            {
                // Valores mal copiados o Play Services ausente: sin push, con consulta periódica.
                NativeLog.Warn("No se pudo inicializar Firebase; los avisos irán por consulta.", ex);
            }
        }
    }
}
