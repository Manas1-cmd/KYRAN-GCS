using System;

namespace SimpleDroneGCS.Models
{
    public enum NotificationType { Info, Success, Warning, Error }

    public class NotificationToast
    {
        public string Message { get; init; }
        public string TimeLabel { get; init; }
        public string AccentColor { get; init; }
        public NotificationType Type { get; init; }
    }

    public class NotificationRecord
    {
        public string Message { get; init; }
        public string TimeLabel { get; init; }
        public string AccentColor { get; init; }
        public string TypeIcon { get; init; }
        public NotificationType Type { get; init; }
    }
}