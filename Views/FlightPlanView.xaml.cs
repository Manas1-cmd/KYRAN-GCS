using GMap.NET;
using GMap.NET.MapProviders;
using GMap.NET.WindowsPresentation;
using SimpleDroneGCS.Models;
using SimpleDroneGCS.Services;
using SimpleDroneGCS.UI.Dialogs;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;



namespace SimpleDroneGCS.Views
{
    public partial class FlightPlanView : UserControl
    {

        private Window OwnerWindow => Window.GetWindow(this) ?? Application.Current?.MainWindow;
        private VehicleType _currentVehicleType = VehicleType.Copter;

        private ObservableCollection<WaypointItem> _waypoints;
        private GMapMarker _currentDragMarker;
        private double _waypointRadius = 30; // метры
        private MAVLinkService _mavlinkService;
        private GMapMarker _droneMarker = null;
        private WaypointItem _homePosition = null; // HOME позиция
        private bool _isInitialized = false; // Защита от повторной инициализации
        private Dictionary<VehicleType, List<WaypointItem>> _missionsByType = new(); // Кэш миссий при переключении типа (RAM)
        private Dictionary<VehicleType, WaypointItem> _homeByType = new();
        private DispatcherTimer _droneUpdateTimer; // ДОБАВЬ
        private bool _isSettingHomeMode = false; // режим установки HOME
        private double _takeoffAltitude = 10;  // высота взлёта по умолчанию
        private double _rtlAltitude = 15;      // высота RTL по умолчанию
        private bool _wasArmed = false; // для отслеживания армирования
        private SrtmElevationProvider _elevationProvider = new(); // Провайдер высот SRTM

        public FlightPlanView(MAVLinkService mavlinkService = null)
        {
            InitializeComponent();
            var testElev = new SrtmElevationProvider();
            var result = testElev.GetElevation(43.238, 76.945);
            System.Diagnostics.Debug.WriteLine($"[SRTM TEST] Результат: {result?.ToString() ?? "NULL"}");
            _mavlinkService = mavlinkService;

            try
            {
                var vm = VehicleManager.Instance;
                _currentVehicleType = vm.CurrentVehicleType;
                vm.VehicleTypeChanged += (_, profile) =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        // Если тип реально изменился и карта инициализирована
                        if (_currentVehicleType != profile.Type && _isInitialized)
                        {
                            // 1. Сохраняем миссию текущего типа
                            SaveCurrentMissionForType();

                            // 2. Меняем тип
                            _currentVehicleType = profile.Type;

                            // 3. Загружаем миссию нового типа
                            LoadMissionForType(_currentVehicleType);
                        }
                        else
                        {
                            _currentVehicleType = profile.Type;
                        }

                        UpdateVehicleTypeDisplay();
                    });
                };
            }
            catch
            {
                _currentVehicleType = VehicleType.Copter;
            }

            if (_mavlinkService != null)
            {
                _droneUpdateTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(100)
                };
                _droneUpdateTimer.Tick += UpdateDronePosition;
                _droneUpdateTimer.Start();
            }

            // НОВОЕ: Сохраняем MAVLink

            _waypoints = new ObservableCollection<WaypointItem>();
            _waypoints.CollectionChanged += (s, e) =>
            {
                UpdateStatistics();
                UpdateWaypointsList();
            };



            // ... остальной код без изменений

            // Инициализация карты ПОСЛЕ полной загрузки UI через Dispatcher
            this.Loaded += (s, e) =>
            {
                // Инициализируем только ОДИН раз
                if (_isInitialized) return;
                _isInitialized = true;

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        InitializePlanMap();
                        UpdateVehicleTypeDisplay();
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Ошибка инициализации: {ex.Message}");
                    }
                }), DispatcherPriority.Background);
            };

            this.Unloaded += (s, e) =>
            {
                try
                {
                    if (_droneUpdateTimer != null)
                    {
                        _droneUpdateTimer.Stop();
                        _droneUpdateTimer.Tick -= UpdateDronePosition;
                        _droneUpdateTimer = null;
                    }
                }
                catch { }
            };

            this.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape && _isSettingHomeMode)
                {
                    _isSettingHomeMode = false;
                    PlanMap.Cursor = Cursors.Arrow;
                }
            };
        }





        /// <summary>
        /// Инициализация карты планирования
        /// </summary>
        private void InitializePlanMap()
        {
            try
            {
                System.Diagnostics.Debug.WriteLine("Начало инициализации карты планирования...");

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

                System.Diagnostics.Debug.WriteLine($"Режим карты: {(hasInternet ? "ОНЛАЙН" : "ОФЛАЙН")}, кэш: {cacheFolder}");

                // SSL fix
                System.Net.ServicePointManager.ServerCertificateValidationCallback =
                    (snd, certificate, chain, sslPolicyErrors) => true;

                if (PlanMap == null)
                {
                    System.Diagnostics.Debug.WriteLine("ОШИБКА: PlanMap is null!");
                    return;
                }

                // === КЭШИРОВАНИЕ ===
                PlanMap.CacheLocation = cacheFolder;

                // Провайдер
                PlanMap.MapProvider = GMapProviders.GoogleSatelliteMap;

                // Настройки
                PlanMap.Position = new PointLatLng(43.238949, 76.889709);
                PlanMap.Zoom = 17;
                PlanMap.MinZoom = 2;
                PlanMap.MaxZoom = 20;
                PlanMap.MouseWheelZoomEnabled = true;
                PlanMap.MouseWheelZoomType = MouseWheelZoomType.MousePositionAndCenter;
                PlanMap.CanDragMap = true;
                PlanMap.DragButton = MouseButton.Left;
                PlanMap.ShowCenter = false;
                PlanMap.ShowTileGridLines = false;
                PlanMap.Markers.Clear();

                // События
                PlanMap.MouseMove += PlanMap_MouseMove;

                System.Diagnostics.Debug.WriteLine("Карта планирования инициализирована");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Ошибка инициализации карты: {ex.Message}");
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



        private void DownloadMapButton_Click(object sender, RoutedEventArgs e)
        {
            var currentPos = PlanMap?.Position;
            var dialog = new MapDownloaderDialog(currentPos);
            dialog.Owner = OwnerWindow;
            dialog.ShowDialog();
        }

        /// <summary>
        /// Смена провайдера карты
        /// </summary>
        private void MapTypeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (PlanMap == null || MapTypeCombo.SelectedItem == null) return;

            try
            {
                var selected = (ComboBoxItem)MapTypeCombo.SelectedItem;
                var tag = selected.Tag?.ToString();

                switch (tag)
                {
                    case "GoogleSatellite":
                        PlanMap.MapProvider = GMapProviders.GoogleSatelliteMap;
                        System.Diagnostics.Debug.WriteLine("Провайдер изменён: Google Satellite");
                        break;
                    case "GoogleMap":
                        PlanMap.MapProvider = GMapProviders.GoogleMap;
                        System.Diagnostics.Debug.WriteLine("Провайдер изменён: Google Map");
                        break;
                    case "OpenStreetMap":
                        PlanMap.MapProvider = GMapProviders.OpenStreetMap;
                        System.Diagnostics.Debug.WriteLine("Провайдер изменён: OpenStreetMap");
                        break;
                    case "BingSatellite":
                        PlanMap.MapProvider = GMapProviders.BingSatelliteMap;
                        System.Diagnostics.Debug.WriteLine("Провайдер изменён: Bing Satellite");
                        break;
                    case "BingMap":
                        PlanMap.MapProvider = GMapProviders.BingMap;
                        System.Diagnostics.Debug.WriteLine("Провайдер изменён: Bing Map");
                        break;
                }

                PlanMap.ReloadMap();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Ошибка смены провайдера: {ex.Message}");
            }
        }

        /// <summary>
        /// Двойной клик по карте - добавить waypoint
        /// </summary>
        /// 
        /// <summary>
        /// Установить HOME в указанной позиции (для планирования)
        /// </summary>
        private void SetHomeAtPosition(double lat, double lon)
        {
            // Удаляем старый HOME
            if (_homePosition?.Marker != null)
                PlanMap.Markers.Remove(_homePosition.Marker);

            // Создаём новый HOME
            _homePosition = new WaypointItem
            {
                Number = 0,
                Latitude = lat,
                Longitude = lon,
                Altitude = 0,
                CommandType = "HOME",
                Radius = 20
            };

            AddHomeMarkerToMap(_homePosition);
            UpdateRoute();

            MissionStore.SetHome((int)_currentVehicleType, _homePosition);

            System.Diagnostics.Debug.WriteLine($"[HOME] Установлен вручную: {lat:F6}, {lon:F6}");
        }
        private void PlanMap_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (_isSettingHomeMode)
            {
                var point = e.GetPosition(PlanMap);
                var latLng = PlanMap.FromLocalToLatLng((int)point.X, (int)point.Y);

                SetHomeAtPosition(latLng.Lat, latLng.Lng);

                // Выключаем режим
                _isSettingHomeMode = false;
                PlanMap.Cursor = Cursors.Arrow;

                e.Handled = true;
                return;
            }
            if (e.ChangedButton == MouseButton.Left)
            {
                // Получаем позицию клика на карте
                Point clickPoint = e.GetPosition(PlanMap);
                PointLatLng position = PlanMap.FromLocalToLatLng((int)clickPoint.X, (int)clickPoint.Y);

                // Создаём новый waypoint
                var waypoint = new WaypointItem
                {
                    Number = _waypoints.Count + 1,
                    Latitude = position.Lat,
                    Longitude = position.Lng,
                    Altitude = 100, // По умолчанию 100м
                    CommandType = "WAYPOINT",
                    Radius = _waypointRadius // Используем текущий радиус
                };

                _waypoints.Add(waypoint);
                AddMarkerToMap(waypoint);
                UpdateRoute();

                System.Diagnostics.Debug.WriteLine($"Waypoint {waypoint.Number} добавлен: {waypoint.Latitude:F6}, {waypoint.Longitude:F6}");
            }
        }

        /// <summary>
        /// Добавление метки на карту
        /// </summary>
        private void AddMarkerToMap(WaypointItem waypoint)
        {
            var position = new PointLatLng(waypoint.Latitude, waypoint.Longitude);

            // Создаём визуальный элемент
            var shape = CreateMarkerShape(waypoint);

            // Создаём маркер
            var marker = new GMapMarker(position)
            {
                Shape = shape,
                Offset = new Point(-((FrameworkElement)shape).Width / 2, -((FrameworkElement)shape).Height / 2),
                ZIndex = 100
            };

            // Привязываем waypoint к маркеру
            marker.Tag = waypoint;
            waypoint.Marker = marker;

            // Добавляем на карту
            PlanMap.Markers.Add(marker);

            // Drag&Drop
            SetupMarkerDragDrop(marker, waypoint);
        }

        /// <summary>
        /// Создание визуального элемента метки
        /// </summary>
        private UIElement CreateMarkerShape(WaypointItem waypoint)
        {
            // Пересчитываем радиус в метрах в пиксели на основе зума
            double radiusInPixels = MetersToPixels(waypoint.Radius, waypoint.Latitude, PlanMap.Zoom);
            radiusInPixels = Math.Max(3, Math.Min(500, radiusInPixels));

            // МИНИМАЛЬНЫЙ размер Grid для нормального отображения
            double minGridSize = 60;
            double gridSize = Math.Max(minGridSize, radiusInPixels * 2);

            var grid = new Grid
            {
                Width = gridSize,
                Height = gridSize
            };

            // Круг радиуса (показываем только если достаточно большой)
            if (radiusInPixels > 15)
            {
                double strokeThickness = radiusInPixels < 20 ? 3 : 2;

                var radiusCircle = new Ellipse
                {
                    Width = radiusInPixels * 2,
                    Height = radiusInPixels * 2,
                    Stroke = new SolidColorBrush(Color.FromArgb(200, 152, 240, 25)),
                    StrokeThickness = strokeThickness,
                    Fill = new SolidColorBrush(Color.FromArgb(50, 152, 240, 25)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };

                grid.Children.Add(radiusCircle);
                waypoint.RadiusCircle = radiusCircle;
            }
            else
            {
                waypoint.RadiusCircle = null;
            }

            // Центральная точка
            var centerPoint = new Ellipse
            {
                Width = 28,
                Height = 28,
                Fill = new SolidColorBrush(Color.FromRgb(152, 240, 25)),
                Stroke = Brushes.White,
                StrokeThickness = 3,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            // Номер waypoint
            var numberText = new TextBlock
            {
                Text = waypoint.Number.ToString(),
                Foreground = Brushes.Black,
                FontWeight = FontWeights.Bold,
                FontSize = 14,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            grid.Children.Add(centerPoint);
            grid.Children.Add(numberText);

            waypoint.ShapeGrid = grid;

            return grid;
        }
        /// <summary>
        /// Конвертация метров в пиксели на карте (на основе зума)
        /// </summary>
        private double MetersToPixels(double meters, double latitude, double zoom)
        {
            // Формула: пиксели на метр = (256 * 2^zoom) / (40075017 * cos(lat))
            double latRad = latitude * Math.PI / 180.0;
            double metersPerPixel = 40075017 * Math.Cos(latRad) / (256 * Math.Pow(2, zoom));
            return meters / metersPerPixel;
        }

        /// <summary>
        /// Настройка Drag&Drop для метки
        /// </summary>
        private void SetupMarkerDragDrop(GMapMarker marker, WaypointItem waypoint)
        {
            var shape = marker.Shape as FrameworkElement;
            if (shape == null) return;

            shape.MouseLeftButtonDown += (s, e) =>
            {
                _currentDragMarker = marker;
                shape.CaptureMouse();
                PlanMap.CanDragMap = false;
                e.Handled = true;
            };

            shape.MouseMove += (s, e) =>
            {
                if (_currentDragMarker == marker && shape.IsMouseCaptured)
                {
                    Point p = e.GetPosition(PlanMap);
                    var newPosition = PlanMap.FromLocalToLatLng((int)p.X, (int)p.Y);

                    marker.Position = newPosition;
                    waypoint.Latitude = newPosition.Lat;
                    waypoint.Longitude = newPosition.Lng;

                    UpdateRoute();
                    UpdateStatistics();
                }
            };

            shape.MouseLeftButtonUp += (s, e) =>
            {
                if (_currentDragMarker == marker)
                {
                    shape.ReleaseMouseCapture();
                    PlanMap.CanDragMap = true;
                    _currentDragMarker = null;
                }
            };

            // ПКМ - удаление
            shape.MouseRightButtonDown += (s, e) =>
            {
                RemoveWaypoint(waypoint);
                e.Handled = true;
            };
        }

        /// <summary>
        /// Удаление waypoint
        /// </summary>
        private void RemoveWaypoint(WaypointItem waypoint)
        {
            // Удаляем маркер с карты
            if (waypoint.Marker != null)
            {
                PlanMap.Markers.Remove(waypoint.Marker);
            }

            // Удаляем из коллекции
            _waypoints.Remove(waypoint);

            // Перенумеровываем оставшиеся
            RenumberWaypoints();

            // Обновляем линии
            UpdateRoute();

            System.Diagnostics.Debug.WriteLine($"Waypoint {waypoint.Number} удалён");
        }

        /// <summary>
        /// Перенумерация waypoints
        /// </summary>
        private void RenumberWaypoints()
        {
            for (int i = 0; i < _waypoints.Count; i++)
            {
                _waypoints[i].Number = i + 1;

                // Обновляем текст на метке
                if (_waypoints[i].Marker?.Shape is Grid grid)
                {
                    var textBlock = grid.Children.OfType<TextBlock>().FirstOrDefault();
                    if (textBlock != null)
                    {
                        textBlock.Text = (i + 1).ToString();
                    }
                }
            }
        }

        /// <summary>
        /// Обновление линий между метками
        /// </summary>
        private void UpdateRoute()
        {
            // Удаляем старые маршруты
            var oldRoutes = PlanMap.Markers.OfType<GMapRoute>().ToList();
            foreach (var r in oldRoutes)
            {
                PlanMap.Markers.Remove(r);
            }

            // Автоматическое создание HOME если её нет, но дрон подключен
            if (_homePosition == null && _mavlinkService != null &&
                _mavlinkService.CurrentTelemetry.Latitude != 0 &&
                _mavlinkService.CurrentTelemetry.GpsFixType >= 2)
            {
                var telemetry = _mavlinkService.CurrentTelemetry;
                _homePosition = new WaypointItem
                {
                    Number = 0,
                    Latitude = telemetry.Latitude,
                    Longitude = telemetry.Longitude,
                    Altitude = 0,
                    CommandType = "HOME",
                    Radius = 20
                };
                AddHomeMarkerToMap(_homePosition);
                System.Diagnostics.Debug.WriteLine($"HOME AUTO-HOME создан: {telemetry.Latitude:F6}, {telemetry.Longitude:F6}");
            }

            // 1. ПУНКТИРНЫЕ ЛИНИИ ОТ HOME
            if (_homePosition != null && _waypoints.Count > 0)
            {
                var homePoint = new PointLatLng(_homePosition.Latitude, _homePosition.Longitude);

                // От HOME к первой точке
                var firstPoint = new PointLatLng(_waypoints[0].Latitude, _waypoints[0].Longitude);
                var homeToFirstRoute = new GMapRoute(new List<PointLatLng> { homePoint, firstPoint });
                homeToFirstRoute.Shape = new Path
                {
                    Stroke = new SolidColorBrush(Color.FromRgb(239, 68, 68)), // Красный
                    StrokeThickness = 2,
                    StrokeDashArray = new DoubleCollection { 5, 3 }, // ПУНКТИР
                    Opacity = 0.8
                };
                homeToFirstRoute.ZIndex = 40;
                PlanMap.Markers.Add(homeToFirstRoute);

                // От последней точки к HOME
                var lastPoint = new PointLatLng(_waypoints[_waypoints.Count - 1].Latitude,
                                               _waypoints[_waypoints.Count - 1].Longitude);
                var lastToHomeRoute = new GMapRoute(new List<PointLatLng> { lastPoint, homePoint });
                lastToHomeRoute.Shape = new Path
                {
                    Stroke = new SolidColorBrush(Color.FromRgb(239, 68, 68)), // Красный
                    StrokeThickness = 2,
                    StrokeDashArray = new DoubleCollection { 5, 3 }, // ПУНКТИР
                    Opacity = 0.8
                };
                lastToHomeRoute.ZIndex = 40;
                PlanMap.Markers.Add(lastToHomeRoute);
            }

            // 2. ОСНОВНОЙ МАРШРУТ (сплошные линии между waypoints)
            if (_waypoints.Count >= 2)
            {
                var routePoints = _waypoints.Select(w => new PointLatLng(w.Latitude, w.Longitude)).ToList();
                var route = new GMapRoute(routePoints);
                route.Shape = new Path
                {
                    Stroke = new SolidColorBrush(Color.FromRgb(152, 240, 25)), // Зеленый
                    StrokeThickness = 3,
                    Opacity = 0.8
                };
                route.ZIndex = 50;
                PlanMap.Markers.Add(route);
            }

            System.Diagnostics.Debug.WriteLine($"UpdateRoute() - Точек: {_waypoints.Count}, HOME: {_homePosition != null}");
        }

        /// <summary>
        /// Обновить маршрут БЕЗ автосоздания HOME
        /// </summary>
        private void UpdateRouteOnly()
        {
            // Удаляем старые маршруты
            var oldRoutes = PlanMap.Markers.OfType<GMapRoute>().ToList();
            foreach (var r in oldRoutes)
            {
                PlanMap.Markers.Remove(r);
            }

            // НЕ создаём HOME автоматически!

            // 1. Пунктирные линии от HOME
            if (_homePosition != null && _waypoints.Count > 0)
            {
                var homePoint = new PointLatLng(_homePosition.Latitude, _homePosition.Longitude);

                // От HOME к первой точке
                var firstPoint = new PointLatLng(_waypoints[0].Latitude, _waypoints[0].Longitude);
                var homeToFirstRoute = new GMapRoute(new List<PointLatLng> { homePoint, firstPoint });
                homeToFirstRoute.Shape = new Path
                {
                    Stroke = new SolidColorBrush(Color.FromRgb(239, 68, 68)),
                    StrokeThickness = 2,
                    StrokeDashArray = new DoubleCollection { 5, 3 },
                    Opacity = 0.8
                };
                homeToFirstRoute.ZIndex = 40;
                PlanMap.Markers.Add(homeToFirstRoute);

                // От последней точки к HOME
                var lastPoint = new PointLatLng(_waypoints[_waypoints.Count - 1].Latitude,
                                               _waypoints[_waypoints.Count - 1].Longitude);
                var lastToHomeRoute = new GMapRoute(new List<PointLatLng> { lastPoint, homePoint });
                lastToHomeRoute.Shape = new Path
                {
                    Stroke = new SolidColorBrush(Color.FromRgb(239, 68, 68)),
                    StrokeThickness = 2,
                    StrokeDashArray = new DoubleCollection { 5, 3 },
                    Opacity = 0.8
                };
                lastToHomeRoute.ZIndex = 40;
                PlanMap.Markers.Add(lastToHomeRoute);
            }

            // 2. Основной маршрут
            if (_waypoints.Count >= 2)
            {
                var routePoints = _waypoints.Select(w => new PointLatLng(w.Latitude, w.Longitude)).ToList();
                var route = new GMapRoute(routePoints);
                route.Shape = new Path
                {
                    Stroke = new SolidColorBrush(Color.FromRgb(152, 240, 25)),
                    StrokeThickness = 3,
                    Opacity = 0.8
                };
                route.ZIndex = 50;
                PlanMap.Markers.Add(route);
            }
        }



        private void RadiusSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            _waypointRadius = e.NewValue;

            // ОБНОВЛЯЕМ радиус для ВСЕХ существующих waypoints
            if (_waypoints != null && _waypoints.Count > 0)
            {
                foreach (var wp in _waypoints)
                {
                    wp.Radius = _waypointRadius; // КРИТИЧНО: обновляем свойство
                }

                // Теперь перерисовываем с новым радиусом
                RefreshMarkers();
            }
        }

        /// <summary>
        /// Обновление статистики
        /// </summary>
        private void UpdateStatistics()
        {
            WaypointsCountText.Text = $"Точек: {_waypoints.Count}";

            double totalDistance = 0;

            // 1. HOME → первая точка
            if (_homePosition != null && _waypoints.Count > 0)
            {
                totalDistance += CalculateDistanceLatLng(
                    _homePosition.Latitude, _homePosition.Longitude,
                    _waypoints[0].Latitude, _waypoints[0].Longitude);
            }

            // 2. Между всеми точками (WP1→WP2→WP3...)
            for (int i = 0; i < _waypoints.Count - 1; i++)
            {
                totalDistance += CalculateDistance(_waypoints[i], _waypoints[i + 1]);
            }

            // 3. Последняя точка → HOME (RTL)
            if (_homePosition != null && _waypoints.Count > 0)
            {
                var lastWp = _waypoints[_waypoints.Count - 1];
                totalDistance += CalculateDistanceLatLng(
                    lastWp.Latitude, lastWp.Longitude,
                    _homePosition.Latitude, _homePosition.Longitude);
            }

            // Обновляем UI
            string distText = FormatDistance(totalDistance);
            //DistanceText.Text = $"Общая дистанция: {distText}";

            if (TotalDistanceOverlay != null)
                TotalDistanceOverlay.Text = $"Маршрут: {distText}";
        }

        /// <summary>
        /// Расчёт расстояния между двумя точками (метры)
        /// </summary>
        private double CalculateDistance(WaypointItem wp1, WaypointItem wp2)
        {
            const double R = 6371000; // Радиус Земли в метрах
            double dLat = ToRadians(wp2.Latitude - wp1.Latitude);
            double dLon = ToRadians(wp2.Longitude - wp1.Longitude);

            double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                       Math.Cos(ToRadians(wp1.Latitude)) * Math.Cos(ToRadians(wp2.Latitude)) *
                       Math.Sin(dLon / 2) * Math.Sin(dLon / 2);

            double c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
            return R * c;
        }

        private double ToRadians(double degrees) => degrees * Math.PI / 180.0;

        /// <summary>
        /// Обновление списка waypoints в UI
        /// </summary>
        private void UpdateWaypointsList()
        {
            WaypointsListPanel.Children.Clear();

            foreach (var wp in _waypoints)
            {
                // Строка таблицы
                var rowBorder = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(13, 23, 51)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(42, 67, 97)),
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    Padding = new Thickness(4, 4, 4, 4),
                    Margin = new Thickness(0, 0, 0, 2)
                };

                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });              // Номер - фикс
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // Команда - растягивается
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(50) });              // Высота - фикс
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(50) });              // Задержка - фикс
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(85) });              // Кнопки - фикс

                // Номер
                var numberText = new TextBlock
                {
                    Text = wp.Number.ToString(),
                    Foreground = new SolidColorBrush(Color.FromRgb(152, 240, 25)),
                    FontSize = 12,
                    FontWeight = FontWeights.Bold,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                Grid.SetColumn(numberText, 0);

                // ComboBox команды
                var commandCombo = new ComboBox
                {
                    SelectedIndex = 0,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Height = 26,
                    Margin = new Thickness(2, 0, 2, 0),
                    Tag = wp,
                    Style = (Style)Application.Current.FindResource("CompactComboBoxStyle")
                };

                // Список команд БЕЗ TAKEOFF, RTL, SET_HOME (они фиксированы)
                var commands = new[]
                {
                    new { Content = "Путевая точка", Tag = "WAYPOINT" },       // 16
                    new { Content = "Кружение", Tag = "LOITER_UNLIM" },        // 17
                    new { Content = "Кружение (время)", Tag = "LOITER_TIME" }, // 19
                    new { Content = "Посадка", Tag = "LAND" },                 // 21
                    new { Content = "Задержка", Tag = "DELAY" },               // 93
                    new { Content = "Смена скорости", Tag = "CHANGE_SPEED" },  // 178
                };

                foreach (var cmd in commands)
                {
                    var item = new ComboBoxItem
                    {
                        Content = cmd.Content,
                        Tag = cmd.Tag,
                        Style = (Style)Application.Current.FindResource("CustomComboBoxItemStyle")
                    };

                    if (cmd.Tag == wp.CommandType)
                    {
                        item.IsSelected = true;
                    }

                    commandCombo.Items.Add(item);
                }

                commandCombo.SelectionChanged += CommandCombo_SelectionChanged;
                Grid.SetColumn(commandCombo, 1);

                // TextBox высоты
                var altitudeBox = new TextBox
                {
                    Text = wp.Altitude.ToString("F0"),
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Width = 44,
                    Height = 24,
                    FontSize = 10,
                    Background = new SolidColorBrush(Color.FromRgb(26, 36, 51)),
                    Foreground = Brushes.White,
                    BorderBrush = new SolidColorBrush(Color.FromRgb(42, 67, 97)),
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(3, 0, 3, 0),
                    Margin = new Thickness(0, 0, 2, 0),
                    VerticalContentAlignment = VerticalAlignment.Center,
                    Tag = wp
                };

                var altitudeStyle = new Style(typeof(TextBox));
                altitudeStyle.Setters.Add(new Setter(TextBox.TemplateProperty, CreateRoundedTextBoxTemplate()));
                altitudeBox.Style = altitudeStyle;

                altitudeBox.LostFocus += AltitudeBox_LostFocus;
                Grid.SetColumn(altitudeBox, 2);

                // TextBox задержки
                var delayBox = new TextBox
                {
                    Text = wp.Delay.ToString("F0"),
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Width = 44,
                    Height = 24,
                    FontSize = 10,
                    Background = new SolidColorBrush(Color.FromRgb(26, 36, 51)),
                    Foreground = Brushes.White,
                    BorderBrush = new SolidColorBrush(Color.FromRgb(42, 67, 97)),
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(3, 0, 3, 0),
                    Margin = new Thickness(0, 0, 2, 0),
                    VerticalContentAlignment = VerticalAlignment.Center,
                    Tag = wp
                };

                var delayStyle = new Style(typeof(TextBox));
                delayStyle.Setters.Add(new Setter(TextBox.TemplateProperty, CreateRoundedTextBoxTemplate()));
                delayBox.Style = delayStyle;

                delayBox.LostFocus += DelayBox_LostFocus;
                Grid.SetColumn(delayBox, 3);

                // Кнопки действий
                var actionsStack = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };

                // Кнопка вверх
                var upButton = new Button
                {
                    Background = new SolidColorBrush(Color.FromRgb(62, 69, 83)),
                    BorderThickness = new Thickness(1),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(50, 55, 65)),
                    Width = 24,
                    Height = 24,
                    Padding = new Thickness(0),
                    Margin = new Thickness(1, 0, 1, 0),
                    Cursor = Cursors.Hand,
                    Tag = wp
                };

                var upTemplate = new ControlTemplate(typeof(Button));
                var upBorder = new FrameworkElementFactory(typeof(Border));
                upBorder.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
                upBorder.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Button.BorderBrushProperty));
                upBorder.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Button.BorderThicknessProperty));
                upBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(12));

                var upContent = new FrameworkElementFactory(typeof(ContentPresenter));
                upContent.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
                upContent.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
                upBorder.AppendChild(upContent);
                upTemplate.VisualTree = upBorder;
                upButton.Template = upTemplate;

                var upIcon = new Path
                {
                    Data = System.Windows.Media.Geometry.Parse("M 12 4 L 6 10 L 7.41 11.41 L 11 7.83 L 11 20 L 13 20 L 13 7.83 L 16.59 11.41 L 18 10 Z"),
                    Fill = Brushes.White,
                    Stretch = Stretch.Uniform,
                    Width = 12,
                    Height = 12,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                upButton.Content = upIcon;
                upButton.Click += MoveUpButton_Click;

                upButton.MouseEnter += (s, e) =>
                {
                    var anim = new ColorAnimation { To = Color.FromRgb(82, 89, 103), Duration = TimeSpan.FromMilliseconds(150) };
                    upButton.Background.BeginAnimation(SolidColorBrush.ColorProperty, anim);
                };
                upButton.MouseLeave += (s, e) =>
                {
                    var anim = new ColorAnimation { To = Color.FromRgb(62, 69, 83), Duration = TimeSpan.FromMilliseconds(150) };
                    upButton.Background.BeginAnimation(SolidColorBrush.ColorProperty, anim);
                };

                // Кнопка вниз
                var downButton = new Button
                {
                    Background = new SolidColorBrush(Color.FromRgb(62, 69, 83)),
                    BorderThickness = new Thickness(1),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(50, 55, 65)),
                    Width = 24,
                    Height = 24,
                    Padding = new Thickness(0),
                    Margin = new Thickness(1, 0, 1, 0),
                    Cursor = Cursors.Hand,
                    Tag = wp
                };

                var downTemplate = new ControlTemplate(typeof(Button));
                var downBorder = new FrameworkElementFactory(typeof(Border));
                downBorder.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
                downBorder.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Button.BorderBrushProperty));
                downBorder.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Button.BorderThicknessProperty));
                downBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(12));

                var downContent = new FrameworkElementFactory(typeof(ContentPresenter));
                downContent.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
                downContent.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
                downBorder.AppendChild(downContent);
                downTemplate.VisualTree = downBorder;
                downButton.Template = downTemplate;

                var downIcon = new Path
                {
                    Data = System.Windows.Media.Geometry.Parse("M 12 20 L 18 14 L 16.59 12.59 L 13 16.17 L 13 4 L 11 4 L 11 16.17 L 7.41 12.59 L 6 14 Z"),
                    Fill = Brushes.White,
                    Stretch = Stretch.Uniform,
                    Width = 12,
                    Height = 12,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                downButton.Content = downIcon;
                downButton.Click += MoveDownButton_Click;

                downButton.MouseEnter += (s, e) =>
                {
                    var anim = new ColorAnimation { To = Color.FromRgb(82, 89, 103), Duration = TimeSpan.FromMilliseconds(150) };
                    downButton.Background.BeginAnimation(SolidColorBrush.ColorProperty, anim);
                };
                downButton.MouseLeave += (s, e) =>
                {
                    var anim = new ColorAnimation { To = Color.FromRgb(62, 69, 83), Duration = TimeSpan.FromMilliseconds(150) };
                    downButton.Background.BeginAnimation(SolidColorBrush.ColorProperty, anim);
                };

                // Кнопка удалить
                var deleteButton = new Button
                {
                    Background = new SolidColorBrush(Color.FromRgb(239, 68, 68)),
                    BorderThickness = new Thickness(1),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(220, 38, 38)),
                    Width = 24,
                    Height = 24,
                    Padding = new Thickness(0),
                    Margin = new Thickness(1, 0, 1, 0),
                    Cursor = Cursors.Hand,
                    Tag = wp
                };

                var deleteTemplate = new ControlTemplate(typeof(Button));
                var deleteBorder = new FrameworkElementFactory(typeof(Border));
                deleteBorder.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
                deleteBorder.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Button.BorderBrushProperty));
                deleteBorder.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Button.BorderThicknessProperty));
                deleteBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(12));

                var deleteContent = new FrameworkElementFactory(typeof(ContentPresenter));
                deleteContent.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
                deleteContent.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
                deleteBorder.AppendChild(deleteContent);
                deleteTemplate.VisualTree = deleteBorder;
                deleteButton.Template = deleteTemplate;

                var deleteIcon = new Path
                {
                    Data = System.Windows.Media.Geometry.Parse("M 6 19 C 6 20.1 6.9 21 8 21 L 16 21 C 17.1 21 18 20.1 18 19 L 18 7 L 6 7 L 6 19 Z M 19 4 L 15.5 4 L 14.5 3 L 9.5 3 L 8.5 4 L 5 4 L 5 6 L 19 6 L 19 4 Z"),
                    Fill = Brushes.White,
                    Stretch = Stretch.Uniform,
                    Width = 12,
                    Height = 12,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                deleteButton.Content = deleteIcon;
                deleteButton.Click += DeleteButton_Click;

                deleteButton.MouseEnter += (s, e) =>
                {
                    var anim = new ColorAnimation { To = Color.FromRgb(255, 88, 88), Duration = TimeSpan.FromMilliseconds(150) };
                    deleteButton.Background.BeginAnimation(SolidColorBrush.ColorProperty, anim);
                };
                deleteButton.MouseLeave += (s, e) =>
                {
                    var anim = new ColorAnimation { To = Color.FromRgb(239, 68, 68), Duration = TimeSpan.FromMilliseconds(150) };
                    deleteButton.Background.BeginAnimation(SolidColorBrush.ColorProperty, anim);
                };

                // Добавляем кнопки
                actionsStack.Children.Add(upButton);
                actionsStack.Children.Add(downButton);
                actionsStack.Children.Add(deleteButton);
                Grid.SetColumn(actionsStack, 4);

                // Добавляем все в grid
                grid.Children.Add(numberText);
                grid.Children.Add(commandCombo);
                grid.Children.Add(altitudeBox);
                grid.Children.Add(delayBox);
                grid.Children.Add(actionsStack);

                rowBorder.Child = grid;
                WaypointsListPanel.Children.Add(rowBorder);
            }
        }


        /// <summary>
        /// Добавление HOME позиции
        /// </summary>
        private void AddHomePosition()
        {
            if (_mavlinkService == null || _mavlinkService.CurrentTelemetry.Latitude == 0)
            {
                AppMessageBox.ShowWarning(
                    "Дрон не подключен или отсутствует GPS сигнал.",
                    owner: OwnerWindow,
                    subtitle: "Невозможно установить HOME",
                    hint: "Подключитесь к дрону и дождитесь корректного GPS FIX."
                );
                return;
            }

            // Если HOME уже есть - удаляем старую
            if (_homePosition != null)
            {
                if (_homePosition.Marker != null)
                    PlanMap.Markers.Remove(_homePosition.Marker);
            }

            // Создаем новую HOME на текущей позиции дрона
            var telemetry = _mavlinkService.CurrentTelemetry;
            _homePosition = new WaypointItem
            {
                Number = 0,
                Latitude = telemetry.Latitude,
                Longitude = telemetry.Longitude,
                Altitude = 0,
                CommandType = "HOME",
                Radius = 20
            };

            AddHomeMarkerToMap(_homePosition);
            UpdateRoute(); // Обновляем линии

            MissionStore.SetHome((int)_currentVehicleType, _homePosition);

            System.Diagnostics.Debug.WriteLine($" HOME установлена: {telemetry.Latitude:F6}, {telemetry.Longitude:F6}");
        }

        /// <summary>
        /// Добавление HOME маркера на карту
        /// </summary>
        private void AddHomeMarkerToMap(WaypointItem home)
        {
            var position = new PointLatLng(home.Latitude, home.Longitude);
            var shape = CreateHomeMarkerShape();

            var marker = new GMapMarker(position)
            {
                Shape = shape,
                Offset = new Point(-20, -20),
                ZIndex = 150
            };

            home.Marker = marker;
            PlanMap.Markers.Add(marker);
        }


        /// <summary>
        /// Обработчик кнопки установки HOME
        /// </summary>
        private void SetHomeButton_Click(object sender, RoutedEventArgs e)
        {
            // Включаем режим установки HOME
            _isSettingHomeMode = true;
            PlanMap.Cursor = Cursors.Cross;

            // Подсказка пользователю
            AppMessageBox.ShowInfo(
                "Кликните на карте для установки HOME.",
                owner: OwnerWindow,
                subtitle: "Установка HOME"
            );
        }


        /// <summary>
        /// Создание визуального элемента HOME
        /// </summary>
        private UIElement CreateHomeMarkerShape()
        {
            var grid = new Grid { Width = 40, Height = 40 };

            var homeCircle = new Ellipse
            {
                Width = 40,
                Height = 40,
                Fill = new SolidColorBrush(Color.FromArgb(180, 239, 68, 68)),
                Stroke = Brushes.White,
                StrokeThickness = 3
            };

            var homeIcon = new Image
            {
                Source = new System.Windows.Media.Imaging.BitmapImage(
                    new Uri("pack://application:,,,/Images/home_icon.png")),
                Width = 18,
                Height = 18,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            grid.Children.Add(homeCircle);
            grid.Children.Add(homeIcon);

            return grid;
        }


        // НОВЫЙ МЕТОД: Создание закругленного Template для TextBox
        private ControlTemplate CreateRoundedTextBoxTemplate()
        {
            var template = new ControlTemplate(typeof(TextBox));

            var border = new FrameworkElementFactory(typeof(Border));
            border.Name = "border";
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(TextBox.BackgroundProperty));
            border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(TextBox.BorderBrushProperty));
            border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(TextBox.BorderThicknessProperty));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6)); // ЗАКРУГЛЕНИЕ

            var scrollViewer = new FrameworkElementFactory(typeof(ScrollViewer));
            scrollViewer.Name = "PART_ContentHost";
            scrollViewer.SetValue(ScrollViewer.FocusableProperty, false);
            scrollViewer.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
            scrollViewer.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);

            border.AppendChild(scrollViewer);
            template.VisualTree = border;

            return template;
        }

        /// <summary>
        /// Изменение типа команды
        /// </summary>
        private void CommandCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var combo = sender as ComboBox;
            if (combo?.Tag is WaypointItem wp && combo.SelectedItem is ComboBoxItem selectedItem)
            {
                string newCommandType = selectedItem.Tag?.ToString();

                if (!string.IsNullOrEmpty(newCommandType) && wp.CommandType != newCommandType)
                {
                    wp.CommandType = newCommandType;
                    System.Diagnostics.Debug.WriteLine($"WP{wp.Number}: Команда изменена на {newCommandType}");
                }
            }
        }

        /// <summary>
        /// Изменение высоты
        /// </summary>
        private void AltitudeBox_LostFocus(object sender, RoutedEventArgs e)
        {
            var textBox = sender as TextBox;
            var wp = textBox?.Tag as WaypointItem;
            if (wp != null && double.TryParse(textBox.Text, out double altitude))
            {
                wp.Altitude = altitude;
                System.Diagnostics.Debug.WriteLine($"Waypoint {wp.Number} высота изменена на: {altitude}м");
            }
            else if (textBox != null)
            {
                // Возвращаем старое значение если ввод некорректный
                textBox.Text = wp?.Altitude.ToString("F0") ?? "100";
            }
        }

        /// <summary>
        /// Обработчик изменения задержки
        /// </summary>
        private void DelayBox_LostFocus(object sender, RoutedEventArgs e)
        {
            var textBox = sender as TextBox;
            var wp = textBox?.Tag as WaypointItem;
            if (wp != null && double.TryParse(textBox.Text, out double newDelay))
            {
                wp.Delay = newDelay;
            }
        }



        /// <summary>
        /// Кнопка вверх
        /// </summary>
        private void MoveUpButton_Click(object sender, RoutedEventArgs e)
        {
            var waypoint = (sender as Button)?.Tag as WaypointItem;
            if (waypoint == null) return;

            int index = _waypoints.IndexOf(waypoint);
            if (index > 0)
            {
                _waypoints.Move(index, index - 1);
                RenumberWaypoints();
                UpdateRoute();
                UpdateWaypointsList(); // ИСПРАВЛЕНИЕ: обновляем список
            }
        }

        /// <summary>
        /// Кнопка вниз
        /// </summary>
        private void MoveDownButton_Click(object sender, RoutedEventArgs e)
        {
            var waypoint = (sender as Button)?.Tag as WaypointItem;
            if (waypoint == null) return;

            int index = _waypoints.IndexOf(waypoint);
            if (index < _waypoints.Count - 1)
            {
                _waypoints.Move(index, index + 1);
                RenumberWaypoints();
                UpdateRoute();
                UpdateWaypointsList(); // ИСПРАВЛЕНИЕ: обновляем список
            }
        }

        /// <summary>
        /// Кнопка удалить
        /// </summary>
        private void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            var waypoint = (sender as Button)?.Tag as WaypointItem;
            if (waypoint != null)
            {
                RemoveWaypoint(waypoint);
            }
        }





        /// <summary>
        /// Ползунок зума
        /// </summary>
        private void ZoomSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (PlanMap != null)
            {
                PlanMap.Zoom = e.NewValue;

                // Перерисовываем метки с новым зумом (для обновления радиусов)
                RefreshMarkers();
            }
        }

        /// <summary>
        /// Перерисовка всех меток (например при изменении зума или радиуса)
        /// </summary>

        private void RefreshMarkers()
        {
            if (_waypoints == null || _waypoints.Count == 0 || PlanMap == null) return;

            System.Diagnostics.Debug.WriteLine($" RefreshMarkers: обновляем {_waypoints.Count} меток, текущий zoom={PlanMap.Zoom:F1}");

            foreach (var wp in _waypoints)
            {
                // Проверяем что у нас есть сохраненные ссылки
                if (wp.ShapeGrid != null && wp.RadiusCircle != null)
                {
                    double radiusInPixels = MetersToPixels(wp.Radius, wp.Latitude, PlanMap.Zoom);

                    System.Diagnostics.Debug.WriteLine($"   WP{wp.Number}: Radius={wp.Radius:F0}м → radiusInPixels = {radiusInPixels:F2}px (zoom={PlanMap.Zoom:F1})");

                    // РЕАЛИЗМ: Только ограничиваем максимум, НЕ увеличиваем минимум!
                    // Пусть маленькие круги остаются маленькими - это реально!
                    radiusInPixels = Math.Min(500, radiusInPixels); // Максимум 500px (большой радиус)

                    // Минимум 3px чтобы было хоть что-то видно
                    radiusInPixels = Math.Max(3, radiusInPixels);

                    double diameter = radiusInPixels * 2;

                    System.Diagnostics.Debug.WriteLine($"   WP{wp.Number}: radiusInPixels ПОСЛЕ clamp = {radiusInPixels:F0}px (диаметр: {diameter:F0}px)");

                    // КРИТИЧНО: Меняем размеры НАПРЯМУЮ у существующих элементов!
                    wp.ShapeGrid.Width = diameter;
                    wp.ShapeGrid.Height = diameter;

                    wp.RadiusCircle.Width = diameter;
                    wp.RadiusCircle.Height = diameter;

                    // Обновляем Offset маркера (чтобы центр остался на месте)
                    if (wp.Marker != null)
                    {
                        wp.Marker.Offset = new Point(-diameter / 2, -diameter / 2);
                    }

                    // Принудительно обновляем визуал
                    wp.ShapeGrid.InvalidateVisual();
                    wp.RadiusCircle.InvalidateVisual();
                }
                else
                {
                    // Если ссылок нет - пересоздаем маркер (для старых меток)
                    System.Diagnostics.Debug.WriteLine($"   WP{wp.Number}: нет сохраненных ссылок, пересоздаем");

                    if (wp.Marker != null)
                    {
                        PlanMap.Markers.Remove(wp.Marker);
                    }

                    var position = new PointLatLng(wp.Latitude, wp.Longitude);
                    var shape = CreateMarkerShape(wp);

                    var marker = new GMapMarker(position)
                    {
                        Shape = shape,
                        Offset = new Point(-((FrameworkElement)shape).Width / 2, -((FrameworkElement)shape).Height / 2),
                        ZIndex = 100
                    };

                    marker.Tag = wp;
                    wp.Marker = marker;

                    PlanMap.Markers.Add(marker);
                    SetupMarkerDragDrop(marker, wp);
                }
            }

            // Обновляем линии
            UpdateRoute();

            // Принудительное обновление карты
            PlanMap.InvalidateVisual();

            System.Diagnostics.Debug.WriteLine($" RefreshMarkers: завершено\n");
        }

        /// <summary>
        /// Кнопки управления миссией (TODO)
        /// </summary>
        private void AddWaypointButton_Click(object sender, RoutedEventArgs e)
        {
            AppMessageBox.ShowInfo(
                "Функция добавления точки кнопкой пока в разработке.\n\nИспользуйте двойной клик по карте для добавления точки.",
                owner: OwnerWindow,
                subtitle: "В разработке"
            );
        }

        private void ExecuteButton_Click(object sender, RoutedEventArgs e)
        {
            AppMessageBox.ShowInfo("Функция пока в разработке.", owner: OwnerWindow, subtitle: "В разработке");
        }

        private void LoiterButton_Click(object sender, RoutedEventArgs e)
        {
            AppMessageBox.ShowInfo("Функция пока в разработке.", owner: OwnerWindow, subtitle: "В разработке");
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            AppMessageBox.ShowInfo("Функция пока в разработке.", owner: OwnerWindow, subtitle: "В разработке");
        }

        //private void RthButton_Click(object sender, RoutedEventArgs e)
        //{
        // TODO: Возврат на базу (в разработке)
        // }

        private void ToggleButton_Click(object sender, RoutedEventArgs e)
        {
            AppMessageBox.ShowInfo("Функция пока в разработке.", owner: OwnerWindow, subtitle: "В разработке");
        }

        private void UploadButton_Click(object sender, RoutedEventArgs e)
        {
            if (_waypoints == null || _waypoints.Count == 0)
            {
                AppMessageBox.ShowWarning(
                    "Нет точек для сохранения.",
                    owner: OwnerWindow,
                    subtitle: "Пустая миссия",
                    hint: "Добавьте точки двойным кликом по карте."
                );
                return;
            }

            try
            {
                System.Diagnostics.Debug.WriteLine($" Начало сохранения миссии: {_waypoints.Count} точек");

                // КРИТИЧНО: ВСЕГДА сохраняем в файл (для резервной копии и отладки)
                SaveMissionToFile("mission_planned.txt");

                string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                string fullPath = System.IO.Path.Combine(desktopPath, "mission_planned.txt");

                System.Diagnostics.Debug.WriteLine($" Файл сохранён: {fullPath}");

                // Если MAVLink доступен - сохраняем ДОПОЛНИТЕЛЬНО в сервис
                if (_mavlinkService != null)
                {
                    _mavlinkService.SavePlannedMission(GetFullMission());
                    System.Diagnostics.Debug.WriteLine($" Миссия сохранена в MAVLink");

                    // Создаём полную миссию с HOME
                    var fullMission = new List<WaypointItem>();

                    // Добавляем HOME первой точкой
                    if (_homePosition != null)
                    {
                        fullMission.Add(new WaypointItem
                        {
                            Number = 0,
                            Latitude = _homePosition.Latitude,
                            Longitude = _homePosition.Longitude,
                            Altitude = _homePosition.Altitude,
                            CommandType = "HOME",
                            Radius = _homePosition.Radius
                        });
                    }

                    // Добавляем все waypoints
                    fullMission.AddRange(_waypoints.Select(wp => new WaypointItem
                    {
                        Number = wp.Number,
                        Latitude = wp.Latitude,
                        Longitude = wp.Longitude,
                        Altitude = wp.Altitude,
                        CommandType = wp.CommandType,
                        Delay = wp.Delay,
                        Radius = wp.Radius
                    }));

                    MissionStore.Set((int)_currentVehicleType, fullMission);



                    AppMessageBox.ShowSuccess(
                        $"Миссия сохранена: {_waypoints.Count} точек.",
                        owner: OwnerWindow,
                        subtitle: "Миссия сохранена"
                    );
                }
                else
                {
                    // ДОБАВЬ ЭТУ СТРОКУ:
                    MissionStore.Set((int)_currentVehicleType, _waypoints.ToList());

                    AppMessageBox.ShowSuccess(
                        $"Миссия сохранена: {_waypoints.Count} точек.",
                        owner: OwnerWindow,
                        subtitle: "Миссия сохранена"
                    );
                }
            }
            catch (Exception ex)
            {
                AppMessageBox.ShowError(
                    $"Ошибка сохранения: {ex.Message}",
                    owner: OwnerWindow,
                    subtitle: "Ошибка записи миссии",
                    hint: "Проверьте доступ к папке и права на запись."
                );
                System.Diagnostics.Debug.WriteLine($" Ошибка: {ex.Message}\n{ex.StackTrace}");
            }
            //  Сохраняем миссию как активную для отображения на FlightDataView
            if (_mavlinkService != null)
            {
                System.Diagnostics.Debug.WriteLine(" Миссия передана для мониторинга на FlightDataView");
            }
        }



        private void DownloadButton_Click(object sender, RoutedEventArgs e)
        {
            AppMessageBox.ShowInfo("Функция пока в разработке.", owner: OwnerWindow, subtitle: "В разработке");
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            AppMessageBox.ShowInfo("Функция пока в разработке.", owner: OwnerWindow, subtitle: "В разработке");
        }

        private void LoadButton_Click(object sender, RoutedEventArgs e)
        {
            AppMessageBox.ShowInfo("Функция пока в разработке.", owner: OwnerWindow, subtitle: "В разработке");
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            if (AppMessageBox.ShowConfirm(
                "Удалить все точки маршрута?",
                owner: OwnerWindow,
                subtitle: "Подтверждение очистки",
                hint: "Действие необратимо."
            ))
            {
                PlanMap.Markers.Clear();
                _waypoints.Clear();
                System.Diagnostics.Debug.WriteLine("Все waypoints удалены");
            }
        }

        /// <summary>
        /// Сохранение миссии в файл (когда MAVLink недоступен)
        /// </summary>
        private void SaveMissionToFile(string filename)
        {
            // КРИТИЧНО: Получаем полный путь к Desktop для надёжности
            string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            string fullPath = System.IO.Path.Combine(desktopPath, filename);

            System.Diagnostics.Debug.WriteLine($" Сохранение миссии в: {fullPath}");

            var lines = new List<string>();

            // Формат QGroundControl
            lines.Add("QGC WPL 110");

            // HOME точка (первая строка всегда HOME)
            if (_waypoints.Count > 0)
            {
                var first = _waypoints[0];
                lines.Add($"0\t1\t0\t16\t0\t0\t0\t0\t{first.Latitude:F7}\t{first.Longitude:F7}\t{first.Altitude:F2}\t1");
            }

            // Остальные waypoints
            for (int i = 0; i < _waypoints.Count; i++)
            {
                var wp = _waypoints[i];

                // Конвертируем тип команды в MAV_CMD
                ushort mavCmd = ConvertCommandTypeToMAVCmd(wp.CommandType);

                System.Diagnostics.Debug.WriteLine($"  WP{i + 1}: {wp.CommandType} (MAV_CMD={mavCmd}) at {wp.Latitude:F7}, {wp.Longitude:F7}, alt={wp.Altitude:F2}");

                // Формат: index current frame command p1 p2 p3 p4 lat lon alt autocontinue
                lines.Add($"{i + 1}\t0\t3\t{mavCmd}\t{wp.Delay}\t0\t0\t0\t{wp.Latitude:F7}\t{wp.Longitude:F7}\t{wp.Altitude:F2}\t1");
            }

            // КРИТИЧНО: Записываем с перезаписью
            System.IO.File.WriteAllLines(fullPath, lines);

            System.Diagnostics.Debug.WriteLine($" Миссия сохранена в {fullPath}");
            System.Diagnostics.Debug.WriteLine($"   Всего строк: {lines.Count}");
        }

        /// <summary>
        /// Конвертация типа команды в MAV_CMD номер
        /// </summary>
        private ushort ConvertCommandTypeToMAVCmd(string commandType)
        {
            ushort result;

            switch (commandType)
            {
                case "WAYPOINT": result = 16; break;
                case "LOITER_UNLIM": result = 17; break;
                case "LOITER_TIME": result = 19; break;
                case "RETURN_TO_LAUNCH": result = 20; break;
                case "LAND": result = 21; break;
                case "TAKEOFF": result = 22; break;
                case "DELAY": result = 93; break;
                case "CHANGE_SPEED": result = 178; break;
                case "SET_HOME": result = 179; break;
                default:
                    System.Diagnostics.Debug.WriteLine($" Неизвестный тип команды: '{commandType}', использую WAYPOINT");
                    result = 16;
                    break;
            }

            return result;
        }


        /// <summary>
        /// Принудительный зум карты планирования колесиком
        /// </summary>
        private void PlanMap_MouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
        {
            if (PlanMap == null) return;

            // Зумим карту напрямую
            double newZoom = PlanMap.Zoom + (e.Delta > 0 ? 1 : -1);

            // Ограничиваем зум в пределах Min/Max
            if (newZoom >= PlanMap.MinZoom && newZoom <= PlanMap.MaxZoom)
            {
                PlanMap.Zoom = newZoom;

                // Обновляем слайдер зума
                if (ZoomSlider != null)
                {
                    ZoomSlider.Value = newZoom;
                }

                System.Diagnostics.Debug.WriteLine($" Plan Map Zoom: {newZoom}");
            }

            e.Handled = true; // Останавливаем распространение события
        }

        /// <summary>
        /// Создание иконки дрона с линией направления (такой же как на FlightDataView)
        /// </summary>
        private GMapMarker CreateDroneMarker(PointLatLng position)
        {
            var grid = new Grid
            {
                Width = 500,
                Height = 500
            };

            var headingLine = new Line
            {
                X1 = 250,
                Y1 = 250,
                X2 = 250,
                Y2 = 0,
                Stroke = new SolidColorBrush(Color.FromRgb(239, 68, 68)),
                StrokeThickness = 4,
                StrokeEndLineCap = PenLineCap.Triangle,
                Name = "HeadingLine"
            };

            var droneIcon = new Image
            {
                Source = new System.Windows.Media.Imaging.BitmapImage(
                    new Uri("pack://application:,,,/Images/drone_icon.png")),
                Width = 40,
                Height = 40,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            droneIcon.ImageFailed += (s, e) =>
            {
                var fallback = new Ellipse
                {
                    Width = 18,
                    Height = 18,
                    Fill = new SolidColorBrush(Color.FromRgb(152, 240, 25)),
                    Stroke = Brushes.White,
                    StrokeThickness = 2,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };

                grid.Children.Remove(droneIcon);
                grid.Children.Add(fallback);
            };

            grid.Children.Add(headingLine);
            grid.Children.Add(droneIcon);

            var marker = new GMapMarker(position)
            {
                Shape = grid,
                Offset = new Point(-250, -250),
                ZIndex = 1000,
                Tag = grid
            };

            return marker;
        }

        /// <summary>
        /// Обновление позиции дрона на карте планирования
        /// </summary>
        private void UpdateDronePosition(object sender, EventArgs e)
        {
            if (_mavlinkService == null || PlanMap == null) return;
            if (!_mavlinkService.IsConnected) return;

            var telemetry = _mavlinkService.CurrentTelemetry;

            // === АВТОМАТИЧЕСКАЯ УСТАНОВКА HOME ПРИ АРМИРОВАНИИ ===
            if (telemetry.Armed && !_wasArmed)
            {
                // Дрон только что заармился
                if (telemetry.Latitude != 0 && telemetry.Longitude != 0 && telemetry.GpsFixType >= 2)
                {
                    SetHomeFromDronePosition(telemetry.Latitude, telemetry.Longitude);
                }
                _wasArmed = true;
            }
            else if (!telemetry.Armed)
            {
                _wasArmed = false;
            }

            // === ОБНОВЛЕНИЕ ПОЗИЦИИ ДРОНА НА КАРТЕ ===
            if (telemetry.Latitude != 0 && telemetry.Longitude != 0)
            {
                var dronePosition = new PointLatLng(telemetry.Latitude, telemetry.Longitude);

                if (_droneMarker == null)
                {
                    _droneMarker = CreateDroneMarker(dronePosition);
                    PlanMap.Markers.Add(_droneMarker);

                    if (_droneMarker.Tag is Grid grid)
                        grid.RenderTransform = new RotateTransform(telemetry.Heading, 250, 250);
                }
                else
                {
                    _droneMarker.Position = dronePosition;

                    if (_droneMarker.Tag is Grid grid)
                        grid.RenderTransform = new RotateTransform(telemetry.Heading, 250, 250);
                }
            }
            else if (_droneMarker != null && !_mavlinkService.IsConnected)
            {
                PlanMap.Markers.Remove(_droneMarker);
                _droneMarker = null;
            }
        }

        /// <summary>
        /// Установка HOME из позиции дрона при армировании
        /// </summary>
        private void SetHomeFromDronePosition(double lat, double lon)
        {
            // Удаляем старый HOME маркер
            if (_homePosition?.Marker != null)
                PlanMap.Markers.Remove(_homePosition.Marker);

            // Создаём новый HOME
            _homePosition = new WaypointItem
            {
                Number = 0,
                Latitude = lat,
                Longitude = lon,
                Altitude = 0,
                CommandType = "HOME",
                Radius = 20
            };

            AddHomeMarkerToMap(_homePosition);

            // Отправляем SET_HOME в дрон
            _mavlinkService?.SendSetHome(useCurrentLocation: true);

            UpdateRoute();

            // === ДОБАВЬ ЭТУ СТРОКУ ===
            MissionStore.SetHome((int)_currentVehicleType, _homePosition);

            System.Diagnostics.Debug.WriteLine($"[HOME] Автоматически установлен при армировании: {lat:F6}, {lon:F6}");
        }


        /// <summary>
        /// Собрать полную миссию: TAKEOFF + waypoints + RTL
        /// </summary>
        public List<WaypointItem> GetFullMission()
        {
            var mission = new List<WaypointItem>();

            // 1. TAKEOFF
            if (_homePosition != null)
            {
                mission.Add(new WaypointItem
                {
                    Number = 0,
                    Latitude = _homePosition.Latitude,
                    Longitude = _homePosition.Longitude,
                    Altitude = _takeoffAltitude,
                    CommandType = "TAKEOFF"
                });
            }

            // 2. Все waypoints
            foreach (var wp in _waypoints)
            {
                mission.Add(new WaypointItem
                {
                    Number = mission.Count,
                    Latitude = wp.Latitude,
                    Longitude = wp.Longitude,
                    Altitude = wp.Altitude,
                    CommandType = wp.CommandType,
                    Delay = wp.Delay,
                    Radius = wp.Radius
                });
            }

            // 3. RTL
            mission.Add(new WaypointItem
            {
                Number = mission.Count,
                Latitude = 0, // RTL не требует координат
                Longitude = 0,
                Altitude = _rtlAltitude,
                CommandType = "RETURN_TO_LAUNCH"
            });

            return mission;
        }


        #region MISSION CACHE BY TYPE

        /// <summary>
        /// Сохранить текущую миссию для текущего типа (в RAM)
        /// </summary>
        private void SaveCurrentMissionForType()
        {
            // Сохраняем waypoints
            if (_waypoints.Count > 0)
            {
                _missionsByType[_currentVehicleType] = _waypoints.Select(wp => new WaypointItem
                {
                    Number = wp.Number,
                    Latitude = wp.Latitude,
                    Longitude = wp.Longitude,
                    Altitude = wp.Altitude,
                    CommandType = wp.CommandType,
                    Delay = wp.Delay,
                    Radius = wp.Radius
                }).ToList();
            }
            else
            {
                _missionsByType.Remove(_currentVehicleType);
            }

            // Сохраняем HOME
            if (_homePosition != null)
            {
                _homeByType[_currentVehicleType] = new WaypointItem
                {
                    Number = 0,
                    Latitude = _homePosition.Latitude,
                    Longitude = _homePosition.Longitude,
                    Altitude = _homePosition.Altitude,
                    CommandType = "HOME",
                    Radius = _homePosition.Radius
                };
            }
            else
            {
                _homeByType.Remove(_currentVehicleType);
            }

            System.Diagnostics.Debug.WriteLine($"[Mission] Сохранено: {_currentVehicleType} = {_waypoints.Count} точек");
        }

        /// <summary>
        /// Загрузить миссию для указанного типа (из RAM на карту)
        /// </summary>
        /// <summary>
/// Загрузить миссию для указанного типа (из RAM на карту)
/// </summary>
private void LoadMissionForType(VehicleType type)
{
    System.Diagnostics.Debug.WriteLine($"[LoadMission] Загрузка миссии для {type}...");

    // 1. Удаляем ВСЕ маркеры waypoints с карты
    foreach (var wp in _waypoints)
    {
        if (wp.Marker != null)
        {
            PlanMap.Markers.Remove(wp.Marker);
            wp.Marker = null;
        }
    }

    // 2. Удаляем HOME маркер
    if (_homePosition?.Marker != null)
    {
        PlanMap.Markers.Remove(_homePosition.Marker);
        _homePosition.Marker = null;
    }

    // 3. Удаляем ВСЕ маршруты (линии)
    var oldRoutes = PlanMap.Markers.OfType<GMapRoute>().ToList();
    foreach (var r in oldRoutes)
    {
        PlanMap.Markers.Remove(r);
    }

    // 4. Очищаем коллекции БЕЗ триггера CollectionChanged
    var tempCollection = _waypoints;
    _waypoints = new ObservableCollection<WaypointItem>();
    tempCollection.Clear();
    
    // Восстанавливаем подписку
    _waypoints.CollectionChanged += (s, e) =>
    {
        UpdateStatistics();
        UpdateWaypointsList();
    };

    _homePosition = null;

    System.Diagnostics.Debug.WriteLine($"[LoadMission] Карта очищена. Загружаем тип {type}...");

    // 5. Загружаем HOME для нового типа
    if (_homeByType.TryGetValue(type, out var savedHome) && savedHome != null)
    {
        _homePosition = new WaypointItem
        {
            Number = 0,
            Latitude = savedHome.Latitude,
            Longitude = savedHome.Longitude,
            Altitude = savedHome.Altitude,
            CommandType = "HOME",
            Radius = savedHome.Radius
        };
        AddHomeMarkerToMap(_homePosition);
        System.Diagnostics.Debug.WriteLine($"[LoadMission] HOME загружен: {savedHome.Latitude:F6}, {savedHome.Longitude:F6}");
    }

    // 6. Загружаем waypoints для нового типа
    if (_missionsByType.TryGetValue(type, out var savedWaypoints) && savedWaypoints != null)
    {
        foreach (var wp in savedWaypoints)
        {
            var newWp = new WaypointItem
            {
                Number = _waypoints.Count + 1,
                Latitude = wp.Latitude,
                Longitude = wp.Longitude,
                Altitude = wp.Altitude,
                CommandType = wp.CommandType,
                Delay = wp.Delay,
                Radius = wp.Radius > 0 ? wp.Radius : _waypointRadius
            };
            _waypoints.Add(newWp);
            AddMarkerToMap(newWp);
        }
        System.Diagnostics.Debug.WriteLine($"[LoadMission] Загружено {savedWaypoints.Count} точек");
    }
    else
    {
        System.Diagnostics.Debug.WriteLine($"[LoadMission] Нет сохранённой миссии для {type}");
    }


    // 7. Обновляем UI (без авто-создания HOME!)
    UpdateRouteOnly();  // ← Новый метод!
    UpdateStatistics();
    UpdateWaypointsList();

    System.Diagnostics.Debug.WriteLine($"[LoadMission] Завершено: {_waypoints.Count} точек, HOME: {_homePosition != null}");
}

        #endregion



        #region VEHICLE TYPE SELECTOR

        private void VehicleTypeSelector_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            try
            {
                var menu = BuildVehicleTypeMenu();
                menu.PlacementTarget = VehicleTypeSelector;
                menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                menu.IsOpen = true;
                e.Handled = true;
            }
            catch (Exception ex)
            {
                AppMessageBox.ShowError(
                    $"Не удалось открыть выбор типа аппарата: {ex.Message}",
                    owner: OwnerWindow,
                    subtitle: "Ошибка",
                    hint: "Повторите попытку. Если ошибка повторяется — проверьте логи."
                );
            }
        }

        private ContextMenu BuildVehicleTypeMenu()
        {
            var menu = new ContextMenu
            {
                Background = (SolidColorBrush)new BrushConverter().ConvertFromString("#0D1733"),
                BorderBrush = (SolidColorBrush)new BrushConverter().ConvertFromString("#2A4361"),
                BorderThickness = new Thickness(2),
                Padding = new Thickness(6),
                HasDropShadow = true
            };

            // Единый стиль пунктов меню
            var itemStyle = new Style(typeof(MenuItem));
            itemStyle.Setters.Add(new Setter(MenuItem.ForegroundProperty, Brushes.White));
            itemStyle.Setters.Add(new Setter(MenuItem.BackgroundProperty, Brushes.Transparent));
            itemStyle.Setters.Add(new Setter(MenuItem.PaddingProperty, new Thickness(10, 8, 10, 8)));
            itemStyle.Setters.Add(new Setter(MenuItem.CursorProperty, Cursors.Hand));
            itemStyle.Setters.Add(new Setter(MenuItem.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));

            var hoverTrigger = new Trigger { Property = MenuItem.IsHighlightedProperty, Value = true };
            hoverTrigger.Setters.Add(new Setter(MenuItem.BackgroundProperty,
                (SolidColorBrush)new BrushConverter().ConvertFromString("#1A2433")));
            itemStyle.Triggers.Add(hoverTrigger);

            menu.Resources[typeof(MenuItem)] = itemStyle;

            var copter = new MenuItem
            {
                Header = BuildVehicleMenuHeader("/Images/drone_icon.png", "Мультикоптер", "MC"),
                Tag = VehicleType.Copter
            };
            copter.Click += VehicleTypeMenuItem_Click;

            var vtol = new MenuItem
            {
                Header = BuildVehicleMenuHeader("/Images/pl.png", "СВВП", "VTOL"),
                Tag = VehicleType.QuadPlane
            };
            vtol.Click += VehicleTypeMenuItem_Click;

            menu.Items.Add(copter);
            menu.Items.Add(vtol);

            return menu;
        }

        private static UIElement BuildVehicleMenuHeader(string iconPath, string title, string shortCode)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var icon = new Image
            {
                Source = new System.Windows.Media.Imaging.BitmapImage(new Uri($"pack://application:,,,{iconPath}")),
                Width = 18,
                Height = 18,
                Stretch = Stretch.Uniform,
                Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(icon, 0);

            var text = new TextBlock
            {
                Text = title,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(text, 1);

            var badge = new Border
            {
                Background = (SolidColorBrush)new BrushConverter().ConvertFromString("#122244"),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(6, 2, 6, 2),
                VerticalAlignment = VerticalAlignment.Center
            };
            var badgeText = new TextBlock
            {
                Text = shortCode,
                Foreground = (SolidColorBrush)new BrushConverter().ConvertFromString("#98F019"),
                FontWeight = FontWeights.Bold,
                FontSize = 11
            };
            badge.Child = badgeText;
            Grid.SetColumn(badge, 2);

            grid.Children.Add(icon);
            grid.Children.Add(text);
            grid.Children.Add(badge);

            return grid;
        }

        private void VehicleTypeMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem mi || mi.Tag is not VehicleType newType)
                return;

            if (newType == _currentVehicleType)
                return;

            bool ok = AppMessageBox.ShowConfirm(
                "Переключить тип аппарата?",
                owner: OwnerWindow,
                subtitle: "Смена типа аппарата"
            );

            if (!ok) return;

            try
            {
                // 1. Сохраняем текущую миссию
                SaveCurrentMissionForType();

                // 2. Меняем тип
                VehicleManager.Instance.SetVehicleType(newType);
                _currentVehicleType = newType;

                if (_mavlinkService != null)
                {
                    var mavType = (byte)VehicleManager.Instance.CurrentProfile.MavType;
                    _mavlinkService.SetVehicleType(mavType);
                }

                // 3. Загружаем миссию нового типа
                LoadMissionForType(newType);

                // 4. Обновляем UI
                UpdateVehicleTypeDisplay();
            }
            catch (Exception ex)
            {
                AppMessageBox.ShowError(
                    $"Ошибка: {ex.Message}",
                    owner: OwnerWindow,
                    subtitle: "Ошибка переключения"
                );
            }
        }

        private void UpdateVehicleTypeDisplay()
        {
            try
            {
                var profile = VehicleManager.Instance.CurrentProfile;
                _currentVehicleType = profile.Type;

                if (VehicleTypeShortText != null)
                    VehicleTypeShortText.Text = profile.Type == VehicleType.Copter ? "MC" : "VTOL";

                if (VehicleTypeFullText != null)
                    VehicleTypeFullText.Text = profile.Type == VehicleType.Copter ? "Мультикоптер" : "СВВП";
            }
            catch
            {
                if (VehicleTypeShortText != null) VehicleTypeShortText.Text = "MC";
                if (VehicleTypeFullText != null) VehicleTypeFullText.Text = "Мультикоптер";
            }
        }

        #endregion

        private void DownloadSRTM_Click(object sender, RoutedEventArgs e)
        {
            // Берём текущую позицию карты
            var center = PlanMap.Position;

            var dialog = new UI.Dialogs.SRTMDownloadDialog(center.Lat, center.Lng);
            dialog.Owner = Window.GetWindow(this);
            dialog.ShowDialog();
        }

        #region CURSOR DISTANCE

        private void PlanMap_MouseMove(object sender, MouseEventArgs e)
        {
            var point = e.GetPosition(PlanMap);
            var cursorLatLng = PlanMap.FromLocalToLatLng((int)point.X, (int)point.Y);

           

            // === ОТЛАДКА ===
            System.Diagnostics.Debug.WriteLine($"[MouseMove] Lat={cursorLatLng.Lat:F4}, Lng={cursorLatLng.Lng:F4}");

            // === КООРДИНАТЫ КУРСОРА ===
            if (CursorLatText != null)
                CursorLatText.Text = cursorLatLng.Lat.ToString("F6");

            if (CursorLngText != null)
                CursorLngText.Text = cursorLatLng.Lng.ToString("F6");

            // === КООРДИНАТЫ И ВЫСОТА ===
            if (CursorLatText != null)
                CursorLatText.Text = cursorLatLng.Lat.ToString("F6");

            if (CursorLngText != null)
                CursorLngText.Text = cursorLatLng.Lng.ToString("F6");

            // Высота из SRTM
            if (CursorAltText != null)
            {
                System.Diagnostics.Debug.WriteLine($"[SRTM] Запрос высоты...");
                double? elevation = _elevationProvider.GetElevation(cursorLatLng.Lat, cursorLatLng.Lng);
                System.Diagnostics.Debug.WriteLine($"[SRTM] Результат: {elevation?.ToString() ?? "NULL"}");
                CursorAltText.Text = elevation.HasValue ? $"{elevation.Value:F0} м" : "— м";
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[SRTM] CursorAltText is NULL!");
            }

            // === ДИСТАНЦИЯ ОТ ПОСЛЕДНЕЙ ТОЧКИ ===
            if (_waypoints.Count > 0 && CursorDistanceFromLast != null)
            {
                var lastWp = _waypoints[_waypoints.Count - 1];
                double dist = CalculateDistanceLatLng(lastWp.Latitude, lastWp.Longitude,
                                                       cursorLatLng.Lat, cursorLatLng.Lng);
                CursorDistanceFromLast.Text = $"От WP{lastWp.Number}: {FormatDistance(dist)}";
            }
            else if (CursorDistanceFromLast != null)
            {
                CursorDistanceFromLast.Text = "От WP: —";
            }

            // === ДИСТАНЦИЯ ОТ HOME ===
            if (_homePosition != null && CursorDistanceFromHome != null)
            {
                double dist = CalculateDistanceLatLng(_homePosition.Latitude, _homePosition.Longitude,
                                                       cursorLatLng.Lat, cursorLatLng.Lng);
                CursorDistanceFromHome.Text = $"От HOME: {FormatDistance(dist)}";
            }
            else if (CursorDistanceFromHome != null)
            {
                CursorDistanceFromHome.Text = "От HOME: —";
            }
        }

        private double CalculateDistanceLatLng(double lat1, double lon1, double lat2, double lon2)
        {
            const double R = 6371000; // радиус Земли в метрах
            double dLat = (lat2 - lat1) * Math.PI / 180;
            double dLon = (lon2 - lon1) * Math.PI / 180;
            double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                       Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180) *
                       Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
            double c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
            return R * c;
        }

        private string FormatDistance(double meters)
        {
            return $"{meters:F0} м";
        }
    }

       #endregion 


    public class WaypointItem : INotifyPropertyChanged
    {
        private int _number;
        private double _delay;
        private double _latitude;
        private double _longitude;
        private double _altitude;
        private string _commandType;
        private double _radius;
        public GMapMarker Marker { get; set; }

        // НОВОЕ: Сохраняем ссылки на визуальные элементы для прямого изменения
        public Grid ShapeGrid { get; set; }
        public Ellipse RadiusCircle { get; set; }

        public int Number
        {
            get => _number;
            set { _number = value; OnPropertyChanged(); }
        }

        public double Latitude
        {
            get => _latitude;
            set { _latitude = value; OnPropertyChanged(); }
        }

        public double Longitude
        {
            get => _longitude;
            set { _longitude = value; OnPropertyChanged(); }
        }

        public double Altitude
        {
            get => _altitude;
            set { _altitude = value; OnPropertyChanged(); }
        }

        public string CommandType
        {
            get => _commandType;
            set { _commandType = value; OnPropertyChanged(); }
        }

        public double Radius
        {
            get => _radius;
            set { _radius = value; OnPropertyChanged(); }
        }
        public double Delay
        {
            get => _delay;
            set { _delay = value; OnPropertyChanged(); }
        }


        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }


    }


}