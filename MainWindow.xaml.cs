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

namespace SimpleDroneGCS
{
    public partial class MainWindow : Window
    {
        private DispatcherTimer _comPortRefreshTimer;
        private string _selectedComPort = null;
        private int _selectedBaudRate = 57600;
        private bool _isConnected = false;
        private Button _activeNavButton = null;

        // Кэш страниц - создаются один раз
        private FlightDataView _flightDataView;
        private FlightPlanView _flightPlanView;

        public MAVLinkService MAVLink { get; private set; }

        public MainWindow()
        {
            System.Diagnostics.Debug.WriteLine("[MainWindow] Constructor called");
            InitializeComponent();
            System.Diagnostics.Debug.WriteLine("[MainWindow] InitializeComponent completed");

            InitializeMAVLink();
            InitializeComPorts();
            SetupComPortAutoRefresh();
            NavigateToPage(FlightDataButton);
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

        #region COM PORT

        private Dictionary<string, string> _comPortDescriptions = new Dictionary<string, string>();
        private DateTime _lastWmiScan = DateTime.MinValue;

        private void InitializeComPorts()
        {
            RefreshComPortsList(false);
        }

        private void RefreshComPortsList(bool autoSelect)
        {
            try
            {
                var ports = SerialPort.GetPortNames().OrderBy(p => p).ToArray();
                string currentSelection = _selectedComPort;

                var selectedItem = ComPortComboBox.SelectedItem as ComboBoxItem;
                string previousSelection = selectedItem?.Content?.ToString();

                ComPortComboBox.Items.Clear();

                var placeholderItem = new ComboBoxItem
                {
                    Content = "COM Порт",
                    Tag = "placeholder"
                };
                ComPortComboBox.Items.Add(placeholderItem);

                var udpItem = new ComboBoxItem
                {
                    Content = "📡 UDP (14550)",
                    Tag = "UDP"
                };
                ComPortComboBox.Items.Add(udpItem);

                ComboBoxItem itemToSelect = null;
                foreach (var port in ports)
                {
                    string fullDescription = GetComPortDescription(port);

                    var item = new ComboBoxItem
                    {
                        Content = fullDescription,
                        Tag = port
                    };
                    ComPortComboBox.Items.Add(item);

                    if (port == previousSelection || port == currentSelection)
                    {
                        itemToSelect = item;
                    }
                }

                if (itemToSelect != null && ports.Contains(itemToSelect.Tag?.ToString()))
                {
                    ComPortComboBox.SelectedItem = itemToSelect;
                }
                else if (ports.Length > 0 && autoSelect)
                {
                    ComPortComboBox.SelectedIndex = 1;
                }
                else if (!ports.Contains(previousSelection) && previousSelection != "COM Порт")
                {
                    ComPortComboBox.SelectedIndex = 0;
                    _selectedComPort = null;
                }
                else
                {
                    ComPortComboBox.SelectedIndex = 0;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Ошибка обновления COM портов: {ex.Message}");
            }
        }

        private string GetComPortDescription(string portName)
        {
            if ((DateTime.Now - _lastWmiScan).TotalSeconds > 10 || _comPortDescriptions.Count == 0)
            {
                RefreshComPortDescriptions();
            }

            return _comPortDescriptions.ContainsKey(portName)
                ? _comPortDescriptions[portName]
                : portName;
        }

        private void RefreshComPortDescriptions()
        {
            _lastWmiScan = DateTime.Now;
            _comPortDescriptions.Clear();

            var allPorts = SerialPort.GetPortNames();
            foreach (var port in allPorts)
            {
                _comPortDescriptions[port] = port;
            }

            Task.Run(() =>
            {
                try
                {
                    using (var searcher = new ManagementObjectSearcher(
                        "SELECT * FROM Win32_PnPEntity WHERE Caption LIKE '%(COM%'"))
                    {
                        foreach (ManagementObject obj in searcher.Get())
                        {
                            string caption = obj["Caption"]?.ToString();
                            if (caption != null && caption.Contains("COM"))
                            {
                                var match = System.Text.RegularExpressions.Regex.Match(caption, @"COM\d+");
                                if (match.Success)
                                {
                                    string portName = match.Value;

                                    Dispatcher.Invoke(() =>
                                    {
                                        if (_comPortDescriptions.ContainsKey(portName))
                                        {
                                            _comPortDescriptions[portName] = caption;
                                        }
                                    });
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"⚠️ WMI ошибка: {ex.Message}");
                }
            });
        }

        private void ComPortComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ComPortComboBox.SelectedItem is ComboBoxItem item)
            {
                string portTag = item.Tag?.ToString();

                if (string.IsNullOrEmpty(portTag) || portTag == "placeholder")
                {
                    _selectedComPort = null;
                    return;
                }

                _selectedComPort = portTag;
                System.Diagnostics.Debug.WriteLine($"📍 COM порт выбран: {_selectedComPort}");
            }
        }

        private void ComPortComboBox_DropDownOpened(object sender, EventArgs e)
        {
            RefreshComPortsList(false);
        }

        private void SetupComPortAutoRefresh()
        {
            _comPortRefreshTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(5)
            };
            _comPortRefreshTimer.Tick += (s, e) =>
            {
                if (!ComPortComboBox.IsDropDownOpen &&
                    !_isConnected &&
                    (string.IsNullOrEmpty(_selectedComPort) || _selectedComPort == "COM Порт"))
                {
                    RefreshComPortsList(false);
                }
            };
            _comPortRefreshTimer.Start();
        }

        private void BaudRateComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (BaudRateComboBox.SelectedItem is ComboBoxItem item)
            {
                if (int.TryParse(item.Content.ToString(), out int baud))
                    _selectedBaudRate = baud;
            }
        }

        #endregion

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
            if (ComPortComboBox.SelectedItem is ComboBoxItem item)
            {
                string tag = item.Tag?.ToString();

                if (string.IsNullOrEmpty(tag) || tag == "placeholder")
                {
                    AppMessageBox.ShowWarning("Выберите тип подключения", owner: this);
                    return;
                }

                try
                {
                    if (tag == "UDP")
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
                    else
                    {
                        _selectedComPort = tag;
                        MAVLink.Connect(_selectedComPort, _selectedBaudRate);
                    }
                }
                catch (Exception ex)
                {
                    AppMessageBox.ShowError($"Ошибка: {ex.Message}", owner: this);
                }
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
                ConnectButton.Background = new SolidColorBrush(Color.FromRgb(239, 68, 68));
                ConnectionIndicator.Fill = new SolidColorBrush(Color.FromRgb(152, 240, 25));
                ConnectionStatusText.Text = "Подключено";
                _comPortRefreshTimer?.Stop();
            }
            else
            {
                ConnectButton.Background = new SolidColorBrush(Color.FromRgb(42, 67, 97));
                ConnectionIndicator.Fill = Brushes.Red;
                ConnectionStatusText.Text = "Не подключено";
                _comPortRefreshTimer?.Start();
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
                    case "FlightData":
                        _flightDataView ??= new FlightDataView(MAVLink);
                        MainFrame.Navigate(_flightDataView);
                        break;

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
                    subtitle: "Ошибка"
                );
            }
        }

        #endregion

        #region КОМАНДЫ

        private DispatcherTimer _spinnerTimer;

        private async void CameraButton_Click(object sender, RoutedEventArgs e)
        {
            string viewLinkPath = Properties.Settings.Default.ViewLinkPath;

            // Если путь не задан или файл не существует — спрашиваем
            if (string.IsNullOrEmpty(viewLinkPath) || !System.IO.File.Exists(viewLinkPath))
            {
                var dialog = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "Укажите путь к ViewLink.exe",
                    Filter = "ViewLink|ViewLink.exe|Исполняемые файлы|*.exe",
                    FileName = "ViewLink.exe"
                };

                if (dialog.ShowDialog() == true)
                {
                    viewLinkPath = dialog.FileName;
                    Properties.Settings.Default.ViewLinkPath = viewLinkPath;
                    Properties.Settings.Default.Save();
                }
                else
                {
                    return; // Отменил выбор
                }
            }

            try
            {
                ShowLoading("Запуск ViewLink...");

                await Task.Run(() =>
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = viewLinkPath,
                        UseShellExecute = true
                    });
                });

                await Task.Delay(1500);
                HideLoading();
            }
            catch (Exception ex)
            {
                HideLoading();

                // Сбрасываем путь если не удалось запустить
                Properties.Settings.Default.ViewLinkPath = "";
                Properties.Settings.Default.Save();

                AppMessageBox.ShowError(
                    $"Не удалось запустить ViewLink: {ex.Message}",
                    owner: this,
                    subtitle: "Ошибка запуска"
                );
            }
        }

        private void ShowLoading(string text = "Загрузка...")
        {
            LoadingText.Text = text;
            LoadingOverlay.Visibility = Visibility.Visible;

            // Запускаем анимацию вращения
            _spinnerTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(20)
            };
            _spinnerTimer.Tick += (s, e) =>
            {
                SpinnerRotate.Angle = (SpinnerRotate.Angle + 10) % 360;
            };
            _spinnerTimer.Start();
        }

        private void HideLoading()
        {
            _spinnerTimer?.Stop();
            _spinnerTimer = null;
            LoadingOverlay.Visibility = Visibility.Collapsed;
        }

        private void LandButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_isConnected)
            {
                AppMessageBox.ShowWarning(
                    "Дрон не подключен",
                    owner: this,
                    subtitle: "Ошибка"
                );
                return;
            }

            if (AppMessageBox.ShowConfirm("АВАРИЙНАЯ ПОСАДКА?", owner: this, subtitle: "ВНИМАНИЕ"))
            {
                MAVLink.SendLand();
            }
        }

        private void RthButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_isConnected)
            {
                AppMessageBox.ShowWarning(
                    "Дрон не подключен",
                    owner: this,
                    subtitle: "Ошибка"
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
            _comPortRefreshTimer?.Stop();
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
