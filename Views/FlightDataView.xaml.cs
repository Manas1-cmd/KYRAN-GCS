using GMap.NET;
using GMap.NET.MapProviders;
using GMap.NET.WindowsPresentation;
using SimpleDroneGCS.Models;
using SimpleDroneGCS.Services;
using SimpleDroneGCS.UI.Dialogs;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using static SimpleDroneGCS.Helpers.Loc;

namespace SimpleDroneGCS.Views
{
    public partial class FlightDataView : Page
    {
        private MAVLinkService _mavlinkService;
        private VehicleType _currentVehicleType;

        private DispatcherTimer _updateTimer;
        private GMapMarker _droneMarker = null;

        private GMapRoute _headingLine = null;

        private readonly List<GMapMarker> _missionMarkers = new List<GMapMarker>();
        private GMapMarker _homeMarker = null;

        private DispatcherTimer _connectionTimer;
        private DateTime _lastHeadingLog = DateTime.MinValue;

        // Защита от повторной инициализации
        private bool _isInitialized = false;

        public FlightDataView(MAVLinkService mavlinkService)
        {
            InitializeComponent();

            _mavlinkService = mavlinkService;

            // ИНИЦИАЛИЗАЦИЯ ТИПА ДРОНА
            try
            {
                var vehicleManager = VehicleManager.Instance;
                if (vehicleManager != null)
                {
                    _currentVehicleType = vehicleManager.CurrentVehicleType;
                    vehicleManager.VehicleTypeChanged += OnVehicleTypeChanged;
                    UpdateVehicleTypeDisplay();
                    System.Diagnostics.Debug.WriteLine($"[FlightDataView] Vehicle: {_currentVehicleType}");
                }
                else
                {
                    _currentVehicleType = VehicleType.Copter;
                    System.Diagnostics.Debug.WriteLine("[FlightDataView] VehicleManager null, using Copter");
                }
            }
            catch (Exception ex)
            {
                _currentVehicleType = VehicleType.Copter;
                System.Diagnostics.Debug.WriteLine($"[FlightDataView] Init error: {ex.Message}");
            }

            // Подписки
            _mavlinkService.TelemetryReceived += OnTelemetryReceived;
            _mavlinkService.MessageReceived += OnDroneMessage;
            _mavlinkService.OnStatusTextReceived += OnCalibrationStatus;

            // Таймер UI
            _updateTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(33)
            };
            _updateTimer.Tick += UpdateUI;
            _updateTimer.Start();

            // Таймер секундомера
            _connectionTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _connectionTimer.Tick += UpdateConnectionTimer;
            _connectionTimer.Start();

            // Инициализация карты после загрузки страницы
            // Инициализация карты после загрузки страницы
            Loaded += (s, e) =>
            {
                // Таймеры запускаем при каждом показе
                if (!_updateTimer.IsEnabled)
                    _updateTimer.Start();
                if (!_connectionTimer.IsEnabled)
                    _connectionTimer.Start();

                // Карту инициализируем только ОДИН раз
                if (!_isInitialized)
                {
                    _isInitialized = true;

                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            InitializeMap();
                            UpdateComboBoxes();
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"[FlightDataView] Init error: {ex.Message}");
                        }
                    }), DispatcherPriority.Background);
                }

                // Миссию загружаем КАЖДЫЙ раз при показе страницы
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    LoadActiveMission();
                }), DispatcherPriority.Background);
            };

            UpdateUI(null, null);
        }

        private void OnTelemetryReceived(object sender, EventArgs e)
        {
            // Телеметрия применяется в UpdateUI по таймеру
        }

        /// <summary>
        /// Вычислить точку на заданном расстоянии и направлении от исходной
        /// </summary>
        private PointLatLng CalculatePointAtDistance(PointLatLng start, double headingDegrees, double distanceKm)
        {
            const double R = 6371; // Радиус Земли в км
            double lat1 = start.Lat * Math.PI / 180;
            double lon1 = start.Lng * Math.PI / 180;
            double bearing = headingDegrees * Math.PI / 180;

            double lat2 = Math.Asin(Math.Sin(lat1) * Math.Cos(distanceKm / R) +
                                   Math.Cos(lat1) * Math.Sin(distanceKm / R) * Math.Cos(bearing));
            double lon2 = lon1 + Math.Atan2(Math.Sin(bearing) * Math.Sin(distanceKm / R) * Math.Cos(lat1),
                                             Math.Cos(distanceKm / R) - Math.Sin(lat1) * Math.Sin(lat2));

            return new PointLatLng(lat2 * 180 / Math.PI, lon2 * 180 / Math.PI);
        }

        /// <summary>
        /// Обновить линию направления дрона (до края карты)
        /// </summary>
        private void UpdateHeadingLine(PointLatLng dronePosition, double heading)
        {
            // Удаляем старую линию
            if (_headingLine != null)
                MainMap.Markers.Remove(_headingLine);

            // Вычисляем конечную точку (50 км вперёд - достаточно для любого зума)
            var endPoint = CalculatePointAtDistance(dronePosition, heading, 50);

            var points = new List<PointLatLng> { dronePosition, endPoint };

            _headingLine = new GMapRoute(points)
            {
                Shape = new System.Windows.Shapes.Path
                {
                    Stroke = new SolidColorBrush(Color.FromRgb(255, 180, 0)), // Оранжевый
                    StrokeThickness = 2,
                    StrokeDashArray = new DoubleCollection { 6, 3 }, // Пунктир
                    Opacity = 0.8
                },
                ZIndex = 500
            };

            MainMap.Markers.Add(_headingLine);
        }


        /// <summary>
        /// Активация миссии - отправка в дрон и запуск AUTO режима
        /// </summary>
        private async void ActivateMissionButton_Click(object sender, RoutedEventArgs e)
        {
            var owner = Window.GetWindow(this);

            if (_mavlinkService == null || !_mavlinkService.IsConnected)
            {
                AppMessageBox.ShowWarning(Get("Msg_DroneNotConnectedDot"), owner, subtitle: Get("Msg_ConnectionSubtitle"));
                return;
            }

            if (!_mavlinkService.HasPlannedMission)
            {
                AppMessageBox.ShowWarning(
                    "Миссия не загружена.\n\nСоздайте миссию на странице 'План полёта' и нажмите 'Write'.",
                    owner,
                    subtitle: Get("Msg_MissionSub")
                );
                return;
            }

            // Проверяем ARM статус
            bool isArmed = _mavlinkService.CurrentTelemetry?.Armed ?? false;

            string confirmMsg = isArmed
                ? $"Запустить миссию из {_mavlinkService.PlannedMissionCount} точек?\n\n" +
                  "Последовательность:\n" +
                  "1. Загрузка миссии в дрон\n" +
                  "2. AUTO режим (выполнение)\n\n" +
                  "⚠️ Дрон начнёт выполнение миссии!"
                : $"Загрузить миссию из {_mavlinkService.PlannedMissionCount} точек?\n\n" +
                  "⚠️ Дрон НЕ активирован!\n" +
                  "После загрузки нажмите 'АКТИВИРОВАТЬ' для ARM,\n" +
                  "затем миссия запустится автоматически.";

            bool confirm = AppMessageBox.ShowConfirm(confirmMsg, owner, subtitle: "Подтверждение");

            if (!confirm) return;

            try
            {
                ActivateMissionButton.IsEnabled = false;

                // ШАГ 1: Загрузка миссии
                ActivateMissionButton.Content = "Загрузка...";
                System.Diagnostics.Debug.WriteLine("[Mission] Шаг 1: Загрузка миссии...");

                bool uploadSuccess = await _mavlinkService.UploadPlannedMission();
                if (!uploadSuccess)
                {
                    AppMessageBox.ShowError("Ошибка загрузки миссии.", owner, subtitle: "Ошибка");
                    ResetActivateButton();
                    return;
                }

                await Task.Delay(300);

                // ШАГ 2: AUTO режим (только если ARM)
                if (isArmed)
                {
                    ActivateMissionButton.Content = "AUTO...";
                    System.Diagnostics.Debug.WriteLine("[Mission] Шаг 2: Запуск AUTO режима...");

                    _mavlinkService.StartMission();

                    System.Diagnostics.Debug.WriteLine("[Mission] ✅ Миссия запущена!");

                    AppMessageBox.ShowSuccess(
                        "Миссия запущена!\n\nДрон выполняет миссию в AUTO режиме.",
                        owner,
                        subtitle: "Успех"
                    );
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("[Mission] ✅ Миссия загружена (ожидание ARM)");

                    AppMessageBox.ShowSuccess(
                        "Миссия загружена в дрон!\n\n" +
                        "Нажмите 'АКТИВИРОВАТЬ' для ARM.\n" +
                        "Миссия запустится автоматически после ARM.",
                        owner,
                        subtitle: "Готово"
                    );
                }

                ResetActivateButton();
            }
            catch (Exception ex)
            {
                AppMessageBox.ShowError($"Ошибка: {ex.Message}", owner, subtitle: "Активация миссии");
                System.Diagnostics.Debug.WriteLine($"[Mission] Exception: {ex.Message}");
                ResetActivateButton();
            }
        }

        private void ResetActivateButton()
        {
            ActivateMissionButton.IsEnabled = true;
            ActivateMissionButton.Content = "Активировать миссию";
        }

        /// <summary>
        /// Загрузка и отображение активной миссии на карте
        /// </summary>
        private void LoadActiveMission()
        {
            if (MainMap == null) return;

            // Удаляем старые маркеры миссии
            foreach (var marker in _missionMarkers)
            {
                if (marker != _droneMarker)
                    MainMap.Markers.Remove(marker);
            }
            _missionMarkers.Clear();

            // Удаляем HOME маркер
            if (_homeMarker != null)
            {
                MainMap.Markers.Remove(_homeMarker);
                _homeMarker = null;
            }

            // Удаляем маршруты миссии
            var oldRoutes = MainMap.Markers
                .Where(m => m.Tag?.ToString() == "MissionRoute" || m.Tag?.ToString() == "HomeRoute")
                .ToList();
            foreach (var route in oldRoutes)
                MainMap.Markers.Remove(route);

            // Загружаем миссию для текущего типа
            var mission = MissionStore.Get((int)_currentVehicleType);

            if (mission == null || mission.Count == 0)
            {
                UpdateMissionStatus();
                return;
            }

            System.Diagnostics.Debug.WriteLine($"[FlightDataView] Загрузка миссии: {mission.Count} точек");

            // Находим HOME и waypoints отдельно
            var homeWp = mission.FirstOrDefault(w => w.CommandType == "HOME");
            var waypoints = mission.Where(w => w.CommandType != "HOME" &&
                                                w.CommandType != "TAKEOFF" &&
                                                w.CommandType != "RETURN_TO_LAUNCH").ToList();

            // Отображаем HOME
            if (homeWp != null)
            {
                var homePos = new PointLatLng(homeWp.Latitude, homeWp.Longitude);
                _homeMarker = CreateHomeMarker(homePos);
                MainMap.Markers.Add(_homeMarker);
                System.Diagnostics.Debug.WriteLine($"[FlightDataView] HOME: {homeWp.Latitude:F6}, {homeWp.Longitude:F6}");
            }

            // Отображаем waypoints
            int wpNumber = 1;
            foreach (var wp in waypoints)
            {
                var position = new PointLatLng(wp.Latitude, wp.Longitude);
                var marker = CreateMissionWaypointMarker(position, wpNumber++);
                MainMap.Markers.Add(marker);
                _missionMarkers.Add(marker);
            }

            // Рисуем маршрут
            if (waypoints.Count >= 1)
            {
                var routePoints = new List<PointLatLng>();

                // HOME → первая точка (пунктир)
                if (homeWp != null && waypoints.Count > 0)
                {
                    var homePoint = new PointLatLng(homeWp.Latitude, homeWp.Longitude);
                    var firstPoint = new PointLatLng(waypoints[0].Latitude, waypoints[0].Longitude);

                    var homeToFirst = new GMapRoute(new List<PointLatLng> { homePoint, firstPoint })
                    {
                        Shape = new Path
                        {
                            Stroke = new SolidColorBrush(Color.FromRgb(239, 68, 68)),
                            StrokeThickness = 2,
                            StrokeDashArray = new DoubleCollection { 5, 3 },
                            Opacity = 0.8
                        },
                        Tag = "HomeRoute",
                        ZIndex = 30
                    };
                    MainMap.Markers.Add(homeToFirst);

                    // Последняя точка → HOME (пунктир)
                    var lastPoint = new PointLatLng(waypoints[waypoints.Count - 1].Latitude,
                                                    waypoints[waypoints.Count - 1].Longitude);
                    var lastToHome = new GMapRoute(new List<PointLatLng> { lastPoint, homePoint })
                    {
                        Shape = new Path
                        {
                            Stroke = new SolidColorBrush(Color.FromRgb(239, 68, 68)),
                            StrokeThickness = 2,
                            StrokeDashArray = new DoubleCollection { 5, 3 },
                            Opacity = 0.8
                        },
                        Tag = "HomeRoute",
                        ZIndex = 30
                    };
                    MainMap.Markers.Add(lastToHome);
                }

                // Основной маршрут (сплошной)
                if (waypoints.Count >= 2)
                {
                    routePoints = waypoints.Select(w => new PointLatLng(w.Latitude, w.Longitude)).ToList();
                    var route = new GMapRoute(routePoints)
                    {
                        Shape = new Path
                        {
                            Stroke = new SolidColorBrush(Color.FromRgb(152, 240, 25)),
                            StrokeThickness = 3,
                            Opacity = 0.8
                        },
                        Tag = "MissionRoute",
                        ZIndex = 35
                    };
                    MainMap.Markers.Add(route);
                }
            }

            UpdateMissionStatus();
        }

        /// <summary>
        /// Создать маркер HOME
        /// </summary>
        private GMapMarker CreateHomeMarker(PointLatLng position)
        {
            var grid = new Grid { Width = 36, Height = 36 };

            var circle = new Ellipse
            {
                Width = 36,
                Height = 36,
                Fill = new SolidColorBrush(Color.FromArgb(200, 239, 68, 100)),
                Stroke = Brushes.White,
                StrokeThickness = 3
            };

            var text = new TextBlock
            {
                Text = "H",
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
                FontSize = 16,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            grid.Children.Add(circle);
            grid.Children.Add(text);

            return new GMapMarker(position)
            {
                Shape = grid,
                Offset = new Point(-18, -18),
                ZIndex = 60
            };
        }

        private GMapMarker CreateMissionWaypointMarker(PointLatLng position, int number)
        {
            var grid = new Grid { Width = 30, Height = 30 };

            var circle = new Ellipse
            {
                Width = 30,
                Height = 30,
                Fill = new SolidColorBrush(Color.FromArgb(180, 152, 240, 25)),
                Stroke = Brushes.White,
                StrokeThickness = 2
            };

            var numberText = new TextBlock
            {
                Text = number.ToString(),
                Foreground = Brushes.Black,
                FontWeight = FontWeights.Bold,
                FontSize = 14,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            grid.Children.Add(circle);
            grid.Children.Add(numberText);

            return new GMapMarker(position)
            {
                Shape = grid,
                Offset = new Point(-15, -15),
                ZIndex = 50
            };
        }

        /// <summary>
        /// Обновление UI
        /// </summary>
        private void UpdateUI(object sender, EventArgs e)
        {
            if (_mavlinkService == null) return;

            var telemetry = _mavlinkService.CurrentTelemetry;

            try
            {
                AltitudeValue.Text = $"{telemetry.RelativeAltitude:F1} м";
                SpeedValue.Text = $"{telemetry.Speed:F1} м/с";

                // Дрон вращается по Heading
                DroneHeadingRotation.Angle = _mavlinkService.CurrentTelemetry.Heading;
                HeadingText.Text = $"{_mavlinkService.CurrentTelemetry.Heading:F0}°";

                // Координаты дрона
                DroneLatText.Text = _mavlinkService.CurrentTelemetry.Latitude.ToString("F6");
                DroneLonText.Text = _mavlinkService.CurrentTelemetry.Longitude.ToString("F6");

                // Высота MSL
                AltMslText.Text = _mavlinkService.CurrentTelemetry.Altitude.ToString("F1");

                // HOME: приоритет — реальный от дрона, иначе — из плана
                if (_mavlinkService.HasHomePosition)
                {
                    // Реальный HOME после армирования
                    HomeLatText.Text = _mavlinkService.HomeLat.Value.ToString("F6");
                    HomeLonText.Text = _mavlinkService.HomeLon.Value.ToString("F6");
                    HomeSourceText.Text = " (дрон)";
                    HomeSourceText.Foreground = new SolidColorBrush(Color.FromRgb(152, 240, 25));
                }
                else
                {
                    // Кастомный HOME из MissionStore
                    var home = MissionStore.GetHome((int)_currentVehicleType);

                    if (home != null)
                    {
                        HomeLatText.Text = home.Latitude.ToString("F6");
                        HomeLonText.Text = home.Longitude.ToString("F6");

                        HomeSourceText.Foreground = new SolidColorBrush(Color.FromRgb(102, 102, 102));
                    }
                    else
                    {
                        HomeLatText.Text = "---.------";
                        HomeLonText.Text = "---.------";
                        HomeSourceText.Text = "";
                    }
                }

                // Обновление VTOL моторов
                if (Motor1Border.Visibility == Visibility.Visible)
                {
                    Motor1Value.Text = $"{_mavlinkService.CurrentTelemetry.Motor1Percent}%";
                    Motor2Value.Text = $"{_mavlinkService.CurrentTelemetry.Motor2Percent}%";
                    Motor3Value.Text = $"{_mavlinkService.CurrentTelemetry.Motor3Percent}%";
                    Motor4Value.Text = $"{_mavlinkService.CurrentTelemetry.Motor4Percent}%";
                    PusherMotorValue.Text = $"{_mavlinkService.CurrentTelemetry.PusherPercent}%";
                }

                UpdateGpsStatus();
                UpdateArmButton();

                SatellitesValue.Text = $"{telemetry.SatellitesVisible}";
                FlightModeValue.Text = telemetry.FlightMode;
                BatteryVoltageValue.Text = $"{telemetry.BatteryVoltage:F1}V";
                BatteryPercentValue.Text = $"{telemetry.BatteryPercent}%";

                AttitudeIndicator.Roll = telemetry.Roll;
                AttitudeIndicator.Pitch = telemetry.Pitch;

                UpdateMapPosition();
                UpdateMissionStatus();

                if (!_mavlinkService.IsConnected || telemetry.IsStale())
                {
                    ShowError("Потеряна связь с дроном.");
                }
                else
                {
                    ErrorPanel.Visibility = Visibility.Collapsed;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[UI] Update error: {ex.Message}");
            }
        }

        private void ScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (MainMap == null) return;

            var mousePos = e.GetPosition(MainMap);
            if (mousePos.X >= 0 && mousePos.Y >= 0 &&
                mousePos.X <= MainMap.ActualWidth && mousePos.Y <= MainMap.ActualHeight)
            {
                e.Handled = false;
            }
        }

        private void UpdateConnectionTimer(object sender, EventArgs e)
        {
            if (_mavlinkService == null || !_mavlinkService.IsConnected)
            {
                ConnectionTimerText.Text = "00:00:00";
                return;
            }

            var elapsed = _mavlinkService.GetConnectionTime();
            ConnectionTimerText.Text = $"{elapsed.Hours:D2}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2}";
        }

        private void MainMap_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (MainMap == null) return;

            double newZoom = MainMap.Zoom + (e.Delta > 0 ? 1 : -1);

            if (newZoom >= MainMap.MinZoom && newZoom <= MainMap.MaxZoom)
            {
                MainMap.Zoom = newZoom;

                if (ZoomSlider != null)
                {
                    ZoomSlider.Value = newZoom;
                }
            }

            e.Handled = true;
        }

        private void UpdateMissionStatus()
        {
            if (_mavlinkService == null) return;

            if (_mavlinkService.HasPlannedMission)
            {
                MissionStatusText.Text = $"Готова миссия: {_mavlinkService.PlannedMissionCount} точек";
                MissionStatusText.Foreground = new SolidColorBrush(Color.FromRgb(152, 240, 25));
                ActivateMissionButton.IsEnabled = _mavlinkService.IsConnected;
            }
            else
            {
                MissionStatusText.Text = "Миссия не загружена";
                MissionStatusText.Foreground = Brushes.Gray;
                ActivateMissionButton.IsEnabled = false;
            }
        }

        private void InitializeMap()
        {
            try
            {
                // === НАСТРОЙКА КЭША ===
                string cacheFolder = System.IO.Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, "MapCache");

                if (!System.IO.Directory.Exists(cacheFolder))
                    System.IO.Directory.CreateDirectory(cacheFolder);

                // === АВТООПРЕДЕЛЕНИЕ ОНЛАЙН/ОФЛАЙН ===
                bool hasInternet = CheckInternetConnection();

                GMap.NET.GMaps.Instance.Mode = hasInternet
                    ? GMap.NET.AccessMode.ServerAndCache
                    : GMap.NET.AccessMode.CacheOnly;

                System.Net.ServicePointManager.ServerCertificateValidationCallback =
                    (snd, certificate, chain, sslPolicyErrors) => true;

                if (MainMap == null) return;

                // === КЭШИРОВАНИЕ ===
                MainMap.CacheLocation = cacheFolder;

                MainMap.MapProvider = GMapProviders.GoogleSatelliteMap;
                MainMap.Position = new PointLatLng(43.238949, 76.889709);
                MainMap.Zoom = 15;
                MainMap.MinZoom = 2;
                MainMap.MaxZoom = 20;
                MainMap.MouseWheelZoomType = MouseWheelZoomType.MousePositionAndCenter;
                MainMap.CanDragMap = true;
                MainMap.DragButton = MouseButton.Left;
                MainMap.ShowCenter = false;
                MainMap.ShowTileGridLines = false;
                MainMap.MouseWheelZoomEnabled = true;

                LoadActiveMission();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Map] Init error: {ex.Message}");
            }
        }

        private bool CheckInternetConnection()
        {
            // Способ 1: HTTP
            try
            {
                using (var client = new System.Net.WebClient())
                using (client.OpenRead("http://www.google.com"))
                    return true;
            }
            catch { }

            // Способ 2: Ping
            try
            {
                using (var ping = new System.Net.NetworkInformation.Ping())
                {
                    var result = ping.Send("8.8.8.8", 1000);
                    if (result.Status == System.Net.NetworkInformation.IPStatus.Success)
                        return true;
                }
            }
            catch { }

            // Способ 3: DNS
            try
            {
                var host = System.Net.Dns.GetHostEntry("www.google.com");
                return host.AddressList.Length > 0;
            }
            catch { }

            return false;
        }

        private void MapTypeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (MainMap == null || MapTypeCombo.SelectedItem == null) return;

            try
            {
                var selected = (ComboBoxItem)MapTypeCombo.SelectedItem;
                var tag = selected.Tag?.ToString();

                switch (tag)
                {
                    case "GoogleSatellite":
                        MainMap.MapProvider = GMapProviders.GoogleSatelliteMap;
                        break;
                    case "GoogleMap":
                        MainMap.MapProvider = GMapProviders.GoogleMap;
                        break;
                    case "OpenStreetMap":
                        MainMap.MapProvider = GMapProviders.OpenStreetMap;
                        break;
                    case "BingSatellite":
                        MainMap.MapProvider = GMapProviders.BingSatelliteMap;
                        break;
                    case "BingMap":
                        MainMap.MapProvider = GMapProviders.BingMap;
                        break;
                }

                MainMap.ReloadMap();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Map] Provider change error: {ex.Message}");
            }
        }

        private void UpdateMapPosition()
        {
            if (_mavlinkService == null || MainMap == null) return;
            var telemetry = _mavlinkService.CurrentTelemetry;

            if (telemetry.Latitude != 0 && telemetry.Longitude != 0)
            {
                var dronePosition = new PointLatLng(telemetry.Latitude, telemetry.Longitude);

                if (_droneMarker == null)
                {
                    _droneMarker = CreateDroneMarker(dronePosition);
                    MainMap.Markers.Add(_droneMarker);

                    if (_droneMarker.Tag is Grid grid)
                    {
                        grid.RenderTransform = new RotateTransform(telemetry.Heading, 250, 250);
                    }

                    // === ДОБАВЬ ЭТУ СТРОКУ ===
                    UpdateHeadingLine(dronePosition, _mavlinkService.CurrentTelemetry.Heading);
                }
                else
                {
                    _droneMarker.Position = dronePosition;

                    if (_droneMarker.Tag is Grid grid)
                    {
                        grid.RenderTransform = new RotateTransform(telemetry.Heading, 250, 250);

                        if ((DateTime.Now - _lastHeadingLog).TotalSeconds > 1)
                        {
                            _lastHeadingLog = DateTime.Now;
                        }
                    }
                }

                if (Math.Abs(MainMap.Position.Lat - dronePosition.Lat) > 0.0001 ||
                    Math.Abs(MainMap.Position.Lng - dronePosition.Lng) > 0.0001)
                {
                    MainMap.Position = dronePosition;
                }

                UpdateDroneToMissionLines();
            }
        }

        /// <summary>
        /// Пунктирные линии от дрона к миссии
        /// </summary>
        private void UpdateDroneToMissionLines()
        {
            if (_mavlinkService == null || MainMap == null) return;
            if (!_mavlinkService.HasActiveMission) return;

            var telemetry = _mavlinkService.CurrentTelemetry;
            if (telemetry.Latitude == 0 || telemetry.Longitude == 0) return;

            var mission = _mavlinkService.ActiveMission;
            if (mission == null || mission.Count == 0) return;

            var dronePosition = new PointLatLng(telemetry.Latitude, telemetry.Longitude);

            var oldDroneLines = MainMap.Markers
                .Where(m => m is GMapRoute && m.Tag?.ToString() == "DroneToMission")
                .Cast<GMapRoute>()
                .ToList();

            foreach (var line in oldDroneLines)
            {
                MainMap.Markers.Remove(line);
            }

            var firstWp = mission[0];
            var firstPoint = new PointLatLng(firstWp.Latitude, firstWp.Longitude);
            var droneToFirstRoute = new GMapRoute(new List<PointLatLng> { dronePosition, firstPoint })
            {
                Shape = new Path
                {
                    Stroke = new SolidColorBrush(Color.FromRgb(239, 68, 68)),
                    StrokeThickness = 3,
                    StrokeDashArray = new DoubleCollection { 8, 4 },
                    Opacity = 0.8
                },
                Tag = "DroneToMission",
                ZIndex = 40
            };
            MainMap.Markers.Add(droneToFirstRoute);

            if (mission.Count > 1)
            {
                var lastWp = mission[mission.Count - 1];
                var lastPoint = new PointLatLng(lastWp.Latitude, lastWp.Longitude);
                var lastToDroneRoute = new GMapRoute(new List<PointLatLng> { lastPoint, dronePosition })
                {
                    Shape = new Path
                    {
                        Stroke = new SolidColorBrush(Color.FromRgb(239, 68, 68)),
                        StrokeThickness = 3,
                        StrokeDashArray = new DoubleCollection { 8, 4 },
                        Opacity = 0.8
                    },
                    Tag = "DroneToMission",
                    ZIndex = 40
                };
                MainMap.Markers.Add(lastToDroneRoute);
            }
        }

        /// <summary>
        /// Маркер дрона (без эмодзи fallback)
        /// </summary>
        private GMapMarker CreateDroneMarker(PointLatLng position)
        {
            var grid = new Grid
            {
                Width = 500,
                Height = 500
            };



            var droneIcon = new Image
            {
                Source = new System.Windows.Media.Imaging.BitmapImage(
                    new Uri("pack://application:,,,/Images/drone_icon.png")),
                Width = 50,
                Height = 50,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            // Fallback: белый треугольник
            droneIcon.ImageFailed += (s, e) =>
            {
                var tri = new Polygon
                {
                    Points = new PointCollection
                    {
                        new Point(250, 220),
                        new Point(230, 270),
                        new Point(270, 270)
                    },
                    Fill = Brushes.White,
                    Stroke = Brushes.Black,
                    StrokeThickness = 1
                };

                grid.Children.Remove(droneIcon);
                grid.Children.Add(tri);
            };


            grid.Children.Add(droneIcon);

            return new GMapMarker(position)
            {
                Shape = grid,
                Offset = new Point(-250, -250),
                ZIndex = 1000,
                Tag = grid
            };
        }

        private void OnDroneMessage(object sender, string message)
        {
            Dispatcher.Invoke(() =>
            {
                System.Diagnostics.Debug.WriteLine($"[Drone] Message: {message}");

                if (message.Contains("Calibrat") || message.Contains("calib"))
                {
                    MissionStatusText.Text = message;
                    MissionStatusText.Foreground = new SolidColorBrush(Color.FromRgb(255, 165, 0));
                }
            });
        }

        private void UpdateGpsStatus()
        {
            if (_mavlinkService == null) return;
            var telemetry = _mavlinkService.CurrentTelemetry;

            switch (telemetry.GpsFixType)
            {
                case 0:
                case 1:
                    GpsStatusText.Text = "NO GPS";
                    GpsStatusText.Foreground = Brushes.Red;
                    GpsIndicator.Fill = Brushes.Red;
                    break;

                case 2:
                    GpsStatusText.Text = "2D FIX";
                    GpsStatusText.Foreground = Brushes.Yellow;
                    GpsIndicator.Fill = Brushes.Yellow;
                    break;

                case 3:
                default:
                    GpsStatusText.Text = "GPS FIX";
                    GpsStatusText.Foreground = new SolidColorBrush(Color.FromRgb(152, 240, 25));
                    GpsIndicator.Fill = new SolidColorBrush(Color.FromRgb(76, 175, 80));
                    break;
            }
        }

        private void UpdateArmButton()
        {
            if (_mavlinkService == null) return;

            if (_mavlinkService.CurrentTelemetry.Armed)
            {
                ArmButton.Content = "ДЕАКТИВИРОВАТЬ";
                ArmButton.Background = new SolidColorBrush(Color.FromRgb(239, 68, 68));
                ArmButton.BorderBrush = new SolidColorBrush(Color.FromRgb(220, 38, 38));
            }
            else
            {
                ArmButton.Content = "АКТИВИРОВАТЬ";
                ArmButton.Background = new SolidColorBrush(Color.FromRgb(42, 67, 97));
                ArmButton.BorderBrush = new SolidColorBrush(Color.FromRgb(42, 90, 143));
            }
        }

        private void ArmButton_Click(object sender, RoutedEventArgs e)
        {
            var owner = Window.GetWindow(this);

            if (_mavlinkService == null || !_mavlinkService.IsConnected)
            {
                AppMessageBox.ShowWarning(Get("Msg_DroneNotConnectedDot"), owner, subtitle: Get("Msg_ConnectionSubtitle"));
                return;
            }

            var telemetry = _mavlinkService.CurrentTelemetry;

            // DISARM (если уже активирован)
            if (telemetry.Armed)
            {
                bool inAir = telemetry.RelativeAltitude > 2.0;

                if (inAir)
                {
                    // Сначала предлагаем безопасную посадку
                    if (AppMessageBox.ShowConfirm(
                        "Дрон в воздухе!\n\nВыполнить безопасную посадку (LAND)?",
                        owner,
                        subtitle: "Деактивация"))
                    {
                        _mavlinkService.Land();
                    }
                    else
                    {
                        // Если отказался от LAND - предлагаем аварийный DISARM
                        if (AppMessageBox.ShowConfirm(
                            "Выполнить аварийный DISARM?\n\n⚠️ ВНИМАНИЕ: Дрон упадёт!",
                            owner,
                            subtitle: "Опасно!"))
                        {
                            _mavlinkService.SetArm(false, true);
                        }
                    }
                }
                else
                {
                    // На земле - обычный DISARM
                    if (AppMessageBox.ShowConfirm(
                        "Деактивировать дрон (DISARM)?",
                        owner,
                        subtitle: "Подтверждение"))
                    {
                        _mavlinkService.SetArm(false, true);
                    }
                }
            }
            // ARM (если не активирован)
            else
            {
                if (AppMessageBox.ShowConfirm(
                    "Активировать дрон (ARM)?\n\nУбедитесь что:\n• Пропеллеры свободны\n• Зона безопасна\n• GPS Fix получен",
                    owner,
                    subtitle: "Подтверждение"))
                {
                    _mavlinkService.SetArm(true, false);
                }
            }
        }

        /// <summary>
        /// Показать панель ошибки (не модальное окно)
        /// </summary>
        private void ShowError(string message)
        {
            ErrorText.Text = message;
            ErrorPanel.Visibility = Visibility.Visible;
        }

        private void ZoomSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (MainMap != null)
            {
                MainMap.Zoom = e.NewValue;
            }
        }

        public void Cleanup()
        {
            if (_updateTimer != null)
            {
                _updateTimer.Stop();
                _updateTimer = null;
            }

            if (_mavlinkService != null)
            {
                _mavlinkService.TelemetryReceived -= OnTelemetryReceived;
            }
        }

        #region УПРАВЛЯЮЩИЕ КНОПКИ

        private void LoiterButton_Click(object sender, RoutedEventArgs e)
        {
            if (!CheckConnection()) return;

            var owner = Window.GetWindow(this);

            // Выбираем режим в зависимости от типа дрона
            string mode = (_currentVehicleType == VehicleType.QuadPlane) ? "QLOITER" : "LOITER";
            string description = (_currentVehicleType == VehicleType.QuadPlane)
                ? "Переключить в режим QLOITER?\n\nVTOL будет удерживать позицию в режиме коптера."
                : "Переключить в режим LOITER?\n\nДрон будет удерживать текущую позицию GPS.";

            if (AppMessageBox.ShowConfirm(description, owner, subtitle: "Подтверждение"))
            {
                _mavlinkService.SetFlightMode(mode);
            }
        }

        private void AltHoldButton_Click(object sender, RoutedEventArgs e)
        {
            if (!CheckConnection()) return;

            var owner = Window.GetWindow(this);

            // Выбираем режим в зависимости от типа дрона
            string mode = (_currentVehicleType == VehicleType.QuadPlane) ? "QHOVER" : "ALT_HOLD";
            string description = (_currentVehicleType == VehicleType.QuadPlane)
                ? "Переключить в режим QHOVER?\n\nVTOL будет удерживать высоту в режиме коптера."
                : "Переключить в режим ALT_HOLD?\n\nДрон будет удерживать текущую высоту.";

            if (AppMessageBox.ShowConfirm(description, owner, subtitle: "Подтверждение"))
            {
                _mavlinkService.SetFlightMode(mode);
            }
        }

        private void CalibrateButton_Click(object sender, RoutedEventArgs e)
        {
            if (!CheckConnection()) return;

            var owner = Window.GetWindow(this);

            if (CalibrationCombo.SelectedItem is ComboBoxItem item)
            {
                string calibrationType = item.Tag?.ToString();

                if (string.IsNullOrEmpty(calibrationType))
                {
                    AppMessageBox.ShowWarning("Выберите тип калибровки из списка.", owner, subtitle: "Калибровка");
                    return;
                }

                if (calibrationType == "PREFLIGHT")
                {
                    if (AppMessageBox.ShowConfirm(
                        "Запустить Preflight Calibration?\n\nЭто выполнит предполётную калибровку датчиков.\nДрон должен быть неподвижен на ровной поверхности.",
                        owner,
                        subtitle: "Подтверждение"
                    ))
                    {
                        _mavlinkService.SendPreflightCalibration();
                    }
                }
            }
        }

        private void ExecuteModeButton_Click(object sender, RoutedEventArgs e)
        {
            if (!CheckConnection()) return;

            var owner = Window.GetWindow(this);

            if (FlightModeCombo.SelectedItem is ComboBoxItem item)
            {
                string modeName = item.Tag?.ToString();

                if (string.IsNullOrEmpty(modeName))
                {
                    AppMessageBox.ShowWarning("Выберите режим полета из списка.", owner, subtitle: "Режим полёта");
                    return;
                }

                if (AppMessageBox.ShowConfirm(
                    $"Переключить в режим {modeName}?",
                    owner,
                    subtitle: "Подтверждение"
                ))
                {
                    _mavlinkService.SetFlightMode(modeName);
                }
            }
        }

        private void ManualModeButton_Click(object sender, RoutedEventArgs e)
        {
            if (!CheckConnection()) return;

            var owner = Window.GetWindow(this);

            // Выбираем режим в зависимости от типа дрона
            string mode = (_currentVehicleType == VehicleType.QuadPlane) ? "QSTABILIZE" : "STABILIZE";
            string description = (_currentVehicleType == VehicleType.QuadPlane)
                ? "Переключить в режим QSTABILIZE?\n\nVTOL перейдёт в ручной режим коптера.\nПотребуется ручное управление через пульт."
                : "Переключить в ручной режим (STABILIZE)?\n\nПотребуется ручное управление через пульт.";

            if (AppMessageBox.ShowConfirm(description, owner, subtitle: "Подтверждение"))
            {
                _mavlinkService.SetFlightMode(mode);
            }
        }

        private void RtlButton_Click(object sender, RoutedEventArgs e)
        {
            if (!CheckConnection()) return;

            var owner = Window.GetWindow(this);

            string description = (_currentVehicleType == VehicleType.QuadPlane)
                ? "Активировать возврат домой (RTL)?\n\nVTOL вернётся на точку взлёта и выполнит посадку."
                : "Активировать возврат домой (RTL)?\n\nДрон вернется на точку взлёта и выполнит посадку.";

            if (AppMessageBox.ShowConfirm(description, owner, subtitle: "Подтверждение"))
            {
                _mavlinkService.SendRTL();
            }
        }

        private bool CheckConnection()
        {
            var owner = Window.GetWindow(this);

            if (_mavlinkService == null || !_mavlinkService.IsConnected)
            {
                AppMessageBox.ShowWarning(Get("Msg_DroneNotConnectedDot"), owner, subtitle: Get("Msg_ConnectionSubtitle"));
                return false;
            }
            return true;
        }

        #endregion

        #region VEHICLE TYPE MANAGEMENT

        private void OnVehicleTypeChanged(object sender, VehicleProfile profile)
        {
            Dispatcher.Invoke(() =>
            {
                _currentVehicleType = profile.Type;
                UpdateVehicleTypeDisplay();

                // Обновляем режимы полёта и кнопки для нового типа
                UpdateComboBoxes();

                // Перезагружаем миссию для нового типа
                LoadActiveMission();

                System.Diagnostics.Debug.WriteLine($"[FlightDataView] Тип изменён на: {profile.Type}");
            });
        }

        private void UpdateComboBoxes()
        {
            try
            {
                Dispatcher.Invoke(() =>
                {
                    if (FlightModeCombo == null || CalibrationCombo == null)
                    {
                        System.Diagnostics.Debug.WriteLine("[UpdateComboBoxes] ComboBoxes are NULL!");
                        return;
                    }

                    bool isVTOL = _currentVehicleType == VehicleType.QuadPlane;

                    // Режимы полета
                    FlightModeCombo.Items.Clear();
                    FlightModeCombo.Items.Add(new ComboBoxItem { Content = "Режимы полетов", Tag = "" });

                    var modes = _currentVehicleType.GetFlightModes();
                    if (modes != null)
                    {
                        foreach (var mode in modes)
                        {
                            FlightModeCombo.Items.Add(new ComboBoxItem { Content = mode, Tag = mode });
                        }
                    }
                    FlightModeCombo.SelectedIndex = 0;

                    // Калибровки
                    CalibrationCombo.Items.Clear();
                    CalibrationCombo.Items.Add(new ComboBoxItem { Content = "Калибровки", Tag = "" });

                    var calibrations = _currentVehicleType.GetCalibrations();
                    if (calibrations != null)
                    {
                        foreach (var calib in calibrations)
                        {
                            CalibrationCombo.Items.Add(new ComboBoxItem { Content = calib, Tag = calib });
                        }
                    }
                    CalibrationCombo.SelectedIndex = 0;

                    // Обновляем текст кнопок быстрых режимов
                    if (LoiterButton != null)
                        LoiterButton.Content = isVTOL ? "Q-Удерж" : "Удержание";

                    if (AltHoldButton != null)
                        AltHoldButton.Content = isVTOL ? "Q-Высота" : "Высота";

                    if (ManualModeButton != null)
                        ManualModeButton.Content = isVTOL ? "Q-Стаб" : "Ручной";

                    System.Diagnostics.Debug.WriteLine($"[UpdateComboBoxes] Обновлено для {(isVTOL ? "VTOL" : "Copter")}");
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[UpdateComboBoxes] ERROR: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"[UpdateComboBoxes] Stack: {ex.StackTrace}");
            }
        }

        private void FlightModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Режим активируется только кнопкой "Выполнить"
        }

        private void CalibrationCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Калибровка активируется только кнопкой "Калибровать"
        }

        private void StartNewCalibration(string calibration)
        {
            var owner = Window.GetWindow(this);

            if (_mavlinkService == null || !_mavlinkService.IsConnected)
            {
                AppMessageBox.ShowWarning("Подключитесь к дрону!", owner, subtitle: Get("Msg_ConnectionSubtitle"));
                return;
            }

            // Специальное предупреждение для Barometer + Airspeed
            if (calibration == "BarAS")
            {
                bool ok = AppMessageBox.ShowConfirm(
                    "Калибровка Barometer + Airspeed.\n\nВажно:\n" +
                    "- Накройте трубку Пито тканью или рукой\n" +
                    "- Дрон должен быть неподвижен\n" +
                    "- Калибровка займет около 30 секунд\n\nПродолжить?",
                    owner,
                    subtitle: "Подтверждение"
                );

                if (!ok) return;
            }

            switch (calibration)
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
                    if (AppMessageBox.ShowConfirm(
                        "Калибровка акселерометра.\n\nДрон должен лежать на ровной поверхности.\n\nПродолжить?",
                        owner,
                        subtitle: "Подтверждение"
                    ))
                    {
                        _mavlinkService.SendPreflightCalibration(accelerometer: true);
                    }
                    break;

                case "CompassMot":
                    if (AppMessageBox.ShowConfirm(
                        "Калибровка CompassMot.\n\nБудет проверка помех от моторов на компас.\n" +
                        "Моторы могут вращаться.\n\nУбедитесь что дрон надежно закреплен.\n\nПродолжить?",
                        owner,
                        subtitle: "Подтверждение"
                    ))
                    {
                        _mavlinkService.SendPreflightCalibration(compassMot: true);
                    }
                    break;

                case "Radio Trim":
                    if (AppMessageBox.ShowConfirm(
                        "Калибровка Radio Trim.\n\nУстановите все стики пульта в центральное положение.\n\nПродолжить?",
                        owner,
                        subtitle: "Подтверждение"
                    ))
                    {
                        _mavlinkService.SendPreflightCalibration(radioTrim: true);
                    }
                    break;

                default:
                    AppMessageBox.ShowError($"Неизвестная калибровка: {calibration}", owner, subtitle: "Калибровка");
                    break;
            }
        }

        private void VehicleTypeSelector_Click(object sender, MouseButtonEventArgs e)
        {
            var contextMenu = new ContextMenu();

            var copterItem = new MenuItem
            {
                Header = "Мультикоптер",
                Tag = VehicleType.Copter
            };
            copterItem.Click += VehicleTypeMenuItem_Click;
            contextMenu.Items.Add(copterItem);

            var quadPlaneItem = new MenuItem
            {
                Header = "VTOL",
                Tag = VehicleType.QuadPlane
            };
            quadPlaneItem.Click += VehicleTypeMenuItem_Click;
            contextMenu.Items.Add(quadPlaneItem);

            contextMenu.IsOpen = true;
            contextMenu.PlacementTarget = sender as UIElement;
            contextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        }

        private void VehicleTypeMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var owner = Window.GetWindow(this);

            if (sender is MenuItem menuItem && menuItem.Tag is VehicleType newType)
            {
                string name = (newType == VehicleType.Copter) ? "Мультикоптер" : "VTOL";

                if (AppMessageBox.ShowConfirm(
                    $"Переключить на {name}?\n\nРежимы полета и калибровки обновятся.",
                    owner,
                    subtitle: "Подтверждение"
                ))
                {
                    VehicleManager.Instance.SetVehicleType(newType);
                }
            }
        }

        private void UpdateVehicleTypeDisplay()
        {
            Dispatcher.Invoke(() =>
            {
                var profile = VehicleManager.Instance.CurrentProfile;

                VehicleTypeName.Text = profile.DisplayName;

                VehicleIcon.Text = profile.Type switch
                {
                    VehicleType.Copter => "MC",
                    VehicleType.QuadPlane => "VT",
                    _ => "MC"
                };

                // Показать/скрыть моторы VTOL
                var vtolVisibility = profile.Type == VehicleType.QuadPlane
                    ? Visibility.Visible
                    : Visibility.Collapsed;

                Motor1Border.Visibility = vtolVisibility;
                Motor2Border.Visibility = vtolVisibility;
                Motor3Border.Visibility = vtolVisibility;
                Motor4Border.Visibility = vtolVisibility;
                PusherBorder.Visibility = vtolVisibility;
            });
        }

        private void OnCalibrationStatus(string statusText)
        {
            Dispatcher.Invoke(() =>
            {
                if (statusText.Contains("Calibrat") || statusText.Contains("calib") ||
                    statusText.Contains("level") || statusText.Contains("Place") ||
                    statusText.Contains("Complete") || statusText.Contains("Failed"))
                {
                    MissionStatusText.Text = statusText;

                    if (statusText.Contains("Complete") || statusText.Contains("success"))
                    {
                        MissionStatusText.Foreground = new SolidColorBrush(Color.FromRgb(152, 240, 25));
                    }
                    else if (statusText.Contains("Failed") || statusText.Contains("Error"))
                    {
                        MissionStatusText.Foreground = Brushes.Red;
                    }
                    else
                    {
                        MissionStatusText.Foreground = new SolidColorBrush(Color.FromRgb(255, 165, 0));
                    }
                }
            });
        }

        #endregion
    }
}
