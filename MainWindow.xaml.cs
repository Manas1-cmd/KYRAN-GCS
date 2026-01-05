using SimpleDroneGCS.Services;
using SimpleDroneGCS.Views;
using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;
using System.Management; // Нужно добавить Reference на System.Management
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


        public MAVLinkService MAVLink { get; private set; }

        public MainWindow()
        {
            System.Diagnostics.Debug.WriteLine("[MainWindow] Constructor called");
            InitializeComponent();
            System.Diagnostics.Debug.WriteLine("[MainWindow] InitializeComponent completed");
            InitializeComponent();
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

        private void InitializeComPorts()
        {
            RefreshComPortsList(false);
        }

        private void RefreshComPortsList(bool autoSelect)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                var ports = SerialPort.GetPortNames().OrderBy(p => p).ToArray();
                string currentSelection = _selectedComPort; // Сохраняем текущий выбор

                // Сохраняем текущий выбранный элемент
                var selectedItem = ComPortComboBox.SelectedItem as ComboBoxItem;
                string previousSelection = selectedItem?.Content?.ToString();

                ComPortComboBox.Items.Clear();

                // Добавляем placeholder
                var placeholderItem = new ComboBoxItem
                {
                    Content = "COM Порт",
                    Tag = "placeholder"
                };
                ComPortComboBox.Items.Add(placeholderItem);

                // Добавляем реальные порты С ПОЛНЫМИ НАЗВАНИЯМИ
                ComboBoxItem itemToSelect = null;
                foreach (var port in ports)
                {
                    string fullDescription = GetComPortDescription(port);

                    var item = new ComboBoxItem
                    {
                        Content = fullDescription,  // Полное имя: "USB Serial Port (COM3)"
                        Tag = port                  // Короткое имя для подключения: "COM3"
                    };
                    ComPortComboBox.Items.Add(item);

                    // Запоминаем элемент для восстановления выбора
                    if (port == previousSelection || port == currentSelection)
                    {
                        itemToSelect = item;
                    }
                }

                // УМНОЕ ВОССТАНОВЛЕНИЕ ВЫБОРА
                if (itemToSelect != null && ports.Contains(itemToSelect.Content.ToString()))
                {
                    // Порт всё ещё доступен - восстанавливаем выбор
                    ComPortComboBox.SelectedItem = itemToSelect;
                    System.Diagnostics.Debug.WriteLine($"✅ COM порт восстановлен: {itemToSelect.Content}");
                }
                else if (ports.Length > 0 && autoSelect)
                {
                    // Автовыбор первого порта (только если запрошено)
                    ComPortComboBox.SelectedIndex = 1; // Пропускаем placeholder
                }
                else if (!ports.Contains(previousSelection) && previousSelection != "COM Порт")
                {
                    // Порт отключился - сбрасываем на placeholder
                    ComPortComboBox.SelectedIndex = 0;
                    _selectedComPort = null;
                    System.Diagnostics.Debug.WriteLine($"⚠️ COM порт {previousSelection} отключён");
                }
                else
                {
                    // Выбираем placeholder
                    ComPortComboBox.SelectedIndex = 0;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Ошибка обновления COM портов: {ex.Message}");
            }
        }


        // Добавь в начало класса MainWindow:
        private Dictionary<string, string> _comPortDescriptions = new Dictionary<string, string>();
        private DateTime _lastWmiScan = DateTime.MinValue;

        /// <summary>
        /// Получение полного описания COM порта с кэшированием
        /// </summary>
        /// <summary>
        /// Получение полного описания COM порта с кэшированием
        /// </summary>
        private string GetComPortDescription(string portName)
        {
            // Обновляем кэш только раз в 10 секунд
            if ((DateTime.Now - _lastWmiScan).TotalSeconds > 10 || _comPortDescriptions.Count == 0)
            {
                RefreshComPortDescriptions();
            }

            // Возвращаем из кэша (или просто "COM3" если WMI ещё не обновился)
            return _comPortDescriptions.ContainsKey(portName)
                ? _comPortDescriptions[portName]
                : portName;
        }

        /// <summary>
        /// Обновление кэша описаний портов (асинхронно в фоне)
        /// </summary>
        private void RefreshComPortDescriptions()
        {
            _lastWmiScan = DateTime.Now;
            _comPortDescriptions.Clear();

            // Сначала добавляем ВСЕ порты с простыми именами
            var allPorts = SerialPort.GetPortNames();
            foreach (var port in allPorts)
            {
                _comPortDescriptions[port] = port; // По умолчанию просто "COM3"
            }

            // Потом в фоне дополняем красивыми именами из WMI
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
                                // Извлекаем номер порта из строки типа "USB Serial (COM3)"
                                var match = System.Text.RegularExpressions.Regex.Match(caption, @"COM\d+");
                                if (match.Success)
                                {
                                    string portName = match.Value; // Получаем "COM3"

                                    Dispatcher.Invoke(() =>
                                    {
                                        // Обновляем ТОЛЬКО если порт существует
                                        if (_comPortDescriptions.ContainsKey(portName))
                                        {
                                            _comPortDescriptions[portName] = caption;
                                            System.Diagnostics.Debug.WriteLine($"  ✅ {portName} → {caption}");
                                        }
                                    });
                                }
                            }
                        }
                    }

                    System.Diagnostics.Debug.WriteLine($"✅ WMI сканирование завершено");
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
                string portName = item.Content.ToString();

                // Игнорируем placeholder
                if (portName == "COM Порт" || item.Tag?.ToString() == "placeholder")
                {
                    _selectedComPort = null;
                    return;
                }

                _selectedComPort = portName;
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
                Interval = TimeSpan.FromSeconds(5) // Каждые 5 секунд
            };
            _comPortRefreshTimer.Tick += (s, e) =>
            {
                // Обновляем ТОЛЬКО если:
                // 1. Dropdown ЗАКРЫТ
                // 2. НЕ подключены
                // 3. НЕ выбран конкретный порт
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
                // Берём КОРОТКОЕ имя из Tag (COM3), а не полное из Content
                string portTag = item.Tag?.ToString();

                if (string.IsNullOrEmpty(portTag) || portTag == "placeholder")
                {
                    MessageBox.Show("Выберите реальный COM порт из списка", "Ошибка",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                _selectedComPort = portTag; // Сохраняем короткое имя

                try
                {
                    MAVLink.Connect(_selectedComPort, _selectedBaudRate);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Ошибка подключения: {ex.Message}", "Ошибка",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            else
            {
                MessageBox.Show("Выберите COM порт из списка", "Ошибка",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
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
                // Красная кнопка при подключении
                ConnectButton.Background = new SolidColorBrush(Color.FromRgb(239, 68, 68));

                ConnectionIndicator.Fill = new SolidColorBrush(Color.FromRgb(152, 240, 25));
                ConnectionStatusText.Text = "Подключено";

                _comPortRefreshTimer?.Stop();
            }
            else
            {
                // Зелёная кнопка при отключении
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
                        // Передаем MAVLink сервис в View
                        var flightDataView = new FlightDataView(MAVLink);
                        MainFrame.Navigate(flightDataView);
                        break;
                    case "FlightPlan":
                        // НОВОЕ: Тоже передаем MAVLink!
                        var flightPlanView = new FlightPlanView(MAVLink);
                        MainFrame.Navigate(flightPlanView);
                        break;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка навигации: {ex.Message}");
            }
        }

        #endregion

        #region КОМАНДЫ

        private void TakeoffButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_isConnected)
            {
                MessageBox.Show("Дрон не подключен", "Ошибка",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (MAVLink.CurrentTelemetry.Armed)
            {
                MessageBox.Show("Дрон уже активирован!", "Информация",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // ВЫБОР: обычный ARM или принудительный
            var result = MessageBox.Show(
                "🔴 АКТИВИРОВАТЬ моторы?\n\n" +
                "⚠️ Есть ошибка конфигурации: 'Check frame class and type'\n\n" +
                "Нажмите:\n" +
                "• YES - Принудительный ARM (игнорирует проверки)\n" +
                "• NO - Обычный ARM (может отклониться)\n" +
                "• CANCEL - Отмена",
                "ARM - Выбор режима",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
            {
                // ПРИНУДИТЕЛЬНЫЙ ARM
                MAVLink.ForceArm();
                System.Diagnostics.Debug.WriteLine("🔴 FORCE ARM команда отправлена");
            }
            else if (result == MessageBoxResult.No)
            {
                // ОБЫЧНЫЙ ARM
                MAVLink.SetArm(true);
                System.Diagnostics.Debug.WriteLine("🔴 ARM команда отправлена");
            }
        }

        private void LandButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_isConnected) return;

            if (MessageBox.Show("АВАРИЙНАЯ ПОСАДКА?", "ВНИМАНИЕ",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            {
                MAVLink.SendLand();
            }
        }

        private void RthButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_isConnected) return;

            if (MessageBox.Show("Возврат домой?", "Подтверждение",
                MessageBoxButton.YesNo) == MessageBoxResult.Yes)
            {
                MAVLink.SendRTL();
            }
        }

        #endregion

        protected override void OnClosed(EventArgs e)
        {
            _comPortRefreshTimer?.Stop();
            MAVLink?.Disconnect();
            base.OnClosed(e);
        }

        /// <summary>
        /// Перетаскивание окна за заголовок
        /// </summary>
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

        /// <summary>
        /// Минимизация окна
        /// </summary>
        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            this.WindowState = WindowState.Minimized;
        }

        /// <summary>
        /// Максимизация/восстановление окна
        /// </summary>
        private void MaximizeButton_Click(object sender, RoutedEventArgs e)
        {
            if (this.WindowState == WindowState.Maximized)
            {
                this.WindowState = WindowState.Normal;
            }
            else
            {
                this.WindowState = WindowState.Maximized;
            }
        }

        /// <summary>
        /// Закрытие окна
        /// </summary>
        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        

    }

}