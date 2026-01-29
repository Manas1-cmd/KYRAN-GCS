using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SimpleDroneGCS.Views
{
    public partial class CameraConnectionDialog : Window
    {
        // RTSP URL шаблоны из мануала TRA
        private static readonly Dictionary<string, string> RtspTemplates = new()
        {
            ["Z30T"] = "rtsp://{0}:{1}/chn0",
            ["Z30D"] = "rtsp://{0}:{1}/chn0",
            ["Z40DT"] = "rtsp://{0}:{1}/chn0",
            ["G40"] = "rtsp://{0}:{1}/live/1_0",
            ["G40FH"] = "rtsp://{0}:{1}/chn0",
            ["Custom"] = "rtsp://{0}:{1}/chn0"
        };

        // TCP порты по типу камеры
        private static readonly Dictionary<string, int> TcpPorts = new()
        {
            ["Z30T"] = 2000,
            ["Z30D"] = 2000,
            ["Z40DT"] = 2000,
            ["G40"] = 2000,
            ["G40FH"] = 2000,
            ["Custom"] = 2000
        };

        // Флаг инициализации
        private bool _isInitialized = false;

        /// <summary>
        /// Результат - настройки подключения
        /// </summary>
        public CameraConnectionSettings ConnectionSettings { get; private set; }

        public CameraConnectionDialog()
        {
            InitializeComponent();

            // Загружаем сохранённые настройки
            LoadSavedSettings();

            // Устанавливаем флаг - теперь контролы готовы
            _isInitialized = true;

            // Обновляем RTSP URL
            UpdateRtspUrl();
        }

        #region Preset Selection

        private void PresetComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Защита от вызова до инициализации
            if (!_isInitialized) return;
            if (TcpPortTextBox == null || RtspUrlTextBox == null) return;

            if (PresetComboBox?.SelectedItem is ComboBoxItem item)
            {
                string preset = item.Tag?.ToString() ?? "Z30T";

                // Обновляем TCP порт
                if (TcpPorts.TryGetValue(preset, out int port))
                {
                    TcpPortTextBox.Text = port.ToString();
                }

                // Обновляем RTSP URL
                UpdateRtspUrl();

                // Для Custom разрешаем редактирование всех полей
                bool isCustom = preset == "Custom";
                RtspUrlTextBox.IsReadOnly = !isCustom;
            }
        }

        private void UpdateRtspUrl()
        {
            // Защита от null
            if (!_isInitialized) return;
            if (PresetComboBox == null || IpAddressTextBox == null ||
                RtspPortTextBox == null || RtspUrlTextBox == null) return;

            string preset = (PresetComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Z30T";
            string ip = IpAddressTextBox.Text?.Trim() ?? "192.168.144.68";
            string rtspPort = RtspPortTextBox.Text?.Trim() ?? "554";

            if (RtspTemplates.TryGetValue(preset, out string template))
            {
                RtspUrlTextBox.Text = string.Format(template, ip, rtspPort);
            }
        }

        #endregion

        #region Settings Persistence

        private void LoadSavedSettings()
        {
            try
            {
                // Проверяем что контролы существуют
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
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CameraDialog] LoadSettings error: {ex.Message}");
            }
        }

        private void SaveSettings()
        {
            try
            {
                // Проверяем что контролы существуют
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
                System.Diagnostics.Debug.WriteLine($"[CameraDialog] SaveSettings error: {ex.Message}");
            }
        }

        #endregion

        #region Buttons

        private void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            // Проверка контролов
            if (IpAddressTextBox == null || TcpPortTextBox == null || RtspUrlTextBox == null) return;

            // Валидация
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

            // Формируем настройки
            ConnectionSettings = new CameraConnectionSettings
            {
                CameraIP = ip,
                TcpPort = tcpPort,
                RtspUrl = RtspUrlTextBox.Text?.Trim() ?? $"rtsp://{ip}:554/chn0",
                CameraType = (PresetComboBox?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Z30T"
            };

            // Сохраняем для следующего раза
            SaveSettings();

            // Закрываем с успехом
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
            if (e.ClickCount == 1)
                DragMove();
        }

        #endregion
    }

    /// <summary>
    /// Настройки подключения камеры
    /// </summary>
    public class CameraConnectionSettings
    {
        public string CameraIP { get; set; } = "192.168.144.68";
        public int TcpPort { get; set; } = 2000;
        public string RtspUrl { get; set; } = "rtsp://192.168.144.68:554/chn0";
        public string CameraType { get; set; } = "Z30T";
    }
}
