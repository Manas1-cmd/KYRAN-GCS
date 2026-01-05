using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SimpleDroneGCS.Helpers;
using SimpleDroneGCS.Models;
using SimpleDroneGCS.Services;

namespace SimpleDroneGCS.ViewModels
{
    /// <summary>
    /// ViewModel для главного окна (полётные данные)
    /// </summary>
    public class FlightDataViewModel : INotifyPropertyChanged
    {
        private readonly MAVLinkService _mavlinkService;
        private readonly DispatcherTimer _portRefreshTimer;
        private readonly DispatcherTimer _connectionCheckTimer;
        private VehicleManager _vehicleManager;

        #region Свойства

        // Телеметрия
        private Telemetry _currentTelemetry;
        public Telemetry CurrentTelemetry
        {
            get => _currentTelemetry;
            set { _currentTelemetry = value; OnPropertyChanged(); }
        }

        // Подключение
        private ObservableCollection<string> _availablePorts;
        public ObservableCollection<string> AvailablePorts
        {
            get => _availablePorts;
            set { _availablePorts = value; OnPropertyChanged(); }
        }

        private string _selectedPort;
        public string SelectedPort
        {
            get => _selectedPort;
            set { _selectedPort = value; OnPropertyChanged(); }
        }

        private string _baudRate;
        public string BaudRate
        {
            get => _baudRate;
            set { _baudRate = value; OnPropertyChanged(); }
        }

        public ObservableCollection<string> CommonBaudRates { get; set; }

        // Статус подключения
        private string _connectButtonText;
        public string ConnectButtonText
        {
            get => _connectButtonText;
            set { _connectButtonText = value; OnPropertyChanged(); }
        }

        private string _connectionStatusText;
        public string ConnectionStatusText
        {
            get => _connectionStatusText;
            set { _connectionStatusText = value; OnPropertyChanged(); }
        }

        private Brush _connectionStatusColor;
        public Brush ConnectionStatusColor
        {
            get => _connectionStatusColor;
            set { _connectionStatusColor = value; OnPropertyChanged(); }
        }

        #endregion

        #region Команды

        public ICommand ConnectCommand { get; }
        public ICommand RefreshPortsCommand { get; }
        public ICommand ArmCommand { get; }
        public ICommand DisarmCommand { get; }
        public ICommand TakeoffCommand { get; }
        public ICommand LandCommand { get; }
        public ICommand RthCommand { get; }

        #endregion

        public FlightDataViewModel()
        {
            // Инициализация сервиса
            _mavlinkService = new MAVLinkService();
            _mavlinkService.TelemetryUpdated += OnTelemetryUpdated;
            _mavlinkService.ConnectionStatusChanged += OnConnectionStatusChanged;
            _mavlinkService.ErrorOccurred += OnError;

            // Инициализация свойств
            CurrentTelemetry = new Telemetry();
            AvailablePorts = new ObservableCollection<string>();
            CommonBaudRates = new ObservableCollection<string> { "9600", "57600", "115200", "921600" };
            BaudRate = "57600";
            ConnectButtonText = "Подключить";
            ConnectionStatusText = "Не подключено";
            ConnectionStatusColor = Brushes.Red;

            _vehicleManager = VehicleManager.Instance;
            _vehicleManager.VehicleTypeChanged += OnVehicleTypeChanged;

            UpdateVehicleSpecificUI();

            // Команды
            ConnectCommand = new RelayCommand(OnConnect);
            RefreshPortsCommand = new RelayCommand(OnRefreshPorts);
            ArmCommand = new RelayCommand(OnArm, CanExecuteArm);
            DisarmCommand = new RelayCommand(OnDisarm, CanExecuteDisarm);
            TakeoffCommand = new RelayCommand(OnTakeoff, CanExecuteTakeoff);
            LandCommand = new RelayCommand(OnLand, CanExecuteLand);
            RthCommand = new RelayCommand(OnRth, CanExecuteRth);

            // Таймеры
            _portRefreshTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(3)
            };
            _portRefreshTimer.Tick += (s, e) => RefreshPorts();
            _portRefreshTimer.Start();

            _connectionCheckTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _connectionCheckTimer.Tick += (s, e) => CheckConnection();
            _connectionCheckTimer.Start();

            // Первое обновление портов
            RefreshPorts();
        }

        // Новые свойства для UI
        private bool _showAirspeedIndicator;
        public bool ShowAirspeedIndicator
        {
            get => _showAirspeedIndicator;
            set { _showAirspeedIndicator = value; OnPropertyChanged(); }
        }

        private string _vehicleTypeName;
        public string VehicleTypeName
        {
            get => _vehicleTypeName;
            set { _vehicleTypeName = value; OnPropertyChanged(); }
        }

        private ObservableCollection<string> _availableFlightModes;
        public ObservableCollection<string> AvailableFlightModes
        {
            get => _availableFlightModes;
            set { _availableFlightModes = value; OnPropertyChanged(); }
        }

        // Обработчик смены типа дрона
        private void OnVehicleTypeChanged(object sender, VehicleProfile profile)
        {
            UpdateVehicleSpecificUI();
        }

        private void UpdateVehicleSpecificUI()
        {
            var profile = _vehicleManager.CurrentProfile;

            VehicleTypeName = profile.DisplayName;
            ShowAirspeedIndicator = profile.TelemetryConfig.ShowAirspeed;
            AvailableFlightModes = new ObservableCollection<string>(profile.SupportedFlightModes);

            // Обновляем флайт моды в MAVLinkService
            if (_mavlinkService != null)
            {
                _mavlinkService.SetVehicleType(profile.MavType);
            }
        }

        #region Обработчики команд

        private void OnConnect(object parameter)
        {
            if (_mavlinkService.IsConnected)
            {
                _mavlinkService.Disconnect();
                ConnectButtonText = "Подключить";
            }
            else
            {
                if (string.IsNullOrEmpty(SelectedPort))
                {
                    OnError("Выберите COM-порт");
                    return;
                }

                if (!int.TryParse(BaudRate, out int baud))
                {
                    OnError("Неверная скорость");
                    return;
                }

                bool connected = _mavlinkService.Connect(SelectedPort, baud);
                if (connected)
                {
                    ConnectButtonText = "Отключить";
                }
            }
        }

        private void OnRefreshPorts(object parameter)
        {
            RefreshPorts();
        }

        private void OnArm(object parameter)
        {
            _mavlinkService.SetArm(true);
        }

        private bool CanExecuteArm(object parameter)
        {
            return _mavlinkService.IsConnected && !CurrentTelemetry.Armed;
        }

        private void OnDisarm(object parameter)
        {
            _mavlinkService.SetArm(false);
        }

        private bool CanExecuteDisarm(object parameter)
        {
            return _mavlinkService.IsConnected && CurrentTelemetry.Armed;
        }

        private void OnTakeoff(object parameter)
        {
            // Взлёт на 5 метров
            _mavlinkService.Takeoff(5.0);
        }

        private bool CanExecuteTakeoff(object parameter)
        {
            return _mavlinkService.IsConnected && CurrentTelemetry.Armed;
        }

        private void OnLand(object parameter)
        {
            _mavlinkService.Land();
        }

        private bool CanExecuteLand(object parameter)
        {
            return _mavlinkService.IsConnected && CurrentTelemetry.Armed;
        }

        private void OnRth(object parameter)
        {
            _mavlinkService.ReturnToLaunch();
        }

        private bool CanExecuteRth(object parameter)
        {
            return _mavlinkService.IsConnected && CurrentTelemetry.Armed;
        }

        #endregion

        #region Обработчики событий MAVLink

        private void OnTelemetryUpdated(object sender, Telemetry telemetry)
        {
            // Обновление в UI потоке
            App.Current.Dispatcher.Invoke(() =>
            {
                CurrentTelemetry = telemetry;
            });
        }

        private void OnConnectionStatusChanged(object sender, string status)
        {
            App.Current.Dispatcher.Invoke(() =>
            {
                ConnectionStatusText = status;
                UpdateConnectionStatus();
            });
        }

        private void OnError(object sender, string error)
        {
            App.Current.Dispatcher.Invoke(() =>
            {
                System.Windows.MessageBox.Show(error, "Ошибка", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            });
        }

        private void OnError(string error)
        {
            System.Windows.MessageBox.Show(error, "Ошибка", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }

        #endregion

        #region Вспомогательные методы

        /// <summary>
        /// Обновление списка COM портов
        /// </summary>
        private void RefreshPorts()
        {
            string[] ports = MAVLinkService.GetAvailablePorts();

            // Обновляем только если список изменился
            if (!ports.SequenceEqual(AvailablePorts))
            {
                string currentSelected = SelectedPort;
                AvailablePorts.Clear();

                foreach (string port in ports)
                {
                    AvailablePorts.Add(port);
                }

                // Восстанавливаем выбор, если порт ещё доступен
                if (AvailablePorts.Contains(currentSelected))
                {
                    SelectedPort = currentSelected;
                }
                else if (AvailablePorts.Count > 0)
                {
                    SelectedPort = AvailablePorts[0];
                }
            }
        }

        /// <summary>
        /// Проверка статуса подключения
        /// </summary>
        private void CheckConnection()
        {
            UpdateConnectionStatus();
        }

        /// <summary>
        /// Обновление индикатора подключения
        /// </summary>
        private void UpdateConnectionStatus()
        {
            if (_mavlinkService.IsConnected && _mavlinkService.DroneStatus.IsAlive())
            {
                ConnectionStatusColor = Brushes.LimeGreen;
                if (ConnectionStatusText == "Не подключено")
                {
                    ConnectionStatusText = "Подключено";
                }
            }
            else if (_mavlinkService.IsConnected)
            {
                ConnectionStatusColor = Brushes.Yellow;
                ConnectionStatusText = "Нет данных";
            }
            else
            {
                ConnectionStatusColor = Brushes.Red;
                ConnectionStatusText = "Не подключено";
            }
        }

        #endregion

        #region INotifyPropertyChanged

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        #endregion
    }
}