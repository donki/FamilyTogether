namespace FamilyTogether.Mobile.Services;

// Lo que la interfaz necesita de la parte nativa de Android. Las implementaciones viven en
// Platforms\Android (LocationSharing, PushService y Notifier) y se registran en MauiProgram.

/// <summary>Permiso de ubicacion concedido: ninguno, solo con la app en uso, o siempre.</summary>
public enum LocationPermissionState { Denied, WhileInUse, Always }

/// <summary>
/// El servicio de ubicacion en primer plano (tipo <c>location</c>) y los ajustes del sistema que lo
/// dejan vivir: permisos, ahorro de bateria y el inicio automatico de cada fabricante.
/// </summary>
public interface ILocationSharing
{
    Task<LocationPermissionState> CheckPermissionAsync();

    /// <summary>Pide la ubicacion precisa y luego la de segundo plano.</summary>
    Task<LocationPermissionState> RequestPermissionAsync();

    void Start();

    void Stop();

    bool IsRunning { get; }

    /// <summary>Para el SOS: la posicion de ahora o, sin GPS, la ultima conocida (<c>Stale</c>).</summary>
    Task<(double Lat, double Lon, double Accuracy, DateTimeOffset At, bool Stale)?> GetCurrentOrLastAsync();

    bool IsIgnoringBatteryOptimizations { get; }

    void OpenBatteryOptimizationSettings();

    void OpenAppSettings();

    /// <summary>Texto localizado para Xiaomi, Huawei, Samsung…, o <c>null</c> si no hace falta.</summary>
    string? ManufacturerAutostartHint { get; }

    void OpenManufacturerAutostartSettings();
}

/// <summary>Firebase Cloud Messaging (solo Messaging).</summary>
public interface IPushService
{
    bool IsConfigured { get; }

    Task<string?> GetTokenAsync();
}

/// <summary>Avisos del sistema: SOS, zonas y solicitudes.</summary>
public interface INotifier
{
    bool AreEnabled { get; }

    Task<bool> RequestPermissionAsync();

    void Show(FamilyTogether.Core.NotificationContent content);
}
