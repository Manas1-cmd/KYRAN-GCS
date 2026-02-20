using SimpleDroneGCS.Controls;
using SimpleDroneGCS.Services;
using SimpleDroneGCS.UI.Dialogs;
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

        // Кэш страниц - создаются один раз
        private FlightPlanView _flightPlanView;

        private CameraWindow _cameraWindow;

        public MAVLinkService MAVLink { get; private set; }

        public MainWindow()
        {
            System.Diagnostics.Debug.WriteLine("[MainWindow] Constructor called");
            InitializeComponent();
            System.Diagnostics.Debug.WriteLine("[MainWindow] InitializeComponent completed");

            InitializeMAVLink();
            NavigateToPage(FlightPlanButton);
        }

        private void InitializeMAVLink()
        {
            MAVLink = new MAVLinkService();
            MAVLink.ConnectionStatusChanged_Bool += OnConnectionStatusChanged;
            MAVLink.TelemetryReceived += OnTelemetryReceived;
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
            // Данные обновляются автоматически через MAVLink.CurrentTelemetry
        }



        #region ПОДКЛЮЧЕНИЕ

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
            catch (Exception ex)
            {
                AppMessageBox.ShowError($"Ошибка подключения: {ex.Message}", owner: this);
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
                ConnectionIndicator.Fill = new SolidColorBrush(Color.FromRgb(34, 197, 94)); // Зелёный
                ConnectionStatusText.Text = Get("Connected");
            }
            else
            {
                ConnectionIndicator.Fill = new SolidColorBrush(Color.FromRgb(239, 68, 68)); // Красный
                ConnectionStatusText.Text = Get("NotConnected");
            }
        }

        #endregion

        #region НАВИГАЦИЯ

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

        #endregion

        #region КОМАНДЫ


        private void CameraButton_Click(object sender, RoutedEventArgs e)
        {
            // Если окно камеры уже открыто - фокусируемся на нём
            if (_cameraWindow != null && _cameraWindow.IsLoaded)
            {
                _cameraWindow.Activate();
                _cameraWindow.WindowState = WindowState.Normal;
                return;
            }

            // Открываем диалог настроек подключения
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

            if (AppMessageBox.ShowConfirm("Возврат домой?", owner: this, subtitle: "Подтверждение"))
            {
                MAVLink.SendRTL();
            }
        }

        #endregion

        #region ОКНО

        protected override void OnClosed(EventArgs e)
        {

            MAVLink?.Disconnect();
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

        #endregion
    }
}
