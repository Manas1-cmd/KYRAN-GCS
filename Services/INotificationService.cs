using System.Collections.ObjectModel;
using System.ComponentModel;
using SimpleDroneGCS.Models;

namespace SimpleDroneGCS.Services
{
    public interface INotificationService : INotifyPropertyChanged
    {
        ObservableCollection<NotificationToast> Toasts { get; }
        ObservableCollection<NotificationRecord> History { get; }
        int UnreadCount { get; }
        bool HasUnread { get; }
        string UnreadLabel { get; }

        void Info(string message);
        void Success(string message);
        void Warning(string message);
        void Error(string message);
        void Show(string message, NotificationType type = NotificationType.Info);
        void Hud(string message, NotificationType type);

        event System.Action<string, NotificationType> HudRequested;

        void DismissToast(NotificationToast toast);
        void MarkAllRead();
        void ClearHistory();
        void ClearSpamCache();
    }
}