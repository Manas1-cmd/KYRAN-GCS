using SimpleDroneGCS.Properties;
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SimpleDroneGCS.Views
{
    public partial class CameraConnectionDialog : Window
    {
        // RTSP URL шаблоны по модели камеры
        // ViewPro камеры: rtsp://IP:554/stream0 (стандартный RTSP порт 554)
        private static readonly Dictionary<string, string> RtspTemplates = new()
        {
            ["A40TR"] = "rtsp://{0}:554/stream0",
            ["A40TRPro"] = "rtsp://{0}:554/stream0",
            ["Q30TIR"] = "rtsp://{0}:554/stream0",
            ["U30TIR"] = "rtsp://{0}:554/stream0",
            ["Z30T"] = "rtsp://{0}:{1}/chn0",
            ["Z30D"] = "rtsp://{0}:{1}/chn0",
            ["Z40DT"] = "rtsp://{0}:{1}/chn0",
            ["Custom"] = "rtsp://{0}:{1}"
        };

        // TCP порты по типу камеры
        private static readonly Dictionary<string, int> TcpPorts = new()
        {
            ["A40TR"] = 2000,
            ["A40TRPro"] = 2000,
            ["Q30TIR"] = 2000,
            ["U30TIR"] = 2000,
            ["Z30T"] = 2000,
            ["Z30D"] = 2000,
            ["Z40DT"] = 2000,
            ["Custom"] = 2000
        };

        // Описания камер
        private static readonly Dictionary<string, string> CameraDescriptions = new()
        {
            ["A40TR"] = "📷 40x Zoom | 🔥 640×512 IR | 🎯 AI Tracking | 📏 LRF 3000m",
            ["A40TRPro"] = "📷 40x Zoom | 🔥 640×512 IR | 🎯 AI Tracking | 📏 LRF 5000m",
            ["Q30TIR"] = "📷 30x Zoom | 🔥 640×512 IR | 🎯 AI Tracking",
            ["U30TIR"] = "📷 30x Zoom | 🔥 256×192 IR | 🎯 AI Tracking",
            ["Z30T"] = "📷 30x Zoom | Sony Sensor",
            ["Z30D"] = "📷 30x Zoom | Dual Sensor",
            ["Z40DT"] = "📷 40x Zoom | Dual Sensor",
            ["Custom"] = "Пользовательская конфигурация"
        };

        private static readonly Dictionary<string, string> DefaultIPs = new()
        {
            ["A40TR"] = "192.168.2.119",
            ["A40TRPro"] = "192.168.2.119",
            ["Q30TIR"] = "192.168.2.119",
            ["U30TIR"] = "192.168.2.119",
            ["Z30T"] = "192.168.144.68",
            ["Z30D"] = "192.168.144.68",
            ["Z40DT"] = "192.168.144.68",
            ["Custom"] = "192.168.1.1"
        };

        private bool _isInitialized = false;

        public CameraConnectionSettings ConnectionSettings { get; private set; }

        public CameraConnectionDialog()
        {
            InitializeComponent();
            LoadSavedSettings();
            _isInitialized = true;
            UpdateRtspUrl();
        }

        #region Выбор пресета

        private void PresetComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_isInitialized) return;
            if (TcpPortTextBox == null || RtspUrlTextBox == null) return;

            if (PresetComboBox?.SelectedItem is ComboBoxItem item)
            {
                string preset = item.Tag?.ToString() ?? "A40TR";

                if (TcpPorts.TryGetValue(preset, out int port))
                    TcpPortTextBox.Text = port.ToString();

                if (CameraDescriptions.TryGetValue(preset, out string desc))
                    CameraInfoText.Text = desc;

                UpdateRtspUrl();

                bool isCustom = preset == "Custom";
                RtspUrlTextBox.IsReadOnly = !isCustom;
            }
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
            if (!_isInitialized) return;
            if (PresetComboBox == null || IpAddressTextBox == null ||
                RtspPortTextBox == null || RtspUrlTextBox == null) return;

            string preset = (PresetComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "A40TR";
            string ip = IpAddressTextBox.Text?.Trim() ?? "192.168.1.108";
            string rtspPort = RtspPortTextBox.Text?.Trim() ?? "554";
            if (string.IsNullOrEmpty(rtspPort)) rtspPort = "554";

            if (RtspTemplates.TryGetValue(preset, out string template))
            {
                RtspUrlTextBox.Text = string.Format(template, ip, rtspPort);
            }
        }

        #endregion

        #region Сохранение настроек

        private void LoadSavedSettings()
        {
            try
            {
                if (IpAddressTextBox == null || TcpPortTextBox == null ||
                    RtspPortTextBox == null || PresetComboBox == null) return;

                var settings = Properties.Settings.Default;

                if (!string.IsNullOrEmpty(settings.CameraIP))
                    IpAddressTextBox.Text = settings.CameraIP;

                if (settings.CameraTcpPort > 0)
                    TcpPortTextBox.Text = settings.CameraTcpPort.ToString();

                if (settings.CameraRtspPort > 0)
                    RtspPortTextBox.Text = settings.CameraRtspPort.ToString();

                if (!string.IsNullOrEmpty(settings.CameraPreset))
                {
                    foreach (ComboBoxItem item in PresetComboBox.Items)
                    {
                        if (item.Tag?.ToString() == settings.CameraPreset)
                        {
                            PresetComboBox.SelectedItem = item;
                            break;
                        }
                    }
                }

                string preset = (PresetComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "A40TR";
                if (CameraDescriptions.TryGetValue(preset, out string desc))
                    CameraInfoText.Text = desc;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CameraDialog] LoadSettings: {ex.Message}");
            }
        }

        private void SaveSettings()
        {
            try
            {
                if (IpAddressTextBox == null || TcpPortTextBox == null ||
                    RtspPortTextBox == null || PresetComboBox == null) return;

                var settings = Properties.Settings.Default;
                settings.CameraIP = IpAddressTextBox.Text?.Trim() ?? "192.168.1.108";
                settings.CameraTcpPort = int.TryParse(TcpPortTextBox.Text, out int tcp) ? tcp : 2000;
                settings.CameraRtspPort = int.TryParse(RtspPortTextBox.Text, out int rtsp) ? rtsp : 554;
                settings.CameraPreset = (PresetComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "A40TR";
                settings.Save();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CameraDialog] SaveSettings: {ex.Message}");
            }
        }

        #endregion

        #region Кнопки

        private void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            if (IpAddressTextBox == null || TcpPortTextBox == null || RtspUrlTextBox == null) return;

            string ip = IpAddressTextBox.Text?.Trim() ?? "";
            if (string.IsNullOrEmpty(ip))
            {
                MessageBox.Show("Введите IP адрес камеры", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
                IpAddressTextBox.Focus();
                return;
            }

            if (!int.TryParse(TcpPortTextBox.Text, out int tcpPort) || tcpPort <= 0 || tcpPort > 65535)
            {
                MessageBox.Show("Неверный TCP порт", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
                TcpPortTextBox.Focus();
                return;
            }

            ConnectionSettings = new CameraConnectionSettings
            {
                CameraIP = ip,
                TcpPort = tcpPort,
                RtspUrl = RtspUrlTextBox.Text?.Trim() ?? $"rtsp://{ip}:554/stream0",
                CameraType = (PresetComboBox?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "A40TR"
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

        #endregion
    }

    /// <summary>
    /// Настройки подключения камеры
    /// </summary>
    public class CameraConnectionSettings
    {
        public string CameraIP { get; set; } = "192.168.1.108";
        public int TcpPort { get; set; } = 2000;
        public string RtspUrl { get; set; } = "rtsp://192.168.1.108";
        public string CameraType { get; set; } = "A40TR";

        public bool IsViewProCamera => CameraType.StartsWith("A40") ||
                                        CameraType.StartsWith("Q") ||
                                        CameraType.StartsWith("U");
    }
}
