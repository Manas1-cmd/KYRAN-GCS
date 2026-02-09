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
        private WaypointItem _selectedWaypoint;
        private double _waypointRadius = 80; // метры
        private WaypointItem _radiusDragWaypoint = null;  // Точка у которой меняем радиус
        private bool _isRadiusDragging = false;           // Флаг drag радиуса
        private TextBlock _radiusTooltip = null;          // Подсказка с радиусом
        private MAVLinkService _mavlinkService;
        private GMapMarker _droneMarker = null;
        private WaypointItem _homePosition = null; // HOME позиция
        private bool _isInitialized = false; // Защита от повторной инициализации
        private Dictionary<VehicleType, List<WaypointItem>> _missionsByType = new(); // Кэш миссий при переключении типа (RAM)
        private Dictionary<VehicleType, WaypointItem> _homeByType = new();
        private Dictionary<VehicleType, WaypointItem> _startByType = new();
        private Dictionary<VehicleType, WaypointItem> _landingByType = new();
        private DispatcherTimer _droneUpdateTimer; // ДОБАВЬ
        private bool _isSettingHomeMode = false; // режим установки HOME
        private double _takeoffAltitude = 10;  // высота взлёта по умолчанию
        private double _rtlAltitude = 15;      // высота RTL по умолчанию
        private bool _wasArmed = false; // для отслеживания армирования
        private SrtmElevationProvider _elevationProvider = new(); // Провайдер высот SRTM
        private Dictionary<WaypointItem, GMapMarker> _resizeHandles = new(); // Ручки изменения радиуса

        private DispatcherTimer _telemetryTimer;      // Таймер обновления телеметрии
        private DispatcherTimer _connectionTimer;     // Таймер секундомера подключения
        private DateTime _connectionStartTime;        // Время начала подключения
        private bool _wasConnected = false;           // Флаг подключения

        // === VTOL СПЕЦИАЛЬНЫЕ ТОЧКИ ===
        private WaypointItem _startCircle;            // Точка старта (дрон кружит тут после взлёта)
        private WaypointItem _landingCircle;          // Точка посадки (дрон кружит тут перед посадкой)
        private double _vtolTakeoffAltitude = 30;     // Высота VTOL взлёта
        private double _vtolLandAltitude = 30;        // Высота VTOL посадки
        private bool _isMissionFrozen = false;        // Флаг: миссия заморожена

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
                        PopulateFlightModes(); // Обновляем режимы полёта при смене типа
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

                // Подписка на телеметрию
                _mavlinkService.TelemetryUpdated += OnTelemetryReceived;

                // Таймер обновления UI телеметрии
                _telemetryTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(50)
                };
                _telemetryTimer.Tick += UpdateTelemetryUI;
                _telemetryTimer.Start();

                // Таймер секундомера подключения
                _connectionTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(1)
                };
                _connectionTimer.Tick += UpdateConnectionTimer;
                _connectionTimer.Start();
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

                        // Если QuadPlane — сразу рисуем S/L маркеры
                        if (_currentVehicleType == VehicleType.QuadPlane)
                            DrawVtolSpecialPoints();

                        // Подписка на прогресс миссии (подсветка текущего WP)
                        if (_mavlinkService != null)
                        {
                            _mavlinkService.MissionProgressUpdated += (sender2, seq) =>
                            {
                                HighlightMissionSeq(seq);
                            };
                        }
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

            // Завершение drag радиуса
            this.MouseLeftButtonUp += (s, e) => EndRadiusDrag();
            this.MouseLeave += (s, e) => EndRadiusDrag();
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
                PlanMap.OnMapZoomChanged += PlanMap_OnMapZoomChanged; // Обновление радиусов при зуме

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
            {
                _homePosition.Marker.Shape = null;
                PlanMap.Markers.Remove(_homePosition.Marker);
            }

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

            PlanHomeLatText.Text = _homePosition.Latitude.ToString("F6");
            PlanHomeLonText.Text = _homePosition.Longitude.ToString("F6");

            System.Diagnostics.Debug.WriteLine($"[HOME] Установлен вручную: {lat:F6}, {lon:F6}");
        }
        private void PlanMap_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (_isSettingHomeMode)
            {
                var point = e.GetPosition(PlanMap);
                var latLng = PlanMap.FromLocalToLatLng((int)point.X, (int)point.Y);

                SetHomeAtPosition(latLng.Lat, latLng.Lng);

                _isSettingHomeMode = false;
                PlanMap.Cursor = Cursors.Arrow;

                e.Handled = true;
                return;
            }

            if (e.ChangedButton == MouseButton.Left)
            {
                try
                {
                    Point clickPoint = e.GetPosition(PlanMap);
                    PointLatLng position = PlanMap.FromLocalToLatLng((int)clickPoint.X, (int)clickPoint.Y);

                    var waypoint = new WaypointItem
                    {
                        Number = _waypoints.Count + 1,
                        Latitude = position.Lat,
                        Longitude = position.Lng,
                        Altitude = 100,
                        CommandType = "WAYPOINT",
                        Radius = _waypointRadius
                    };

                    // ТЕСТ: Только добавляем в коллекцию, без маркера
                    System.Diagnostics.Debug.WriteLine($"=== ТЕСТ: Добавляю WP {waypoint.Number} ===");

                    _waypoints.Add(waypoint);
                    System.Diagnostics.Debug.WriteLine("1. _waypoints.Add - OK");

                    AddMarkerToMap(waypoint);
                    System.Diagnostics.Debug.WriteLine("2. AddMarkerToMap - OK");

                    UpdateRoute();
                    System.Diagnostics.Debug.WriteLine("3. UpdateRoute - OK");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"ОШИБКА: {ex.Message}\n{ex.StackTrace}");
                    MessageBox.Show($"Ошибка: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Добавление метки на карту
        /// </summary>
        private void AddMarkerToMap(WaypointItem waypoint)
        {
            // Защита от дублирования - полная очистка
            if (waypoint.Marker != null)
            {
                waypoint.Marker.Shape = null;
                PlanMap.Markers.Remove(waypoint.Marker);
                waypoint.Marker = null;
            }

            // Очищаем старые визуальные элементы
            if (waypoint.ShapeGrid != null)
            {
                waypoint.ShapeGrid.Children.Clear();  // ← ЭТО ВАЖНО!
                waypoint.ShapeGrid = null;
            }
            waypoint.RadiusCircle = null;

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

            // Создаём ручку изменения радиуса (отдельный маркер на краю круга)
            CreateResizeHandle(waypoint);
        }

        /// <summary>
        /// Создание визуального элемента метки (без ручки - она создается отдельным маркером)
        /// </summary>
        private UIElement CreateMarkerShape(WaypointItem waypoint)
        {
            double radiusInPixels = MetersToPixels(waypoint.Radius, waypoint.Latitude, PlanMap.Zoom);
            radiusInPixels = Math.Max(20, Math.Min(5000, radiusInPixels));

            double gridSize = Math.Max(60, radiusInPixels * 2);

            var grid = new Grid
            {
                Width = gridSize,
                Height = gridSize
            };

            // Круг радиуса - ПУНКТИРНЫЙ (скрыт по умолчанию, виден при клике)
            var radiusCircle = new Ellipse
            {
                Width = radiusInPixels * 2,
                Height = radiusInPixels * 2,
                Stroke = new SolidColorBrush(Color.FromArgb(200, 152, 240, 25)),
                StrokeThickness = 2,
                StrokeDashArray = new DoubleCollection { 4, 2 }, // ПУНКТИР
                Fill = new SolidColorBrush(Color.FromArgb(40, 152, 240, 25)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed  // Скрыт по умолчанию
            };
            grid.Children.Add(radiusCircle);
            waypoint.RadiusCircle = radiusCircle;

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
            grid.Children.Add(centerPoint);

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
            grid.Children.Add(numberText);

            waypoint.ShapeGrid = grid;

            // Добавляем ToolTip с параметрами (динамический — обновляется при каждом показе)
            grid.ToolTip = CreateWaypointTooltip(waypoint);
            ToolTipService.SetInitialShowDelay(grid, 300);
            ToolTipService.SetShowDuration(grid, 10000);
            // Динамическое обновление попапа при наведении
            grid.ToolTipOpening += (s, args) =>
            {
                if (s is Grid g) g.ToolTip = CreateWaypointTooltip(waypoint);
            };

            return grid;
        }

        /// <summary>
        /// Создание ToolTip с параметрами waypoint
        /// </summary>
        private ToolTip CreateWaypointTooltip(WaypointItem wp)
        {
            var tooltip = new ToolTip
            {
                Background = new SolidColorBrush(Color.FromRgb(13, 23, 51)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(152, 240, 25)),
                BorderThickness = new Thickness(2),
                Padding = new Thickness(12, 8, 12, 8)
            };

            var stack = new StackPanel();

            // Заголовок
            var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            header.Children.Add(new Ellipse
            {
                Width = 24, Height = 24,
                Fill = new SolidColorBrush(Color.FromRgb(152, 240, 25)),
                Margin = new Thickness(0, 0, 8, 0)
            });
            var numText = new TextBlock
            {
                Text = wp.Number.ToString(),
                Foreground = Brushes.Black,
                FontWeight = FontWeights.Bold,
                FontSize = 14,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(-32, 0, 0, 0)
            };
            header.Children.Add(numText);
            header.Children.Add(new TextBlock
            {
                Text = $"Точка #{wp.Number}",
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center
            });
            stack.Children.Add(header);

            // Параметры
            var paramStyle = new Style(typeof(TextBlock));
            
            stack.Children.Add(CreateTooltipRow("📍 Координаты:", $"{wp.Latitude:F6}, {wp.Longitude:F6}"));
            stack.Children.Add(CreateTooltipRow("📏 Высота:", $"{wp.Altitude:F0} м"));
            stack.Children.Add(CreateTooltipRow("⭕ Радиус:", $"{wp.Radius:F0} м"));
            stack.Children.Add(CreateTooltipRow("🔄 Направление:", wp.Clockwise ? "По часовой (CW)" : "Против часовой (CCW)"));
            stack.Children.Add(CreateTooltipRow("⏱ Задержка:", $"{wp.Delay:F0} сек"));
            stack.Children.Add(CreateTooltipRow("🔁 Кругов:", wp.LoiterTurns.ToString()));
            stack.Children.Add(CreateTooltipRow("▶ Авто-переход:", wp.AutoNext ? "Да" : "Нет (кружит)"));

            tooltip.Content = stack;
            return tooltip;
        }

        /// <summary>
        /// Создание строки для ToolTip
        /// </summary>
        private StackPanel CreateTooltipRow(string label, string value)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            row.Children.Add(new TextBlock
            {
                Text = label,
                Foreground = new SolidColorBrush(Color.FromRgb(156, 163, 175)),
                FontSize = 11,
                Width = 110
            });
            row.Children.Add(new TextBlock
            {
                Text = value,
                Foreground = new SolidColorBrush(Color.FromRgb(152, 240, 25)),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold
            });
            return row;
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
                
                // Показать/скрыть радиус при клике
                SelectWaypoint(waypoint);
                
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

                    // Обновляем позицию ручки радиуса
                    if (_resizeHandles.ContainsKey(waypoint))
                    {
                        var handlePos = CalculatePointAtDistance(
                            waypoint.Latitude, waypoint.Longitude, 90, waypoint.Radius / 1000.0);
                        _resizeHandles[waypoint].Position = new PointLatLng(handlePos.Lat, handlePos.Lng);
                    }

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
        /// Выбрать вейпоинт - показать его радиус, скрыть остальные
        /// </summary>
        private void SelectWaypoint(WaypointItem wp)
        {
            // Скрыть радиус и resize handle у предыдущего выбранного
            if (_selectedWaypoint != null && _selectedWaypoint != wp && _selectedWaypoint.RadiusCircle != null)
            {
                _selectedWaypoint.RadiusCircle.Visibility = Visibility.Collapsed;
                if (_resizeHandles.ContainsKey(_selectedWaypoint))
                    _resizeHandles[_selectedWaypoint].Shape.Visibility = Visibility.Collapsed;
            }

            // Переключаем: если кликнули по уже выбранному - скрываем
            if (_selectedWaypoint == wp)
            {
                if (wp.RadiusCircle != null)
                {
                    var newVis = wp.RadiusCircle.Visibility == Visibility.Visible 
                        ? Visibility.Collapsed : Visibility.Visible;
                    wp.RadiusCircle.Visibility = newVis;
                    if (_resizeHandles.ContainsKey(wp))
                        _resizeHandles[wp].Shape.Visibility = newVis;
                }
                _selectedWaypoint = wp.RadiusCircle?.Visibility == Visibility.Visible ? wp : null;
            }
            else
            {
                // Показать радиус и resize handle выбранного
                if (wp.RadiusCircle != null)
                    wp.RadiusCircle.Visibility = Visibility.Visible;
                if (_resizeHandles.ContainsKey(wp))
                    _resizeHandles[wp].Shape.Visibility = Visibility.Visible;
                _selectedWaypoint = wp;
            }
        }

        #region RADIUS DRAG

        private GMapMarker _tooltipMarker = null;

        /// <summary>
        /// Начало изменения радиуса
        /// </summary>
        private void ResizeHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is Ellipse handle && handle.Tag is WaypointItem wp)
            {
                _radiusDragWaypoint = wp;
                _isRadiusDragging = true;
                handle.CaptureMouse();
                PlanMap.CanDragMap = false;

                // Создаём tooltip как маркер
                CreateRadiusTooltip();
                var pos = e.GetPosition(PlanMap);
                var latLng = PlanMap.FromLocalToLatLng((int)pos.X, (int)pos.Y);
                UpdateRadiusTooltip(latLng, wp.Radius);

                e.Handled = true;
            }
        }

        /// <summary>
        /// Создание tooltip как GMapMarker
        /// </summary>
        private void CreateRadiusTooltip()
        {
            if (_radiusTooltip == null)
            {
                _radiusTooltip = new TextBlock
                {
                    Background = new SolidColorBrush(Color.FromArgb(220, 13, 23, 51)),
                    Foreground = new SolidColorBrush(Color.FromRgb(152, 240, 25)),
                    Padding = new Thickness(6, 3, 6, 3),
                    FontSize = 12,
                    FontWeight = FontWeights.Bold
                };
            }

            if (_tooltipMarker == null)
            {
                _tooltipMarker = new GMapMarker(new PointLatLng(0, 0))
                {
                    Shape = _radiusTooltip,
                    Offset = new Point(15, -10),
                    ZIndex = 9999
                };
            }

            if (!PlanMap.Markers.Contains(_tooltipMarker))
            {
                PlanMap.Markers.Add(_tooltipMarker);
            }
        }

        /// <summary>
        /// Обновление позиции и текста tooltip
        /// </summary>
        private void UpdateRadiusTooltip(PointLatLng position, double radius)
        {
            if (_radiusTooltip == null || _tooltipMarker == null) return;

            double minRadius = GetMinRadius();

            if (radius < minRadius)
            {
                _radiusTooltip.Foreground = Brushes.Red;
                _radiusTooltip.Text = $"{radius:F0}м (мин: {minRadius}м)";
            }
            else
            {
                _radiusTooltip.Foreground = new SolidColorBrush(Color.FromRgb(152, 240, 25));
                _radiusTooltip.Text = $"{radius:F0}м";
            }

            _tooltipMarker.Position = position;
        }

        /// <summary>
        /// Скрыть tooltip
        /// </summary>
        private void HideRadiusTooltip()
        {
            if (_tooltipMarker != null && PlanMap.Markers.Contains(_tooltipMarker))
            {
                _tooltipMarker.Shape = null;
                PlanMap.Markers.Remove(_tooltipMarker);
            }
        }

        /// <summary>
        /// Получить минимальный радиус для текущего типа дрона
        /// </summary>
        private double GetMinRadius()
        {
            return _currentVehicleType == VehicleType.QuadPlane ? 80 : 5;
        }

        /// <summary>
        /// Завершение drag радиуса
        /// </summary>
        private void EndRadiusDrag()
        {
            if (_isRadiusDragging)
            {
                _isRadiusDragging = false;
                _radiusDragWaypoint = null;
                PlanMap.CanDragMap = true;
                HideRadiusTooltip();
                UpdateRoute();
            }
        }

        /// <summary>
        /// Создать ручку изменения радиуса (отдельный маркер на краю круга)
        /// </summary>
        /// <summary>
        /// Создать ручку изменения радиуса (отдельный маркер на краю круга)
        /// </summary>
        private void CreateResizeHandle(WaypointItem waypoint)
        {
            // Удаляем старую если есть - с очисткой Shape
            if (_resizeHandles.ContainsKey(waypoint))
            {
                var oldHandle = _resizeHandles[waypoint];
                oldHandle.Shape = null;  // ВАЖНО!
                PlanMap.Markers.Remove(oldHandle);
                _resizeHandles.Remove(waypoint);
            }

            // Позиция ручки = справа от центра на расстоянии радиуса
            var handlePos = CalculatePointAtDistance(
                waypoint.Latitude, waypoint.Longitude,
                90, // 90° = восток (вправо)
                waypoint.Radius / 1000.0); // в км

            var handle = new Ellipse
            {
                Width = 16,
                Height = 16,
                Fill = Brushes.White,
                Stroke = new SolidColorBrush(Color.FromRgb(152, 240, 25)),
                StrokeThickness = 3,
                Cursor = Cursors.SizeWE,
                Tag = waypoint
            };

            // MouseDown - начало drag
            handle.MouseLeftButtonDown += ResizeHandle_MouseLeftButtonDown;

            // MouseUp - ВАЖНО: завершение drag прямо на ручке
            handle.MouseLeftButtonUp += (s, e) =>
            {
                if (_isRadiusDragging)
                {
                    handle.ReleaseMouseCapture();
                    EndRadiusDrag();
                    e.Handled = true;
                }
            };

            // MouseMove - обработка drag прямо на ручке
            handle.MouseMove += (s, e) =>
            {
                if (_isRadiusDragging && _radiusDragWaypoint == waypoint)
                {
                    var pos = e.GetPosition(PlanMap);
                    var latLng = PlanMap.FromLocalToLatLng((int)pos.X, (int)pos.Y);

                    double newRadius = CalculateDistanceLatLng(
                        waypoint.Latitude, waypoint.Longitude,
                        latLng.Lat, latLng.Lng);

                    double minRadius = GetMinRadius();
                    newRadius = Math.Max(minRadius, Math.Min(500, newRadius));

                    waypoint.Radius = newRadius;
                    UpdateWaypointRadiusVisual(waypoint);
                    UpdateRadiusTooltip(latLng, newRadius);
                }
            };

            handle.MouseEnter += (s, e) => handle.Fill = new SolidColorBrush(Color.FromRgb(152, 240, 25));
            handle.MouseLeave += (s, e) => { if (!_isRadiusDragging) handle.Fill = Brushes.White; };

            var marker = new GMapMarker(new PointLatLng(handlePos.Lat, handlePos.Lng))
            {
                Shape = handle,
                Offset = new Point(-8, -8),
                ZIndex = 150
            };

            PlanMap.Markers.Add(marker);
            _resizeHandles[waypoint] = marker;
            // Скрываем по умолчанию (показываем вместе с радиусом по клику)
            handle.Visibility = Visibility.Collapsed;
        }

        /// <summary>
        /// Рассчитать точку на расстоянии от исходной
        /// </summary>
        private PointLatLng CalculatePointAtDistance(double lat, double lon, double bearingDeg, double distanceKm)
        {
            const double R = 6371; // Радиус Земли в км
            double lat1 = lat * Math.PI / 180;
            double lon1 = lon * Math.PI / 180;
            double bearing = bearingDeg * Math.PI / 180;

            double lat2 = Math.Asin(Math.Sin(lat1) * Math.Cos(distanceKm / R) +
                                   Math.Cos(lat1) * Math.Sin(distanceKm / R) * Math.Cos(bearing));
            double lon2 = lon1 + Math.Atan2(Math.Sin(bearing) * Math.Sin(distanceKm / R) * Math.Cos(lat1),
                                             Math.Cos(distanceKm / R) - Math.Sin(lat1) * Math.Sin(lat2));

            return new PointLatLng(lat2 * 180 / Math.PI, lon2 * 180 / Math.PI);
        }

        #endregion

        /// <summary>
        /// Удаление waypoint
        /// </summary>
        private void RemoveWaypoint(WaypointItem waypoint)
        {
            // Удаляем ручку радиуса
            if (_resizeHandles.ContainsKey(waypoint))
            {
                _resizeHandles[waypoint].Shape = null;
                PlanMap.Markers.Remove(_resizeHandles[waypoint]);
                _resizeHandles.Remove(waypoint);
            }

            // Удаляем маркер с карты
            if (waypoint.Marker != null)
            {
                waypoint.Marker.Shape = null;
                PlanMap.Markers.Remove(waypoint.Marker);
            }

            // Удаляем из коллекции
            _waypoints.Remove(waypoint);

            // ПЕРЕНУМЕРАЦИЯ всех оставшихся точек
            for (int i = 0; i < _waypoints.Count; i++)
            {
                _waypoints[i].Number = i + 1;

                // Обновляем номер на маркере карты
                if (_waypoints[i].Marker?.Shape is Grid grid)
                {
                    foreach (var child in grid.Children)
                    {
                        if (child is TextBlock tb && int.TryParse(tb.Text, out _))
                        {
                            tb.Text = _waypoints[i].Number.ToString();
                            break;
                        }
                    }
                }
            }

            // Обновляем UI
            UpdateRoute();
            UpdateWaypointsList();
            UpdateStatistics();

            System.Diagnostics.Debug.WriteLine($"Waypoint удалён, осталось: {_waypoints.Count}");
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
            var oldRoutes = PlanMap.Markers.OfType<GMapRoute>().ToList();
            foreach (var r in oldRoutes)
            {
                r.Shape = null;
                PlanMap.Markers.Remove(r);
            }

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
            }

            if (_waypoints.Count == 0)
            {
                // Для VTOL: рисуем S/L даже без WP
                if (_currentVehicleType == VehicleType.QuadPlane)
                    DrawVtolSpecialPoints();

                System.Diagnostics.Debug.WriteLine($"UpdateRoute() - Точек: 0, HOME: {_homePosition != null}");
                UpdateStatistics();
                return;
            }

            var entryPoints = new Dictionary<int, PointLatLng>();
            var exitPoints = new Dictionary<int, PointLatLng>();

            bool isVTOL = _currentVehicleType == VehicleType.QuadPlane;

            // === VTOL: инициализируем S/L до расчёта entry/exit ===
            if (isVTOL)
            {
                if (_startCircle == null) InitializeStartCircle();
                if (_landingCircle == null) InitializeLandingCircle();
            }

            // СТАЛО:
            // === Вход в первую точку ===
            if (isVTOL && _startCircle != null)
            {
                var firstWp = _waypoints[0];
                var tangent = GetExternalTangentPoints(
                    _startCircle.Latitude, _startCircle.Longitude, _startCircle.Radius, _startCircle.Clockwise,
                    firstWp.Latitude, firstWp.Longitude, firstWp.Radius, firstWp.Clockwise);
                entryPoints[0] = tangent.Item2;  // Точка входа = куда приходит касательная от S
            }
            else if (_homePosition != null)
            {
                var homePoint = new PointLatLng(_homePosition.Latitude, _homePosition.Longitude);
                var firstWp = _waypoints[0];

                var edgeWp = GetNearEdgePoint(
                    firstWp.Latitude, firstWp.Longitude, firstWp.Radius,
                    _homePosition.Latitude, _homePosition.Longitude);

                entryPoints[0] = edgeWp;

                var homeRoute = new GMapRoute(new List<PointLatLng> { homePoint, edgeWp });
                homeRoute.Shape = new Path
                {
                    Stroke = new SolidColorBrush(Color.FromRgb(239, 68, 68)),
                    StrokeThickness = 2,
                    StrokeDashArray = new DoubleCollection { 5, 3 },
                    Opacity = 0.9
                };
                homeRoute.ZIndex = 40;
                PlanMap.Markers.Add(homeRoute);
            }

            // === Основной маршрут между точками ===
            for (int i = 0; i < _waypoints.Count - 1; i++)
            {
                var wp1 = _waypoints[i];
                var wp2 = _waypoints[i + 1];

                var tangentPoints = GetExternalTangentPoints(
                    wp1.Latitude, wp1.Longitude, wp1.Radius, wp1.Clockwise,
                    wp2.Latitude, wp2.Longitude, wp2.Radius, wp2.Clockwise);

                exitPoints[i] = tangentPoints.Item1;
                entryPoints[i + 1] = tangentPoints.Item2;

                var segmentRoute = new GMapRoute(new List<PointLatLng> { tangentPoints.Item1, tangentPoints.Item2 });
                segmentRoute.Shape = new Path
                {
                    Stroke = new SolidColorBrush(Color.FromRgb(152, 240, 25)),
                    StrokeThickness = 3,
                    Opacity = 0.9
                };
                segmentRoute.ZIndex = 50;
                PlanMap.Markers.Add(segmentRoute);
            }

            // СТАЛО:
            // === Выход из последней точки ===
            if (isVTOL && _landingCircle != null && _waypoints.Count > 0)
            {
                int lastIdx = _waypoints.Count - 1;
                var lastWp = _waypoints[lastIdx];
                var tangent = GetExternalTangentPoints(
                    lastWp.Latitude, lastWp.Longitude, lastWp.Radius, lastWp.Clockwise,
                    _landingCircle.Latitude, _landingCircle.Longitude, _landingCircle.Radius, _landingCircle.Clockwise);
                exitPoints[lastIdx] = tangent.Item1;  // Точка выхода = откуда уходит касательная к L
            }
            else if (_homePosition != null && _waypoints.Count > 0)
            {
                int lastIdx = _waypoints.Count - 1;
                var lastWp = _waypoints[lastIdx];
                var homePoint = new PointLatLng(_homePosition.Latitude, _homePosition.Longitude);

                var edgeWp = GetNearEdgePoint(
                    lastWp.Latitude, lastWp.Longitude, lastWp.Radius,
                    _homePosition.Latitude, _homePosition.Longitude);

                exitPoints[lastIdx] = edgeWp;

                var returnRoute = new GMapRoute(new List<PointLatLng> { edgeWp, homePoint });
                returnRoute.Shape = new Path
                {
                    Stroke = new SolidColorBrush(Color.FromRgb(239, 68, 68)),
                    StrokeThickness = 2,
                    StrokeDashArray = new DoubleCollection { 5, 3 },
                    Opacity = 0.9
                };
                returnRoute.ZIndex = 40;
                PlanMap.Markers.Add(returnRoute);
            }

            // === Дуги на waypoints ===
            for (int i = 0; i < _waypoints.Count; i++)
            {
                if (entryPoints.ContainsKey(i) && exitPoints.ContainsKey(i))
                {
                    DrawArcOnWaypoint(_waypoints[i], entryPoints[i], exitPoints[i]);
                }
            }

            // === VTOL: StartCircle и LandingCircle маркеры и маршруты ===
            if (isVTOL)
            {
                DrawVtolSpecialPoints();
            }

            System.Diagnostics.Debug.WriteLine($"UpdateRoute() - Точек: {_waypoints.Count}, HOME: {_homePosition != null}, VTOL: {isVTOL}");
        }

        #region VTOL VISUAL — маркеры S/L и маршруты

        /// <summary>
        /// Отрисовка StartCircle и LandingCircle на карте + маршрутные линии
        /// </summary>
        private void DrawVtolSpecialPoints()
        {
            // Удалить старые VTOL ЛИНИИ (но НЕ маркеры S/L если они уже есть)
            var vtolLines = PlanMap.Markers
                .Where(m => m.Tag?.ToString()?.StartsWith("vtol_line") == true ||
                            m.Tag?.ToString()?.StartsWith("vtol_arc") == true)
                .ToList();
            foreach (var m in vtolLines) { m.Shape = null; PlanMap.Markers.Remove(m); }

            // Авто-создание S/L (даже без HOME — используют центр карты)
            if (_startCircle == null) InitializeStartCircle();
            if (_landingCircle == null) InitializeLandingCircle();
            if (_startCircle == null || _landingCircle == null) return;

            var cyan = Color.FromRgb(34, 211, 238);    // #22D3EE — Start
            var pink = Color.FromRgb(244, 114, 182);   // #F472B6 — Landing  
            var green = Color.FromRgb(152, 240, 25);

            // === Маркеры S и L — создаём ТОЛЬКО если нет, иначе обновляем размер ===
            if (_startCircle.Marker == null || !PlanMap.Markers.Contains(_startCircle.Marker))
            {
                AddSpecialCircleMarker(_startCircle, "S", cyan, Color.FromRgb(14, 116, 144));
            }
            else
            {
                UpdateSpecialCircleSize(_startCircle, "S");
            }

            if (_landingCircle.Marker == null || !PlanMap.Markers.Contains(_landingCircle.Marker))
            {
                AddSpecialCircleMarker(_landingCircle, "L", pink, Color.FromRgb(190, 24, 93));
            }
            else
            {
                UpdateSpecialCircleSize(_landingCircle, "L");
            }

            // Словари для entry/exit точек S и L (для отрисовки дуг)
            PointLatLng? sEntry = null, sExit = null;
            PointLatLng? lEntry = null, lExit = null;

            // === HOME → S (касательная, HOME как точка с радиусом 0) ===
            if (_homePosition != null)
            {
                var homePos = new PointLatLng(_homePosition.Latitude, _homePosition.Longitude);
                var tangent = GetExternalTangentPoints(
                    _homePosition.Latitude, _homePosition.Longitude, 0, true,
                    _startCircle.Latitude, _startCircle.Longitude, _startCircle.Radius, _startCircle.Clockwise);
                sEntry = tangent.Item2;
                DrawVtolLine(homePos, tangent.Item2, cyan, true);
            }

            // === S → WP1 (касательная, как между WP↔WP) ===
            if (_waypoints.Count > 0)
            {
                var wp1 = _waypoints[0];
                var tangent = GetExternalTangentPoints(
                    _startCircle.Latitude, _startCircle.Longitude, _startCircle.Radius, _startCircle.Clockwise,
                    wp1.Latitude, wp1.Longitude, wp1.Radius, wp1.Clockwise);
                sExit = tangent.Item1;
                DrawVtolLine(tangent.Item1, tangent.Item2, green, false);
            }

            // === WPN → L (касательная, как между WP↔WP) ===
            if (_waypoints.Count > 0)
            {
                var wpN = _waypoints[^1];
                var tangent = GetExternalTangentPoints(
                    wpN.Latitude, wpN.Longitude, wpN.Radius, wpN.Clockwise,
                    _landingCircle.Latitude, _landingCircle.Longitude, _landingCircle.Radius, _landingCircle.Clockwise);
                lEntry = tangent.Item2;
                DrawVtolLine(tangent.Item1, tangent.Item2, pink, false);
            }

            // === L → HOME (касательная, HOME как точка с радиусом 0) ===
            if (_homePosition != null)
            {
                var homePos = new PointLatLng(_homePosition.Latitude, _homePosition.Longitude);
                var tangent = GetExternalTangentPoints(
                    _landingCircle.Latitude, _landingCircle.Longitude, _landingCircle.Radius, _landingCircle.Clockwise,
                    _homePosition.Latitude, _homePosition.Longitude, 0, true);
                lExit = tangent.Item1;
                DrawVtolLine(tangent.Item1, homePos, pink, true);
            }

            // === Дуги на S и L ===
            if (sEntry.HasValue && sExit.HasValue)
                DrawArcOnSpecialCircle(_startCircle, sEntry.Value, sExit.Value, Color.FromRgb(161, 98, 7));
            if (lEntry.HasValue && lExit.HasValue)
                DrawArcOnSpecialCircle(_landingCircle, lEntry.Value, lExit.Value, Color.FromRgb(194, 65, 12));
        }

        /// <summary>
        /// Дуга на S/L кругах (аналог DrawArcOnWaypoint, но с другим цветом)
        /// </summary>
        private void DrawArcOnSpecialCircle(WaypointItem wp, PointLatLng entryPoint, PointLatLng exitPoint, Color color)
        {
            double entryAngle = CalculateBearing(wp.Latitude, wp.Longitude, entryPoint.Lat, entryPoint.Lng);
            double exitAngle = CalculateBearing(wp.Latitude, wp.Longitude, exitPoint.Lat, exitPoint.Lng);

            double angleDiff;
            if (wp.Clockwise)
            {
                angleDiff = exitAngle - entryAngle;
                while (angleDiff <= 0) angleDiff += 360;
                while (angleDiff > 360) angleDiff -= 360;
            }
            else
            {
                angleDiff = exitAngle - entryAngle;
                while (angleDiff >= 0) angleDiff -= 360;
                while (angleDiff < -360) angleDiff += 360;
            }

            if (Math.Abs(angleDiff) < 5) return;

            var arcPoints = new List<PointLatLng>();
            int steps = Math.Max(3, (int)(Math.Abs(angleDiff) / 5));
            double stepAngle = angleDiff / steps;

            for (int i = 0; i <= steps; i++)
            {
                double angle = entryAngle + stepAngle * i;
                var pt = CalculatePointAtDistance(wp.Latitude, wp.Longitude, angle, wp.Radius / 1000.0);
                arcPoints.Add(pt);
            }

            if (arcPoints.Count >= 2)
            {
                var arcRoute = new GMapRoute(arcPoints);
                arcRoute.Shape = new Path
                {
                    Stroke = new SolidColorBrush(color),
                    StrokeThickness = 2.5,
                    Opacity = 0.9
                };
                arcRoute.ZIndex = 48;
                arcRoute.Tag = "vtol_arc"; // Для идентификации при очистке
                PlanMap.Markers.Add(arcRoute);
            }
        }

        /// <summary>
        /// Создать маркер S или L на карте (точно как обычный WP но с другим цветом)
        /// </summary>
        private void AddSpecialCircleMarker(WaypointItem wp, string label, Color dotColor, Color circleColor)
        {
            if (wp == null) return;

            // Удаляем старый маркер если есть
            if (wp.Marker != null)
            {
                PlanMap.Markers.Remove(wp.Marker);
                wp.Marker = null;
            }
            if (wp.ShapeGrid != null)
            {
                wp.ShapeGrid.Children.Clear();
                wp.ShapeGrid = null;
            }
            wp.RadiusCircle = null;

            // Рассчитываем размер как у обычных WP
            double radiusInPixels = MetersToPixels(wp.Radius, wp.Latitude, PlanMap.Zoom);
            radiusInPixels = Math.Max(3, Math.Min(5000, radiusInPixels));
            double gridSize = Math.Max(60, radiusInPixels * 2);

            var grid = new Grid
            {
                Width = gridSize,
                Height = gridSize,
                Cursor = Cursors.Hand
            };

            // Круг радиуса — ВСЕГДА ВИДИМ для S/L
            var radiusCircle = new Ellipse
            {
                Width = radiusInPixels * 2,
                Height = radiusInPixels * 2,
                Stroke = new SolidColorBrush(circleColor),
                StrokeThickness = wp.AutoNext ? 2 : 3,
                Fill = new SolidColorBrush(Color.FromArgb(30, circleColor.R, circleColor.G, circleColor.B)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            if (wp.AutoNext)
                radiusCircle.StrokeDashArray = new DoubleCollection { 6, 3 };
            grid.Children.Add(radiusCircle);
            wp.RadiusCircle = radiusCircle;

            // Центральная точка
            var centerPoint = new Ellipse
            {
                Width = 28,
                Height = 28,
                Fill = new SolidColorBrush(dotColor),
                Stroke = new SolidColorBrush(Color.FromRgb(6, 11, 26)),
                StrokeThickness = 3,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            grid.Children.Add(centerPoint);

            // Буква S или L
            var letterText = new TextBlock
            {
                Text = label,
                Foreground = Brushes.Black,
                FontWeight = FontWeights.Bold,
                FontSize = 14,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            grid.Children.Add(letterText);

            wp.ShapeGrid = grid;

            // ToolTip
            grid.ToolTip = CreateCircleTooltip(wp, label);
            ToolTipService.SetInitialShowDelay(grid, 300);
            grid.ToolTipOpening += (s, args) =>
            {
                if (s is Grid g) g.ToolTip = CreateCircleTooltip(wp, label);
            };

            // Создаём маркер
            var marker = new GMapMarker(new PointLatLng(wp.Latitude, wp.Longitude))
            {
                Shape = grid,
                Offset = new Point(-gridSize / 2, -gridSize / 2),
                ZIndex = label == "S" ? 95 : 90,
                Tag = $"vtol_{label}"
            };

            wp.Marker = marker;
            PlanMap.Markers.Add(marker);

            // === DRAG & DROP ===
            grid.MouseLeftButtonDown += (s, e) =>
            {
                if (e.ClickCount == 2)
                {
                    // Двойной клик — диалог редактирования
                    string title = label == "S" ? "СТАРТ" : "ПОСАДКА";
                    var dialog = new CircleEditDialog(title, wp.Radius, wp.Altitude, wp.AutoNext, wp.Clockwise);
                    dialog.Owner = OwnerWindow;

                    if (dialog.ShowDialog() == true)
                    {
                        wp.Radius = Math.Max(150, dialog.Radius);
                        wp.Altitude = dialog.Altitude;
                        wp.AutoNext = dialog.AutoNext;
                        wp.Clockwise = dialog.Clockwise;
                        UpdateRoute();
                    }
                    e.Handled = true;
                    return;
                }

                // Одинарный клик — начало drag
                _currentDragMarker = marker;
                grid.CaptureMouse();
                PlanMap.CanDragMap = false;
                e.Handled = true;
            };

            grid.MouseMove += (s, e) =>
            {
                if (_currentDragMarker == marker && grid.IsMouseCaptured)
                {
                    Point p = e.GetPosition(PlanMap);
                    var newPos = PlanMap.FromLocalToLatLng((int)p.X, (int)p.Y);
                    marker.Position = newPos;
                    wp.Latitude = newPos.Lat;
                    wp.Longitude = newPos.Lng;
                    UpdateRouteOnly();
                }
            };

            grid.MouseLeftButtonUp += (s, e) =>
            {
                if (_currentDragMarker == marker)
                {
                    grid.ReleaseMouseCapture();
                    PlanMap.CanDragMap = true;
                    _currentDragMarker = null;
                    UpdateRoute();
                }
            };

            // ПКМ — удаление (с подтверждением)
            grid.MouseRightButtonDown += (s, e) =>
            {
                string name = label == "S" ? "СТАРТ" : "ПОСАДКА";
                if (AppMessageBox.ShowConfirm($"Удалить точку {name}?", OwnerWindow, subtitle: "Подтверждение"))
                {
                    if (label == "S") { _startCircle = null; }
                    else { _landingCircle = null; }
                    UpdateRoute();
                }
                e.Handled = true;
            };
        }

        /// <summary>
        /// ToolTip для S/L точки
        /// </summary>
        private ToolTip CreateCircleTooltip(WaypointItem wp, string label)
        {
            var tooltip = new ToolTip
            {
                Background = new SolidColorBrush(Color.FromRgb(13, 23, 51)),
                BorderBrush = new SolidColorBrush(label == "S" 
                    ? Color.FromRgb(250, 204, 21)  // Yellow
                    : Color.FromRgb(249, 115, 22)), // Orange
                BorderThickness = new Thickness(2),
                Padding = new Thickness(10, 6, 10, 6)
            };

            var stack = new StackPanel();
            stack.Children.Add(new TextBlock
            {
                Text = label == "S" ? "СТАРТ" : "ПОСАДКА",
                Foreground = tooltip.BorderBrush,
                FontWeight = FontWeights.Bold,
                FontSize = 12
            });
            stack.Children.Add(new TextBlock
            {
                Text = $"Радиус: {wp.Radius:F0} м\n" +
                       $"Высота: {wp.Altitude:F0} м\n" +
                       $"Направление: {(wp.Clockwise ? "CW ↻" : "CCW ↺")}\n" +
                       $"Авто: {(wp.AutoNext ? "Да" : "Нет (ждать)")}",
                Foreground = Brushes.White,
                FontSize = 11
            });

            tooltip.Content = stack;
            return tooltip;
        }

        /// <summary>
        /// Нарисовать VTOL маршрутную линию
        /// </summary>
        private void DrawVtolLine(PointLatLng from, PointLatLng to, Color color, bool dashed)
        {
            var route = new GMapRoute(new List<PointLatLng> { from, to });
            var path = new Path
            {
                Stroke = new SolidColorBrush(color),
                StrokeThickness = 2.5,
                Opacity = 0.85
            };
            if (dashed)
                path.StrokeDashArray = new DoubleCollection { 6, 3 };
            route.Shape = path;
            route.ZIndex = 45;
            route.Tag = "vtol_line"; // Для идентификации при очистке
            PlanMap.Markers.Add(route);
        }

        #endregion


        /// <summary>
        /// Получить точки внешней касательной между двумя кругами (как ремень на шестерёнках)
        /// </summary>
        /// <summary>
        /// Вычисление точек касательной между двумя кругами с учётом направления кружения
        /// </summary>
        /// <param name="lat1">Широта центра 1</param>
        /// <param name="lon1">Долгота центра 1</param>
        /// <param name="r1">Радиус круга 1 (метры)</param>
        /// <param name="cw1">Направление кружения на круге 1 (true=CW)</param>
        /// <param name="lat2">Широта центра 2</param>
        /// <param name="lon2">Долгота центра 2</param>
        /// <param name="r2">Радиус круга 2 (метры)</param>
        /// <param name="cw2">Направление кружения на круге 2 (true=CW)</param>
        private (PointLatLng, PointLatLng) GetExternalTangentPoints(
            double lat1, double lon1, double r1, bool cw1,
            double lat2, double lon2, double r2, bool cw2)
        {
            double dist = CalculateDistanceLatLng(lat1, lon1, lat2, lon2);
            double bearing = CalculateBearing(lat1, lon1, lat2, lon2);

            // CW: дрон кружит по часовой → выход слева (−90° от bearing к следующему)
            // CCW: дрон кружит против часовой → выход справа (+90° от bearing)
            // Это единая формула для ВСЕХ комбинаций — меняется только знак tangentAngle
            double exitOffset = cw1 ? -90 : 90;
            double entryOffset = cw2 ? -90 : 90;

            bool sameSide = (cw1 == cw2);
            double tangentAngle = 0;

            if (sameSide)
            {
                // Внешняя касательная (CW→CW или CCW→CCW): ремень по одной стороне
                if (dist > Math.Abs(r1 - r2))
                {
                    double sinAlpha = (r1 - r2) / dist;
                    sinAlpha = Math.Max(-1, Math.Min(1, sinAlpha));
                    tangentAngle = Math.Asin(sinAlpha) * 180 / Math.PI;
                }

                // Внешняя: tangentAngle вычитается
                double exitAngle = bearing + exitOffset + tangentAngle;
                double entryAngle = bearing + entryOffset + tangentAngle;

                return (
                    CalculatePointAtDistance(lat1, lon1, exitAngle, r1 / 1000.0),
                    CalculatePointAtDistance(lat2, lon2, entryAngle, r2 / 1000.0)
                );
            }
            else
            {
                // Перекрёстная касательная (CW→CCW или CCW→CW): ремень пересекает
                if (dist > (r1 + r2))
                {
                    double sinAlpha = (r1 + r2) / dist;
                    sinAlpha = Math.Max(-1, Math.Min(1, sinAlpha));
                    tangentAngle = Math.Asin(sinAlpha) * 180 / Math.PI;
                }

                // Перекрёстная: знак tangentAngle зависит от того, кто CW
                // CW→CCW: +α, CCW→CW: -α (зеркальная касательная)
                double crossSign = cw1 ? 1 : -1;
                double exitAngle = bearing + exitOffset + crossSign * tangentAngle;
                double entryAngle = bearing + entryOffset + crossSign * tangentAngle;

                return (
                    CalculatePointAtDistance(lat1, lon1, exitAngle, r1 / 1000.0),
                    CalculatePointAtDistance(lat2, lon2, entryAngle, r2 / 1000.0)
                );
            }
        }

        /// <summary>
        /// Упрощённая версия для совместимости (HOME не имеет радиуса)
        /// </summary>
        private (PointLatLng, PointLatLng) GetExternalTangentPoints(
            double lat1, double lon1, double r1,
            double lat2, double lon2, double r2)
        {
            return GetExternalTangentPoints(lat1, lon1, r1, true, lat2, lon2, r2, true);
        }


        /// <summary>
        /// Ближайшая точка на краю круга со стороны внешней точки
        /// </summary>
        private PointLatLng GetNearEdgePoint(double circleLat, double circleLon, double circleRadius,
                                              double pointLat, double pointLon)
        {
            double bearing = CalculateBearing(circleLat, circleLon, pointLat, pointLon);
            return CalculatePointAtDistance(circleLat, circleLon, bearing, circleRadius / 1000.0);
        }


        /// <summary>
        /// Обновить маршрут БЕЗ автосоздания HOME
        /// </summary>
        private void UpdateRouteOnly()
        {
            var oldRoutes = PlanMap.Markers.OfType<GMapRoute>().ToList();
            foreach (var r in oldRoutes)
            {
                r.Shape = null;
                PlanMap.Markers.Remove(r);
            }

            if (_waypoints.Count == 0) return;

            var entryPoints = new Dictionary<int, PointLatLng>();
            var exitPoints = new Dictionary<int, PointLatLng>();

            bool isVTOL = _currentVehicleType == VehicleType.QuadPlane;

            // VTOL: инициализация S/L
            if (isVTOL)
            {
                if (_startCircle == null) InitializeStartCircle();
                if (_landingCircle == null) InitializeLandingCircle();
            }

            // Вход в первую точку
            // СТАЛО:
            if (isVTOL && _startCircle != null)
            {
                var firstWp = _waypoints[0];
                entryPoints[0] = GetNearEdgePoint(
                    firstWp.Latitude, firstWp.Longitude, firstWp.Radius,
                    _startCircle.Latitude, _startCircle.Longitude);
            }
            else if (_homePosition != null)
            {
                var homePoint = new PointLatLng(_homePosition.Latitude, _homePosition.Longitude);
                var firstWp = _waypoints[0];

                var edgeWp = GetNearEdgePoint(
                    firstWp.Latitude, firstWp.Longitude, firstWp.Radius,
                    _homePosition.Latitude, _homePosition.Longitude);

                entryPoints[0] = edgeWp;

                var homeRoute = new GMapRoute(new List<PointLatLng> { homePoint, edgeWp });
                homeRoute.Shape = new Path
                {
                    Stroke = new SolidColorBrush(Color.FromRgb(239, 68, 68)),
                    StrokeThickness = 2,
                    StrokeDashArray = new DoubleCollection { 5, 3 },
                    Opacity = 0.9
                };
                homeRoute.ZIndex = 40;
                PlanMap.Markers.Add(homeRoute);
            }

            // Между точками
            for (int i = 0; i < _waypoints.Count - 1; i++)
            {
                var wp1 = _waypoints[i];
                var wp2 = _waypoints[i + 1];

                var tangentPoints = GetExternalTangentPoints(
                    wp1.Latitude, wp1.Longitude, wp1.Radius, wp1.Clockwise,
                    wp2.Latitude, wp2.Longitude, wp2.Radius, wp2.Clockwise);

                exitPoints[i] = tangentPoints.Item1;
                entryPoints[i + 1] = tangentPoints.Item2;

                var segmentRoute = new GMapRoute(new List<PointLatLng> { tangentPoints.Item1, tangentPoints.Item2 });
                segmentRoute.Shape = new Path
                {
                    Stroke = new SolidColorBrush(Color.FromRgb(152, 240, 25)),
                    StrokeThickness = 3,
                    Opacity = 0.9
                };
                segmentRoute.ZIndex = 50;
                PlanMap.Markers.Add(segmentRoute);
            }

            // Выход из последней точки
            if (isVTOL && _landingCircle != null && _waypoints.Count > 0)
            {
                int lastIdx = _waypoints.Count - 1;
                var lastWp = _waypoints[lastIdx];
                var tangentPoints = GetExternalTangentPoints(
                    lastWp.Latitude, lastWp.Longitude, lastWp.Radius, lastWp.Clockwise,
                    _landingCircle.Latitude, _landingCircle.Longitude, _landingCircle.Radius, _landingCircle.Clockwise);
                exitPoints[lastIdx] = tangentPoints.Item1;
            }
            else if (_homePosition != null && _waypoints.Count > 0)
            {
                int lastIdx = _waypoints.Count - 1;
                var lastWp = _waypoints[lastIdx];
                var homePoint = new PointLatLng(_homePosition.Latitude, _homePosition.Longitude);

                var edgeWp = GetNearEdgePoint(
                    lastWp.Latitude, lastWp.Longitude, lastWp.Radius,
                    _homePosition.Latitude, _homePosition.Longitude);

                exitPoints[lastIdx] = edgeWp;

                var returnRoute = new GMapRoute(new List<PointLatLng> { edgeWp, homePoint });
                returnRoute.Shape = new Path
                {
                    Stroke = new SolidColorBrush(Color.FromRgb(239, 68, 68)),
                    StrokeThickness = 2,
                    StrokeDashArray = new DoubleCollection { 5, 3 },
                    Opacity = 0.9
                };
                returnRoute.ZIndex = 40;
                PlanMap.Markers.Add(returnRoute);
            }

            // Дуги
            for (int i = 0; i < _waypoints.Count; i++)
            {
                if (entryPoints.ContainsKey(i) && exitPoints.ContainsKey(i))
                {
                    DrawArcOnWaypoint(_waypoints[i], entryPoints[i], exitPoints[i]);
                }
            }
        }



        /// <summary>
        /// Расчёт точки касания линии с кругом радиуса
        /// </summary>
        /// <summary>
        /// <summary>
        /// Расчёт азимута между двумя точками (в градусах)
        /// </summary>
        private double CalculateBearing(double lat1, double lon1, double lat2, double lon2)
        {
            double dLon = (lon2 - lon1) * Math.PI / 180;
            double lat1Rad = lat1 * Math.PI / 180;
            double lat2Rad = lat2 * Math.PI / 180;

            double y = Math.Sin(dLon) * Math.Cos(lat2Rad);
            double x = Math.Cos(lat1Rad) * Math.Sin(lat2Rad) - Math.Sin(lat1Rad) * Math.Cos(lat2Rad) * Math.Cos(dLon);

            double bearing = Math.Atan2(y, x) * 180 / Math.PI;
            return (bearing + 360) % 360;
        }

        /// <summary>
        /// Расчёт расстояния между координатами (в метрах)
        /// </summary>
        private double CalculateDistanceLatLng(double lat1, double lon1, double lat2, double lon2)
        {
            const double R = 6371000; // Радиус Земли в метрах
            double dLat = (lat2 - lat1) * Math.PI / 180;
            double dLon = (lon2 - lon1) * Math.PI / 180;
            double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                       Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180) *
                       Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
            double c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
            return R * c;
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
        /// Рисование дуги на waypoint В НАПРАВЛЕНИИ КРУЖЕНИЯ дрона
        /// Дуга показывает РЕАЛЬНЫЙ путь дрона по кругу от входа до выхода
        /// </summary>
        private void DrawArcOnWaypoint(WaypointItem wp, PointLatLng entryPoint, PointLatLng exitPoint)
        {
            double entryAngle = CalculateBearing(wp.Latitude, wp.Longitude, entryPoint.Lat, entryPoint.Lng);
            double exitAngle = CalculateBearing(wp.Latitude, wp.Longitude, exitPoint.Lat, exitPoint.Lng);

            double angleDiff;
            
            // CW (по часовой): bearing увеличивается (N→E→S→W) → angleDiff POSITIVE
            // CCW (против часовой): bearing уменьшается (N→W→S→E) → angleDiff NEGATIVE
            if (wp.Clockwise)
            {
                angleDiff = exitAngle - entryAngle;
                while (angleDiff <= 0) angleDiff += 360;
                while (angleDiff > 360) angleDiff -= 360;
            }
            else
            {
                angleDiff = exitAngle - entryAngle;
                while (angleDiff >= 0) angleDiff -= 360;
                while (angleDiff < -360) angleDiff += 360;
            }

            // Пропускаем очень маленькие дуги
            if (Math.Abs(angleDiff) < 5) return;

            var arcPoints = new List<PointLatLng>();
            int steps = Math.Max(3, (int)(Math.Abs(angleDiff) / 5));
            double stepAngle = angleDiff / steps;

            for (int i = 0; i <= steps; i++)
            {
                double angle = entryAngle + stepAngle * i;
                var pt = CalculatePointAtDistance(wp.Latitude, wp.Longitude, angle, wp.Radius / 1000.0);
                arcPoints.Add(pt);
            }

            if (arcPoints.Count >= 2)
            {
                var arcRoute = new GMapRoute(arcPoints);
                arcRoute.Shape = new Path
                {
                    Stroke = new SolidColorBrush(Color.FromRgb(152, 240, 25)),
                    StrokeThickness = 3,
                    Opacity = 0.9
                };
                arcRoute.ZIndex = 50;
                PlanMap.Markers.Add(arcRoute);
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

            // Обновляем ленту миссии
            UpdateMissionStrip();
        }



        #region MISSION STRIP — S/L карточки в нижней панели

        /// <summary>
        /// Обновить видимость карточек S и L в нижней панели
        /// </summary>
        /// 

        

        private void UpdateMissionStrip()
        {
            bool isVtol = _currentVehicleType == VehicleType.QuadPlane;

            // Показать/скрыть VTOL карточки
            var vis = isVtol ? Visibility.Visible : Visibility.Collapsed;

            if (StartCircleCard != null) StartCircleCard.Visibility = vis;
            if (LandingCircleCard != null) LandingCircleCard.Visibility = vis; 
            if (ArrowAfterTakeoff != null) ArrowAfterTakeoff.Visibility = isVtol ? Visibility.Collapsed : Visibility.Visible;
            if (ArrowAfterTakeoffVtol != null) ArrowAfterTakeoffVtol.Visibility = vis;
            if (ArrowAfterStart != null) ArrowAfterStart.Visibility = vis;
            if (ArrowBeforeLand != null) ArrowBeforeLand.Visibility = vis;

            // Обновить текст с параметрами S/L
            if (isVtol)
            {
                if (StartCircleInfo != null && _startCircle != null)
                {
                    StartCircleInfo.Text = $"· {_startCircle.Radius:F0}м";
                }

                if (LandingCircleInfo != null && _landingCircle != null)
                {
                    LandingCircleInfo.Text = $"· {_landingCircle.Radius:F0}м";
                }
            }

            // RTL и стрелка видны всегда при наличии WP
            bool hasWp = _waypoints.Count > 0;
            if (ArrowBeforeRtl != null) ArrowBeforeRtl.Visibility = hasWp ? Visibility.Visible : Visibility.Collapsed;
            if (RtlCard != null) RtlCard.Visibility = hasWp ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>
        /// Подсветить карточку текущего WP (вызывается из MissionProgressUpdated)
        /// </summary>
        public void HighlightMissionSeq(int seq)
        {
            Dispatcher.BeginInvoke(() =>
            {
                // Сброс подсветки предыдущего
                // Подсветка нового WP в WaypointsListPanel
                if (WaypointsListPanel == null) return;
                foreach (var child in WaypointsListPanel.Children)
                {
                    if (child is Border border && border.Tag is int wpNum)
                    {
                        bool isVtol = _currentVehicleType == VehicleType.QuadPlane;
                        int expectedSeq = isVtol ? wpNum + 3 : wpNum; // VTOL: WP1=seq4, Copter: WP1=seq1
                        
                        if (expectedSeq == seq)
                        {
                            border.BorderBrush = new SolidColorBrush(Color.FromRgb(74, 222, 128)); // Зелёный
                            border.BorderThickness = new Thickness(3);
                        }
                        else
                        {
                            border.BorderBrush = new SolidColorBrush(Color.FromRgb(152, 240, 25));
                            border.BorderThickness = new Thickness(2);
                        }
                    }
                }

                // Подсветка S и L
                if (StartCircleCard != null)
                    StartCircleCard.BorderThickness = new Thickness(seq == 3 ? 3 : 2);
                if (LandingCircleCard != null)
                {
                    int landSeq = _waypoints.Count + 4;
                    LandingCircleCard.BorderThickness = new Thickness(seq == landSeq ? 3 : 2);
                }
            });
        }

        #endregion

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
        /// Обновление списка waypoints в UI (ГОРИЗОНТАЛЬНЫЙ LAYOUT)
        /// </summary>
        private void UpdateWaypointsList()
        {
            WaypointsListPanel.Children.Clear();
            WaypointsCountText.Text = $"{_waypoints.Count} точек";

            // Стрелка перед RTL
            if (ArrowBeforeRtl != null)
                ArrowBeforeRtl.Visibility = _waypoints.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            foreach (var wp in _waypoints)
            {
                // Стрелка между waypoints
                if (wp.Number > 1)
                {
                    WaypointsListPanel.Children.Add(new TextBlock
                    {
                        Text = "›",
                        Foreground = new SolidColorBrush(Color.FromRgb(152, 240, 25)),
                        FontSize = 20,
                        FontWeight = FontWeights.Bold,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(6, 0, 6, 0)
                    });
                }

                WaypointsListPanel.Children.Add(CreateWaypointCard(wp));
            }
        }

        /// <summary>
        /// Создание карточки waypoint
        /// </summary>
        /// <summary>
        /// Создание КОМПАКТНОЙ карточки waypoint (минималистичный дизайн)
        /// </summary>
        private Border CreateWaypointCard(WaypointItem wp)
        {
            var card = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(13, 23, 51)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(42, 67, 97)),
                BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(3, 3, 8, 3),
                Margin = new Thickness(0),
                Cursor = Cursors.Hand,
                Tag = wp.Number
            };

            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            // Номер
            var numBorder = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(152, 240, 25)),
                CornerRadius = new CornerRadius(12),
                Width = 24,
                Height = 24,
                Margin = new Thickness(0, 0, 6, 0)
            };
            numBorder.Child = new TextBlock
            {
                Text = wp.Number.ToString(),
                Foreground = new SolidColorBrush(Color.FromRgb(6, 11, 26)),
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            row.Children.Add(numBorder);

            // Координаты
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            row.Children.Add(new TextBlock
            {
                Text = $"Ш: {wp.Latitude.ToString("F2", inv)} ► {wp.Longitude.ToString("F2", inv)}",
                Foreground = new SolidColorBrush(Color.FromRgb(152, 240, 25)),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center
            });

            // Крестик удаления
            var delBtn = new TextBlock
            {
                Text = "✕",
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(107, 114, 128)),
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0),
                ToolTip = "Удалить точку"
            };
            delBtn.MouseEnter += (s, e) => delBtn.Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68));
            delBtn.MouseLeave += (s, e) => delBtn.Foreground = new SolidColorBrush(Color.FromRgb(107, 114, 128));
            delBtn.MouseLeftButtonDown += (s, e) =>
            {
                e.Handled = true;
                RemoveWaypoint(wp);
            };
            row.Children.Add(delBtn);

            card.Child = row;

            card.MouseLeftButtonDown += (s, e) =>
            {
                if (e.OriginalSource is TextBlock tb && tb.Text == "✕") return;
                e.Handled = true;
                SelectWaypoint(wp);
                PlanMap.Position = new PointLatLng(wp.Latitude, wp.Longitude);
                OpenWaypointEditDialog(wp);
            };

            return card;
        }


        /// <summary>
        /// Открыть диалог редактирования waypoint
        /// </summary>
        private void OpenWaypointEditDialog(WaypointItem wp)
        {
            var dialog = new WaypointEditDialog(
                wp.Number,
                wp.Latitude,
                wp.Longitude,
                wp.Altitude,
                wp.Radius,
                wp.Delay,
                wp.LoiterTurns,
                wp.AutoNext,
                wp.Clockwise
            );

            dialog.Owner = Window.GetWindow(this);

            if (dialog.ShowDialog() == true)
            {
                // Проверяем изменились ли координаты
                bool positionChanged = (wp.Latitude != dialog.Latitude || wp.Longitude != dialog.Longitude);

                // Обновляем данные точки
                wp.Latitude = dialog.Latitude;
                wp.Longitude = dialog.Longitude;
                wp.Altitude = dialog.Altitude;
                wp.Radius = dialog.Radius;
                wp.Delay = dialog.Delay;
                wp.LoiterTurns = dialog.LoiterTurns;
                wp.AutoNext = dialog.AutoNext;
                wp.Clockwise = dialog.Clockwise;

                // Обновляем маркер на карте если координаты изменились
                if (positionChanged && wp.Marker != null)
                {
                    wp.Marker.Position = new PointLatLng(wp.Latitude, wp.Longitude);

                    // Обновляем ручку радиуса
                    if (_resizeHandles.ContainsKey(wp))
                    {
                        var handlePos = CalculatePointAtDistance(wp.Latitude, wp.Longitude, 90, wp.Radius / 1000.0);
                        _resizeHandles[wp].Position = new PointLatLng(handlePos.Lat, handlePos.Lng);
                    }
                }

                // Обновляем визуал радиуса
                UpdateWaypointRadiusVisual(wp);

                // Обновляем маршрут и список
                UpdateRoute();
                UpdateWaypointsList();
                UpdateStatistics();

                // === РЕАЛ-ТАЙМ ОБНОВЛЕНИЕ МИССИИ ===
                // Если дрон летит (Armed + AUTO mode), переотправляем миссию на лету
                TryRealTimeMissionUpdate(wp);

                System.Diagnostics.Debug.WriteLine($"[WP EDIT] Точка {wp.Number} обновлена: {wp.Latitude:F6}, {wp.Longitude:F6}, Alt={wp.Altitude}м");
            }
        }

        /// <summary>
        /// Реал-тайм обновление миссии на дроне
        /// Если дрон в полёте (AUTO режим) — переотправляет всю миссию
        /// и возвращает дрон к текущей точке
        /// </summary>
        private async void TryRealTimeMissionUpdate(WaypointItem changedWp = null)
        {
            try
            {
                if (_mavlinkService == null || !_mavlinkService.IsConnected) return;

                var telem = _mavlinkService.CurrentTelemetry;
                if (telem == null || !telem.Armed) return;

                string mode = telem.FlightMode?.ToUpper() ?? "";
                if (mode != "AUTO" && mode != "MISSION" && mode != "LOITER") return;

                System.Diagnostics.Debug.WriteLine($"[REALTIME] Дрон в {mode}, обновляем миссию...");

                bool success;
                if (_currentVehicleType == VehicleType.QuadPlane && _startCircle != null && _landingCircle != null)
                {
                    success = await _mavlinkService.UploadVtolMission(
                        _homePosition, _startCircle, _waypoints.ToList(), _landingCircle,
                        _vtolTakeoffAltitude, _vtolLandAltitude);
                }
                else
                {
                    _mavlinkService.SavePlannedMission(_waypoints.ToList());
                    success = await _mavlinkService.UploadPlannedMission();
                }

                System.Diagnostics.Debug.WriteLine(success
                    ? "[REALTIME] ✅ Миссия обновлена"
                    : "[REALTIME] ❌ Ошибка обновления");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[REALTIME] Ошибка: {ex.Message}");
            }
        }


        /// <summary>
        /// Команды для текущего типа ЛА (сокращённые названия)
        /// </summary>
        private dynamic[] GetCommandsForVehicleType()
        {
            if (_currentVehicleType == VehicleType.QuadPlane)
            {
                // ВТОЛ команды (переход автоматический через Q_OPTIONS)
                return new dynamic[]
                {
            new { Name = "ТОЧКА", Value = "WAYPOINT" },
            new { Name = "КРУГ", Value = "LOITER_UNLIM" },
            new { Name = "КРУГ(время)", Value = "LOITER_TIME" },
            new { Name = "КРУГ(обор)", Value = "LOITER_TURNS" },
            new { Name = "ПОСАДКА", Value = "LAND" },
            new { Name = "ЗАДЕРЖКА", Value = "DELAY" },
            new { Name = "СКОРОСТЬ", Value = "CHANGE_SPEED" }
                };
            }

            // Мультикоптер команды
            return new dynamic[]
            {
        new { Name = "ТОЧКА", Value = "WAYPOINT" },
        new { Name = "КРУГ", Value = "LOITER_UNLIM" },
        new { Name = "КРУГ(время)", Value = "LOITER_TIME" },
        new { Name = "КРУГ(обор)", Value = "LOITER_TURNS" },
        new { Name = "ВЗЛЁТ", Value = "TAKEOFF" },
        new { Name = "ПОСАДКА", Value = "LAND" },
        new { Name = "ЗАДЕРЖКА", Value = "DELAY" },
        new { Name = "СКОРОСТЬ", Value = "CHANGE_SPEED" },
        new { Name = "ВОЗВРАТ", Value = "RETURN_TO_LAUNCH" },
        new { Name = "СПЛАЙН", Value = "SPLINE_WP" }
            };
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
                    _homePosition.Marker.Shape = null;
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

            PlanHomeLatText.Text = _homePosition.Latitude.ToString("F6");
            PlanHomeLonText.Text = _homePosition.Longitude.ToString("F6");

            System.Diagnostics.Debug.WriteLine($" HOME установлена: {telemetry.Latitude:F6}, {telemetry.Longitude:F6}");
        }

        /// <summary>
        /// Добавление HOME маркера на карту
        /// </summary>
        private void AddHomeMarkerToMap(WaypointItem home)
        {

            // Защита от дублирования
            if (home.Marker != null)
            {
                home.Marker.Shape = null;  // ВАЖНО!
                PlanMap.Markers.Remove(home.Marker);
                home.Marker = null;
            }

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
        /// Горизонтальный скролл панели миссии колёсиком мыши
        /// </summary>
        private void MissionScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (sender is ScrollViewer sv)
            {
                // Конвертируем вертикальный скролл в горизонтальный
                if (e.Delta > 0)
                    sv.LineLeft();
                else
                    sv.LineRight();

                e.Handled = true;
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

                    // Без жёсткого ограничения — circle должен совпадать с geo-arc
                    radiusInPixels = Math.Min(5000, radiusInPixels);

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
                        wp.Marker.Shape = null;
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

            // === ОБНОВЛЯЕМ S/L КРУГИ (VTOL) ===
            if (_currentVehicleType == VehicleType.QuadPlane)
            {
                UpdateSpecialCircleSize(_startCircle, "S");
                UpdateSpecialCircleSize(_landingCircle, "L");
            }

            // Обновляем линии
            UpdateRoute();

            // Принудительное обновление карты
            PlanMap.InvalidateVisual();

            System.Diagnostics.Debug.WriteLine($" RefreshMarkers: завершено\n");
        }

        /// <summary>
        /// Обновить размер S/L круга при изменении зума
        /// </summary>
        private void UpdateSpecialCircleSize(WaypointItem wp, string label)
        {
            if (wp == null || wp.ShapeGrid == null || wp.RadiusCircle == null) return;

            double radiusInPixels = MetersToPixels(wp.Radius, wp.Latitude, PlanMap.Zoom);
            radiusInPixels = Math.Max(3, Math.Min(5000, radiusInPixels));
            double diameter = radiusInPixels * 2;
            double gridSize = Math.Max(60, diameter);

            // Обновляем размеры
            wp.ShapeGrid.Width = gridSize;
            wp.ShapeGrid.Height = gridSize;
            wp.RadiusCircle.Width = diameter;
            wp.RadiusCircle.Height = diameter;

            // Обновляем Offset маркера
            if (wp.Marker != null)
            {
                wp.Marker.Offset = new Point(-gridSize / 2, -gridSize / 2);
            }

            wp.ShapeGrid.InvalidateVisual();
            wp.RadiusCircle.InvalidateVisual();

            System.Diagnostics.Debug.WriteLine($"   {label}: Radius={wp.Radius:F0}м → {radiusInPixels:F0}px");
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

                    // Для VTOL: всегда включаем автопереход после взлёта (Q_OPTIONS=128)
                    if (_currentVehicleType == VehicleType.QuadPlane && _mavlinkService.IsConnected)
                    {
                        _mavlinkService.SetVTOLAutoTransition(true);
                        System.Diagnostics.Debug.WriteLine($" VTOL: Q_OPTIONS=128 (автопереход после взлёта)");
                    }

                    // MissionStore получает полную миссию (с VTOL-структурой если нужно)
                    MissionStore.Set((int)_currentVehicleType, GetFullMission());

                    // Формируем сообщение об успехе
                    string successMsg = $"Миссия сохранена: {_waypoints.Count} точек.";
                    if (_currentVehicleType == VehicleType.QuadPlane)
                    {
                        successMsg += "\n✈️ Взлёт → авто в самолёт → точки → коптер → посадка";
                    }

                    AppMessageBox.ShowSuccess(
                        successMsg,
                        owner: OwnerWindow,
                        subtitle: "Миссия сохранена"
                    );
                }
                else
                {
                    MissionStore.Set((int)_currentVehicleType, GetFullMission());

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



        private async void DownloadButton_Click(object sender, RoutedEventArgs e)
        {
            if (_mavlinkService == null || !_mavlinkService.IsConnected)
            {
                AppMessageBox.ShowWarning(
                    "Подключитесь к дрону для чтения миссии.",
                    owner: OwnerWindow,
                    subtitle: "Нет подключения",
                    hint: "Подключитесь через серийный порт или UDP."
                );
                return;
            }

            try
            {
                System.Diagnostics.Debug.WriteLine("[DOWNLOAD] Начало чтения миссии с дрона...");

                var items = await _mavlinkService.DownloadMission(15000);

                if (items == null || items.Count == 0)
                {
                    AppMessageBox.ShowWarning("Миссия на дроне пуста или не удалось прочитать.", owner: OwnerWindow, subtitle: "Пустая миссия");
                    return;
                }

                // Подтверждение перезаписи текущей миссии
                if (_waypoints.Count > 0)
                {
                    if (!AppMessageBox.ShowConfirm(
                        $"Текущая миссия ({_waypoints.Count} точек) будет заменена на миссию с дрона ({items.Count} элементов).\n\nПродолжить?",
                        owner: OwnerWindow,
                        subtitle: "Заменить миссию?"))
                    {
                        return;
                    }
                }

                // Очищаем текущую миссию
                PlanMap.Markers.Clear();
                _waypoints.Clear();
                _startCircle = null;
                _landingCircle = null;
                _homePosition = null;
                _resizeHandles.Clear();
                _isMissionFrozen = false;

                // Определяем тип миссии
                bool isVtolMission = items.Any(it => it.command == 84 || it.command == 85 || it.command == 3000);

                // HOME (seq=0, обычно cmd=16 с frame=0)
                var homeItem = items.FirstOrDefault(it => it.seq == 0);
                if (homeItem.x != 0 || homeItem.y != 0)
                {
                    _homePosition = new WaypointItem
                    {
                        Number = 0,
                        Latitude = homeItem.x / 1e7,
                        Longitude = homeItem.y / 1e7,
                        Altitude = homeItem.z,
                        CommandType = "HOME",
                        Radius = 20
                    };
                    AddHomeMarkerToMap(_homePosition);
                    PlanMap.Position = new PointLatLng(_homePosition.Latitude, _homePosition.Longitude);
                }

                if (isVtolMission)
                {
                    // VTOL миссия: TAKEOFF(84), TRANSITION(3000), NAV points, TRANSITION(3000), VTOL_LAND(85)
                    var takeoff = items.FirstOrDefault(it => it.command == 84);
                    if (takeoff.z > 0) _vtolTakeoffAltitude = takeoff.z;

                    // Все навигационные точки (16=WP, 17=LOITER_UNLIM, 18=LOITER_TURNS, 19=LOITER_TIME)
                    var navItems = items.Where(it =>
                        it.seq > 0 &&
                        (it.command == 16 || it.command == 17 || it.command == 18 || it.command == 19) &&
                        (it.x != 0 || it.y != 0)
                    ).ToList();

                    if (navItems.Count >= 3)
                    {
                        // Первая NAV = StartCircle, последняя NAV = LandingCircle
                        var sItem = navItems.First();
                        _startCircle = new WaypointItem
                        {
                            Number = 0,
                            Latitude = sItem.x / 1e7,
                            Longitude = sItem.y / 1e7,
                            Altitude = sItem.z,
                            Radius = Math.Max(150, Math.Abs(sItem.param3) > 0 ? Math.Abs(sItem.param3) : 150),
                            Clockwise = sItem.param3 >= 0,
                            CommandType = sItem.command == 18 ? "LOITER_TURNS" : "LOITER_UNLIM",
                            AutoNext = sItem.autocontinue == 1,
                            LoiterTurns = (int)sItem.param1
                        };

                        var lItem = navItems.Last();
                        _landingCircle = new WaypointItem
                        {
                            Number = 0,
                            Latitude = lItem.x / 1e7,
                            Longitude = lItem.y / 1e7,
                            Altitude = lItem.z,
                            Radius = Math.Max(150, Math.Abs(lItem.param3) > 0 ? Math.Abs(lItem.param3) : 150),
                            Clockwise = lItem.param3 >= 0,
                            CommandType = lItem.command == 18 ? "LOITER_TURNS" : "LOITER_UNLIM",
                            AutoNext = lItem.autocontinue == 1,
                            LoiterTurns = (int)lItem.param1
                        };

                        // Waypoints (всё между S и L)
                        for (int w = 1; w < navItems.Count - 1; w++)
                        {
                            var nav = navItems[w];
                            var wp = new WaypointItem
                            {
                                Number = _waypoints.Count + 1,
                                Latitude = nav.x / 1e7,
                                Longitude = nav.y / 1e7,
                                Altitude = nav.z,
                                Radius = Math.Abs(nav.param3) > 0 ? Math.Abs(nav.param3) : 80,
                                Clockwise = nav.param3 >= 0,
                                CommandType = ConvertMAVCmdToCommandType(nav.command),
                                AutoNext = nav.autocontinue == 1,
                                Delay = (nav.command == 16 || nav.command == 93) ? nav.param1 : 0,
                                LoiterTurns = (nav.command == 18) ? (int)nav.param1 : 0
                            };
                            _waypoints.Add(wp);
                            AddMarkerToMap(wp);
                        }
                    }
                    else
                    {
                        foreach (var nav in navItems)
                        {
                            var wp = new WaypointItem
                            {
                                Number = _waypoints.Count + 1,
                                Latitude = nav.x / 1e7,
                                Longitude = nav.y / 1e7,
                                Altitude = nav.z,
                                Radius = Math.Abs(nav.param3) > 0 ? Math.Abs(nav.param3) : 80,
                                Clockwise = nav.param3 >= 0,
                                CommandType = ConvertMAVCmdToCommandType(nav.command),
                                AutoNext = nav.autocontinue == 1
                            };
                            _waypoints.Add(wp);
                            AddMarkerToMap(wp);
                        }
                    }
                }
                else
                {
                    // Copter миссия
                    var takeoff = items.FirstOrDefault(it => it.command == 22);
                    if (takeoff.z > 0) _takeoffAltitude = takeoff.z;

                    var navItems = items.Where(it =>
                        it.seq > 0 &&
                        it.command != 22 && it.command != 20 &&
                        (it.command == 16 || it.command == 17 || it.command == 18 || it.command == 19 || it.command == 82) &&
                        (it.x != 0 || it.y != 0)
                    ).ToList();

                    foreach (var nav in navItems)
                    {
                        var wp = new WaypointItem
                        {
                            Number = _waypoints.Count + 1,
                            Latitude = nav.x / 1e7,
                            Longitude = nav.y / 1e7,
                            Altitude = nav.z,
                            Radius = Math.Abs(nav.param3) > 0 ? Math.Abs(nav.param3) : 80,
                            Clockwise = nav.param3 >= 0,
                            CommandType = ConvertMAVCmdToCommandType(nav.command),
                            AutoNext = nav.autocontinue == 1,
                            Delay = (nav.command == 16 || nav.command == 93) ? nav.param1 : 0,
                            LoiterTurns = (nav.command == 18) ? (int)nav.param1 : 0
                        };
                        _waypoints.Add(wp);
                        AddMarkerToMap(wp);
                    }
                }

                // Обновляем UI
                UpdateRoute();
                UpdateWaypointsList();
                UpdateStatistics();

                string typeStr = isVtolMission ? "VTOL" : "Copter";
                AppMessageBox.ShowSuccess(
                    $"Прочитано с дрона: {_waypoints.Count} точек ({typeStr}).",
                    owner: OwnerWindow,
                    subtitle: "Миссия загружена"
                );

                System.Diagnostics.Debug.WriteLine($"[DOWNLOAD] ✅ Миссия прочитана: {_waypoints.Count} WPs, VTOL={isVtolMission}");
            }
            catch (Exception ex)
            {
                AppMessageBox.ShowError($"Ошибка чтения миссии: {ex.Message}", owner: OwnerWindow, subtitle: "Ошибка");
                System.Diagnostics.Debug.WriteLine($"[DOWNLOAD] Ошибка: {ex.Message}\n{ex.StackTrace}");
            }
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            if (_waypoints == null || _waypoints.Count == 0)
            {
                AppMessageBox.ShowWarning("Нет точек для сохранения.", owner: OwnerWindow, subtitle: "Пустая миссия");
                return;
            }

            try
            {
                var dlg = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "Сохранить миссию",
                    Filter = "Mission files (*.txt;*.waypoints)|*.txt;*.waypoints|All files (*.*)|*.*",
                    DefaultExt = ".txt",
                    FileName = $"mission_{DateTime.Now:yyyyMMdd_HHmm}.txt"
                };

                if (dlg.ShowDialog() != true) return;

                SaveMissionToPath(dlg.FileName);

                AppMessageBox.ShowSuccess(
                    $"Миссия сохранена: {_waypoints.Count} точек.\n📁 {System.IO.Path.GetFileName(dlg.FileName)}",
                    owner: OwnerWindow,
                    subtitle: "Файл сохранён"
                );
            }
            catch (Exception ex)
            {
                AppMessageBox.ShowError($"Ошибка сохранения: {ex.Message}", owner: OwnerWindow, subtitle: "Ошибка");
            }
        }

        private void LoadButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "Загрузить миссию",
                    Filter = "Mission files (*.txt;*.waypoints)|*.txt;*.waypoints|All files (*.*)|*.*"
                };

                if (dlg.ShowDialog() != true) return;

                var lines = System.IO.File.ReadAllLines(dlg.FileName);
                if (lines.Length < 2 || !lines[0].StartsWith("QGC WPL"))
                {
                    AppMessageBox.ShowError("Неверный формат файла. Ожидается QGC WPL 110.", owner: OwnerWindow, subtitle: "Ошибка формата");
                    return;
                }

                // Очищаем текущую миссию
                PlanMap.Markers.Clear();
                _waypoints.Clear();
                _startCircle = null;
                _landingCircle = null;
                _homePosition = null;
                _resizeHandles.Clear();
                _isMissionFrozen = false;

                // Парсим все строки
                var parsedItems = new List<(int seq, int frame, ushort cmd, double p1, double p2, double p3, double p4, double lat, double lon, double alt, int autoCont)>();

                for (int i = 1; i < lines.Length; i++)
                {
                    var parts = lines[i].Split('\t');
                    if (parts.Length < 12) continue;

                    parsedItems.Add((
                        seq: int.Parse(parts[0]),
                        frame: int.Parse(parts[2]),
                        cmd: ushort.Parse(parts[3]),
                        p1: double.Parse(parts[4], System.Globalization.CultureInfo.InvariantCulture),
                        p2: double.Parse(parts[5], System.Globalization.CultureInfo.InvariantCulture),
                        p3: double.Parse(parts[6], System.Globalization.CultureInfo.InvariantCulture),
                        p4: double.Parse(parts[7], System.Globalization.CultureInfo.InvariantCulture),
                        lat: double.Parse(parts[8], System.Globalization.CultureInfo.InvariantCulture),
                        lon: double.Parse(parts[9], System.Globalization.CultureInfo.InvariantCulture),
                        alt: double.Parse(parts[10], System.Globalization.CultureInfo.InvariantCulture),
                        autoCont: int.Parse(parts[11])
                    ));
                }

                // Определяем тип миссии
                bool isVtolMission = parsedItems.Any(p => p.cmd == 84 || p.cmd == 85 || p.cmd == 3000);

                // HOME (seq=0, cmd=16, frame=0)
                var homeItem = parsedItems.FirstOrDefault(p => p.seq == 0);
                if (homeItem.lat != 0 || homeItem.lon != 0)
                {
                    _homePosition = new WaypointItem
                    {
                        Number = 0,
                        Latitude = homeItem.lat,
                        Longitude = homeItem.lon,
                        Altitude = homeItem.alt,
                        CommandType = "HOME",
                        Radius = 20
                    };
                    AddHomeMarkerToMap(_homePosition);
                    PlanMap.Position = new PointLatLng(_homePosition.Latitude, _homePosition.Longitude);
                }

                if (isVtolMission)
                {
                    // VTOL миссия: пропускаем HOME(0), TAKEOFF(84), TRANSITION_FW(3000), ...WPs..., TRANSITION_MC(3000), VTOL_LAND(85)
                    // Ищем TAKEOFF для высоты
                    var takeoff = parsedItems.FirstOrDefault(p => p.cmd == 84);
                    if (takeoff.alt > 0) _vtolTakeoffAltitude = takeoff.alt;

                    // Навигационные точки: WAYPOINT(16), LOITER_UNLIM(17), LOITER_TURNS(18), LOITER_TIME(19)
                    var navItems = parsedItems.Where(p =>
                        p.seq > 0 && // не HOME
                        (p.cmd == 16 || p.cmd == 17 || p.cmd == 18 || p.cmd == 19) &&
                        (p.lat != 0 || p.lon != 0) // имеют координаты
                    ).ToList();

                    // Первый nav = StartCircle, последний nav = LandingCircle, остальные = WPs
                    if (navItems.Count >= 3)
                    {
                        // StartCircle
                        var sItem = navItems.First();
                        _startCircle = new WaypointItem
                        {
                            Number = 0,
                            Latitude = sItem.lat,
                            Longitude = sItem.lon,
                            Altitude = sItem.alt,
                            Radius = Math.Max(150, Math.Abs(sItem.p3) > 0 ? Math.Abs(sItem.p3) : 150),
                            Clockwise = sItem.p3 >= 0,
                            CommandType = sItem.cmd == 18 ? "LOITER_TURNS" : "LOITER_UNLIM",
                            AutoNext = sItem.autoCont == 1,
                            LoiterTurns = (int)sItem.p1
                        };

                        // LandingCircle
                        var lItem = navItems.Last();
                        _landingCircle = new WaypointItem
                        {
                            Number = 0,
                            Latitude = lItem.lat,
                            Longitude = lItem.lon,
                            Altitude = lItem.alt,
                            Radius = Math.Max(150, Math.Abs(lItem.p3) > 0 ? Math.Abs(lItem.p3) : 150),
                            Clockwise = lItem.p3 >= 0,
                            CommandType = lItem.cmd == 18 ? "LOITER_TURNS" : "LOITER_UNLIM",
                            AutoNext = lItem.autoCont == 1,
                            LoiterTurns = (int)lItem.p1
                        };

                        // Waypoints (всё между S и L)
                        for (int w = 1; w < navItems.Count - 1; w++)
                        {
                            var nav = navItems[w];
                            var wp = new WaypointItem
                            {
                                Number = _waypoints.Count + 1,
                                Latitude = nav.lat,
                                Longitude = nav.lon,
                                Altitude = nav.alt,
                                Radius = Math.Abs(nav.p3) > 0 ? Math.Abs(nav.p3) : 80,
                                Clockwise = nav.p3 >= 0,
                                CommandType = ConvertMAVCmdToCommandType(nav.cmd),
                                AutoNext = nav.autoCont == 1,
                                Delay = (nav.cmd == 16 || nav.cmd == 93) ? nav.p1 : 0,
                                LoiterTurns = (nav.cmd == 18) ? (int)nav.p1 : 0
                            };
                            _waypoints.Add(wp);
                            AddMarkerToMap(wp);
                        }

                        // S/L маркеры добавятся автоматически через UpdateRoute()
                    }
                    else
                    {
                        // Мало точек — все как обычные WP
                        foreach (var nav in navItems)
                        {
                            var wp = new WaypointItem
                            {
                                Number = _waypoints.Count + 1,
                                Latitude = nav.lat, Longitude = nav.lon, Altitude = nav.alt,
                                Radius = Math.Abs(nav.p3) > 0 ? Math.Abs(nav.p3) : 80,
                                Clockwise = nav.p3 >= 0,
                                CommandType = ConvertMAVCmdToCommandType(nav.cmd),
                                AutoNext = nav.autoCont == 1
                            };
                            _waypoints.Add(wp);
                            AddMarkerToMap(wp);
                        }
                    }
                }
                else
                {
                    // Copter миссия: пропускаем HOME(0), TAKEOFF(22), ...WPs..., RTL(20)
                    var takeoff = parsedItems.FirstOrDefault(p => p.cmd == 22);
                    if (takeoff.alt > 0) _takeoffAltitude = takeoff.alt;

                    var navItems = parsedItems.Where(p =>
                        p.seq > 0 &&
                        p.cmd != 22 && p.cmd != 20 && // не TAKEOFF, не RTL
                        (p.cmd == 16 || p.cmd == 17 || p.cmd == 18 || p.cmd == 19 || p.cmd == 82) &&
                        (p.lat != 0 || p.lon != 0)
                    ).ToList();

                    foreach (var nav in navItems)
                    {
                        var wp = new WaypointItem
                        {
                            Number = _waypoints.Count + 1,
                            Latitude = nav.lat,
                            Longitude = nav.lon,
                            Altitude = nav.alt,
                            Radius = Math.Abs(nav.p3) > 0 ? Math.Abs(nav.p3) : 80,
                            Clockwise = nav.p3 >= 0,
                            CommandType = ConvertMAVCmdToCommandType(nav.cmd),
                            AutoNext = nav.autoCont == 1,
                            Delay = (nav.cmd == 16 || nav.cmd == 93) ? nav.p1 : 0,
                            LoiterTurns = (nav.cmd == 18) ? (int)nav.p1 : 0
                        };
                        _waypoints.Add(wp);
                        AddMarkerToMap(wp);
                    }
                }

                // Обновляем UI
                UpdateRoute();
                UpdateWaypointsList();
                UpdateStatistics();

                string typeStr = isVtolMission ? "VTOL" : "Copter";
                AppMessageBox.ShowSuccess(
                    $"Загружено: {_waypoints.Count} точек ({typeStr}).\n📁 {System.IO.Path.GetFileName(dlg.FileName)}",
                    owner: OwnerWindow,
                    subtitle: "Миссия загружена"
                );

                System.Diagnostics.Debug.WriteLine($"[LOAD] Миссия загружена из {dlg.FileName}: {_waypoints.Count} WPs, VTOL={isVtolMission}");
            }
            catch (Exception ex)
            {
                AppMessageBox.ShowError($"Ошибка загрузки: {ex.Message}", owner: OwnerWindow, subtitle: "Ошибка");
                System.Diagnostics.Debug.WriteLine($"[LOAD] Ошибка: {ex.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Конвертация MAV_CMD числа обратно в строковый CommandType
        /// </summary>
        private string ConvertMAVCmdToCommandType(ushort cmd)
        {
            switch (cmd)
            {
                case 16: return "WAYPOINT";
                case 17: return "LOITER_UNLIM";
                case 18: return "LOITER_TURNS";
                case 19: return "LOITER_TIME";
                case 20: return "RETURN_TO_LAUNCH";
                case 21: return "LAND";
                case 22: return "TAKEOFF";
                case 82: return "SPLINE_WP";
                case 84: return "VTOL_TAKEOFF";
                case 85: return "VTOL_LAND";
                case 93: return "DELAY";
                case 178: return "CHANGE_SPEED";
                case 3000: return "VTOL_TRANSITION_FW";
                default: return "WAYPOINT";
            }
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
                // Очищаем ВСЕ маркеры с карты
                PlanMap.Markers.Clear();

                // Очищаем коллекцию waypoints
                _waypoints.Clear();

                // Сбрасываем VTOL-объекты
                _startCircle = null;
                _landingCircle = null;

                // Сбрасываем HOME
                _homePosition = null;

                // Очищаем ручки радиуса
                _resizeHandles.Clear();

                // Сбрасываем флаг заморозки
                _isMissionFrozen = false;

                // Обновляем UI
                UpdateWaypointsList();
                UpdateStatistics();

                System.Diagnostics.Debug.WriteLine("Все waypoints, HOME, S/L удалены");
            }
        }

        /// <summary>
        /// Сохранение миссии в файл (когда MAVLink недоступен)
        /// </summary>
        private void SaveMissionToFile(string filename)
        {
            string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            string fullPath = System.IO.Path.Combine(desktopPath, filename);
            SaveMissionToPath(fullPath);
        }

        /// <summary>
        /// Сохранить миссию по указанному полному пути (QGC WPL 110)
        /// </summary>
        private void SaveMissionToPath(string fullPath)
        {
            System.Diagnostics.Debug.WriteLine($" Сохранение миссии в: {fullPath}");

            var lines = new List<string>();
            lines.Add("QGC WPL 110");

            // Используем GetFullMission() для полной структуры (включая VTOL)
            var fullMission = GetFullMission();

            for (int i = 0; i < fullMission.Count; i++)
            {
                var wp = fullMission[i];
                ushort mavCmd = ConvertCommandTypeToMAVCmd(wp.CommandType);
                var (p1, p2, p3, p4) = GetCommandParams(wp);
                int autoContinue = wp.AutoNext ? 1 : 0;
                int current = (i == 0) ? 1 : 0;
                int frame = (wp.CommandType == "HOME") ? 0 : 3;

                System.Diagnostics.Debug.WriteLine($"  seq={i}: {wp.CommandType} (MAV_CMD={mavCmd}) p1={p1} at {wp.Latitude:F7}, {wp.Longitude:F7}, alt={wp.Altitude:F2}");

                lines.Add($"{i}\t{current}\t{frame}\t{mavCmd}\t{p1}\t{p2}\t{p3}\t{p4}\t{wp.Latitude:F7}\t{wp.Longitude:F7}\t{wp.Altitude:F2}\t{autoContinue}");
            }

            System.IO.File.WriteAllLines(fullPath, lines);

            System.Diagnostics.Debug.WriteLine($" Миссия сохранена в {fullPath}");
            System.Diagnostics.Debug.WriteLine($"   Всего строк: {lines.Count}");
        }

        /// <summary>
        /// Получить параметры команды (p1, p2, p3, p4)
        /// </summary>
        private (double p1, double p2, double p3, double p4) GetCommandParams(WaypointItem wp)
        {
            // ArduPilot: положительный radius = CW, отрицательный = CCW
            double signedRadius = wp.Clockwise ? Math.Abs(wp.Radius) : -Math.Abs(wp.Radius);

            switch (wp.CommandType)
            {
                case "VTOL_TRANSITION_FW":
                    return (4, 0, 0, 0);  // param1=4 = MAV_VTOL_STATE_FW (переход в самолёт)
                case "VTOL_TRANSITION_MC":
                    return (3, 0, 0, 0);  // param1=3 = MAV_VTOL_STATE_MC (переход в коптер)
                case "LOITER_TIME":
                    return (wp.Delay, 0, signedRadius, 0);  // p1=время(сек), p3=радиус(знак=направление)
                case "LOITER_TURNS":
                    return (wp.LoiterTurns, 0, signedRadius, 0);  // p1=кругов, p3=радиус(знак=направление)
                case "LOITER_UNLIM":
                    return (0, 0, signedRadius, 0);  // p3=радиус(знак=направление)
                case "WAYPOINT":
                    return (wp.Delay, 0, 0, 0);  // p1=hold time
                case "DELAY":
                    return (wp.Delay, 0, 0, 0);
                default:
                    return (wp.Delay, 0, 0, 0);
            }
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
                case "LOITER_TURNS": result = 18; break;
                case "LOITER_TIME": result = 19; break;
                case "RETURN_TO_LAUNCH": result = 20; break;
                case "LAND": result = 21; break;
                case "TAKEOFF": result = 22; break;
                case "SPLINE_WP": result = 82; break;
                case "VTOL_TAKEOFF": result = 84; break;
                case "VTOL_LAND": result = 85; break;
                case "DELAY": result = 93; break;
                case "CHANGE_SPEED": result = 178; break;
                case "SET_HOME": result = 179; break;
                case "VTOL_TRANSITION_FW": result = 3000; break;
                case "VTOL_TRANSITION_MC": result = 3000; break;
                case "HOME": result = 16; break; // HOME = NAV_WAYPOINT (seq=0)
                case "START_CIRCLE": result = 17; break; // Как LOITER_UNLIM
                case "LANDING_CIRCLE": result = 17; break; // Как LOITER_UNLIM
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
                _droneMarker.Shape = null;
                PlanMap.Markers.Remove(_droneMarker);
                _droneMarker = null;
            }
            UpdateDroneInfoPanel(telemetry);
        }

        /// <summary>
        /// Обновление панели информации о дроне и HOME
        /// </summary>
        /// <summary>
        /// Обновление панели информации о дроне и HOME
        /// </summary>
        private void UpdateDroneInfoPanel(Telemetry telemetry)
        {
            // Координаты дрона
            if (telemetry.Latitude != 0 || telemetry.Longitude != 0)
            {
                PlanDroneLatText.Text = telemetry.Latitude.ToString("F6");
                PlanDroneLonText.Text = telemetry.Longitude.ToString("F6");
            }

            // Heading + компас
            PlanHeadingRotation.Angle = telemetry.Heading;
            PlanHeadingText.Text = $"{telemetry.Heading:F0}°";

            // HOME: приоритет — реальный от дрона, иначе — из плана
            if (_mavlinkService.HasHomePosition)
            {
                // Реальный HOME после армирования
                PlanHomeLatText.Text = _mavlinkService.HomeLat.Value.ToString("F6");
                PlanHomeLonText.Text = _mavlinkService.HomeLon.Value.ToString("F6");
            }
            else if (_homePosition != null)
            {
                // Кастомный HOME установленный на карте
                PlanHomeLatText.Text = _homePosition.Latitude.ToString("F6");
                PlanHomeLonText.Text = _homePosition.Longitude.ToString("F6");
            }
            else
            {
                // Пробуем из MissionStore
                var home = MissionStore.GetHome((int)_currentVehicleType);
                if (home != null)
                {
                    PlanHomeLatText.Text = home.Latitude.ToString("F6");
                    PlanHomeLonText.Text = home.Longitude.ToString("F6");
                }
                else
                {
                    PlanHomeLatText.Text = "---.------";
                    PlanHomeLonText.Text = "---.------";
                }
            }
        }

        /// <summary>
        /// Установка HOME из позиции дрона при армировании
        /// </summary>
        private void SetHomeFromDronePosition(double lat, double lon)
        {
            // Удаляем старый HOME маркер
            if (_homePosition?.Marker != null)
            {
                _homePosition.Marker.Shape = null;
                PlanMap.Markers.Remove(_homePosition.Marker);
            }

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
        /// Собрать полную миссию: TAKEOFF + waypoints + LAND/RTL
        /// </summary>
        public List<WaypointItem> GetFullMission()
        {
            var mission = new List<WaypointItem>();
            bool isVTOL = _currentVehicleType == VehicleType.QuadPlane;

            // 1. HOME (seq=0)
            if (_homePosition != null)
            {
                mission.Add(new WaypointItem
                {
                    Number = 0,
                    Latitude = _homePosition.Latitude,
                    Longitude = _homePosition.Longitude,
                    Altitude = _homePosition.Altitude,
                    CommandType = "HOME"
                });
            }

            if (isVTOL)
            {
                // === VTOL МИССИЯ ===
                // seq=1: VTOL_TAKEOFF
                mission.Add(new WaypointItem
                {
                    Number = mission.Count,
                    Latitude = _homePosition?.Latitude ?? 0,
                    Longitude = _homePosition?.Longitude ?? 0,
                    Altitude = _vtolTakeoffAltitude,
                    CommandType = "VTOL_TAKEOFF"
                });

                // seq=2: DO_VTOL_TRANSITION → самолёт
                mission.Add(new WaypointItem
                {
                    Number = mission.Count,
                    Latitude = 0, Longitude = 0, Altitude = 0,
                    CommandType = "VTOL_TRANSITION_FW"
                });

                // seq=3: StartCircle (LOITER — набор высоты и переход)
                if (_startCircle != null)
                {
                    double signedR = _startCircle.Clockwise ? Math.Abs(_startCircle.Radius) : -Math.Abs(_startCircle.Radius);
                    mission.Add(new WaypointItem
                    {
                        Number = mission.Count,
                        Latitude = _startCircle.Latitude,
                        Longitude = _startCircle.Longitude,
                        Altitude = _startCircle.Altitude,
                        CommandType = _startCircle.AutoNext ? "LOITER_TURNS" : "LOITER_UNLIM",
                        Radius = Math.Abs(_startCircle.Radius),
                        Clockwise = _startCircle.Clockwise,
                        LoiterTurns = _startCircle.LoiterTurns,
                        AutoNext = _startCircle.AutoNext
                    });
                }

                // seq=4..N+3: Waypoints
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
                        Radius = wp.Radius,
                        Clockwise = wp.Clockwise,
                        AutoNext = wp.AutoNext,
                        LoiterTurns = wp.LoiterTurns
                    });
                }

                // LandingCircle (LOITER — ожидание перед посадкой)
                if (_landingCircle != null)
                {
                    mission.Add(new WaypointItem
                    {
                        Number = mission.Count,
                        Latitude = _landingCircle.Latitude,
                        Longitude = _landingCircle.Longitude,
                        Altitude = _landingCircle.Altitude,
                        CommandType = _landingCircle.AutoNext ? "LOITER_TURNS" : "LOITER_UNLIM",
                        Radius = Math.Abs(_landingCircle.Radius),
                        Clockwise = _landingCircle.Clockwise,
                        LoiterTurns = _landingCircle.LoiterTurns,
                        AutoNext = _landingCircle.AutoNext
                    });
                }

                // DO_VTOL_TRANSITION → коптер
                mission.Add(new WaypointItem
                {
                    Number = mission.Count,
                    Latitude = 0, Longitude = 0,
                    Altitude = _vtolLandAltitude > 0 ? _vtolLandAltitude : 30,
                    CommandType = "VTOL_TRANSITION_MC"
                });

                // VTOL_LAND
                if (_homePosition != null)
                {
                    mission.Add(new WaypointItem
                    {
                        Number = mission.Count,
                        Latitude = _homePosition.Latitude,
                        Longitude = _homePosition.Longitude,
                        Altitude = 0,
                        CommandType = "VTOL_LAND"
                    });
                }
            }
            else
            {
                // === ОБЫЧНАЯ МИССИЯ (Copter/Plane) ===
                // TAKEOFF
                if (_homePosition != null)
                {
                    mission.Add(new WaypointItem
                    {
                        Number = mission.Count,
                        Latitude = _homePosition.Latitude,
                        Longitude = _homePosition.Longitude,
                        Altitude = _takeoffAltitude,
                        CommandType = "TAKEOFF"
                    });
                }

                // Все waypoints
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
                        Radius = wp.Radius,
                        Clockwise = wp.Clockwise,
                        AutoNext = wp.AutoNext,
                        LoiterTurns = wp.LoiterTurns
                    });
                }

                // RTL
                mission.Add(new WaypointItem
                {
                    Number = mission.Count,
                    Latitude = 0,
                    Longitude = 0,
                    Altitude = _rtlAltitude,
                    CommandType = "RETURN_TO_LAUNCH"
                });
            }

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
                    Radius = wp.Radius,
                    Clockwise = wp.Clockwise,
                    AutoNext = wp.AutoNext,
                    LoiterTurns = wp.LoiterTurns
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

            // Сохраняем VTOL S/L
            if (_startCircle != null)
            {
                _startByType[_currentVehicleType] = new WaypointItem
                {
                    Number = 0,
                    Latitude = _startCircle.Latitude,
                    Longitude = _startCircle.Longitude,
                    Altitude = _startCircle.Altitude,
                    CommandType = _startCircle.CommandType,
                    Radius = _startCircle.Radius,
                    Clockwise = _startCircle.Clockwise,
                    AutoNext = _startCircle.AutoNext,
                    LoiterTurns = _startCircle.LoiterTurns
                };
            }
            else { _startByType.Remove(_currentVehicleType); }

            if (_landingCircle != null)
            {
                _landingByType[_currentVehicleType] = new WaypointItem
                {
                    Number = -1,
                    Latitude = _landingCircle.Latitude,
                    Longitude = _landingCircle.Longitude,
                    Altitude = _landingCircle.Altitude,
                    CommandType = _landingCircle.CommandType,
                    Radius = _landingCircle.Radius,
                    Clockwise = _landingCircle.Clockwise,
                    AutoNext = _landingCircle.AutoNext,
                    LoiterTurns = _landingCircle.LoiterTurns
                };
            }
            else { _landingByType.Remove(_currentVehicleType); }
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
                    wp.Marker.Shape = null;
                    PlanMap.Markers.Remove(wp.Marker);
                    wp.Marker = null;
                }
            }

            // 2. Удаляем ВСЕ ручки радиуса
            foreach (var handle in _resizeHandles.Values)
            {
                handle.Shape = null;
                PlanMap.Markers.Remove(handle);
            }
            _resizeHandles.Clear();

            // 3. Удаляем HOME маркер
            if (_homePosition?.Marker != null)
            {
                _homePosition.Marker.Shape = null;
                PlanMap.Markers.Remove(_homePosition.Marker);
                _homePosition.Marker = null;
            }

            // 4. Удаляем ВСЕ маршруты (линии)
            var oldRoutes = PlanMap.Markers.OfType<GMapRoute>().ToList();
            foreach (var r in oldRoutes)
            {
                r.Shape = null;
                PlanMap.Markers.Remove(r);
            }

            // 5. Очищаем коллекции БЕЗ триггера CollectionChanged
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

            // 5b. Удаляем VTOL маркеры (S, L) и сбрасываем объекты
            var vtolMarkers = PlanMap.Markers
                .Where(m => m.Tag?.ToString()?.StartsWith("vtol_") == true)
                .ToList();
            foreach (var m in vtolMarkers) { m.Shape = null; PlanMap.Markers.Remove(m); }
            _startCircle = null;
            _landingCircle = null;

            System.Diagnostics.Debug.WriteLine($"[LoadMission] Карта очищена. Загружаем тип {type}...");

            // 6. Загружаем HOME для нового типа
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

            // 7. Загружаем waypoints для нового типа
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
                        Radius = wp.Radius > 0 ? wp.Radius : _waypointRadius,
                        Clockwise = wp.Clockwise,
                        AutoNext = wp.AutoNext,
                        LoiterTurns = wp.LoiterTurns
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

            // 8. Восстанавливаем VTOL S/L для нового типа
            if (_startByType.TryGetValue(type, out var savedStart) && savedStart != null)
            {
                _startCircle = new WaypointItem
                {
                    Number = 0,
                    Latitude = savedStart.Latitude,
                    Longitude = savedStart.Longitude,
                    Altitude = savedStart.Altitude,
                    CommandType = savedStart.CommandType,
                    Radius = Math.Max(150, savedStart.Radius),
                    Clockwise = savedStart.Clockwise,
                    AutoNext = savedStart.AutoNext,
                    LoiterTurns = savedStart.LoiterTurns
                };
            }
            if (_landingByType.TryGetValue(type, out var savedLanding) && savedLanding != null)
            {
                _landingCircle = new WaypointItem
                {
                    Number = -1,
                    Latitude = savedLanding.Latitude,
                    Longitude = savedLanding.Longitude,
                    Altitude = savedLanding.Altitude,
                    CommandType = savedLanding.CommandType,
                    Radius = Math.Max(150, savedLanding.Radius),
                    Clockwise = savedLanding.Clockwise,
                    AutoNext = savedLanding.AutoNext,
                    LoiterTurns = savedLanding.LoiterTurns
                };
            }

            // 9. Обновляем UI
            UpdateRoute(); // Полный UpdateRoute для отрисовки S/L при VTOL
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

                // Обновляем надписи TAKEOFF/RTL для VTOL
                if (profile.Type == VehicleType.QuadPlane)
                {
                    if (TakeoffLabel != null) TakeoffLabel.Text = "VTOL ВЗЛЁТ";
                    if (RtlLabel != null) RtlLabel.Text = "VTOL ПОСАДКА";
                }
                else
                {
                    if (TakeoffLabel != null) TakeoffLabel.Text = "ВЗЛЁТ";
                    if (RtlLabel != null) RtlLabel.Text = "ВОЗВРАТ (RTL)";
                }

                // Обновляем быстрые кнопки режимов полёта
                UpdateQuickModeButtons();

                // Обновляем список команд
                UpdateWaypointsList();
            }
            catch
            {
                if (VehicleTypeShortText != null) VehicleTypeShortText.Text = "MC";
                if (VehicleTypeFullText != null) VehicleTypeFullText.Text = "Мультикоптер";
            }
        }

        /// <summary>
        /// Обновить быстрые кнопки режимов полёта при смене типа ЛА
        /// </summary>
        private void UpdateQuickModeButtons()
        {
            if (_currentVehicleType == VehicleType.QuadPlane)
            {
                // VTOL: Q-режимы
                if (QuickModeBtn1 != null) { QuickModeBtn1.Content = "Q-Удерж"; QuickModeBtn1.Tag = "QLOITER"; }
                if (QuickModeBtn2 != null) { QuickModeBtn2.Content = "Q-Высота"; QuickModeBtn2.Tag = "QHOVER"; }
                if (QuickModeBtn3 != null) { QuickModeBtn3.Content = "Выполнить"; QuickModeBtn3.Tag = "AUTO"; }
                if (QuickModeBtn4 != null) { QuickModeBtn4.Content = "Q-Стаб"; QuickModeBtn4.Tag = "QSTABILIZE"; }
                if (QuickModeBtn5 != null) { QuickModeBtn5.Content = "Домой"; QuickModeBtn5.Tag = "QRTL"; }
            }
            else
            {
                // Copter: обычные режимы
                if (QuickModeBtn1 != null) { QuickModeBtn1.Content = "Удержание"; QuickModeBtn1.Tag = "LOITER"; }
                if (QuickModeBtn2 != null) { QuickModeBtn2.Content = "Высота"; QuickModeBtn2.Tag = "ALT_HOLD"; }
                if (QuickModeBtn3 != null) { QuickModeBtn3.Content = "Выполнить"; QuickModeBtn3.Tag = "AUTO"; }
                if (QuickModeBtn4 != null) { QuickModeBtn4.Content = "Стаб"; QuickModeBtn4.Tag = "STABILIZE"; }
                if (QuickModeBtn5 != null) { QuickModeBtn5.Content = "Домой"; QuickModeBtn5.Tag = "RTL"; }
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

        /// <summary>
        /// Обновление визуала радиуса точки
        /// </summary>
        private void UpdateWaypointRadiusVisual(WaypointItem wp)
        {
            if (wp.RadiusCircle == null || wp.ShapeGrid == null) return;

            double radiusInPixels = MetersToPixels(wp.Radius, wp.Latitude, PlanMap.Zoom);
            radiusInPixels = Math.Max(20, Math.Min(5000, radiusInPixels));

            double diameter = radiusInPixels * 2;
            double gridSize = Math.Max(60, diameter);

            wp.RadiusCircle.Width = diameter;
            wp.RadiusCircle.Height = diameter;
            wp.ShapeGrid.Width = gridSize;
            wp.ShapeGrid.Height = gridSize;

            // Пунктир
            wp.RadiusCircle.StrokeDashArray = new DoubleCollection { 4, 2 };

            double minRadius = GetMinRadius();
            if (wp.Radius < minRadius)
            {
                wp.RadiusCircle.Stroke = Brushes.Red;
                wp.RadiusCircle.Fill = new SolidColorBrush(Color.FromArgb(40, 255, 0, 0));
            }
            else
            {
                wp.RadiusCircle.Stroke = new SolidColorBrush(Color.FromArgb(200, 152, 240, 25));
                wp.RadiusCircle.Fill = new SolidColorBrush(Color.FromArgb(40, 152, 240, 25));
            }

            if (wp.Marker != null)
            {
                wp.Marker.Offset = new Point(-gridSize / 2, -gridSize / 2);
            }

            if (_resizeHandles.ContainsKey(wp))
            {
                var handlePos = CalculatePointAtDistance(
                    wp.Latitude, wp.Longitude, 90, wp.Radius / 1000.0);
                _resizeHandles[wp].Position = new PointLatLng(handlePos.Lat, handlePos.Lng);
            }
        }

        #region CURSOR DISTANCE

        /// <summary>
        /// Обработчик изменения зума карты — обновляем размеры всех кругов в реальном времени
        /// </summary>
        private void PlanMap_OnMapZoomChanged()
        {
            // Обновляем обычные WP
            foreach (var wp in _waypoints)
            {
                if (wp.ShapeGrid != null && wp.RadiusCircle != null)
                {
                    double radiusInPixels = MetersToPixels(wp.Radius, wp.Latitude, PlanMap.Zoom);
                    radiusInPixels = Math.Max(3, Math.Min(5000, radiusInPixels));
                    double diameter = radiusInPixels * 2;
                    double gridSize = Math.Max(60, diameter);

                    wp.ShapeGrid.Width = gridSize;
                    wp.ShapeGrid.Height = gridSize;
                    wp.RadiusCircle.Width = diameter;
                    wp.RadiusCircle.Height = diameter;

                    if (wp.Marker != null)
                        wp.Marker.Offset = new Point(-gridSize / 2, -gridSize / 2);
                }
            }

            // Обновляем S/L круги (VTOL)
            if (_currentVehicleType == VehicleType.QuadPlane)
            {
                UpdateSpecialCircleSize(_startCircle, "S");
                UpdateSpecialCircleSize(_landingCircle, "L");
            }
        }

        private void PlanMap_MouseMove(object sender, MouseEventArgs e)
        {
            // === DRAG РАДИУСА ===
            if (_isRadiusDragging && _radiusDragWaypoint != null)
            {
                var dragPoint = e.GetPosition(PlanMap);
                var dragLatLng = PlanMap.FromLocalToLatLng((int)dragPoint.X, (int)dragPoint.Y);

                // Расстояние от центра точки до курсора = новый радиус
                double newRadius = CalculateDistanceLatLng(
                    _radiusDragWaypoint.Latitude, _radiusDragWaypoint.Longitude,
                    dragLatLng.Lat, dragLatLng.Lng);

                // Ограничиваем радиус
                double minRadius = GetMinRadius();
                newRadius = Math.Max(minRadius, Math.Min(500, newRadius));

                // Обновляем waypoint
                _radiusDragWaypoint.Radius = newRadius;

                // Обновляем визуал круга
                UpdateWaypointRadiusVisual(_radiusDragWaypoint);

                // Обновляем tooltip
                UpdateRadiusTooltip(dragLatLng, newRadius);

                return; // Не обрабатываем остальное
            }

            var point = e.GetPosition(PlanMap);
            var cursorLatLng = PlanMap.FromLocalToLatLng((int)point.X, (int)point.Y);

            // === ОТЛАДКА ===
            System.Diagnostics.Debug.WriteLine($"[MouseMove] Lat={cursorLatLng.Lat:F4}, Lng={cursorLatLng.Lng:F4}");

            // === КООРДИНАТЫ КУРСОРА ===
            if (CursorLatText != null)
                CursorLatText.Text = cursorLatLng.Lat.ToString("F6");

            if (CursorLngText != null)
                CursorLngText.Text = cursorLatLng.Lng.ToString("F6");

            // Высота из SRTM
            if (CursorAltText != null)
            {
                double? elevation = _elevationProvider.GetElevation(cursorLatLng.Lat, cursorLatLng.Lng);
                CursorAltText.Text = elevation.HasValue ? $"{elevation.Value:F0} м" : "— м";
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

        #region ПЛАВАЮЩИЕ КНОПКИ УПРАВЛЕНИЯ МИССИЕЙ

        /// <summary>
        /// Loiter - перейти в режим кружения на месте
        /// </summary>
        private void LoiterBtn_Click(object sender, MouseButtonEventArgs e)
        {
            if (_mavlinkService?.IsConnected != true)
            {
                ShowNotConnectedMessage();
                return;
            }
            
            // Для VTOL: QLOITER, для Copter: LOITER
            var mode = VehicleManager.Instance.CurrentVehicleType == Models.VehicleType.QuadPlane 
                ? "QLOITER" : "LOITER";
            _mavlinkService.SetFlightMode(mode);
        }

        /// <summary>
        /// Resume - продолжить миссию
        /// </summary>
        private void ResumeBtn_Click(object sender, MouseButtonEventArgs e)
        {
            if (_mavlinkService?.IsConnected != true)
            {
                ShowNotConnectedMessage();
                return;
            }
            
            _mavlinkService.SetFlightMode("AUTO");
        }

        /// <summary>
        /// Start - запустить миссию с начала
        /// </summary>
        private void StartBtn_Click(object sender, MouseButtonEventArgs e)
        {
            if (_mavlinkService?.IsConnected != true)
            {
                ShowNotConnectedMessage();
                return;
            }
            
            _mavlinkService.SetCurrentWaypoint(0);
            _mavlinkService.SetFlightMode("AUTO");
            _mavlinkService.StartMission();
        }

        /// <summary>
        /// RTL - возврат домой
        /// </summary>
        private void RtlBtn_Click(object sender, MouseButtonEventArgs e)
        {
            if (_mavlinkService?.IsConnected != true)
            {
                ShowNotConnectedMessage();
                return;
            }
            
            // Для VTOL: QRTL
            var mode = VehicleManager.Instance.CurrentVehicleType == Models.VehicleType.QuadPlane 
                ? "QRTL" : "RTL";
            _mavlinkService.SetFlightMode(mode);
        }

        private void ShowNotConnectedMessage()
        {
            MessageBox.Show("Дрон не подключён", "KYRAN GCS", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        #endregion

        #region ТЕЛЕМЕТРИЯ

        /// <summary>
        /// Обработчик получения телеметрии
        /// </summary>
        private void OnTelemetryReceived(object sender, Telemetry telemetry)
        {
            // Телеметрия приходит в фоновом потоке
            // UI обновляется через таймер
        }

        /// <summary>
        /// Обновление UI телеметрии
        /// </summary>
        private void UpdateTelemetryUI(object sender, EventArgs e)
        {
            if (_mavlinkService == null) return;

            var telemetry = _mavlinkService.CurrentTelemetry;

            // Высота и скорость
            if (AltitudeValue != null)
                AltitudeValue.Text = $"{telemetry.Altitude:F1} м";
            if (AltitudeMslValue != null)
                AltitudeMslValue.Text = $"{telemetry.Altitude:F1} м";

            // Универсальный показатель: Airspeed для VTOL, ClimbRate для коптера
            if (SecondarySpeedValue != null && SecondarySpeedLabel != null)
            {
                if (_currentVehicleType == VehicleType.QuadPlane)
                {
                    SecondarySpeedLabel.Text = "Возд. ск.";
                    SecondarySpeedValue.Text = $"{telemetry.Airspeed:F1} м/с";
                    SecondarySpeedValue.Foreground = new SolidColorBrush(Color.FromRgb(34, 211, 238)); // #22D3EE голубой
                }
                else
                {
                    SecondarySpeedLabel.Text = "Верт. ск.";
                    string sign = telemetry.ClimbRate >= 0 ? "+" : "";
                    SecondarySpeedValue.Text = $"{sign}{telemetry.ClimbRate:F1} м/с";
                    SecondarySpeedValue.Foreground = new SolidColorBrush(
                        telemetry.ClimbRate > 0.5 ? Color.FromRgb(74, 222, 128) :    // зелёный ↑
                        telemetry.ClimbRate < -0.5 ? Color.FromRgb(251, 146, 60) :   // оранжевый ↓
                        Color.FromRgb(156, 163, 175));                                // серый (висит)
                }
            }

            if (SpeedValue != null)
                SpeedValue.Text = $"{telemetry.GroundSpeed:F1} м/с";

            // Attitude Indicator
            if (AttitudeIndicator != null)
            {
                AttitudeIndicator.Roll = telemetry.Roll;
                AttitudeIndicator.Pitch = telemetry.Pitch;
            }

            // GPS статус
            UpdateGpsStatus(telemetry);

            // Батарея
            if (BatteryVoltage != null)
                BatteryVoltage.Text = $"{telemetry.BatteryVoltage:F1}V";
            if (BatteryPercent != null)
                BatteryPercent.Text = $"{telemetry.BatteryPercent}%";

            // Спутники
            if (SatellitesValue != null)
                SatellitesValue.Text = telemetry.SatellitesVisible.ToString();

            // Режим полёта
            if (FlightModeValue != null)
                FlightModeValue.Text = telemetry.FlightMode ?? "UNKNOWN";

            // Моторы
            UpdateMotorValues(telemetry);

            // Кнопка ARM/DISARM
            UpdateArmButton(telemetry);

            // Показываем кнопку "Следующая точка" когда дрон Armed и в AUTO/LOITER
            UpdateNextWaypointButtonVisibility(telemetry);
        }

        private void UpdateGpsStatus(Telemetry telemetry)
        {
            if (GpsIndicator == null || GpsStatusText == null) return;

            if (telemetry.GpsFixType >= 3)
            {
                GpsIndicator.Fill = new SolidColorBrush(Color.FromRgb(34, 197, 94)); // Зелёный
                GpsStatusText.Text = "GPS OK";
                GpsStatusText.Foreground = new SolidColorBrush(Color.FromRgb(34, 197, 94));
            }
            else if (telemetry.GpsFixType >= 2)
            {
                GpsIndicator.Fill = new SolidColorBrush(Color.FromRgb(251, 191, 36)); // Жёлтый
                GpsStatusText.Text = "GPS 2D";
                GpsStatusText.Foreground = new SolidColorBrush(Color.FromRgb(251, 191, 36));
            }
            else
            {
                GpsIndicator.Fill = new SolidColorBrush(Color.FromRgb(239, 68, 68)); // Красный
                GpsStatusText.Text = "NO GPS";
                GpsStatusText.Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68));
            }
        }

        private void UpdateMotorValues(Telemetry telemetry)
        {
            // Мультиротор (среднее от 4 моторов)
            if (MultirotorValue != null)
            {
                int avgMotor = (telemetry.Motor1Percent + telemetry.Motor2Percent + telemetry.Motor3Percent + telemetry.Motor4Percent) / 4;
                MultirotorValue.Text = $"{avgMotor}%";
            }
            if (PusherMotorValue != null) PusherMotorValue.Text = $"{telemetry.PusherPercent}%";
        }

        private void UpdateArmButton(Telemetry telemetry)
        {
            if (ArmButton == null) return;

            if (telemetry.Armed)
            {
                ArmButton.Content = "ДЕАКТИВИРОВАТЬ";
                ArmButton.Background = new SolidColorBrush(Color.FromRgb(127, 29, 29));
                ArmButton.BorderBrush = new SolidColorBrush(Color.FromRgb(239, 68, 68));
            }
            else
            {
                ArmButton.Content = "АКТИВИРОВАТЬ";
                ArmButton.Background = new SolidColorBrush(Color.FromRgb(22, 101, 52));
                ArmButton.BorderBrush = new SolidColorBrush(Color.FromRgb(34, 197, 94));
            }
        }

        /// <summary>
        /// Показать/скрыть кнопку "Следующая точка" для operator-in-the-loop
        /// </summary>
        private void UpdateNextWaypointButtonVisibility(Telemetry telemetry)
        {
            if (NextWaypointBtn == null) return;

            string mode = telemetry.FlightMode?.ToUpper() ?? "";
            bool show = telemetry.Armed && (mode == "AUTO" || mode == "LOITER" || mode == "GUIDED");

            NextWaypointBtn.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdateConnectionTimer(object sender, EventArgs e)
        {
            if (_mavlinkService == null || ConnectionTimerText == null) return;

            if (_mavlinkService.IsConnected)
            {
                if (!_wasConnected)
                {
                    _connectionStartTime = DateTime.Now;
                    _wasConnected = true;
                }

                var elapsed = DateTime.Now - _connectionStartTime;
                ConnectionTimerText.Text = elapsed.ToString(@"hh\:mm\:ss");
            }
            else
            {
                _wasConnected = false;
                ConnectionTimerText.Text = "00:00:00";
            }
        }

        /// <summary>
        /// Заполнение ComboBox режимов полёта
        /// </summary>
        private void PopulateFlightModes()
        {
            if (FlightModeCombo == null) return;

            FlightModeCombo.Items.Clear();
            FlightModeCombo.Items.Add(new ComboBoxItem { Content = "Режимы полётов", IsSelected = true });

            var modes = _currentVehicleType == VehicleType.QuadPlane
                ? new[] { "QSTABILIZE", "QHOVER", "QLOITER", "QLAND", "QRTL", "AUTO", "GUIDED", "LOITER", "RTL", "FBWA", "CRUISE" }
                : new[] { "STABILIZE", "ALT_HOLD", "LOITER", "AUTO", "GUIDED", "RTL", "LAND", "POSHOLD", "BRAKE" };

            foreach (var mode in modes)
            {
                var item = new ComboBoxItem { Content = mode, Tag = mode };
                FlightModeCombo.Items.Add(item);
            }

            FlightModeCombo.SelectionChanged += FlightModeCombo_SelectionChanged;
        }

        private void FlightModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (FlightModeCombo.SelectedItem is ComboBoxItem item && item.Tag is string mode)
            {
                _mavlinkService?.SetFlightMode(mode);
            }
        }

        /// <summary>
        /// Кнопка ARM/DISARM
        /// </summary>
        private void ArmButton_Click(object sender, RoutedEventArgs e)
        {
            if (_mavlinkService?.IsConnected != true)
            {
                ShowNotConnectedMessage();
                return;
            }

            bool isArmed = _mavlinkService.CurrentTelemetry.Armed;
            _mavlinkService.SetArm(!isArmed, true);
        }

        /// <summary>
        /// Быстрые режимы полёта
        /// </summary>
        private void QuickMode_Click(object sender, RoutedEventArgs e)
        {
            if (_mavlinkService?.IsConnected != true)
            {
                ShowNotConnectedMessage();
                return;
            }

            if (sender is Button btn && btn.Tag is string mode)
            {
                _mavlinkService.SetFlightMode(mode);
            }
        }

        /// <summary>
        /// Калибровка
        /// </summary>
        private void CalibrateButton_Click(object sender, RoutedEventArgs e)
        {
            if (_mavlinkService?.IsConnected != true)
            {
                ShowNotConnectedMessage();
                return;
            }

            if (CalibrationCombo?.SelectedItem is ComboBoxItem item)
            {
                string calibType = item.Content?.ToString() ?? "";
                switch (calibType)
                {
                    case "Акселерометр":
                        _mavlinkService.SendPreflightCalibration(accelerometer: true);
                        break;
                    case "Компас":
                        _mavlinkService.SendPreflightCalibration(compassMot: true);
                        break;
                    case "Гироскоп":
                        _mavlinkService.SendPreflightCalibration(gyro: true);
                        break;
                }
            }
        }

        /// <summary>
        /// Активировать миссию
        /// </summary>
        private async void ActivateMissionButton_Click(object sender, RoutedEventArgs e)
        {
            if (_mavlinkService?.IsConnected != true)
            {
                ShowNotConnectedMessage();
                return;
            }

            if (_waypoints.Count == 0)
            {
                MessageBox.Show("Миссия пуста. Добавьте точки!", "KYRAN GCS", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            bool isVtol = _currentVehicleType == VehicleType.QuadPlane;
            string msg = isVtol
                ? $"Активировать VTOL миссию из {_waypoints.Count} точек?\n\nПоследовательность: ВЗЛЁТ → СТАРТ → WPs → ПОСАДКА → ПРИЗЕМЛЕНИЕ\n\n⚠️ Дрон взлетит автоматически!"
                : $"Активировать миссию из {_waypoints.Count} точек?\n\n⚠️ Дрон взлетит автоматически!";

            var result = MessageBox.Show(msg, "Подтверждение", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes) return;

            try
            {
                bool uploadSuccess;

                if (isVtol)
                {
                    // VTOL миссия с StartCircle + LandingCircle
                    if (_homePosition == null) { MessageBox.Show("Установите HOME!", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error); return; }
                    if (_startCircle == null) InitializeStartCircle();
                    if (_landingCircle == null) InitializeLandingCircle();

                    uploadSuccess = await _mavlinkService.UploadVtolMission(
                        _homePosition, _startCircle, _waypoints.ToList(), _landingCircle,
                        _vtolTakeoffAltitude, _vtolLandAltitude);
                }
                else
                {
                    // Обычная миссия (Copter/Plane)
                    _mavlinkService.SavePlannedMission(_waypoints.ToList());
                    uploadSuccess = await _mavlinkService.UploadPlannedMission();
                }

                if (!uploadSuccess)
                {
                    MessageBox.Show("Ошибка загрузки миссии", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                await System.Threading.Tasks.Task.Delay(500);

                // ARM
                _mavlinkService.SetArm(true, true);
                await System.Threading.Tasks.Task.Delay(1000);

                if (!_mavlinkService.CurrentTelemetry.Armed)
                {
                    MessageBox.Show("Не удалось активировать дрон", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                // AUTO
                _mavlinkService.StartMission();

                MessageBox.Show("Миссия активирована!", "Успех", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #endregion

        

        private string FormatDistance(double meters)
        {
            return $"{meters:F0} м";
        }

        #region VTOL StartCircle / LandingCircle

        /// <summary>
        /// Создать StartCircle — 300м от HOME на северо-восток (45°)
        /// </summary>
        private void InitializeStartCircle()
        {
            double baseLat, baseLon;
            if (_homePosition != null)
            {
                baseLat = _homePosition.Latitude;
                baseLon = _homePosition.Longitude;
            }
            else
            {
                // Без HOME — берём центр карты
                baseLat = PlanMap.Position.Lat;
                baseLon = PlanMap.Position.Lng;
            }

            var pos = CalculatePointAtDistance(baseLat, baseLon, 45, 0.3); // 300м
            _startCircle = new WaypointItem
            {
                Number = 0,
                Latitude = pos.Lat,
                Longitude = pos.Lng,
                Altitude = _vtolTakeoffAltitude,
                Radius = 150,
                CommandType = "START_CIRCLE",
                AutoNext = false, // По умолчанию ждёт оператора
                LoiterTurns = 1,
                Clockwise = true,
                Delay = 0
            };
            System.Diagnostics.Debug.WriteLine($"[VTOL] StartCircle создан: {_startCircle.Latitude:F6}, {_startCircle.Longitude:F6}");
        }

        /// <summary>
        /// Создать LandingCircle — 300м от HOME на юго-запад (225°)
        /// </summary>
        private void InitializeLandingCircle()
        {
            double baseLat, baseLon;
            if (_homePosition != null)
            {
                baseLat = _homePosition.Latitude;
                baseLon = _homePosition.Longitude;
            }
            else
            {
                baseLat = PlanMap.Position.Lat;
                baseLon = PlanMap.Position.Lng;
            }

            var pos = CalculatePointAtDistance(baseLat, baseLon, 225, 0.3); // 300м
            _landingCircle = new WaypointItem
            {
                Number = -1,
                Latitude = pos.Lat,
                Longitude = pos.Lng,
                Altitude = _vtolLandAltitude,
                Radius = 150,
                CommandType = "LANDING_CIRCLE",
                AutoNext = false, // По умолчанию ждёт подтверждение посадки
                LoiterTurns = 1,
                Clockwise = true,
                Delay = 0
            };
            System.Diagnostics.Debug.WriteLine($"[VTOL] LandingCircle создан: {_landingCircle.Latitude:F6}, {_landingCircle.Longitude:F6}");
        }

        #endregion

        #region FREEZE / RESUME MISSION

        /// <summary>
        /// Заморозить/Продолжить миссию — единая кнопка
        /// 
        /// ЗАМОРОЗКА:
        ///   - Дрон в пути к WP → ставим LOITER режим (дрон кружит где есть)
        ///   - Дрон уже кружит WP → ставим AutoNext=false на текущий WP, переотправляем миссию
        /// 
        /// ПРОДОЛЖЕНИЕ:
        ///   - Переотправляем миссию (с возможными изменениями)
        ///   - SetCurrentWaypoint(nextSeq) — дрон летит к следующей точке
        ///   - Ставим AUTO режим
        /// 
        /// БЕЗОПАСНОСТЬ:
        ///   - Не замораживаем во время VTOL перехода (опасно!)
        ///   - Не замораживаем на TAKEOFF/LAND seq
        ///   - Всегда проверяем Armed + режим
        /// </summary>
        private async void FreezeResumeMission_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_mavlinkService == null || !_mavlinkService.IsConnected)
                {
                    ShowStatusMessage("Дрон не подключен");
                    return;
                }

                var telem = _mavlinkService.CurrentTelemetry;
                if (telem == null || !telem.Armed)
                {
                    ShowStatusMessage("Дрон не активирован");
                    return;
                }

                string mode = telem.FlightMode?.ToUpper() ?? "";

                if (!_isMissionFrozen)
                {
                    // === ЗАМОРОЗКА ===
                    int currentSeq = _mavlinkService.CurrentMissionSeq;

                    // Защита: не замораживаем на TAKEOFF (seq=1), TRANSITION (seq=2)
                    if (currentSeq <= 2)
                    {
                        ShowStatusMessage("⚠️ Нельзя заморозить: дрон взлетает");
                        return;
                    }

                    // Защита: не замораживаем на VTOL_LAND и TRANSITION_MC
                    if (_currentVehicleType == VehicleType.QuadPlane)
                    {
                        // Общее кол-во seq: HOME(0) + TAKEOFF(1) + TRANS_FW(2) + START(3) + WPs + LANDING + TRANS_MC + LAND
                        int totalItems = _waypoints.Count + 7; // 0..N+3
                        int transitionMcSeq = totalItems - 2;  // N+2
                        int landSeq = totalItems - 1;          // N+3

                        if (currentSeq >= transitionMcSeq)
                        {
                            ShowStatusMessage("⚠️ Нельзя заморозить: дрон на посадке");
                            return;
                        }
                    }

                    // Ставим LOITER режим — дрон начинает кружить на месте
                    _mavlinkService.SetFlightMode("LOITER");
                    _isMissionFrozen = true;

                    // Обновляем UI кнопки
                    if (sender is Button btn)
                    {
                        btn.Content = "▶ Продолжить миссию";
                        btn.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(22, 163, 74)); // Зелёный
                    }

                    System.Diagnostics.Debug.WriteLine($"[FREEZE] Миссия заморожена на seq={currentSeq}");
                    ShowStatusMessage("⏸ Миссия заморожена — дрон кружит");
                }
                else
                {
                    // === ПРОДОЛЖЕНИЕ ===
                    int currentSeq = _mavlinkService.CurrentMissionSeq;

                    // Переотправляем миссию с возможными изменениями
                    if (_currentVehicleType == VehicleType.QuadPlane && _startCircle != null && _landingCircle != null)
                    {
                        // VTOL миссия
                        if (_homePosition == null) { ShowStatusMessage("Нет HOME позиции"); return; }

                        bool success = await _mavlinkService.UploadVtolMission(
                            _homePosition, _startCircle, _waypoints.ToList(), _landingCircle,
                            _vtolTakeoffAltitude, _vtolLandAltitude);

                        if (!success) { ShowStatusMessage("❌ Ошибка загрузки"); return; }
                    }
                    else
                    {
                        // Обычная миссия (Copter/Plane)
                        _mavlinkService.SavePlannedMission(_waypoints.ToList());
                        bool success = await _mavlinkService.UploadPlannedMission();
                        if (!success) { ShowStatusMessage("❌ Ошибка загрузки"); return; }
                    }

                    await System.Threading.Tasks.Task.Delay(500);

                    // Продолжаем с СЛЕДУЮЩЕЙ точки
                    ushort nextSeq = (ushort)(currentSeq + 1);
                    _mavlinkService.SetCurrentWaypoint(nextSeq);

                    await System.Threading.Tasks.Task.Delay(300);
                    _mavlinkService.SetFlightMode("AUTO");

                    _isMissionFrozen = false;

                    if (sender is Button btn)
                    {
                        btn.Content = "⏸ Заморозить миссию";
                        btn.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(37, 99, 235)); // Синий
                    }

                    System.Diagnostics.Debug.WriteLine($"[RESUME] Миссия продолжена с seq={nextSeq}");
                    ShowStatusMessage($"▶ Миссия продолжена → точка {nextSeq}");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[FREEZE/RESUME] Ошибка: {ex.Message}");
                ShowStatusMessage($"Ошибка: {ex.Message}");
            }
        }

        /// <summary>
        /// Показать временное сообщение на карте
        /// </summary>
        private void ShowStatusMessage(string message)
        {
            System.Diagnostics.Debug.WriteLine($"[STATUS] {message}");
            // Используем MessageBox для надёжности
            MessageBox.Show(message, "KYRAN GCS", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        #endregion

        /// <summary>
        /// Продолжить миссию — отправить дрон к следующей точке
        /// Используется когда дрон кружит на LOITER_UNLIM (AutoNext=false)
        /// Отправляет MISSION_SET_CURRENT на следующий seq
        /// </summary>
        private void ResumeMissionButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_mavlinkService == null || !_mavlinkService.IsConnected)
                {
                    MessageBox.Show("Дрон не подключен", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var telem = _mavlinkService.CurrentTelemetry;
                if (telem == null || !telem.Armed)
                {
                    MessageBox.Show("Дрон не активирован", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // Текущая точка миссии (seq от автопилота)
                ushort currentSeq = (ushort)(telem.CurrentWaypoint);
                ushort nextSeq = (ushort)(currentSeq + 1);

                // Проверяем что nextSeq не выходит за пределы миссии
                // Для VTOL: HOME+TAKEOFF+TRANS+S+WPs+L+TRANS+LAND = много больше чем WPs+1
                int totalItems = GetFullMission().Count;
                if (nextSeq >= totalItems)
                {
                    MessageBox.Show("Дрон уже на последней точке миссии", "Информация", 
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                _mavlinkService.SetCurrentWaypoint(nextSeq);

                System.Diagnostics.Debug.WriteLine($"[RESUME] Отправлен MISSION_SET_CURRENT seq={nextSeq}");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
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

        private int _loiterTurns;
        public int LoiterTurns
        {
            get => _loiterTurns;
            set { _loiterTurns = value; OnPropertyChanged(); }
        }

        private bool _autoNext = true;
        /// <summary>
        /// Автопереход к следующей точке.
        /// true  = MAV_CMD_NAV_LOITER_TURNS (N оборотов, потом дальше) или WAYPOINT
        /// false = MAV_CMD_NAV_LOITER_UNLIM (бесконечное кружение, ждёт команду)
        /// Это позволяет оператору менять миссию в реальном времени пока дрон кружит.
        /// </summary>
        public bool AutoNext
        {
            get => _autoNext;
            set { _autoNext = value; OnPropertyChanged(); }
        }

        private bool _clockwise = true;
        /// <summary>
        /// Направление кружения на точке.
        /// true  = по часовой стрелке (CW) - положительный радиус в MAVLink
        /// false = против часовой стрелки (CCW) - отрицательный радиус в MAVLink
        /// </summary>
        public bool Clockwise
        {
            get => _clockwise;
            set { _clockwise = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }


    }


}