using SimpleDroneGCS.Models;
using SimpleDroneGCS.Services;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Windows.Input;

namespace SimpleDroneGCS.ViewModels
{
    public class FlightControlViewModel : INotifyPropertyChanged
    {
        private readonly MAVLinkService _mavlinkService;
        private VehicleType _currentVehicleType;

        // Коллекции для ComboBox
        public ObservableCollection<string> FlightModes { get; set; }
        public ObservableCollection<string> Calibrations { get; set; }

        // Выбранные значения
        private string _selectedFlightMode;
        public string SelectedFlightMode
        {
            get => _selectedFlightMode;
            set
            {
                _selectedFlightMode = value;
                OnPropertyChanged(nameof(SelectedFlightMode));
            }
        }

        private string _selectedCalibration;
        public string SelectedCalibration
        {
            get => _selectedCalibration;
            set
            {
                _selectedCalibration = value;
                OnPropertyChanged(nameof(SelectedCalibration));
            }
        }

        // Команды
        public ICommand SetFlightModeCommand { get; }
        public ICommand StartCalibrationCommand { get; }
        public ICommand ArmCommand { get; }
        public ICommand DisarmCommand { get; }

        public FlightControlViewModel(MAVLinkService mavlinkService)
        {
            _mavlinkService = mavlinkService; // ✅ ИСПОЛЬЗУЕМ ПАРАМЕТР

            // Инициализация коллекций
            FlightModes = new ObservableCollection<string>();
            Calibrations = new ObservableCollection<string>();

            // Получаем текущий тип дрона
            _currentVehicleType = VehicleManager.Instance.CurrentVehicleType;

            // Заполняем списки
            UpdateFlightModes();
            UpdateCalibrations();

            // ✅ ПРАВИЛЬНАЯ ПОДПИСКА
            VehicleManager.Instance.VehicleTypeChanged += OnVehicleTypeChanged;

            // Инициализация команд
            SetFlightModeCommand = new RelayCommand(SetFlightMode, CanSetFlightMode);
            StartCalibrationCommand = new RelayCommand(StartCalibration, CanStartCalibration);
            ArmCommand = new RelayCommand(Arm);
            DisarmCommand = new RelayCommand(Disarm);
        }

        // ✅ ПРАВИЛЬНАЯ СИГНАТУРА
        private void OnVehicleTypeChanged(object sender, VehicleProfile profile)
        {
            _currentVehicleType = profile.Type;
            UpdateFlightModes();
            UpdateCalibrations();
            Debug.WriteLine($"[FlightControl] Vehicle type changed to {profile.Type}");
        }

        private void UpdateFlightModes()
        {
            FlightModes.Clear();
            var modes = _currentVehicleType.GetFlightModes();
            foreach (var mode in modes)
            {
                FlightModes.Add(mode);
            }

            // Устанавливаем первый режим по умолчанию
            if (FlightModes.Any())
            {
                SelectedFlightMode = FlightModes[0];
            }

            Debug.WriteLine($"[FlightControl] Loaded {FlightModes.Count} flight modes for {_currentVehicleType}");
        }

        private void UpdateCalibrations()
        {
            Calibrations.Clear();
            var calibrations = _currentVehicleType.GetCalibrations();
            foreach (var calibration in calibrations)
            {
                Calibrations.Add(calibration);
            }

            // Устанавливаем первую калибровку по умолчанию
            if (Calibrations.Any())
            {
                SelectedCalibration = Calibrations[0];
            }

            Debug.WriteLine($"[FlightControl] Loaded {Calibrations.Count} calibrations for {_currentVehicleType}");
        }

        private bool CanSetFlightMode(object parameter)
        {
            return _mavlinkService.IsConnected && !string.IsNullOrEmpty(SelectedFlightMode);
        }

        private void SetFlightMode(object parameter)
        {
            if (!string.IsNullOrEmpty(SelectedFlightMode))
            {
                _mavlinkService.SetFlightMode(SelectedFlightMode);
                Debug.WriteLine($"[FlightControl] Setting flight mode: {SelectedFlightMode}");
            }
        }

        private bool CanStartCalibration(object parameter)
        {
            return _mavlinkService.IsConnected && !string.IsNullOrEmpty(SelectedCalibration);
        }

        private void StartCalibration(object parameter)
        {
            if (string.IsNullOrEmpty(SelectedCalibration))
                return;

            Debug.WriteLine($"[FlightControl] Starting calibration: {SelectedCalibration}");

            // ✅ ИСПРАВЛЕНО - БЕЗ ПРОБЕЛА!
            switch (SelectedCalibration)
            {
                case "Gyro":
                    _mavlinkService.SendPreflightCalibration(gyro: true);
                    break;
                case "Barometer":
                    _mavlinkService.SendPreflightCalibration(barometer: true);
                    break;
                case "BarAS":
                    _mavlinkService.SendPreflightCalibration(barometer: true);
                    break;
                case "Accelerometer":
                    _mavlinkService.SendPreflightCalibration(accelerometer: true);
                    break;
                case "CompassMot":
                    _mavlinkService.SendPreflightCalibration(compassMot: true);
                    break;
                case "Radio Trim":
                    _mavlinkService.SendPreflightCalibration(radioTrim: true);
                    break;
            }
        }

        // ✅ ПРАВИЛЬНЫЕ НАЗВАНИЯ МЕТОДОВ
        private void Arm(object parameter)
        {
            _mavlinkService.SendArm();
            Debug.WriteLine("[FlightControl] ARM command sent");
        }

        private void Disarm(object parameter)
        {
            _mavlinkService.SendDisarm();
            Debug.WriteLine("[FlightControl] DISARM command sent");
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    // RelayCommand helper
    public class RelayCommand : ICommand
    {
        private readonly Action<object> _execute;
        private readonly Func<object, bool> _canExecute;

        public RelayCommand(Action<object> execute, Func<object, bool> canExecute = null)
        {
            _execute = execute;
            _canExecute = canExecute;
        }

        public bool CanExecute(object parameter) => _canExecute == null || _canExecute(parameter);
        public void Execute(object parameter) => _execute(parameter);
        public event EventHandler CanExecuteChanged
        {
            add { CommandManager.RequerySuggested += value; }
            remove { CommandManager.RequerySuggested -= value; }
        }
    }
}