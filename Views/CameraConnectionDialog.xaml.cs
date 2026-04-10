using SimpleDroneGCS.UI.Dialogs;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SimpleDroneGCS.Views
{
    public partial class CameraConnectionDialog : Window
    {

        private static readonly string RTSP_TEMPLATE = "rtsp://{0}:{1}";
        private const int DEFAULT_TCP_PORT = 2000;
        private const int DEFAULT_RTSP_PORT = 554;
        private const string DEFAULT_IP = "192.168.2.119";

        private bool _isInitialized = false;

        public CameraConnectionSettings ConnectionSettings { get; private set; }

        public CameraConnectionDialog()
        {
            InitializeComponent();
            LoadSavedSettings();
            _isInitialized = true;
            UpdateRtspUrl();
        }

        private void IpAddress_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isInitialized) UpdateRtspUrl();
        }

        private void RtspPort_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isInitialized) UpdateRtspUrl();
        }

        private void UpdateRtspUrl()
        {
            if (!_isInitialized || IpAddressTextBox == null || RtspUrlTextBox == null) return;
            string ip = IpAddressTextBox.Text?.Trim() ?? DEFAULT_IP;
            string port = RtspPortTextBox?.Text?.Trim() ?? DEFAULT_RTSP_PORT.ToString();
            RtspUrlTextBox.Text = string.Format(RTSP_TEMPLATE, ip, port);
        }

        private void LoadSavedSettings()
        {
            try
            {
                var settings = Properties.Settings.Default;
                IpAddressTextBox.Text = !string.IsNullOrEmpty(settings.CameraIP)
                    ? settings.CameraIP : DEFAULT_IP;
                TcpPortTextBox.Text = settings.CameraTcpPort > 0
                    ? settings.CameraTcpPort.ToString() : DEFAULT_TCP_PORT.ToString();
                RtspPortTextBox.Text = settings.CameraRtspPort > 0
                    ? settings.CameraRtspPort.ToString() : DEFAULT_RTSP_PORT.ToString();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CameraDialog] LoadSettings: {ex.Message}");
                IpAddressTextBox.Text = DEFAULT_IP;
                TcpPortTextBox.Text = DEFAULT_TCP_PORT.ToString();
                RtspPortTextBox.Text = DEFAULT_RTSP_PORT.ToString();
            }
        }

        private void SaveSettings()
        {
            try
            {
                var settings = Properties.Settings.Default;
                settings.CameraIP = IpAddressTextBox.Text?.Trim() ?? DEFAULT_IP;
                settings.CameraTcpPort = int.TryParse(TcpPortTextBox.Text, out int tcp) ? tcp : DEFAULT_TCP_PORT;
                settings.CameraRtspPort = int.TryParse(RtspPortTextBox.Text, out int rtsp) ? rtsp : DEFAULT_RTSP_PORT;
                settings.Save();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CameraDialog] SaveSettings: {ex.Message}");
            }
        }

        private void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            string ip = IpAddressTextBox.Text?.Trim() ?? "";
            if (string.IsNullOrEmpty(ip))
            {
                AppMessageBox.ShowWarning("Введите IP адрес камеры", owner: this);
                IpAddressTextBox.Focus();
                return;
            }

            if (!int.TryParse(TcpPortTextBox.Text, out int tcpPort) || tcpPort <= 0 || tcpPort > 65535)
            {
                AppMessageBox.ShowWarning("Неверный TCP порт", owner: this);
                TcpPortTextBox.Focus();
                return;
            }

            ConnectionSettings = new CameraConnectionSettings
            {
                CameraIP = ip,
                TcpPort = tcpPort,
                RtspUrl = RtspUrlTextBox.Text?.Trim() ?? string.Format(RTSP_TEMPLATE, ip, DEFAULT_RTSP_PORT),
                CameraType = "ViewPro"
            };

            SaveSettings();
            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 1) DragMove();
        }

    }

    public class CameraConnectionSettings
    {
        public string CameraIP { get; set; } = "192.168.2.119";
        public int TcpPort { get; set; } = 2000;
        public string RtspUrl { get; set; } = "rtsp://192.168.2.119:554";
        public string CameraType { get; set; } = "ViewPro";
    }
}