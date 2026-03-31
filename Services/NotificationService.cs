using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Media;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using SimpleDroneGCS.Models;

namespace SimpleDroneGCS.Services
{
    public sealed class NotificationService : INotificationService
    {
        private static readonly Lazy<NotificationService> _lazy =
            new(() => new NotificationService(), isThreadSafe: true);

        public static NotificationService Instance => _lazy.Value;

        public ObservableCollection<NotificationToast> Toasts { get; } = new();
        public ObservableCollection<NotificationRecord> History { get; } = new();

        private int _unreadCount;
        public int UnreadCount
        {
            get => _unreadCount;
            private set
            {
                _unreadCount = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasUnread));
                OnPropertyChanged(nameof(UnreadLabel));
            }
        }

        public bool HasUnread => UnreadCount > 0;
        public string UnreadLabel => UnreadCount > 9 ? "9+" : UnreadCount.ToString();

        private const int MaxToasts = 3;
        private const int MaxHistory = 100;
        private const int SpamCooldownSec = 15;

        private static readonly IReadOnlyDictionary<NotificationType, int> ToastLifetimeSec =
            new Dictionary<NotificationType, int>
            {
                [NotificationType.Error] = 10,
                [NotificationType.Warning] = 7,
                [NotificationType.Success] = 5,
                [NotificationType.Info] = 5,
            };

        private readonly Dictionary<string, DateTime> _spamCache = new();
        private readonly object _spamLock = new();
        private readonly HashSet<string> _historyKeys = new();
        private NotificationService() { }

        public void Info(string message) => Show(message, NotificationType.Info);
        public void Success(string message) => Show(message, NotificationType.Success);
        public void Warning(string message) => Show(message, NotificationType.Warning);
        public void Error(string message) => Show(message, NotificationType.Error);

        public void Hud(string message, NotificationType type)
        {
            Show(message, type);                                    // в журнал
            GetDispatcher()?.BeginInvoke(() =>
                HudRequested?.Invoke(message, type));              // в HUD баннер
        }

        public void Show(string message, NotificationType type = NotificationType.Info)
        {
            if (string.IsNullOrWhiteSpace(message)) return;
            if (IsSpam(message)) return;

            GetDispatcher()?.BeginInvoke(() => ShowOnUiThread(message, type));
        }

        public void DismissToast(NotificationToast toast)
        {
            if (toast == null) return;
            GetDispatcher()?.BeginInvoke(() => Toasts.Remove(toast));
        }

        public void MarkAllRead() => GetDispatcher()?.BeginInvoke(() => UnreadCount = 0);
        public void ClearSpamCache() { lock (_spamLock) _spamCache.Clear(); }

        public void ClearHistory()
        {
            GetDispatcher()?.BeginInvoke(() =>
            {
                History.Clear();
                UnreadCount = 0;
            });
        }

        private void ShowOnUiThread(string message, NotificationType type)
        {
            try
            {
                string time = DateTime.Now.ToString("HH:mm:ss");
                string color = GetAccentColor(type);

                AddToast(message, type, time, color);
                AddToHistory(message, type, time, color);
                Task.Run(() => PlaySound(type));

                UnreadCount++;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Notification] {ex.Message}");
            }
        }

        private void AddToast(string message, NotificationType type, string time, string color)
        {
            while (Toasts.Count >= MaxToasts)
                Toasts.RemoveAt(0);

            var toast = new NotificationToast
            {
                Message = message,
                Type = type,
                TimeLabel = time,
                AccentColor = color,
            };
            Toasts.Add(toast);

            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(ToastLifetimeSec[type]) };
            timer.Tick += (_, _) => { timer.Stop(); Toasts.Remove(toast); };
            timer.Start();
        }

        private void AddToHistory(string message, NotificationType type, string time, string color)
        {
            string key = $"{(int)type}:{message}";

            if (_historyKeys.Contains(key))
            {
                // удаляем старую запись и вставляем наверх с новым временем
                for (int i = 0; i < History.Count; i++)
                {
                    if (History[i].Message == message && History[i].Type == type)
                    {
                        History.RemoveAt(i);
                        break;
                    }
                }
            }
            else
            {
                _historyKeys.Add(key);
                if (History.Count >= MaxHistory)
                {
                    // удаляем самую старую запись и её ключ из HashSet
                    _historyKeys.Remove($"{(int)History[History.Count - 1].Type}:{History[History.Count - 1].Message}");
                    History.RemoveAt(History.Count - 1);
                }
            }

            History.Insert(0, new NotificationRecord
            {
                Message = message,
                Type = type,
                TimeLabel = time,
                AccentColor = color,
                TypeIcon = GetTypeIcon(type),
            });
        }

        private bool IsSpam(string message)
        {
            lock (_spamLock)
            {
                if (_spamCache.TryGetValue(message, out var last) &&
                    (DateTime.Now - last).TotalSeconds < SpamCooldownSec)
                    return true;

                _spamCache[message] = DateTime.Now;
                return false;
            }
        }

        private static void PlaySound(NotificationType type)
        {
            try
            {
                if (type == NotificationType.Error) SystemSounds.Hand.Play();
                else if (type == NotificationType.Warning) SystemSounds.Asterisk.Play();
            }
            catch { }
        }

        private static Dispatcher GetDispatcher() => Application.Current?.Dispatcher;

        private static string GetAccentColor(NotificationType type) => type switch
        {
            NotificationType.Success => "#98F019",
            NotificationType.Warning => "#FBC92B",
            NotificationType.Error => "#EF4444",
            _ => "#3B82F6",
        };

        private static string GetTypeIcon(NotificationType type) => type switch
        {
            NotificationType.Success => "✓",
            NotificationType.Warning => "!",
            NotificationType.Error => "✕",
            _ => "i",
        };

        public event PropertyChangedEventHandler PropertyChanged;
        public event System.Action<string, NotificationType> HudRequested;

        private void OnPropertyChanged([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}