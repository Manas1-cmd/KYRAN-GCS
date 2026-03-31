using SimpleDroneGCS.Controls;
using SimpleDroneGCS.Models;
using SimpleDroneGCS.Services;
using SimpleDroneGCS.UI.Dialogs;
using System.ComponentModel;
using System.Windows.Media.Animation;
using SimpleDroneGCS.Views;
using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;
using System.Management;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

using static SimpleDroneGCS.Helpers.Loc;

namespace SimpleDroneGCS
{
    public partial class MainWindow : Window
    {
        private bool _isConnected = false;
        private Button _activeNavButton = null;

        private FlightPlanView _flightPlanView;

        private CameraWindow _cameraWindow;

        public MAVLinkService MAVLink { get; private set; }
        private FlightLogService _flightLogService;

        public MainWindow()
        {
            System.Diagnostics.Debug.WriteLine("[MainWindow] Constructor called");
            InitializeComponent();
            System.Diagnostics.Debug.WriteLine("[MainWindow] InitializeComponent completed");

            InitializeMAVLink();
            InitializeNotifications();
            NavigateToPage(FlightPlanButton);
        }

        private void InitializeNotifications()
        {
            var svc = NotificationService.Instance;
            NotificationPanel.ItemsSource = svc.Toasts;
            HistoryList.ItemsSource = svc.History;

            svc.PropertyChanged += OnNotificationServicePropertyChanged;
            svc.History.CollectionChanged += (_, _) => UpdateHistoryEmpty();
        }

        private void OnNotificationServicePropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(INotificationService.HasUnread)
                               or nameof(INotificationService.UnreadLabel))
            {
                Dispatcher.BeginInvoke(UpdateBellBadge);
            }
        }

        private void UpdateBellBadge()
        {
            var svc = NotificationService.Instance;
            BellBadge.Visibility = svc.HasUnread ? Visibility.Visible : Visibility.Collapsed;
            BellBadgeText.Text = svc.UnreadLabel;
        }

        private void UpdateHistoryEmpty()
        {
            if (HistoryEmptyText == null) return;
            HistoryEmptyText.Visibility = NotificationService.Instance.History.Count == 0
                ? Visibility.Visible : Visibility.Collapsed;
        }


        private bool _historyAnimating = false;
        private bool _historyOpen = false;

        private void BellButton_Click(object sender, RoutedEventArgs e)
        {
            if (_historyAnimating) return; 

            if (_historyOpen) CloseHistory();
            else OpenHistory();
        }

        private void OpenHistory()
        {
            _historyOpen = true;
            _historyAnimating = true;
            HistoryPanel.Visibility = Visibility.Visible;

            NotificationService.Instance.MarkAllRead();
            UpdateBellBadge();
            UpdateHistoryEmpty();

            AnimateHistoryPanel(from: 272, to: 0, onComplete: () => _historyAnimating = false);
        }

        private void CloseHistory()
        {
            _historyAnimating = true;
            AnimateHistoryPanel(from: 0, to: 272, onComplete: () =>
            {
                _historyOpen = false;
                _historyAnimating = false;
                HistoryPanel.Visibility = Visibility.Collapsed;
            });
        }

        private void AnimateHistoryPanel(double from, double to, Action onComplete = null)
        {
            var anim = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(200))
            {
                EasingFunction = new CubicEase
                {
                    EasingMode = to == 0 ? EasingMode.EaseOut : EasingMode.EaseIn
                }
            };
            if (onComplete != null)
                anim.Completed += (_, _) => onComplete();

            HistorySlide.BeginAnimation(TranslateTransform.XProperty, anim);
        }

        private void HistoryClose_Click(object sender, RoutedEventArgs e) => CloseHistory();

        private void HistoryClear_Click(object sender, RoutedEventArgs e)
        {
            NotificationService.Instance.ClearHistory();
            UpdateBellBadge();
        }

        private void DismissNotification_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: NotificationToast toast })
                NotificationService.Instance.DismissToast(toast);
        }

        private void InitializeMAVLink()
        {
            MAVLink = new MAVLinkService();
            MAVLink.ConnectionStatusChanged_Bool += OnConnectionStatusChanged;
            MAVLink.TelemetryReceived += OnTelemetryReceived;
            MAVLink.ErrorOccurred += (s, msg) => NotificationService.Instance.Error(msg);
            MAVLink.OnStatusTextReceived += (msg) => NotificationService.Instance.Info(msg);
            MAVLink.ConnectionStatusChanged += (s, msg) => NotificationService.Instance.Info(msg);

            _flightLogService = new FlightLogService(NotificationService.Instance);
            _flightLogService.Attach(MAVLink);
        }

        private void OnConnectionStatusChanged(object sender, bool isConnected)
        {
            Dispatcher.Invoke(() =>
            {
                _isConnected = isConnected;
                UpdateConnectionUI();
            });
        }

        private void OnTelemetryReceived(object sender, EventArgs e)
        {

        }

        private void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isConnected)
            {
                DisconnectFromDrone();
            }
            else
            {
                ConnectToDrone();
            }
        }

        private void ConnectToDrone()
        {
            try
            {
                var result = AppMessageBox.ShowYesNoCancel(
                    "Выберите тип подключения:",
                    owner: this,
                    yesText: "UDP",
                    noText: "COM порт"
                );

                if (result == AppMessageBoxResult.Yes)
                {
                    var dialog = new UdpConnectionDialog();
                    dialog.Owner = this;
                    dialog.ShowDialog();

                    if (dialog.IsConfirmed)
                    {
                        if (!string.IsNullOrEmpty(dialog.HostIp) && dialog.HostPort.HasValue)
                        {
                            MAVLink.ConnectUDP(dialog.LocalIp, dialog.LocalPort, dialog.HostIp, dialog.HostPort.Value);
                        }
                        else
                        {
                            MAVLink.ConnectUDP(dialog.LocalIp, dialog.LocalPort);
                        }
                    }
                }
                else if (result == AppMessageBoxResult.No)
                {
                    var dialog = new ComPortDialog();
                    dialog.Owner = this;
                    dialog.ShowDialog();

                    if (dialog.IsConfirmed)
                    {
                        MAVLink.Connect(dialog.SelectedPort, dialog.SelectedBaudRate);
                    }
                }
            }
            catch (Exception ex)
            {
                AppMessageBox.ShowError(Fmt("Msg_ErrorConnection", ex.Message), owner: this);
            }
        }

        private void DisconnectFromDrone()
        {
            MAVLink.Disconnect();
        }

        private void UpdateConnectionUI()
        {
            if (_isConnected)
            {
                ConnectionIndicator.Fill = new SolidColorBrush(Color.FromRgb(34, 197, 94));
                ConnectionStatusText.Text = Get("Connected");
            }
            else
            {
                ConnectionIndicator.Fill = new SolidColorBrush(Color.FromRgb(239, 68, 68));
                ConnectionStatusText.Text = Get("NotConnected");
            }
        }

        private void NavigationButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button)
                NavigateToPage(button);
        }

        private void NavigateToPage(Button button)
        {
            if (_activeNavButton != null)
                _activeNavButton.Style = (Style)FindResource("NavButtonStyle");

            button.Style = (Style)FindResource("ActiveNavButtonStyle");
            _activeNavButton = button;

            try
            {
                switch (button.Tag?.ToString())
                {
                    case "FlightPlan":
                        _flightPlanView ??= new FlightPlanView(MAVLink);
                        MainFrame.Navigate(_flightPlanView);
                        break;
                }
            }
            catch (Exception ex)
            {
                AppMessageBox.ShowError(
                    $"Ошибка навигации: {ex.Message}",
                    owner: this,
                    subtitle: Get("Msg_ErrorSub")
                );
            }
        }

        private void CameraButton_Click(object sender, RoutedEventArgs e)
        {

            if (_cameraWindow != null && _cameraWindow.IsLoaded)
            {
                _cameraWindow.Activate();
                _cameraWindow.WindowState = WindowState.Normal;
                return;
            }

            var dialog = new CameraConnectionDialog();
            dialog.Owner = this;

            if (dialog.ShowDialog() == true && dialog.ConnectionSettings != null)
            {
                _cameraWindow = new CameraWindow(dialog.ConnectionSettings, MAVLink);
                _cameraWindow.Closed += (s, args) => _cameraWindow = null;
                _cameraWindow.Show();
            }
        }

        private void LandButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_isConnected)
            {
                AppMessageBox.ShowWarning(
                    Get("Msg_DroneNotConnectedDot"),
                    owner: this,
                    subtitle: Get("Msg_ErrorSub")
                );
                return;
            }

            if (AppMessageBox.ShowConfirm(Get("Msg_EmergencyLand"), owner: this, subtitle: Get("Msg_Warning")))
            {
                MAVLink.SendLand();
            }
        }

        private void RthButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_isConnected)
            {
                AppMessageBox.ShowWarning(
                    Get("Msg_DroneNotConnectedDot"),
                    owner: this,
                    subtitle: Get("Msg_ErrorSub")
                );
                return;
            }

            if (AppMessageBox.ShowConfirm(Get("Msg_ReturnHome"), owner: this, subtitle: Get("MsgBox_Confirm")))
            {
                MAVLink.ReturnToLaunch();
            }
        }

        protected override void OnClosed(EventArgs e)
        {

            MAVLink?.Disconnect();
            _flightLogService?.Detach();
            base.OnClosed(e);
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                MaximizeButton_Click(null, null);
            }
            else
            {
                this.DragMove();
            }
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            this.WindowState = WindowState.Minimized;
        }

        private void MaximizeButton_Click(object sender, RoutedEventArgs e)
        {
            this.WindowState = this.WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

    }
}