using GMap.NET;
using GMap.NET.MapProviders;
using GMap.NET.WindowsPresentation;
using SimpleDroneGCS.Models;
using SimpleDroneGCS.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Shapes; // ДОБАВЬ ЭТО
using System.Windows.Threading;


namespace SimpleDroneGCS.Views
{
    public partial class FlightDataView : Page
    {
        private MAVLinkService _mavlinkService;
        private VehicleType _currentVehicleType;

        private DispatcherTimer _updateTimer;
        private GMapMarker _droneMarker = null; // ДОБАВЬ ЭТО

        private List<GMapMarker> _missionMarkers = new List<GMapMarker>();
        private GMapMarker _homeMarker = null;
        // СЕКУНДОМЕР
        private DispatcherTimer _connectionTimer;
        private DateTime _lastHeadingLog = DateTime.MinValue; // ДОБАВЬ

        public FlightDataView(MAVLinkService mavlinkService)
        {
            InitializeComponent();

            // Получаем единый экземпляр сервиса
            _mavlinkService = mavlinkService;

            // ⭐ ИНИЦИАЛИЗАЦИЯ ТИПА ДРОНА
            try
            {
                var vehicleManager = VehicleManager.Instance;
                if (vehicleManager != null)
                {
                    _currentVehicleType = vehicleManager.CurrentVehicleType;
                    vehicleManager.VehicleTypeChanged += OnVehicleTypeChanged;
                    UpdateVehicleTypeDisplay(); // ⭐ ДОБАВЬ ЭТУ СТРОКУ
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

            // Подписываемся на события
            _mavlinkService.TelemetryReceived += OnTelemetryReceived;
            _mavlinkService.MessageReceived += OnDroneMessage;
            _mavlinkService.OnStatusTextReceived += OnCalibrationStatus;

            // Таймер для обновления UI
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

            // Инициализация карты ПОСЛЕ загрузки
            this.Loaded += (s, e) =>
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        InitializeMap();
                        LoadActiveMission();
                        UpdateComboBoxes(); // ⭐ ДОБАВИЛ ВЫЗОВ
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Ошибка инициализации: {ex.Message}");
                    }
                }), System.Windows.Threading.DispatcherPriority.Background);
            };

            // Первое обновление
            UpdateUI(null, null);
        }

        private void OnTelemetryReceived(object sender, EventArgs e)
        {
            // Телеметрия обновится в UpdateUI по таймеру
        }


        /// <summary>
        /// Активация миссии - отправка в дрон и запуск AUTO режима
        /// </summary>
        private async void ActivateMissionButton_Click(object sender, RoutedEventArgs e)
        {
            if (_mavlinkService == null || !_mavlinkService.IsConnected)
            {
                MessageBox.Show("Дрон не подключен", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!_mavlinkService.HasPlannedMission)
            {
                MessageBox.Show(
                    "Миссия не загружена.\n\n" +
                    "Создайте миссию на странице 'План полёта' и нажмите 'Write'.",
                    "Ошибка",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (!_mavlinkService.CurrentTelemetry.Armed)
            {
                MessageBox.Show(
                    "⚠️ Дрон не активирован!\n\n" +
                    "Сначала:\n" +
                    "1. ARM дрон\n" +
                    "2. Выполните Takeoff\n" +
                    "3. Затем активируйте миссию",
                    "Предупреждение",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (MessageBox.Show(
                $"🚁 Активировать миссию из {_mavlinkService.PlannedMissionCount} точек?\n\n" +
                "⚠️ Дрон переключится в AUTO режим и начнёт выполнение миссии!",
                "Подтверждение",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                return;
            }

            try
            {
                // Блокируем кнопку
                ActivateMissionButton.IsEnabled = false;
                ActivateMissionButton.Content = "Отправка...";

                // 1. Отправляем миссию в дрон
                System.Diagnostics.Debug.WriteLine("📤 Отправка миссии в дрон...");
                bool uploadSuccess = await _mavlinkService.UploadPlannedMission();

                if (!uploadSuccess)
                {
                    MessageBox.Show(
                        "❌ Ошибка отправки миссии в дрон",
                        "Ошибка",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);

                    ActivateMissionButton.IsEnabled = true;
                    ActivateMissionButton.Content = "Активировать миссию";
                    return;
                }

                System.Diagnostics.Debug.WriteLine("✅ Миссия отправлена");

                // 2. Запускаем AUTO режим
                await Task.Delay(1000); // Даём время дрону обработать миссию
                _mavlinkService.StartMission();

                MessageBox.Show(
                    "✅ Миссия активирована!\n\n" +
                    "Дрон переключен в AUTO режим и начал выполнение миссии.",
                    "Успех",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                System.Diagnostics.Debug.WriteLine("🎯 AUTO режим активирован");

                // Возвращаем кнопку
                ActivateMissionButton.IsEnabled = true;
                ActivateMissionButton.Content = "Активировать миссию";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка: {ex.Message}", "Ошибка",
                    MessageBoxButton.OK, MessageBoxImage.Error);

                System.Diagnostics.Debug.WriteLine($"❌ Exception: {ex.Message}");

                ActivateMissionButton.IsEnabled = true;
                ActivateMissionButton.Content = "Активировать миссию";
            }
        }

        /// <summary>
        /// Загрузка и отображение активной миссии на карте
        /// </summary>
        private void LoadActiveMission()
        {
            if (_mavlinkService == null || MainMap == null) return;

            // Очищаем ТОЛЬКО маркеры миссии (НЕ дрон!)
            foreach (var marker in _missionMarkers)
            {
                if (marker != _droneMarker) // ЗАЩИТА от удаления дрона
                {
                    MainMap.Markers.Remove(marker);
                }
            }
            _missionMarkers.Clear();

            // Удаляем ТОЛЬКО маршруты миссии (НЕ линии дрона!)
            var oldRoutes = MainMap.Markers
                .Where(m => m.Tag?.ToString() == "MissionRoute")
                .ToList();
            foreach (var route in oldRoutes)
            {
                MainMap.Markers.Remove(route);
            }

            if (!_mavlinkService.HasActiveMission) return;

            var mission = _mavlinkService.ActiveMission;
            System.Diagnostics.Debug.WriteLine($"📍 Загружаем миссию на FlightDataView: {mission.Count} точек");

            // Отображаем waypoints
            for (int i = 0; i < mission.Count; i++)
            {
                var wp = mission[i];
                var position = new PointLatLng(wp.Latitude, wp.Longitude);
                var marker = CreateMissionWaypointMarker(position, i + 1);
                MainMap.Markers.Add(marker);
                _missionMarkers.Add(marker);
            }

            // Рисуем маршрут
            if (mission.Count >= 2)
            {
                var routePoints = mission.Select(w => new PointLatLng(w.Latitude, w.Longitude)).ToList();
                var route = new GMapRoute(routePoints);
                route.Shape = new Path
                {
                    Stroke = new SolidColorBrush(Color.FromRgb(152, 240, 25)),
                    StrokeThickness = 2,
                    Opacity = 0.6,
                    StrokeDashArray = new DoubleCollection { 4, 2 }
                };
                route.Tag = "MissionRoute";
                route.ZIndex = 30;
                MainMap.Markers.Add(route);
            }
        }

        /// <summary>
        /// Создание маркера waypoint миссии
        /// </summary>
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
        /// Обновление всех данных на странице
        /// </summary>
        private void UpdateUI(object sender, EventArgs e)
        {
            if (_mavlinkService == null) return;

            var telemetry = _mavlinkService.CurrentTelemetry;

            try
            {
                // ВЫСОТА ОТ HOME
                AltitudeValue.Text = $"{telemetry.RelativeAltitude:F1} м";
                // СКОРОСТЬ
                SpeedValue.Text = $"{telemetry.Speed:F1} м/с";

                // GPS СТАТУС
                UpdateGpsStatus();

                // ARM КНОПКА
                UpdateArmButton();

                // ТЕЛЕМЕТРИЯ
                SatellitesValue.Text = $"{telemetry.SatellitesVisible}";
                FlightModeValue.Text = telemetry.FlightMode;
                BatteryVoltageValue.Text = $"{telemetry.BatteryVoltage:F1}V";
                BatteryPercentValue.Text = $"{telemetry.BatteryPercent}%";

                // ATTITUDE INDICATOR
                AttitudeIndicator.Roll = telemetry.Roll;
                AttitudeIndicator.Pitch = telemetry.Pitch;

                // КАРТА
                UpdateMapPosition();

               

                // СТАТУС МИССИИ
                UpdateMissionStatus();

                // ПРОВЕРКА СВЯЗИ
                if (!_mavlinkService.IsConnected || telemetry.IsStale())
                {
                    ShowError("Потеряна связь с дроном");
                }
                else
                {
                    ErrorPanel.Visibility = Visibility.Collapsed;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[UI UPDATE ERROR] {ex.Message}");
            }

            // УБРАЛИ DEBUG ВЫВОД - ОН УБИВАЛ ПРОИЗВОДИТЕЛЬНОСТЬ!
            // Если нужен debug - раскомментируй ТОЛЬКО при тестировании

            // System.Diagnostics.Debug.WriteLine(
            //     $"[UI] Alt={telemetry.Altitude:F1}м, " +
            //     $"Speed={telemetry.Speed:F1}м/с, " +
            //     $"Sats={telemetry.SatellitesVisible}, " +
            //     $"Mode={telemetry.FlightMode}, " +
            //     $"Armed={telemetry.Armed}"
            // );
        }


        /// <summary>
        /// Обработка прокрутки колесика для зума карты
        /// </summary>
        private void ScrollViewer_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
        {
            if (MainMap == null) return;

            // Если курсор над картой - пропускаем событие к карте для зума
            var mousePos = e.GetPosition(MainMap);
            if (mousePos.X >= 0 && mousePos.Y >= 0 &&
                mousePos.X <= MainMap.ActualWidth && mousePos.Y <= MainMap.ActualHeight)
            {
                e.Handled = false; // Пропускаем к карте
            }
        }

        /// <summary>
        /// Обновление секундомера подключения
        /// </summary>
        private void UpdateConnectionTimer(object sender, EventArgs e)
        {
            if (_mavlinkService == null || !_mavlinkService.IsConnected)
            {
                ConnectionTimerText.Text = "00:00:00";
                return;
            }

            // БЕРЁМ ВРЕМЯ ИЗ MAVLinkService (он всегда активен!)
            var elapsed = _mavlinkService.GetConnectionTime();
            ConnectionTimerText.Text = $"{elapsed.Hours:D2}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2}";
        }


        /// <summary>
        /// Принудительный зум карты колесиком
        /// </summary>
        private void MainMap_MouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
        {
            if (MainMap == null) return;

            // Зумим карту напрямую
            double newZoom = MainMap.Zoom + (e.Delta > 0 ? 1 : -1);

            // Ограничиваем зум в пределах Min/Max
            if (newZoom >= MainMap.MinZoom && newZoom <= MainMap.MaxZoom)
            {
                MainMap.Zoom = newZoom;

                // Обновляем слайдер зума
                if (ZoomSlider != null)
                {
                    ZoomSlider.Value = newZoom;
                }
            }

            e.Handled = true; // Останавливаем распространение события
        }

        /// <summary>
        /// Обновление статуса миссии в UI
        /// </summary>
        private void UpdateMissionStatus()
        {
            if (_mavlinkService == null) return;

            if (_mavlinkService.HasPlannedMission)
            {
                MissionStatusText.Text = $"Готова миссия: {_mavlinkService.PlannedMissionCount} точек";
                MissionStatusText.Foreground = new SolidColorBrush(Color.FromRgb(152, 240, 25)); // Зеленый
                ActivateMissionButton.IsEnabled = _mavlinkService.IsConnected;
            }
            else
            {
                MissionStatusText.Text = "Миссия не загружена";
                MissionStatusText.Foreground = Brushes.Gray;
                ActivateMissionButton.IsEnabled = false;
            }
        }


        /// <summary>
        /// Инициализация карты
        /// </summary>
        private void InitializeMap()
        {
            try
            {
                GMap.NET.GMaps.Instance.Mode = GMap.NET.AccessMode.ServerAndCache;
                System.Net.ServicePointManager.ServerCertificateValidationCallback =
                    (snd, certificate, chain, sslPolicyErrors) => true;

                if (MainMap == null) return;

                MainMap.MapProvider = GMapProviders.GoogleSatelliteMap;
                MainMap.Position = new PointLatLng(43.238949, 76.889709); // Алматы
                MainMap.Zoom = 15;
                MainMap.MinZoom = 2;
                MainMap.MaxZoom = 20;
                MainMap.MouseWheelZoomType = MouseWheelZoomType.MousePositionAndCenter;
                MainMap.CanDragMap = true;
                MainMap.DragButton = System.Windows.Input.MouseButton.Left;
                MainMap.ShowCenter = false;
                MainMap.ShowTileGridLines = false;

                MainMap.MouseWheelZoomEnabled = true;
                MainMap.MouseWheelZoomType = MouseWheelZoomType.MousePositionAndCenter;

                System.Diagnostics.Debug.WriteLine("✅ Карта инициализирована");
                // Загружаем миссию если есть
                try
                {
                    LoadActiveMission();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Ошибка загрузки миссии: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"❌ Ошибка карты: {ex.Message}");
            }
            // Загружаем активную миссию если есть
            LoadActiveMission();
        }

        /// <summary>
        /// Смена провайдера карты
        /// </summary>
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
                System.Diagnostics.Debug.WriteLine($"Ошибка смены провайдера: {ex.Message}");
            }
        }

        private void UpdateMapPosition()
        {
            if (_mavlinkService == null || MainMap == null) return;
            var telemetry = _mavlinkService.CurrentTelemetry;

            if (telemetry.Latitude != 0 && telemetry.Longitude != 0)
            {
                var dronePosition = new PointLatLng(telemetry.Latitude, telemetry.Longitude);

                // Создаем маркер дрона если его еще нет
                if (_droneMarker == null)
                {
                    _droneMarker = CreateDroneMarker(dronePosition);
                    MainMap.Markers.Add(_droneMarker);
                    System.Diagnostics.Debug.WriteLine($"🚁 Дрон создан на карте, heading={telemetry.Heading:F1}°");

                    // ПРИМЕНЯЕМ НАЧАЛЬНОЕ НАПРАВЛЕНИЕ
                    if (_droneMarker.Tag is Grid grid)
                    {
                        grid.RenderTransform = new RotateTransform(telemetry.Heading, 250, 250);
                    }
                }
                else
                {
                    // Обновляем позицию существующего маркера
                    _droneMarker.Position = dronePosition;

                    // ОБНОВЛЯЕМ НАПРАВЛЕНИЕ (heading)
                    if (_droneMarker.Tag is Grid grid)
                    {
                        grid.RenderTransform = new RotateTransform(telemetry.Heading, 250, 250);

                        // Debug раз в секунду
                        if ((DateTime.Now - _lastHeadingLog).TotalSeconds > 1)
                        {
                            System.Diagnostics.Debug.WriteLine($"🧭 Heading обновлён: {telemetry.Heading:F1}°");
                            _lastHeadingLog = DateTime.Now;
                        }
                    }
                }

                // Обновляем позицию карты только если дрон переместился значительно
                if (Math.Abs(MainMap.Position.Lat - dronePosition.Lat) > 0.0001 ||
                    Math.Abs(MainMap.Position.Lng - dronePosition.Lng) > 0.0001)
                {
                    MainMap.Position = dronePosition;
                }

                // РИСУЕМ ЛИНИИ ОТ ДРОНА К МИССИИ
                UpdateDroneToMissionLines();
            }
        }


        /// <summary>
        /// Рисование пунктирных линий от дрона к первой и последней точке миссии
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

            // Удаляем ТОЛЬКО линии от дрона (НЕ сам маркер дрона!)
            var oldDroneLines = MainMap.Markers
                .Where(m => m is GMapRoute && m.Tag?.ToString() == "DroneToMission")
                .Cast<GMapRoute>()
                .ToList();
            foreach (var line in oldDroneLines)
            {
                MainMap.Markers.Remove(line);
            }

            // Линия от ДРОНА к ПЕРВОЙ точке миссии (ПУНКТИР)
            var firstWp = mission[0];
            var firstPoint = new PointLatLng(firstWp.Latitude, firstWp.Longitude);
            var droneToFirstRoute = new GMapRoute(new List<PointLatLng> { dronePosition, firstPoint });
            droneToFirstRoute.Shape = new Path
            {
                Stroke = new SolidColorBrush(Color.FromRgb(239, 68, 68)), // Красный
                StrokeThickness = 3,
                StrokeDashArray = new DoubleCollection { 8, 4 }, // ПУНКТИР
                Opacity = 0.8
            };
            droneToFirstRoute.Tag = "DroneToMission";
            droneToFirstRoute.ZIndex = 40;
            MainMap.Markers.Add(droneToFirstRoute);

            // Линия от ПОСЛЕДНЕЙ точки к ДРОНУ (ПУНКТИР)
            if (mission.Count > 1)
            {
                var lastWp = mission[mission.Count - 1];
                var lastPoint = new PointLatLng(lastWp.Latitude, lastWp.Longitude);
                var lastToDroneRoute = new GMapRoute(new List<PointLatLng> { lastPoint, dronePosition });
                lastToDroneRoute.Shape = new Path
                {
                    Stroke = new SolidColorBrush(Color.FromRgb(239, 68, 68)), // Красный
                    StrokeThickness = 3,
                    StrokeDashArray = new DoubleCollection { 8, 4 }, // ПУНКТИР
                    Opacity = 0.8
                };
                lastToDroneRoute.Tag = "DroneToMission";
                lastToDroneRoute.ZIndex = 40;
                MainMap.Markers.Add(lastToDroneRoute);
            }
        }


        /// <summary>
        /// Создание иконки дрона с линией направления
        /// </summary>
        private GMapMarker CreateDroneMarker(PointLatLng position)
        {
            var grid = new Grid
            {
                Width = 500,
                Height = 500
            };

            // ДЛИННАЯ линия направления (heading)
            var headingLine = new Line
            {
                X1 = 250, // Центр grid
                Y1 = 250,
                X2 = 250,
                Y2 = 0,  // Длинная линия до края
                Stroke = new SolidColorBrush(Color.FromRgb(235, 232, 0)), // yellow
                StrokeThickness = 3,
                StrokeEndLineCap = PenLineCap.Triangle,
                Name = "HeadingLine"
            };

            // ИКОНКА ДРОНА (без кругов)
            var droneIcon = new Image
            {
                Source = new System.Windows.Media.Imaging.BitmapImage(
                    new Uri("pack://application:,,,/Images/drone_icon.png")),
                Width = 50,  // Увеличил размер
                Height = 50,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            // Fallback на эмодзи если иконка не загрузится
            droneIcon.ImageFailed += (s, e) =>
            {
                System.Diagnostics.Debug.WriteLine("⚠️ Иконка дрона не найдена, используем эмодзи");
                var fallback = new TextBlock
                {
                    Text = "🚁",
                    FontSize = 36,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                grid.Children.Remove(droneIcon);
                grid.Children.Add(fallback);
            };

            grid.Children.Add(headingLine);  // Линия направления
            grid.Children.Add(droneIcon);    // Иконка дрона поверх

            var marker = new GMapMarker(position)
            {
                Shape = grid,
                Offset = new Point(-250, -250),  // Центрируем grid
                ZIndex = 1000,
                Tag = grid  // Сохраняем для поворота
            };

            return marker;
        }


        /// <summary>
        /// Обработка текстовых сообщений от дрона
        /// </summary>
        private void OnDroneMessage(object sender, string message)
        {
            Dispatcher.Invoke(() =>
            {
                // Показываем в статусе или логах
                System.Diagnostics.Debug.WriteLine($"📢 DRONE MESSAGE: {message}");

                // Если калибровка - показываем
                if (message.Contains("Calibrat") || message.Contains("calib"))
                {
                    MissionStatusText.Text = message;
                    MissionStatusText.Foreground = new SolidColorBrush(Color.FromRgb(255, 165, 0)); // Оранжевый
                }
            });
        }


        /// <summary>
        /// Обновление статуса GPS
        /// </summary>
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

        /// <summary>
        /// Обновление статуса ARM кнопки
        /// </summary>
        private void UpdateArmButton()
        {
            if (_mavlinkService == null) return;

            if (_mavlinkService.CurrentTelemetry.Armed)
            {
                ArmButton.Content = "ДЕАКТИВИРОВАТЬ";
                ArmButton.Background = new SolidColorBrush(Color.FromRgb(239, 68, 68)); // Красный
                ArmButton.BorderBrush = new SolidColorBrush(Color.FromRgb(220, 38, 38));
            }
            else
            {
                ArmButton.Content = "АКТИВИРОВАТЬ";
                ArmButton.Background = new SolidColorBrush(Color.FromRgb(42, 67, 97)); // Темно-синий
                ArmButton.BorderBrush = new SolidColorBrush(Color.FromRgb(42, 90, 143));
            }
        }

        /// <summary>
        /// Обработчик кнопки ARM/DISARM
        /// </summary>
        private void ArmButton_Click(object sender, RoutedEventArgs e)
        {
            if (_mavlinkService == null || !_mavlinkService.IsConnected)
            {
                MessageBox.Show("Дрон не подключен", "Ошибка",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var telemetry = _mavlinkService.CurrentTelemetry;

            // Если уже ARM - делаем DISARM
            if (telemetry.Armed)
            {
                if (MessageBox.Show(
                    "🔴 ДЕАКТИВИРОВАТЬ моторы?\n\n" +
                    "⚠️ Дрон выключится!",
                    "DISARM - Подтверждение",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) == MessageBoxResult.Yes)
                {
                    _mavlinkService.SetArm(false);
                    System.Diagnostics.Debug.WriteLine("🔵 DISARM команда отправлена");
                }
                return;
            }

            // Если НЕ ARM - делаем FORCE ARM (БЕЗ ПРОВЕРОК GPS!)
            if (MessageBox.Show(
                "🔴 ПРИНУДИТЕЛЬНЫЙ ARM?\n\n" +
                "⚠️ ВНИМАНИЕ:\n" +
                "• GPS проверки ОТКЛЮЧЕНЫ\n" +
                "• Все проверки безопасности ИГНОРИРУЮТСЯ\n" +
                "• Используйте на свой риск!\n\n" +
                "Убедитесь что:\n" +
                "• Пропеллеры установлены\n" +
                "• Дрон на безопасном расстоянии\n" +
                "• Готовы к немедленному взлёту",
                "FORCE ARM - Подтверждение",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) == MessageBoxResult.Yes)
            {
                _mavlinkService.ForceArm(); // ИСПОЛЬЗУЕМ FORCE ARM!
                System.Diagnostics.Debug.WriteLine("🔴 FORCE ARM команда отправлена");
            }
        }

        /// <summary>
        /// Показать ошибку
        /// </summary>
        private void ShowError(string message)
        {
            ErrorText.Text = message;
            ErrorPanel.Visibility = Visibility.Visible;
        }

        /// <summary>
        /// Обработчик ползунка зума
        /// </summary>
        private void ZoomSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (MainMap != null)
            {
                MainMap.Zoom = e.NewValue;
            }
        }

        /// <summary>
        /// Cleanup при выгрузке страницы
        /// </summary>
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

        /// <summary>
        /// LOITER - Удержание точки
        /// </summary>
        private void LoiterButton_Click(object sender, RoutedEventArgs e)
        {
            if (!CheckConnection()) return;

            if (MessageBox.Show(
                "Переключить в режим LOITER?\n\n" +
                "Дрон будет удерживать текущую позицию GPS.",
                "LOITER - Удержание точки",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                _mavlinkService.SetFlightMode("LOITER");
                System.Diagnostics.Debug.WriteLine("🎯 LOITER режим активирован");
            }
        }

        /// <summary>
        /// ALT_HOLD - Удержание высоты
        /// </summary>
        private void AltHoldButton_Click(object sender, RoutedEventArgs e)
        {
            if (!CheckConnection()) return;

            if (MessageBox.Show(
                "Переключить в режим ALT_HOLD?\n\n" +
                "Дрон будет удерживать текущую высоту.",
                "ALT_HOLD - Удержание высоты",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                _mavlinkService.SetFlightMode("ALT_HOLD");
                System.Diagnostics.Debug.WriteLine("📏 ALT_HOLD режим активирован");
            }
        }

        /// <summary>
        /// Калибровка - выполнить выбранную калибровку
        /// </summary>
        private void CalibrateButton_Click(object sender, RoutedEventArgs e)
        {
            if (!CheckConnection()) return;

            if (CalibrationCombo.SelectedItem is ComboBoxItem item)
            {
                string calibrationType = item.Tag?.ToString();

                if (string.IsNullOrEmpty(calibrationType))
                {
                    MessageBox.Show(
                        "Выберите тип калибровки из списка",
                        "Ошибка",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                if (calibrationType == "PREFLIGHT")
                {
                    if (MessageBox.Show(
                        "⚠️ Запустить Preflight Calibration?\n\n" +
                        "Это выполнит предполётную калибровку датчиков.\n" +
                        "Дрон должен быть неподвижен на ровной поверхности.",
                        "Preflight Calibration",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning) == MessageBoxResult.Yes)
                    {
                        _mavlinkService.SendPreflightCalibration();
                        System.Diagnostics.Debug.WriteLine("🔧 Preflight Calibration запущена");
                    }
                }
            }
        }

        /// <summary>
        /// Выполнить - переключить на выбранный режим полета
        /// </summary>
        private void ExecuteModeButton_Click(object sender, RoutedEventArgs e)
        {
            if (!CheckConnection()) return;

            if (FlightModeCombo.SelectedItem is ComboBoxItem item)
            {
                string modeName = item.Tag?.ToString();

                if (string.IsNullOrEmpty(modeName))
                {
                    MessageBox.Show(
                        "Выберите режим полета из списка",
                        "Ошибка",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                if (MessageBox.Show(
                    $"Переключить в режим {modeName}?",
                    "Смена режима полета",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question) == MessageBoxResult.Yes)
                {
                    _mavlinkService.SetFlightMode(modeName);
                    System.Diagnostics.Debug.WriteLine($"✈️ Режим {modeName} активирован");
                }
            }
        }

        /// <summary>
        /// STABILIZE - Ручной режим
        /// </summary>
        private void ManualModeButton_Click(object sender, RoutedEventArgs e)
        {
            if (!CheckConnection()) return;

            if (MessageBox.Show(
                "Переключить в ручной режим (STABILIZE)?\n\n" +
                "⚠️ Потребуется ручное управление через пульт!",
                "STABILIZE - Ручной режим",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) == MessageBoxResult.Yes)
            {
                _mavlinkService.SetFlightMode("STABILIZE");
                System.Diagnostics.Debug.WriteLine("🎮 STABILIZE режим активирован");
            }
        }

        /// <summary>
        /// RTL - Возврат домой
        /// </summary>
        private void RtlButton_Click(object sender, RoutedEventArgs e)
        {
            if (!CheckConnection()) return;

            if (MessageBox.Show(
                "🏠 Активировать возврат домой (RTL)?\n\n" +
                "Дрон вернется на точку взлёта и выполнит посадку.",
                "RTL - Возврат домой",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                _mavlinkService.SendRTL();
                System.Diagnostics.Debug.WriteLine("🏠 RTL режим активирован");
            }
        }

        /// <summary>
        /// Проверка подключения к дрону
        /// </summary>
        private bool CheckConnection()
        {
            if (_mavlinkService == null || !_mavlinkService.IsConnected)
            {
                MessageBox.Show(
                    "Дрон не подключен",
                    "Ошибка",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return false;
            }
            return true;
        }

        #endregion

        #region VEHICLE TYPE MANAGEMENT

        private void OnVehicleTypeChanged(object sender, VehicleProfile profile)
        {
            _currentVehicleType = profile.Type;
            UpdateComboBoxes();
            UpdateVehicleTypeDisplay(); // ⭐ ДОБАВЛЕНА ЭТА СТРОКА
            System.Diagnostics.Debug.WriteLine($"[FlightDataView] Vehicle changed: {profile.Type}");
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

                    System.Diagnostics.Debug.WriteLine($"[UpdateComboBoxes] {modes?.Count ?? 0} modes, {calibrations?.Count ?? 0} calibrations");
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[UpdateComboBoxes] ERROR: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"Stack: {ex.StackTrace}");
            }
        }

        private void FlightModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                if (FlightModeCombo.SelectedItem is ComboBoxItem item && !string.IsNullOrEmpty(item.Tag?.ToString()))
                {
                    string mode = item.Tag.ToString();
                    _mavlinkService?.SetFlightMode(mode);
                    System.Diagnostics.Debug.WriteLine($"[FlightMode] Set: {mode}");
                    FlightModeCombo.SelectedIndex = 0;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[FlightMode] ERROR: {ex.Message}");
            }
        }

        private void CalibrationCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                if (CalibrationCombo.SelectedItem is ComboBoxItem item && !string.IsNullOrEmpty(item.Tag?.ToString()))
                {
                    string calibration = item.Tag.ToString();
                    StartNewCalibration(calibration);
                    CalibrationCombo.SelectedIndex = 0;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Calibration] ERROR: {ex.Message}");
            }
        }

        private void StartNewCalibration(string calibration)
        {
            if (_mavlinkService == null || !_mavlinkService.IsConnected)
            {
                MessageBox.Show("Подключитесь к дрону!", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            System.Diagnostics.Debug.WriteLine($"[Calibration] Starting: {calibration}");

            // Специальное предупреждение для Barometer+Airspeed
            if (calibration == "BarAS")
            {
                if (MessageBox.Show(
                    "⚠️ Калибровка Barometer + Airspeed\n\n" +
                    "ВАЖНО:\n" +
                    "• Накройте трубку Пито тканью или рукой\n" +
                    "• Дрон должен быть неподвижен\n" +
                    "• Калибровка займёт ~30 секунд\n\n" +
                    "Продолжить?",
                    "Калибровка BarAS",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) != MessageBoxResult.Yes)
                {
                    return;
                }
            }

            // Отправляем правильную калибровку
            switch (calibration)
            {
                case "Gyro":
                    _mavlinkService.SendPreflightCalibration(gyro: true);
                    break;

                case "Barometer":
                    _mavlinkService.SendPreflightCalibration(barometer: true);
                    break;

                case "BarAS":
                    // Для Plane: barometer включает и airspeed
                    _mavlinkService.SendPreflightCalibration(barometer: true);
                    break;

                case "Accelerometer":
                    if (MessageBox.Show(
                        "⚠️ Калибровка акселерометра\n\n" +
                        "Дрон должен лежать на ровной поверхности.\n\n" +
                        "Продолжить?",
                        "Калибровка Accelerometer",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question) == MessageBoxResult.Yes)
                    {
                        _mavlinkService.SendPreflightCalibration(accelerometer: true);
                    }
                    break;

                case "CompassMot":
                    if (MessageBox.Show(
                        "⚠️ CompassMot калибровка\n\n" +
                        "Проверка помех от моторов на компас.\n" +
                        "Пропеллеры будут вращаться!\n\n" +
                        "ВНИМАНИЕ: Убедитесь что дрон закреплён!\n\n" +
                        "Продолжить?",
                        "CompassMot",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning) == MessageBoxResult.Yes)
                    {
                        _mavlinkService.SendPreflightCalibration(compassMot: true);
                    }
                    break;

                case "Radio Trim":
                    if (MessageBox.Show(
                        "⚠️ Radio Trim калибровка\n\n" +
                        "Установите все стики пульта в центральное положение.\n\n" +
                        "Продолжить?",
                        "Radio Trim",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question) == MessageBoxResult.Yes)
                    {
                        _mavlinkService.SendPreflightCalibration(radioTrim: true);
                    }
                    break;

                default:
                    MessageBox.Show($"Неизвестная калибровка: {calibration}", "Ошибка",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    break;
            }
        }

        private void VehicleTypeSelector_Click(object sender, MouseButtonEventArgs e)
        {
            // Создаём popup меню выбора типа
            var contextMenu = new ContextMenu();

            // Copter
            var copterItem = new MenuItem
            {
                Header = "🚁 Мультикоптер",
                Tag = VehicleType.Copter
            };
            copterItem.Click += VehicleTypeMenuItem_Click;
            contextMenu.Items.Add(copterItem);

            // QuadPlane
            var quadPlaneItem = new MenuItem
            {
                Header = "✈️ VTOL",
                Tag = VehicleType.QuadPlane
            };
            quadPlaneItem.Click += VehicleTypeMenuItem_Click;
            contextMenu.Items.Add(quadPlaneItem);

            // Показываем меню
            contextMenu.IsOpen = true;
            contextMenu.PlacementTarget = sender as UIElement;
            contextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        }

        private void VehicleTypeMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem menuItem && menuItem.Tag is VehicleType newType)
            {
                if (MessageBox.Show(
                    $"Переключить на {(newType == VehicleType.Copter ? "Мультикоптер" : "VTOL")}?\n\n" +
                    "Режимы полета и калибровки обновятся.",
                    "Смена типа дрона",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question) == MessageBoxResult.Yes)
                {
                    VehicleManager.Instance.SetVehicleType(newType);
                    System.Diagnostics.Debug.WriteLine($"[VehicleTypeSelector] Changed to: {newType}");
                }
            }
        }

        private void UpdateVehicleTypeDisplay()
        {
            Dispatcher.Invoke(() =>
            {
                var profile = VehicleManager.Instance.CurrentProfile;

                // Обновляем текст
                VehicleTypeName.Text = profile.DisplayName;

                // Обновляем иконку
                VehicleIcon.Text = profile.Type switch
                {
                    VehicleType.Copter => "🚁",
                    VehicleType.QuadPlane => "✈️",
                    _ => "🚁"
                };

                System.Diagnostics.Debug.WriteLine($"[Display] Vehicle: {profile.DisplayName}");
            });
        }


        private void OnCalibrationStatus(string statusText)
        {
            Dispatcher.Invoke(() =>
            {
                // Фильтруем только важные сообщения о калибровке
                if (statusText.Contains("Calibrat") || statusText.Contains("calib") ||
                    statusText.Contains("level") || statusText.Contains("Place") ||
                    statusText.Contains("Complete") || statusText.Contains("Failed"))
                {
                    System.Diagnostics.Debug.WriteLine($"[CalibrationStatus] {statusText}");

                    // Показываем в статусе миссии (временно)
                    MissionStatusText.Text = statusText;

                    // Зелёный для успеха, красный для ошибок
                    if (statusText.Contains("Complete") || statusText.Contains("success"))
                    {
                        MissionStatusText.Foreground = new SolidColorBrush(Color.FromRgb(152, 240, 25)); // Зелёный
                    }
                    else if (statusText.Contains("Failed") || statusText.Contains("Error"))
                    {
                        MissionStatusText.Foreground = Brushes.Red;
                    }
                    else
                    {
                        MissionStatusText.Foreground = new SolidColorBrush(Color.FromRgb(255, 165, 0)); // Оранжевый (в процессе)
                    }
                }
            });
        }
        #endregion

    }
}