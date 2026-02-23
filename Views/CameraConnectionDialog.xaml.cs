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
        
        private static readonly Dictionary<string, string> RtspTemplates = new()
        {
            ["Z30T"] = "rtsp://{0}:{1}/chn0",
            ["Z30D"] = "rtsp://{0}:{1}/chn0",
            ["Z40DT"] = "rtsp://{0}:{1}/chn0",
            ["Custom"] = "rtsp://{0}:{1}"
        };

        private static readonly Dictionary<string, int> TcpPorts = new()
        {
            ["Z30T"] = 2000,
            ["Z30D"] = 2000,
            ["Z40DT"] = 2000,
            ["Custom"] = 2000
        };

        private static readonly Dictionary<string, int> RtspPorts = new()
        {
            ["Z30T"] = 554,
            ["Z30D"] = 554,
            ["Z40DT"] = 554,
            ["Custom"] = 554
        };

        private static readonly Dictionary<string, string> CameraDescriptions = new()
        {
            ["Z30T"] = "📷 30x Zoom | Sony Sensor | 🎯 Tracking | 🔥 IR | 📏 LRF",
            ["Z30D"] = "📷 30x Zoom | Dual Sensor | 🔥 IR | 📏 LRF",
            ["Z40DT"] = "📷 40x Zoom | Dual Sensor | 🔥 IR | 📏 LRF",
            ["Custom"] = "Пользовательская конфигурация"
        };

        private static readonly Dictionary<string, string> DefaultIPs = new()
        {
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

        private void PresetComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_isInitialized) return;
            if (TcpPortTextBox == null || RtspUrlTextBox == null) return;

            if (PresetComboBox?.SelectedItem is ComboBoxItem item)
            {
                string preset = item.Tag?.ToString() ?? "Z30T";

                if (DefaultIPs.TryGetValue(preset, out string defaultIP))
                    IpAddressTextBox.Text = defaultIP;

                if (TcpPorts.TryGetValue(preset, out int tcpPort))
                    TcpPortTextBox.Text = tcpPort.ToString();

                if (RtspPorts.TryGetValue(preset, out int rtspPort))
                    RtspPortTextBox.Text = rtspPort.ToString();

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

            string preset = (PresetComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Z30T";
            string ip = IpAddressTextBox.Text?.Trim() ?? "192.168.144.68";
            string rtspPort = RtspPortTextBox.Text?.Trim() ?? "554";
            if (string.IsNullOrEmpty(rtspPort)) rtspPort = "554";

            if (RtspTemplates.TryGetValue(preset, out string template))
            {
                RtspUrlTextBox.Text = string.Format(template, ip, rtspPort);
            }
        }

        private void LoadSavedSettings()
        {
            try
            {
                if (IpAddressTextBox == null || TcpPortTextBox == null ||
                    RtspPortTextBox == null || PresetComboBox == null) return;

                var settings = Properties.Settings.Default;

                string savedPreset = !string.IsNullOrEmpty(settings.CameraPreset)
                    ? settings.CameraPreset : "Z30T";

                foreach (ComboBoxItem item in PresetComboBox.Items)
                {
                    if (item.Tag?.ToString() == savedPreset)
                    {
                        PresetComboBox.SelectedItem = item;
                        break;
                    }
                }

                if (!string.IsNullOrEmpty(settings.CameraIP))
                    IpAddressTextBox.Text = settings.CameraIP;
                else if (DefaultIPs.TryGetValue(savedPreset, out string defaultIP))
                    IpAddressTextBox.Text = defaultIP;

                if (settings.CameraTcpPort > 0)
                    TcpPortTextBox.Text = settings.CameraTcpPort.ToString();

                if (settings.CameraRtspPort > 0)
                    RtspPortTextBox.Text = settings.CameraRtspPort.ToString();
                else
                    RtspPortTextBox.Text = "554";

                if (CameraDescriptions.TryGetValue(savedPreset, out string desc))
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
                settings.CameraIP = IpAddressTextBox.Text?.Trim() ?? "192.168.144.68";
                settings.CameraTcpPort = int.TryParse(TcpPortTextBox.Text, out int tcp) ? tcp : 2000;
                settings.CameraRtspPort = int.TryParse(RtspPortTextBox.Text, out int rtsp) ? rtsp : 554;
                settings.CameraPreset = (PresetComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Z30T";
                settings.Save();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CameraDialog] SaveSettings: {ex.Message}");
            }
        }

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
                RtspUrl = RtspUrlTextBox.Text?.Trim() ?? $"rtsp://{ip}:554/chn0",
                CameraType = (PresetComboBox?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Z30T"
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
        public string CameraIP { get; set; } = "192.168.144.68";
        public int TcpPort { get; set; } = 2000;
        public string RtspUrl { get; set; } = "rtsp://192.168.144.68:554/chn0";
        public string CameraType { get; set; } = "Z30T";
    }
}
