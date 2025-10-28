using FamilyTogether.App.Models;

namespace FamilyTogether.App.Services;

public class NotificationService
{
    private readonly List<StatusNotification> _notifications = new();
    private readonly int _maxNotifications = 50;

    public event EventHandler<StatusNotification>? NotificationAdded;

    public void AddStatusChangeNotification(MemberStatusChange statusChange)
    {
        var notification = new StatusNotification
        {
            Id = Guid.NewGuid(),
            Title = GetNotificationTitle(statusChange),
            Message = GetNotificationMessage(statusChange),
            Type = GetNotificationType(statusChange.ChangeType),
            Timestamp = statusChange.Timestamp,
            UserId = statusChange.UserId,
            UserName = statusChange.UserName,
            IsRead = false
        };

        // Agregar al inicio de la lista
        _notifications.Insert(0, notification);

        // Mantener solo las últimas notificaciones
        if (_notifications.Count > _maxNotifications)
        {
            _notifications.RemoveRange(_maxNotifications, _notifications.Count - _maxNotifications);
        }

        NotificationAdded?.Invoke(this, notification);
        System.Diagnostics.Debug.WriteLine($"Notification added: {notification.Title} - {notification.Message}");
    }

    private string GetNotificationTitle(MemberStatusChange statusChange)
    {
        return statusChange.ChangeType switch
        {
            StatusChangeType.CameOnline => "Miembro conectado",
            StatusChangeType.WentOffline => "Miembro desconectado",
            StatusChangeType.BecameActive => "Miembro activo",
            StatusChangeType.BecameInactive => "Miembro inactivo",
            _ => "Cambio de estado"
        };
    }

    private string GetNotificationMessage(MemberStatusChange statusChange)
    {
        return statusChange.ChangeType switch
        {
            StatusChangeType.CameOnline => $"{statusChange.UserName} se ha conectado",
            StatusChangeType.WentOffline => $"{statusChange.UserName} se ha desconectado",
            StatusChangeType.BecameActive => $"{statusChange.UserName} está activo nuevamente",
            StatusChangeType.BecameInactive => $"{statusChange.UserName} ha estado inactivo por más de 30 minutos",
            _ => $"{statusChange.UserName} cambió de {statusChange.PreviousStatus} a {statusChange.NewStatus}"
        };
    }

    private NotificationType GetNotificationType(StatusChangeType changeType)
    {
        return changeType switch
        {
            StatusChangeType.CameOnline => NotificationType.Positive,
            StatusChangeType.BecameActive => NotificationType.Positive,
            StatusChangeType.WentOffline => NotificationType.Warning,
            StatusChangeType.BecameInactive => NotificationType.Warning,
            _ => NotificationType.Info
        };
    }

    public List<StatusNotification> GetNotifications()
    {
        return new List<StatusNotification>(_notifications);
    }

    public List<StatusNotification> GetUnreadNotifications()
    {
        return _notifications.Where(n => !n.IsRead).ToList();
    }

    public void MarkAsRead(Guid notificationId)
    {
        var notification = _notifications.FirstOrDefault(n => n.Id == notificationId);
        if (notification != null)
        {
            notification.IsRead = true;
        }
    }

    public void MarkAllAsRead()
    {
        foreach (var notification in _notifications)
        {
            notification.IsRead = true;
        }
    }

    public void ClearNotifications()
    {
        _notifications.Clear();
    }

    public int GetUnreadCount()
    {
        return _notifications.Count(n => !n.IsRead);
    }
}

public class StatusNotification
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public NotificationType Type { get; set; }
    public DateTime Timestamp { get; set; }
    public int UserId { get; set; }
    public string UserName { get; set; } = string.Empty;
    public bool IsRead { get; set; }
}

public enum NotificationType
{
    Info,
    Positive,
    Warning,
    Error
}