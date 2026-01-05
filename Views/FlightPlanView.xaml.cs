using GMap.NET;
using GMap.NET.MapProviders;
using GMap.NET.WindowsPresentation;
using SimpleDroneGCS.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
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
        private ObservableCollection<WaypointItem> _waypoints;
        private GMapMarker _currentDragMarker;
        private double _waypointRadius = 30; // метры
        private MAVLinkService _mavlinkService;
        private GMapMarker _droneMarker = null;
        private WaypointItem _homePosition = null; // HOME позиция
        private DispatcherTimer _droneUpdateTimer; // ДОБАВЬ

        public FlightPlanView(MAVLinkService mavlinkService = null)
        {
            InitializeComponent();
            _mavlinkService = mavlinkService;


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
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        InitializePlanMap();
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Ошибка отложенной инициализации карты планирования: {ex.Message}");
                    }
                }), System.Windows.Threading.DispatcherPriority.Background);
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

                // КРИТИЧНО: Настройка GMaps.Instance ПЕРЕД всем остальным
                try
                {
                    GMap.NET.GMaps.Instance.Mode = GMap.NET.AccessMode.ServerAndCache;
                    System.Diagnostics.Debug.WriteLine("Plan GMaps.Instance.Mode установлен");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Plan Mode ошибка: {ex.Message}");
                }

                // SSL fix
                try
                {
                    System.Net.ServicePointManager.ServerCertificateValidationCallback =
                        (snd, certificate, chain, sslPolicyErrors) => true;
                    System.Diagnostics.Debug.WriteLine("Plan SSL fix применён");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Plan SSL fix ошибка: {ex.Message}");
                }

                // Проверяем что PlanMap не null
                if (PlanMap == null)
                {
                    System.Diagnostics.Debug.WriteLine("ОШИБКА: PlanMap is null!");
                    return;
                }

                System.Diagnostics.Debug.WriteLine("PlanMap существует, устанавливаем провайдер...");

                // Пробуем разные провайдеры
                bool mapLoaded = false;

                // 1. ✅ Google Satellite (по умолчанию)
                if (!mapLoaded)
                {
                    try
                    {
                        PlanMap.MapProvider = GMapProviders.GoogleSatelliteMap;
                        mapLoaded = true;
                        System.Diagnostics.Debug.WriteLine("✅ План карта: Google Satellite загружена");
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Plan Google Satellite ошибка: {ex.Message}");
                    }
                }

                // 2. OpenStreetMap
                if (!mapLoaded)
                {
                    try
                    {
                        PlanMap.MapProvider = GMapProviders.OpenStreetMap;
                        mapLoaded = true;
                        System.Diagnostics.Debug.WriteLine("✅ План карта: OpenStreetMap загружена");
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Plan OpenStreetMap ошибка: {ex.Message}");
                    }
                }

                // 3. BingMap
                if (!mapLoaded)
                {
                    try
                    {
                        PlanMap.MapProvider = GMapProviders.BingMap;
                        mapLoaded = true;
                        System.Diagnostics.Debug.WriteLine("✅ План карта: BingMap загружена");
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Plan BingMap ошибка: {ex.Message}");
                    }
                }

                // 4. EmptyProvider
                if (!mapLoaded)
                {
                    try
                    {
                        PlanMap.MapProvider = GMapProviders.EmptyProvider;
                        System.Diagnostics.Debug.WriteLine("⚠️ План карта: EmptyProvider (оффлайн)");
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Plan EmptyProvider ошибка: {ex.Message}");
                        return;
                    }
                }

                // Настройки карты
                try
                {
                    // В InitializePlanMap найди эти строки и измени:
                    PlanMap.Position = new PointLatLng(43.238949, 76.889709); // Алматы
                    PlanMap.Zoom = 17; // БЫЛО 15 → СТАЛО 17 (ближе)
                    PlanMap.MinZoom = 2;
                    PlanMap.MaxZoom = 20;
                    PlanMap.MouseWheelZoomEnabled = true;
                    PlanMap.MouseWheelZoomType = MouseWheelZoomType.MousePositionAndCenter;
                    PlanMap.CanDragMap = true;
                    PlanMap.DragButton = MouseButton.Left;
                    PlanMap.ShowCenter = false;
                    PlanMap.ShowTileGridLines = false;
                    PlanMap.Markers.Clear();

                    System.Diagnostics.Debug.WriteLine($"✅ План карта полностью инициализирована: {PlanMap.MapProvider}");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Plan настройки ошибка: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"❌ КРИТИЧЕСКАЯ ошибка инициализации карты планирования: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"StackTrace: {ex.StackTrace}");

                // НЕ показываем MessageBox если это NullReferenceException из GMap
                if (!(ex is NullReferenceException))
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        MessageBox.Show(
                            $"Карта планирования не загрузилась, но приложение работает.\n\nОшибка: {ex.Message}",
                            "Предупреждение",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning);
                    }));
                }
            }
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
        private void PlanMap_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
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

            // РЕАЛИЗМ: только ограничиваем, не масштабируем!
            radiusInPixels = Math.Max(3, Math.Min(500, radiusInPixels));

            System.Diagnostics.Debug.WriteLine($"    CreateMarkerShape WP{waypoint.Number}: {waypoint.Radius:F0}м → {radiusInPixels:F2}px @ zoom {PlanMap.Zoom:F1}");

            var grid = new Grid
            {
                Width = radiusInPixels * 2,
                Height = radiusInPixels * 2
            };

            // КРИТИЧНО: Делаем границу ТОЛЩЕ для маленьких кругов (чтобы их было видно)
            double strokeThickness = radiusInPixels < 20 ? 3 : 2; // Если маленький - толстая граница

            // Радиус (круг)
            var radiusCircle = new Ellipse
            {
                Width = radiusInPixels * 2,
                Height = radiusInPixels * 2,
                Stroke = new SolidColorBrush(Color.FromArgb(200, 152, 240, 25)), // Ярче для видимости
                StrokeThickness = strokeThickness,
                Fill = new SolidColorBrush(Color.FromArgb(50, 152, 240, 25)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            // Центральная точка - КРУПНЕЕ для видимости
            var centerPoint = new Ellipse
            {
                Width = 24, // Было 20
                Height = 24,
                Fill = new SolidColorBrush(Color.FromRgb(152, 240, 25)),
                Stroke = Brushes.White,
                StrokeThickness = 3, // Было 2
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            // Номер - крупнее
            var numberText = new TextBlock
            {
                Text = waypoint.Number.ToString(),
                Foreground = Brushes.Black,
                FontWeight = FontWeights.Bold,
                FontSize = 14, // Было 12
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            grid.Children.Add(radiusCircle);
            grid.Children.Add(centerPoint);
            grid.Children.Add(numberText);

            // Сохраняем ссылки
            waypoint.ShapeGrid = grid;
            waypoint.RadiusCircle = radiusCircle;

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
                System.Diagnostics.Debug.WriteLine($"🏠 AUTO-HOME создан: {telemetry.Latitude:F6}, {telemetry.Longitude:F6}");
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

            // Расчёт общей дистанции (простая формула Haversine)
            double totalDistance = 0;
            for (int i = 0; i < _waypoints.Count - 1; i++)
            {
                totalDistance += CalculateDistance(_waypoints[i], _waypoints[i + 1]);
            }

            DistanceText.Text = $"Общая дистанция: {totalDistance:F0} м";
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
                    Padding = new Thickness(0, 5, 5, 5),
                    Margin = new Thickness(0, 0, 0, 2)
                };

                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });

                // Номер
                var numberText = new TextBlock
                {
                    Text = wp.Number.ToString(),
                    Foreground = new SolidColorBrush(Color.FromRgb(152, 240, 25)),
                    FontSize = 14,
                    FontWeight = FontWeights.Bold,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                Grid.SetColumn(numberText, 0);

                // ComboBox команды с расширенным списком
                var commandCombo = new ComboBox
                {
                    SelectedIndex = 0, // По умолчанию первая
                    VerticalAlignment = VerticalAlignment.Center,
                    Height = 28,
                    FontSize = 11,
                    Margin = new Thickness(5, 0, 5, 0),
                    Tag = wp,
                    Style = (Style)Application.Current.FindResource("CustomComboBoxStyle")
                };

                // РАСШИРЕННЫЙ СПИСОК MAV_CMD команд
                var commands = new[]
                {
                    new { Content = "Путевая точка", Tag = "WAYPOINT" },       // MAV_CMD_NAV_WAYPOINT (16)
                    new { Content = "Кружение", Tag = "LOITER_UNLIM" },        // MAV_CMD_NAV_LOITER_UNLIM (17)
                    new { Content = "Кружение (время)", Tag = "LOITER_TIME" }, // MAV_CMD_NAV_LOITER_TIME (19)
                    new { Content = "Возврат домой", Tag = "RETURN_TO_LAUNCH" }, // MAV_CMD_NAV_RETURN_TO_LAUNCH (20)
                    new { Content = "Посадка", Tag = "LAND" },                 // MAV_CMD_NAV_LAND (21)
                    new { Content = "Взлёт", Tag = "TAKEOFF" },                // MAV_CMD_NAV_TAKEOFF (22)
                    new { Content = "Задержка", Tag = "DELAY" },               // MAV_CMD_NAV_DELAY (93)
                    new { Content = "Смена скорости", Tag = "CHANGE_SPEED" },  // MAV_CMD_DO_CHANGE_SPEED (178)
                    new { Content = "Установить HOME", Tag = "SET_HOME" },     // MAV_CMD_DO_SET_HOME (179)
                };

                foreach (var cmd in commands)
                {
                    var item = new ComboBoxItem
                    {
                        Content = cmd.Content,
                        Tag = cmd.Tag,
                        Style = (Style)Application.Current.FindResource("CustomComboBoxItemStyle")
                    };

                    // Выбираем нужный элемент
                    if (cmd.Tag == wp.CommandType)
                    {
                        item.IsSelected = true;
                    }

                    commandCombo.Items.Add(item);
                }

                commandCombo.SelectionChanged += CommandCombo_SelectionChanged;
                Grid.SetColumn(commandCombo, 1);

                // TextBox высоты с закруглением
                var altitudeBox = new TextBox
                {
                    Text = wp.Altitude.ToString("F0"),
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Height = 28,
                    FontSize = 11,
                    Background = new SolidColorBrush(Color.FromRgb(26, 36, 51)),
                    Foreground = Brushes.White,
                    BorderBrush = new SolidColorBrush(Color.FromRgb(42, 67, 97)),
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(5, 0, 5, 0),
                    Margin = new Thickness(0, 0, 0, 0),
                    VerticalContentAlignment = VerticalAlignment.Center,
                    Tag = wp
                };

                // ДОБАВЛЯЕМ ЗАКРУГЛЕНИЕ
                // СНАЧАЛА создаем Style, ПОТОМ присваиваем
                var altitudeStyle = new Style(typeof(TextBox));
                altitudeStyle.Setters.Add(new Setter(TextBox.TemplateProperty, CreateRoundedTextBoxTemplate()));
                altitudeBox.Style = altitudeStyle;

                altitudeBox.LostFocus += AltitudeBox_LostFocus;
                Grid.SetColumn(altitudeBox, 2);

                // TextBox задержки с закруглением
                var delayBox = new TextBox
                {
                    Text = wp.Delay.ToString("F0"),
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Height = 28,
                    FontSize = 11,
                    Background = new SolidColorBrush(Color.FromRgb(26, 36, 51)),
                    Foreground = Brushes.White,
                    BorderBrush = new SolidColorBrush(Color.FromRgb(42, 67, 97)),
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(5, 0, 5, 0),
                    Margin = new Thickness(0, 0, 10, 0),
                    VerticalContentAlignment = VerticalAlignment.Center,
                    Tag = wp
                };

                // ДОБАВЛЯЕМ ЗАКРУГЛЕНИЕ
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

                // Кнопка вверх (КРУГЛАЯ)
                var upButton = new Button
                {
                    Background = new SolidColorBrush(Color.FromRgb(62, 69, 83)),
                    BorderThickness = new Thickness(1),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(50, 55, 65)),
                    Width = 30,
                    Height = 30,
                    Padding = new Thickness(0),
                    Margin = new Thickness(2, 0, 2, 0),
                    Cursor = Cursors.Hand,
                    Tag = wp
                };

                // Создаем Template для круглой кнопки
                var upTemplate = new ControlTemplate(typeof(Button));
                var upBorder = new FrameworkElementFactory(typeof(Border));
                upBorder.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
                upBorder.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Button.BorderBrushProperty));
                upBorder.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Button.BorderThicknessProperty));
                upBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(15)); // КРУГЛАЯ

                var upContent = new FrameworkElementFactory(typeof(ContentPresenter));
                upContent.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
                upContent.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
                upBorder.AppendChild(upContent);
                upTemplate.VisualTree = upBorder;
                upButton.Template = upTemplate;

                // Кнопка вверх - ВЕКТОРНАЯ иконка
                var upIcon = new Path
                {
                    Data = System.Windows.Media.Geometry.Parse("M 12 4 L 6 10 L 7.41 11.41 L 11 7.83 L 11 20 L 13 20 L 13 7.83 L 16.59 11.41 L 18 10 Z"),
                    Fill = Brushes.White,
                    Stretch = Stretch.Uniform,
                    Width = 16,
                    Height = 16,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                upButton.Content = upIcon;

                upButton.Click += MoveUpButton_Click;

                // Эффект наведения
                // Для кнопки ВВЕРХ с анимацией
                upButton.MouseEnter += (s, e) =>
                {
                    var anim = new ColorAnimation
                    {
                        To = Color.FromRgb(82, 89, 103),
                        Duration = TimeSpan.FromMilliseconds(150)
                    };
                    upButton.Background.BeginAnimation(SolidColorBrush.ColorProperty, anim);
                };
                upButton.MouseLeave += (s, e) =>
                {
                    var anim = new ColorAnimation
                    {
                        To = Color.FromRgb(62, 69, 83),
                        Duration = TimeSpan.FromMilliseconds(150)
                    };
                    upButton.Background.BeginAnimation(SolidColorBrush.ColorProperty, anim);
                };
                upButton.PreviewMouseDown += (s, e) =>
                {
                    var anim = new ColorAnimation
                    {
                        To = Color.FromRgb(42, 49, 63),
                        Duration = TimeSpan.FromMilliseconds(100)
                    };
                    upButton.Background.BeginAnimation(SolidColorBrush.ColorProperty, anim);
                };
                upButton.PreviewMouseUp += (s, e) =>
                {
                    var anim = new ColorAnimation
                    {
                        To = Color.FromRgb(82, 89, 103),
                        Duration = TimeSpan.FromMilliseconds(100)
                    };
                    upButton.Background.BeginAnimation(SolidColorBrush.ColorProperty, anim);
                };

                // Кнопка вниз (КРУГЛАЯ)
                var downButton = new Button
                {
                    Background = new SolidColorBrush(Color.FromRgb(62, 69, 83)),
                    BorderThickness = new Thickness(1),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(50, 55, 65)),
                    Width = 30,
                    Height = 30,
                    Padding = new Thickness(0),
                    Margin = new Thickness(2, 0, 2, 0),
                    Cursor = Cursors.Hand,
                    Tag = wp
                };

                var downTemplate = new ControlTemplate(typeof(Button));
                var downBorder = new FrameworkElementFactory(typeof(Border));
                downBorder.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
                downBorder.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Button.BorderBrushProperty));
                downBorder.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Button.BorderThicknessProperty));
                downBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(15)); // КРУГЛАЯ

                var downContent = new FrameworkElementFactory(typeof(ContentPresenter));
                downContent.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
                downContent.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
                downBorder.AppendChild(downContent);
                downTemplate.VisualTree = downBorder;
                downButton.Template = downTemplate;

                // Кнопка вниз - ВЕКТОРНАЯ иконка
                var downIcon = new Path
                {
                    Data = System.Windows.Media.Geometry.Parse("M 12 20 L 18 14 L 16.59 12.59 L 13 16.17 L 13 4 L 11 4 L 11 16.17 L 7.41 12.59 L 6 14 Z"),
                    Fill = Brushes.White,
                    Stretch = Stretch.Uniform,
                    Width = 16,
                    Height = 16,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                downButton.Content = downIcon;

                downButton.Click += MoveDownButton_Click;

                // После downButton.Click += MoveDownButton_Click; добавь:

                downButton.MouseEnter += (s, e) =>
                {
                    var anim = new ColorAnimation
                    {
                        To = Color.FromRgb(82, 89, 103),
                        Duration = TimeSpan.FromMilliseconds(150)
                    };
                    downButton.Background.BeginAnimation(SolidColorBrush.ColorProperty, anim);
                };
                downButton.MouseLeave += (s, e) =>
                {
                    var anim = new ColorAnimation
                    {
                        To = Color.FromRgb(62, 69, 83),
                        Duration = TimeSpan.FromMilliseconds(150)
                    };
                    downButton.Background.BeginAnimation(SolidColorBrush.ColorProperty, anim);
                };
                downButton.PreviewMouseDown += (s, e) =>
                {
                    var anim = new ColorAnimation
                    {
                        To = Color.FromRgb(42, 49, 63),
                        Duration = TimeSpan.FromMilliseconds(100)
                    };
                    downButton.Background.BeginAnimation(SolidColorBrush.ColorProperty, anim);
                };
                downButton.PreviewMouseUp += (s, e) =>
                {
                    var anim = new ColorAnimation
                    {
                        To = Color.FromRgb(82, 89, 103),
                        Duration = TimeSpan.FromMilliseconds(100)
                    };
                    downButton.Background.BeginAnimation(SolidColorBrush.ColorProperty, anim);
                };

                // Кнопка удалить (КРУГЛАЯ)
                var deleteButton = new Button
                {
                    Background = new SolidColorBrush(Color.FromRgb(239, 68, 68)),
                    BorderThickness = new Thickness(1),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(220, 38, 38)),
                    Width = 30,
                    Height = 30,
                    Padding = new Thickness(0),
                    Margin = new Thickness(2, 0, 2, 0),
                    Cursor = Cursors.Hand,
                    Tag = wp
                };

                var deleteTemplate = new ControlTemplate(typeof(Button));
                var deleteBorder = new FrameworkElementFactory(typeof(Border));
                deleteBorder.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
                deleteBorder.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Button.BorderBrushProperty));
                deleteBorder.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Button.BorderThicknessProperty));
                deleteBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(15)); // КРУГЛАЯ

                var deleteContent = new FrameworkElementFactory(typeof(ContentPresenter));
                deleteContent.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
                deleteContent.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
                deleteBorder.AppendChild(deleteContent);
                deleteTemplate.VisualTree = deleteBorder;
                deleteButton.Template = deleteTemplate;

                // Кнопка удалить - ВЕКТОРНАЯ иконка корзины
                var deleteIcon = new Path
                {
                    Data = System.Windows.Media.Geometry.Parse("M 6 19 C 6 20.1 6.9 21 8 21 L 16 21 C 17.1 21 18 20.1 18 19 L 18 7 L 6 7 L 6 19 Z M 19 4 L 15.5 4 L 14.5 3 L 9.5 3 L 8.5 4 L 5 4 L 5 6 L 19 6 L 19 4 Z"),
                    Fill = Brushes.White,
                    Stretch = Stretch.Uniform,
                    Width = 14,
                    Height = 14,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                deleteButton.Content = deleteIcon;

                deleteButton.Click += DeleteButton_Click;

                // После deleteButton.Click += DeleteButton_Click; добавь:

                deleteButton.MouseEnter += (s, e) =>
                {
                    var anim = new ColorAnimation
                    {
                        To = Color.FromRgb(255, 88, 88), // Светлее красного
                        Duration = TimeSpan.FromMilliseconds(150)
                    };
                    deleteButton.Background.BeginAnimation(SolidColorBrush.ColorProperty, anim);
                };
                deleteButton.MouseLeave += (s, e) =>
                {
                    var anim = new ColorAnimation
                    {
                        To = Color.FromRgb(239, 68, 68), // Обычный красный
                        Duration = TimeSpan.FromMilliseconds(150)
                    };
                    deleteButton.Background.BeginAnimation(SolidColorBrush.ColorProperty, anim);
                };
                deleteButton.PreviewMouseDown += (s, e) =>
                {
                    var anim = new ColorAnimation
                    {
                        To = Color.FromRgb(220, 38, 38), // Темнее красного
                        Duration = TimeSpan.FromMilliseconds(100)
                    };
                    deleteButton.Background.BeginAnimation(SolidColorBrush.ColorProperty, anim);
                };
                deleteButton.PreviewMouseUp += (s, e) =>
                {
                    var anim = new ColorAnimation
                    {
                        To = Color.FromRgb(255, 88, 88), // Как при наведении
                        Duration = TimeSpan.FromMilliseconds(100)
                    };
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
                MessageBox.Show("Дрон не подключен или нет GPS сигнала!", "Ошибка",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
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

            System.Diagnostics.Debug.WriteLine($"✅ HOME установлена: {telemetry.Latitude:F6}, {telemetry.Longitude:F6}");
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
            AddHomePosition();
        }


        /// <summary>
        /// Создание визуального элемента HOME
        /// </summary>
        private UIElement CreateHomeMarkerShape()
        {
            var grid = new Grid { Width = 40, Height = 40 };

            // Красный круг для HOME
            var homeCircle = new Ellipse
            {
                Width = 40,
                Height = 40,
                Fill = new SolidColorBrush(Color.FromArgb(180, 239, 68, 68)), // Красный
                Stroke = Brushes.White,
                StrokeThickness = 3
            };

            // Иконка дома
            var homeIcon = new TextBlock
            {
                Text = "🏠",
                FontSize = 22,
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

            System.Diagnostics.Debug.WriteLine($"🔄 RefreshMarkers: обновляем {_waypoints.Count} меток, текущий zoom={PlanMap.Zoom:F1}");

            foreach (var wp in _waypoints)
            {
                // Проверяем что у нас есть сохраненные ссылки
                if (wp.ShapeGrid != null && wp.RadiusCircle != null)
                {
                    double radiusInPixels = MetersToPixels(wp.Radius, wp.Latitude, PlanMap.Zoom);

                    System.Diagnostics.Debug.WriteLine($"  🔍 WP{wp.Number}: Radius={wp.Radius:F0}м → radiusInPixels = {radiusInPixels:F2}px (zoom={PlanMap.Zoom:F1})");

                    // РЕАЛИЗМ: Только ограничиваем максимум, НЕ увеличиваем минимум!
                    // Пусть маленькие круги остаются маленькими - это реально!
                    radiusInPixels = Math.Min(500, radiusInPixels); // Максимум 500px (большой радиус)

                    // Минимум 3px чтобы было хоть что-то видно
                    radiusInPixels = Math.Max(3, radiusInPixels);

                    double diameter = radiusInPixels * 2;

                    System.Diagnostics.Debug.WriteLine($"  ✨ WP{wp.Number}: radiusInPixels ПОСЛЕ clamp = {radiusInPixels:F0}px (диаметр: {diameter:F0}px)");

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
                    System.Diagnostics.Debug.WriteLine($"  ⚠️ WP{wp.Number}: нет сохраненных ссылок, пересоздаем");

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

            System.Diagnostics.Debug.WriteLine($"✅ RefreshMarkers: завершено\n");
        }

        /// <summary>
        /// Кнопки управления миссией (TODO)
        /// </summary>
        private void AddWaypointButton_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("Добавить маршрутную точку - в разработке\n\nИспользуйте двойной клик на карте", "Информация", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void ExecuteButton_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("Выполнить миссию - в разработке", "TODO", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void LoiterButton_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("Кружить - в разработке", "TODO", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("Отменить - в разработке", "TODO", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        //private void RthButton_Click(object sender, RoutedEventArgs e)
        //{
           // MessageBox.Show("Возврат на базу - в разработке", "TODO", MessageBoxButton.OK, MessageBoxImage.Information);
       // }

        private void ToggleButton_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("Вкл/Выкл - в разработке", "TODO", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void UploadButton_Click(object sender, RoutedEventArgs e)
        {
            if (_waypoints == null || _waypoints.Count == 0)
            {
                MessageBox.Show("Нет точек для сохранения", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                System.Diagnostics.Debug.WriteLine($"💾 Начало сохранения миссии: {_waypoints.Count} точек");

                // КРИТИЧНО: ВСЕГДА сохраняем в файл (для резервной копии и отладки)
                SaveMissionToFile("mission_planned.txt");

                string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                string fullPath = System.IO.Path.Combine(desktopPath, "mission_planned.txt");

                System.Diagnostics.Debug.WriteLine($"✅ Файл сохранён: {fullPath}");

                // Если MAVLink доступен - сохраняем ДОПОЛНИТЕЛЬНО в сервис
                if (_mavlinkService != null)
                {
                    _mavlinkService.SavePlannedMission(_waypoints.ToList());
                    System.Diagnostics.Debug.WriteLine($"✅ Миссия сохранена в MAVLink");

                    MessageBox.Show(
                        $"✅ Миссия сохранена: {_waypoints.Count} точек\n\n" +
                        $"📄 Файл: {fullPath}\n" +
                        $"💾 MAVLink: Готово к отправке\n\n" +
                        "Для отправки в дрон:\n" +
                        "1. Перейдите на страницу 'Полётные данные'\n" +
                        "2. Подключитесь к дрону\n" +
                        "3. Нажмите 'Активировать миссию'",
                        "Миссия сохранена",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show(
                        $"✅ Миссия сохранена: {_waypoints.Count} точек\n\n" +
                        $"📄 Файл: {fullPath}\n\n" +
                        "Для отправки в дрон подключите MAVLink.",
                        "Миссия сохранена",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка сохранения: {ex.Message}", "Ошибка",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                System.Diagnostics.Debug.WriteLine($"❌ Ошибка: {ex.Message}\n{ex.StackTrace}");
            }
            // ✅ Сохраняем миссию как активную для отображения на FlightDataView
            if (_mavlinkService != null)
            {
                _mavlinkService.SetActiveMission(_waypoints.ToList());
                System.Diagnostics.Debug.WriteLine("📤 Миссия передана для мониторинга на FlightDataView");
            }
        }



        private void DownloadButton_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("Download из дрона - в разработке", "TODO", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("Сохранить в файл - в разработке", "TODO", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void LoadButton_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("Загрузить из файла - в разработке", "TODO", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show("Удалить все точки?", "Подтверждение", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
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

            System.Diagnostics.Debug.WriteLine($"📁 Сохранение миссии в: {fullPath}");

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

            System.Diagnostics.Debug.WriteLine($"✅ Миссия сохранена в {fullPath}");
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
                    System.Diagnostics.Debug.WriteLine($"⚠️ Неизвестный тип команды: '{commandType}', использую WAYPOINT");
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

                System.Diagnostics.Debug.WriteLine($"🔍 Plan Map Zoom: {newZoom}");
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

            // ОЧЕНЬ ДЛИННАЯ линия направления (heading)
            var headingLine = new Line
            {
                X1 = 250,
                Y1 = 250,
                X2 = 250,
                Y2 = 0,
                Stroke = new SolidColorBrush(Color.FromRgb(239, 68, 68)), // Красная
                StrokeThickness = 4,
                StrokeEndLineCap = PenLineCap.Triangle,
                Name = "HeadingLine"
            };

            // ИКОНКА ДРОНА
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

            if (telemetry.Latitude != 0 && telemetry.Longitude != 0)
            {
                var dronePosition = new PointLatLng(telemetry.Latitude, telemetry.Longitude);

                // Создаем маркер дрона если его еще нет
                if (_droneMarker == null)
                {
                    _droneMarker = CreateDroneMarker(dronePosition);
                    PlanMap.Markers.Add(_droneMarker);
                    System.Diagnostics.Debug.WriteLine("🚁 Дрон добавлен на карту планирования");

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
                    }
                }
            }
            else if (_droneMarker != null && !_mavlinkService.IsConnected)
            {
                // Убираем маркер при отключении
                PlanMap.Markers.Remove(_droneMarker);
                _droneMarker = null;
                System.Diagnostics.Debug.WriteLine("🚁 Дрон удалён с карты планирования");
            }
        }

    }



    /// <summary>
    /// Класс для waypoint
    /// </summary>
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