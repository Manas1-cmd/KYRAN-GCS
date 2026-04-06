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
using System.Linq;
using System.Runtime.CompilerServices;
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
    public partial class FlightPlanView : UserControl
    {

        private Window OwnerWindow => Window.GetWindow(this) ?? Application.Current?.MainWindow;
        private VehicleType _currentVehicleType = VehicleType.Copter;

        private ObservableCollection<WaypointItem> _waypoints;
        private GMapMarker _currentDragMarker;
        private WaypointItem _selectedWaypoint;
        private double _waypointRadius = 80;
        private WaypointItem _radiusDragWaypoint = null;
        private bool _isRadiusDragging = false;
        private TextBlock _radiusTooltip = null;
        private MAVLinkService _mavlinkService;
        private GMapMarker _droneMarker = null;
        private WaypointItem _homePosition = null;
        private bool _isInitialized = false;
        private Dictionary<VehicleType, List<WaypointItem>> _missionsByType = new();
        private Dictionary<VehicleType, WaypointItem> _homeByType = new();
        private DispatcherTimer _droneUpdateTimer;
        private bool _isSettingHomeMode = false;
        private double _takeoffAltitude = 10;
        private double _rtlAltitude = 15;
        private bool _wasArmed = false;
        private bool _suppressMissionNotify = false;
        private DateTime _lastRouteUpdate = DateTime.MinValue;
        private const double ROUTE_UPDATE_INTERVAL_MS = 16;
        private WaypointItem _draggingWaypoint = null;
        private bool _homeLockedFromArm = false;
        private bool? _lastDisplayedArmed = null;
        private bool? _lastNextBtnVisible = null;
        private string _lastDisplayedMode = null;
        private VehicleType? _lastSpeedLabelType = null;
        private byte _lastGpsFixType = 255;
        private SrtmElevationProvider _elevationProvider = new();
        private Dictionary<WaypointItem, GMapMarker> _resizeHandles = new();

        private DispatcherTimer _telemetryTimer;
        private DispatcherTimer _connectionTimer;
        private DateTime _connectionStartTime;
        private bool _wasConnected = false;
        private TelemetryNotifier _notifier;

        private double _vtolTakeoffAltitude = 30;
        private double _vtolLandAltitude = 30;
        private bool _isMissionFrozen = false;
        private System.Threading.CancellationTokenSource _realtimeUploadCts;
        private readonly List<GMapMarker> _trackMarkers = new();
        private DateTime _lastTrackTime = DateTime.MinValue;
        private const string TARGET_HEADING_LINE = "TargetHeadingLine";
        private GMapMarker _navBearingMarker = null;
        private bool _isDataTabActive = true;

        public FlightPlanView(MAVLinkService mavlinkService = null)
        {
            InitializeComponent();
            DrawCompassTicks();
            NotificationService.Instance.HudRequested += OnHudRequested;

            Services.LocalizationService.Instance.LanguageChanged += (s, e) =>
                Dispatcher.Invoke(() => { UpdateVehicleTypeDisplay(); PopulateFlightModes(); });
            var testElev = new SrtmElevationProvider();
            var result = testElev.GetElevation(43.238, 76.945);
            System.Diagnostics.Debug.WriteLine($"[SRTM TEST] Результат: {result?.ToString() ?? "NULL"}");
            _mavlinkService = mavlinkService;
            _notifier = new TelemetryNotifier(NotificationService.Instance, _mavlinkService);

            try
            {
                var vm = VehicleManager.Instance;
                _currentVehicleType = vm.CurrentVehicleType;
                vm.VehicleTypeChanged += (_, profile) =>
                {
                    Dispatcher.Invoke(() =>
                    {

                        if (_currentVehicleType != profile.Type && _isInitialized)
                        {

                            SaveCurrentMissionForType();

                            _currentVehicleType = profile.Type;

                            LoadMissionForType(_currentVehicleType);
                        }
                        else
                        {
                            _currentVehicleType = profile.Type;
                        }

                        UpdateVehicleTypeDisplay();
                        PopulateFlightModes();
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

                _mavlinkService.TelemetryUpdated += OnTelemetryReceived;

                _telemetryTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(50)
                };
                _telemetryTimer.Tick += UpdateTelemetryUI;
                _telemetryTimer.Start();

                _connectionTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(1)
                };
                _connectionTimer.Tick += UpdateConnectionTimer;
                _connectionTimer.Start();
            }

            _waypoints = new ObservableCollection<WaypointItem>();
            _waypoints.CollectionChanged += (s, e) =>
            {
                UpdateStatistics();
                UpdateWaypointsList();
            };

            this.Loaded += (s, e) =>
            {

                if (_isInitialized) return;
                _isInitialized = true;

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        InitializePlanMap();
                        UpdateVehicleTypeDisplay();
                        PopulateFlightModes();

                        if (_mavlinkService != null)
                        {
                            _mavlinkService.MissionProgressUpdated += OnMissionProgressUpdated;
                            _mavlinkService.MissionUploadStarted += () => _suppressMissionNotify = true;
                            _mavlinkService.MissionUploadCompleted += () => _suppressMissionNotify = false;
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
                    if (_mavlinkService != null)
                    {
                        _mavlinkService.TelemetryUpdated -= OnTelemetryReceived;
                        _mavlinkService.MissionProgressUpdated -= OnMissionProgressUpdated;
                    }

                    if (_droneUpdateTimer != null)
                    {
                        _droneUpdateTimer.Stop();
                        _droneUpdateTimer.Tick -= UpdateDronePosition;
                        _droneUpdateTimer = null;
                    }
                    if (_telemetryTimer != null)
                    {
                        _telemetryTimer.Stop();
                        _telemetryTimer.Tick -= UpdateTelemetryUI;
                        _telemetryTimer = null;
                    }
                    if (_connectionTimer != null)
                    {
                        _connectionTimer.Stop();
                        _connectionTimer.Tick -= UpdateConnectionTimer;
                        _connectionTimer = null;
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

            this.MouseLeftButtonUp += (s, e) => EndRadiusDrag();
            this.MouseLeave += (s, e) => EndRadiusDrag();
        }

        private async void InitializePlanMap()
        {
            try
            {
                System.Diagnostics.Debug.WriteLine("Начало инициализации карты планирования...");

                string cacheFolder = System.IO.Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, "MapCache");

                if (!System.IO.Directory.Exists(cacheFolder))
                    System.IO.Directory.CreateDirectory(cacheFolder);

                bool hasInternet = await CheckInternetConnectionAsync();

                GMap.NET.GMaps.Instance.Mode = hasInternet
                    ? GMap.NET.AccessMode.ServerAndCache
                    : GMap.NET.AccessMode.CacheOnly;

                System.Diagnostics.Debug.WriteLine($"Режим карты: {(hasInternet ? "ОНЛАЙН" : "ОФЛАЙН")}, кэш: {cacheFolder}");

                System.Net.ServicePointManager.ServerCertificateValidationCallback =
                    (snd, certificate, chain, sslPolicyErrors) => true;

                if (PlanMap == null)
                {
                    System.Diagnostics.Debug.WriteLine("ОШИБКА: PlanMap is null!");
                    return;
                }

                PlanMap.CacheLocation = cacheFolder;

                PlanMap.MapProvider = GMapProviders.GoogleSatelliteMap;

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

                PlanMap.MouseMove += PlanMap_MouseMove;
                PlanMap.OnMapZoomChanged += PlanMap_OnMapZoomChanged;
                PlanMap.OnMapDrag += PlanMap_OnMapDrag;

                System.Diagnostics.Debug.WriteLine("Карта планирования инициализирована");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Ошибка инициализации карты: {ex.Message}");
            }
        }

        private async System.Threading.Tasks.Task<bool> CheckInternetConnectionAsync()
        {
            try
            {
                using var client = new System.Net.Http.HttpClient
                {
                    Timeout = TimeSpan.FromSeconds(2)
                };
                var response = await client.GetAsync("https://dns.google/resolve?name=test");
                return response.IsSuccessStatusCode;
            }
            catch { }

            try
            {
                using var ping = new System.Net.NetworkInformation.Ping();
                var reply = await ping.SendPingAsync("8.8.8.8", 1500);
                return reply.Status == System.Net.NetworkInformation.IPStatus.Success;
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

        private void SetHomeAtPosition(double lat, double lon)
        {

            if (_homePosition?.Marker != null)
            {
                _homePosition.Marker.Shape = null;
                PlanMap.Markers.Remove(_homePosition.Marker);
            }

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

                    System.Diagnostics.Debug.WriteLine($"=== ТЕСТ: Добавляю WP {waypoint.Number} ===");

                    _waypoints.Add(waypoint);
                    AddMarkerToMap(waypoint);
                    UpdateRoute();
                    TryRealTimeMissionUpdate(waypoint, isNewWaypoint: true);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"ОШИБКА: {ex.Message}\n{ex.StackTrace}");
                    AppMessageBox.ShowError($"{Get("MsgBox_Error")}: {ex.Message}", owner: OwnerWindow);
                }
            }
        }

        private void AddMarkerToMap(WaypointItem waypoint)
        {

            if (waypoint.Marker != null)
            {
                waypoint.Marker.Shape = null;
                PlanMap.Markers.Remove(waypoint.Marker);
                waypoint.Marker = null;
            }

            if (waypoint.ShapeGrid != null)
            {
                waypoint.ShapeGrid.Children.Clear();
                waypoint.ShapeGrid = null;
            }
            waypoint.RadiusCircle = null;

            var position = new PointLatLng(waypoint.Latitude, waypoint.Longitude);

            var shape = CreateMarkerShape(waypoint);

            var marker = new GMapMarker(position)
            {
                Shape = shape,
                Offset = new Point(-((FrameworkElement)shape).Width / 2, -((FrameworkElement)shape).Height / 2),
                ZIndex = 100
            };

            marker.Tag = waypoint;
            waypoint.Marker = marker;

            PlanMap.Markers.Add(marker);

            SetupMarkerDragDrop(marker, waypoint);

            CreateResizeHandle(waypoint);
        }

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

            var radiusCircle = new Ellipse
            {
                Width = radiusInPixels * 2,
                Height = radiusInPixels * 2,
                Stroke = new SolidColorBrush(Color.FromArgb(200, 152, 240, 25)),
                StrokeThickness = 2,
                StrokeDashArray = new DoubleCollection { 4, 2 },
                Fill = new SolidColorBrush(Color.FromArgb(40, 152, 240, 25)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed
            };
            grid.Children.Add(radiusCircle);
            waypoint.RadiusCircle = radiusCircle;

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

            grid.ToolTip = CreateWaypointTooltip(waypoint);
            ToolTipService.SetInitialShowDelay(grid, 300);
            ToolTipService.SetShowDuration(grid, 10000);

            grid.ToolTipOpening += (s, args) =>
            {
                if (s is Grid g) g.ToolTip = CreateWaypointTooltip(waypoint);
            };

            return grid;
        }

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

            var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            header.Children.Add(new Ellipse
            {
                Width = 24,
                Height = 24,
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
                Text = Fmt("Wp_Number", wp.Number),
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center
            });
            stack.Children.Add(header);

            var paramStyle = new Style(typeof(TextBlock));

            stack.Children.Add(CreateTooltipRow(Get("Wp_Tooltip_Coords"), $"{wp.Latitude:F6}, {wp.Longitude:F6}"));
            stack.Children.Add(CreateTooltipRow(Get("Wp_Tooltip_Alt"), $"{wp.Altitude:F0} м"));
            stack.Children.Add(CreateTooltipRow(Get("Wp_Tooltip_Radius"), $"{wp.Radius:F0} м"));
            stack.Children.Add(CreateTooltipRow(Get("Wp_Tooltip_Dir"), wp.Clockwise ? Get("Dir_CW") : Get("Dir_CCW")));
            stack.Children.Add(CreateTooltipRow(Get("Wp_Tooltip_Delay"), $"{wp.Delay:F0} {Get("Unit_sec")}"));
            stack.Children.Add(CreateTooltipRow(Get("Wp_Tooltip_Turns"), wp.LoiterTurns.ToString()));
            stack.Children.Add(CreateTooltipRow(Get("Wp_Tooltip_Auto"), wp.AutoNext ? Get("Yes") : Get("No_Loiter")));

            tooltip.Content = stack;
            return tooltip;
        }

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

        private double MetersToPixels(double meters, double latitude, double zoom)
        {

            double latRad = latitude * Math.PI / 180.0;
            double metersPerPixel = 40075017 * Math.Cos(latRad) / (256 * Math.Pow(2, zoom));
            return meters / metersPerPixel;
        }

        private void SetupMarkerDragDrop(GMapMarker marker, WaypointItem waypoint)
        {
            var shape = marker.Shape as FrameworkElement;
            if (shape == null) return;

            shape.MouseLeftButtonDown += (s, e) =>
            {
                _currentDragMarker = marker;
                shape.CaptureMouse();
                PlanMap.CanDragMap = false;

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

                    if (_resizeHandles.ContainsKey(waypoint))
                    {
                        var handlePos = CalculatePointAtDistance(
                            waypoint.Latitude, waypoint.Longitude, 90, waypoint.Radius / 1000.0);
                        _resizeHandles[waypoint].Position = new PointLatLng(handlePos.Lat, handlePos.Lng);
                    }
                    var now = DateTime.Now;
                    if ((now - _lastRouteUpdate).TotalMilliseconds >= ROUTE_UPDATE_INTERVAL_MS)
                    {
                        _lastRouteUpdate = now;
                        UpdateRouteFast(waypoint);
                        UpdateWaypointsList();
                        UpdateStatistics();
                    }
                }
            };

            shape.MouseLeftButtonUp += (s, e) =>
            {
                if (_currentDragMarker == marker)
                {
                    shape.ReleaseMouseCapture();
                    PlanMap.CanDragMap = true;
                    _currentDragMarker = null;
                    UpdateRoute();
                    TryRealTimeMissionUpdate(waypoint);
                }
            };

            shape.MouseRightButtonDown += (s, e) =>
            {
                RemoveWaypoint(waypoint);
                e.Handled = true;
            };
        }

        private void SelectWaypoint(WaypointItem wp)
        {

            if (_selectedWaypoint != null && _selectedWaypoint != wp && _selectedWaypoint.RadiusCircle != null)
            {
                _selectedWaypoint.RadiusCircle.Visibility = Visibility.Collapsed;
                if (_resizeHandles.ContainsKey(_selectedWaypoint))
                    _resizeHandles[_selectedWaypoint].Shape.Visibility = Visibility.Collapsed;
            }

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

                if (wp.RadiusCircle != null)
                    wp.RadiusCircle.Visibility = Visibility.Visible;
                if (_resizeHandles.ContainsKey(wp))
                    _resizeHandles[wp].Shape.Visibility = Visibility.Visible;
                _selectedWaypoint = wp;
            }
        }

        private GMapMarker _tooltipMarker = null;

        private void ResizeHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is Ellipse handle && handle.Tag is WaypointItem wp)
            {
                _radiusDragWaypoint = wp;
                _isRadiusDragging = true;
                handle.CaptureMouse();
                PlanMap.CanDragMap = false;

                CreateRadiusTooltip();
                var pos = e.GetPosition(PlanMap);
                var latLng = PlanMap.FromLocalToLatLng((int)pos.X, (int)pos.Y);
                UpdateRadiusTooltip(latLng, wp.Radius);

                e.Handled = true;
            }
        }

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

        private void HideRadiusTooltip()
        {
            if (_tooltipMarker != null && PlanMap.Markers.Contains(_tooltipMarker))
            {
                _tooltipMarker.Shape = null;
                PlanMap.Markers.Remove(_tooltipMarker);
            }
        }

        private double GetMinRadius()
        {
            return _currentVehicleType == VehicleType.QuadPlane ? 80 : 5;
        }

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

        private void CreateResizeHandle(WaypointItem waypoint)
        {

            if (_resizeHandles.ContainsKey(waypoint))
            {
                var oldHandle = _resizeHandles[waypoint];
                oldHandle.Shape = null;
                PlanMap.Markers.Remove(oldHandle);
                _resizeHandles.Remove(waypoint);
            }

            var handlePos = CalculatePointAtDistance(
                waypoint.Latitude, waypoint.Longitude,
                90,
                waypoint.Radius / 1000.0);

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

            handle.MouseLeftButtonDown += ResizeHandle_MouseLeftButtonDown;

            handle.MouseLeftButtonUp += (s, e) =>
            {
                if (_isRadiusDragging)
                {
                    handle.ReleaseMouseCapture();
                    EndRadiusDrag();
                    e.Handled = true;
                }
            };

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

            handle.Visibility = Visibility.Collapsed;
        }

        private PointLatLng CalculatePointAtDistance(double lat, double lon, double bearingDeg, double distanceKm)
        {
            const double R = 6371;
            double lat1 = lat * Math.PI / 180;
            double lon1 = lon * Math.PI / 180;
            double bearing = bearingDeg * Math.PI / 180;

            double lat2 = Math.Asin(Math.Sin(lat1) * Math.Cos(distanceKm / R) +
                                   Math.Cos(lat1) * Math.Sin(distanceKm / R) * Math.Cos(bearing));
            double lon2 = lon1 + Math.Atan2(Math.Sin(bearing) * Math.Sin(distanceKm / R) * Math.Cos(lat1),
                                             Math.Cos(distanceKm / R) - Math.Sin(lat1) * Math.Sin(lat2));

            return new PointLatLng(lat2 * 180 / Math.PI, lon2 * 180 / Math.PI);
        }

        private void RemoveWaypoint(WaypointItem waypoint)
        {
            bool isVtol = _currentVehicleType == VehicleType.QuadPlane;
            int deletedMissionSeq = waypoint.Number - 1 + (isVtol ? 3 : 2);

            if (_resizeHandles.ContainsKey(waypoint))
            {
                _resizeHandles[waypoint].Shape = null;
                PlanMap.Markers.Remove(_resizeHandles[waypoint]);
                _resizeHandles.Remove(waypoint);
            }

            if (waypoint.Marker != null)
            {
                waypoint.Marker.Shape = null;
                PlanMap.Markers.Remove(waypoint.Marker);
            }

            _waypoints.Remove(waypoint);

            for (int i = 0; i < _waypoints.Count; i++)
            {
                _waypoints[i].Number = i + 1;

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

            UpdateRoute();
            UpdateWaypointsList();
            UpdateStatistics();
            TryRealTimeMissionUpdate(null, deletedMissionSeq);

            System.Diagnostics.Debug.WriteLine($"Waypoint удалён, осталось: {_waypoints.Count}, deletedSeq={deletedMissionSeq}");
        }

        private void RenumberWaypoints()
        {
            for (int i = 0; i < _waypoints.Count; i++)
            {
                _waypoints[i].Number = i + 1;

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
        private void UpdateRouteFast(WaypointItem changedWp)
        {
            if (changedWp == null || PlanMap == null) return;

            int idx = _waypoints.IndexOf(changedWp);
            if (idx < 0) return;

            bool isFirst = idx == 0;
            bool isLast = idx == _waypoints.Count - 1;
            bool isVTOL = _currentVehicleType == VehicleType.QuadPlane;
            var affectedTags = new HashSet<string>();
            if (isFirst) affectedTags.Add("home_entry");
            if (isLast) affectedTags.Add("home_return");
            if (idx > 0) affectedTags.Add($"seg_{idx - 1}_{idx}");
            if (idx < _waypoints.Count - 1) affectedTags.Add($"seg_{idx}_{idx + 1}");
            affectedTags.Add($"arc_{changedWp.Number}");
            if (idx > 0) affectedTags.Add($"arc_{_waypoints[idx - 1].Number}");
            if (idx < _waypoints.Count - 1) affectedTags.Add($"arc_{_waypoints[idx + 1].Number}");
            var toRemove = PlanMap.Markers.OfType<GMapRoute>()
                .Where(r => r.Tag is string tag && affectedTags.Contains(tag))
                .ToList();
            foreach (var r in toRemove) { r.Shape = null; PlanMap.Markers.Remove(r); }
            if (isFirst && _homePosition != null)
            {
                var firstWp = _waypoints[0];
                var tp = GetExternalTangentPoints(
                    _homePosition.Latitude, _homePosition.Longitude, 0, firstWp.Clockwise,
                    firstWp.Latitude, firstWp.Longitude, firstWp.Radius, firstWp.Clockwise);
                AddRouteSegment(
                    new PointLatLng(_homePosition.Latitude, _homePosition.Longitude), tp.Item2,
                    Color.FromRgb(239, 68, 68), dashed: true, tag: "home_entry");
            }
            if (isLast && _homePosition != null)
            {
                var lastWp = _waypoints[idx];
                var tp = GetExternalTangentPoints(
                    lastWp.Latitude, lastWp.Longitude, lastWp.Radius, lastWp.Clockwise,
                    _homePosition.Latitude, _homePosition.Longitude, 0, lastWp.Clockwise);
                AddRouteSegment(
                    tp.Item1, new PointLatLng(_homePosition.Latitude, _homePosition.Longitude),
                    Color.FromRgb(239, 68, 68), dashed: true, tag: "home_return");
            }
            if (idx > 0)
            {
                var wp1 = _waypoints[idx - 1];
                var wp2 = _waypoints[idx];
                var tp = GetExternalTangentPoints(
                    wp1.Latitude, wp1.Longitude, wp1.Radius, wp1.Clockwise,
                    wp2.Latitude, wp2.Longitude, wp2.Radius, wp2.Clockwise);
                AddRouteSegment(tp.Item1, tp.Item2,
                    Color.FromRgb(152, 240, 25), dashed: false, tag: $"seg_{idx - 1}_{idx}");
            }
            if (idx < _waypoints.Count - 1)
            {
                var wp1 = _waypoints[idx];
                var wp2 = _waypoints[idx + 1];
                var tp = GetExternalTangentPoints(
                    wp1.Latitude, wp1.Longitude, wp1.Radius, wp1.Clockwise,
                    wp2.Latitude, wp2.Longitude, wp2.Radius, wp2.Clockwise);
                AddRouteSegment(tp.Item1, tp.Item2,
                    Color.FromRgb(152, 240, 25), dashed: false, tag: $"seg_{idx}_{idx + 1}");
            }
            RedrawArcForIndex(idx);
            if (idx > 0) RedrawArcForIndex(idx - 1);
            if (idx < _waypoints.Count - 1) RedrawArcForIndex(idx + 1);
        }
        private void RedrawArcForIndex(int i)
        {
            if (i < 0 || i >= _waypoints.Count) return;
            var wp = _waypoints[i];

            PointLatLng? entry = null;
            PointLatLng? exit = null;
            if (i == 0 && _homePosition != null)
            {
                var tp = GetExternalTangentPoints(
                    _homePosition.Latitude, _homePosition.Longitude, 0, wp.Clockwise,
                    wp.Latitude, wp.Longitude, wp.Radius, wp.Clockwise);
                entry = tp.Item2;
            }
            else if (i > 0)
            {
                var prev = _waypoints[i - 1];
                var tp = GetExternalTangentPoints(
                    prev.Latitude, prev.Longitude, prev.Radius, prev.Clockwise,
                    wp.Latitude, wp.Longitude, wp.Radius, wp.Clockwise);
                entry = tp.Item2;
            }
            if (i == _waypoints.Count - 1 && _homePosition != null)
            {
                var tp = GetExternalTangentPoints(
                    wp.Latitude, wp.Longitude, wp.Radius, wp.Clockwise,
                    _homePosition.Latitude, _homePosition.Longitude, 0, wp.Clockwise);
                exit = tp.Item1;
            }
            else if (i < _waypoints.Count - 1)
            {
                var next = _waypoints[i + 1];
                var tp = GetExternalTangentPoints(
                    wp.Latitude, wp.Longitude, wp.Radius, wp.Clockwise,
                    next.Latitude, next.Longitude, next.Radius, next.Clockwise);
                exit = tp.Item1;
            }

            if (entry.HasValue && exit.HasValue)
                DrawArcOnWaypoint(wp, entry.Value, exit.Value);
        }

        private void AddRouteSegment(PointLatLng from, PointLatLng to,
            Color color, bool dashed, string tag)
        {
            var route = new GMapRoute(new List<PointLatLng> { from, to });
            route.Tag = tag;
            route.Shape = new Path
            {
                Stroke = new SolidColorBrush(color),
                StrokeThickness = dashed ? 2 : 3,
                StrokeDashArray = dashed ? new DoubleCollection { 5, 3 } : null,
                Opacity = 0.9
            };
            route.ZIndex = dashed ? 40 : 50;
            PlanMap.Markers.Add(route);
        }

        private void UpdateRoute()
        {
            var oldRoutes = PlanMap.Markers.OfType<GMapRoute>().ToList();
            foreach (var r in oldRoutes) { r.Shape = null; PlanMap.Markers.Remove(r); }

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

            if (_waypoints.Count == 0) { UpdateStatistics(); return; }

            var entryPoints = new Dictionary<int, PointLatLng>();
            var exitPoints = new Dictionary<int, PointLatLng>();
            if (_homePosition != null)
            {
                var firstWp = _waypoints[0];
                var tp = GetExternalTangentPoints(
                    _homePosition.Latitude, _homePosition.Longitude, 0, firstWp.Clockwise,
                    firstWp.Latitude, firstWp.Longitude, firstWp.Radius, firstWp.Clockwise);
                var homePoint = new PointLatLng(_homePosition.Latitude, _homePosition.Longitude);
                entryPoints[0] = tp.Item2;
                AddRouteSegment(homePoint, tp.Item2,
                    Color.FromRgb(239, 68, 68), dashed: true, tag: "home_entry");
            }
            for (int i = 0; i < _waypoints.Count - 1; i++)
            {
                var wp1 = _waypoints[i];
                var wp2 = _waypoints[i + 1];
                var tp = GetExternalTangentPoints(
                    wp1.Latitude, wp1.Longitude, wp1.Radius, wp1.Clockwise,
                    wp2.Latitude, wp2.Longitude, wp2.Radius, wp2.Clockwise);
                exitPoints[i] = tp.Item1;
                entryPoints[i + 1] = tp.Item2;
                AddRouteSegment(tp.Item1, tp.Item2,
                    Color.FromRgb(152, 240, 25), dashed: false, tag: $"seg_{i}_{i + 1}");
            }
            if (_homePosition != null && _waypoints.Count > 0)
            {
                int lastIdx = _waypoints.Count - 1;
                var lastWp = _waypoints[lastIdx];
                var tp = GetExternalTangentPoints(
                    lastWp.Latitude, lastWp.Longitude, lastWp.Radius, lastWp.Clockwise,
                    _homePosition.Latitude, _homePosition.Longitude, 0, lastWp.Clockwise);
                var homePoint = new PointLatLng(_homePosition.Latitude, _homePosition.Longitude);
                exitPoints[lastIdx] = tp.Item1;
                AddRouteSegment(tp.Item1, homePoint,
                    Color.FromRgb(239, 68, 68), dashed: true, tag: "home_return");
            }
            for (int i = 0; i < _waypoints.Count; i++)
            {
                if (entryPoints.ContainsKey(i) && exitPoints.ContainsKey(i))
                    DrawArcOnWaypoint(_waypoints[i], entryPoints[i], exitPoints[i]);
            }

            UpdateStatistics();
        }

        private (PointLatLng, PointLatLng) GetExternalTangentPoints(
            double lat1, double lon1, double r1, bool cw1,
            double lat2, double lon2, double r2, bool cw2)
        {
            double dist = CalculateDistanceLatLng(lat1, lon1, lat2, lon2);
            double bearing = CalculateBearing(lat1, lon1, lat2, lon2);

            double exitOffset = cw1 ? -90 : 90;
            double entryOffset = cw2 ? -90 : 90;

            bool sameSide = (cw1 == cw2);
            double tangentAngle = 0;

            if (sameSide)
            {

                if (dist > Math.Abs(r1 - r2))
                {
                    double sinAlpha = (r1 - r2) / dist;
                    sinAlpha = Math.Max(-1, Math.Min(1, sinAlpha));
                    tangentAngle = Math.Asin(sinAlpha) * 180 / Math.PI;
                }

                double exitAngle = bearing + exitOffset + tangentAngle;
                double entryAngle = bearing + entryOffset + tangentAngle;

                return (
                    CalculatePointAtDistance(lat1, lon1, exitAngle, r1 / 1000.0),
                    CalculatePointAtDistance(lat2, lon2, entryAngle, r2 / 1000.0)
                );
            }
            else
            {

                if (dist > (r1 + r2))
                {
                    double sinAlpha = (r1 + r2) / dist;
                    sinAlpha = Math.Max(-1, Math.Min(1, sinAlpha));
                    tangentAngle = Math.Asin(sinAlpha) * 180 / Math.PI;
                }

                double crossSign = cw1 ? 1 : -1;
                double exitAngle = bearing + exitOffset + crossSign * tangentAngle;
                double entryAngle = bearing + entryOffset + crossSign * tangentAngle;

                return (
                    CalculatePointAtDistance(lat1, lon1, exitAngle, r1 / 1000.0),
                    CalculatePointAtDistance(lat2, lon2, entryAngle, r2 / 1000.0)
                );
            }
        }

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

        private double CalculateDistanceLatLng(double lat1, double lon1, double lat2, double lon2)
        {
            const double R = 6371000;
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

            if (_waypoints != null && _waypoints.Count > 0)
            {
                foreach (var wp in _waypoints)
                {
                    wp.Radius = _waypointRadius;
                }

                RefreshMarkers();
            }
        }

        private void DrawArcOnWaypoint(WaypointItem wp, PointLatLng entryPoint, PointLatLng exitPoint)
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
                arcPoints.Add(CalculatePointAtDistance(wp.Latitude, wp.Longitude, angle, wp.Radius / 1000.0));
            }

            if (arcPoints.Count >= 2)
            {
                var arcRoute = new GMapRoute(arcPoints);
                arcRoute.Tag = $"arc_{wp.Number}";
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

        private void UpdateStatistics()
        {
            WaypointsCountText.Text = Fmt("Wp_Count", _waypoints.Count);

            double totalDistance = 0;

            if (_homePosition != null && _waypoints.Count > 0)
            {
                totalDistance += CalculateDistanceLatLng(
                    _homePosition.Latitude, _homePosition.Longitude,
                    _waypoints[0].Latitude, _waypoints[0].Longitude);
            }

            for (int i = 0; i < _waypoints.Count - 1; i++)
            {
                totalDistance += CalculateDistance(_waypoints[i], _waypoints[i + 1]);
            }

            if (_homePosition != null && _waypoints.Count > 0)
            {
                var lastWp = _waypoints[_waypoints.Count - 1];
                totalDistance += CalculateDistanceLatLng(
                    lastWp.Latitude, lastWp.Longitude,
                    _homePosition.Latitude, _homePosition.Longitude);
            }

            string distText = FormatDistance(totalDistance);

            if (TotalDistanceOverlay != null)
                TotalDistanceOverlay.Text = $"{Get("Route_Label")}: {distText}";

            UpdateMissionStrip();
        }

        private void UpdateMissionStrip()
        {
            bool isVtol = _currentVehicleType == VehicleType.QuadPlane;

            var vis = isVtol ? Visibility.Visible : Visibility.Collapsed;

            if (ArrowAfterTakeoff != null) ArrowAfterTakeoff.Visibility = isVtol ? Visibility.Collapsed : Visibility.Visible;
            if (ArrowAfterTakeoffVtol != null) ArrowAfterTakeoffVtol.Visibility = Visibility.Collapsed;
            if (ArrowAfterStart != null) ArrowAfterStart.Visibility = Visibility.Collapsed;
            if (ArrowBeforeLand != null) ArrowBeforeLand.Visibility = Visibility.Collapsed;

            if (isVtol)
            {

            }

            bool hasWp = _waypoints.Count > 0;
            if (ArrowBeforeRtl != null) ArrowBeforeRtl.Visibility = hasWp ? Visibility.Visible : Visibility.Collapsed;
            if (RtlCard != null) RtlCard.Visibility = hasWp ? Visibility.Visible : Visibility.Collapsed;
        }

        public void HighlightMissionSeq(int seq)
        {
            Dispatcher.BeginInvoke(() =>
            {

                if (WaypointsListPanel == null) return;

                bool isVtol = _currentVehicleType == VehicleType.QuadPlane;
                int wpOffset = isVtol ? (1 + 1 + 1) : (1 + 1);

                foreach (var child in WaypointsListPanel.Children)
                {
                    if (child is Border border && border.Tag is int wpNum)
                    {
                        int expectedSeq = wpNum - 1 + wpOffset;

                        if (expectedSeq == seq)
                        {
                            border.BorderBrush = new SolidColorBrush(Color.FromRgb(74, 222, 128));
                            border.BorderThickness = new Thickness(3);
                        }
                        else
                        {
                            border.BorderBrush = new SolidColorBrush(Color.FromRgb(152, 240, 25));
                            border.BorderThickness = new Thickness(2);
                        }
                    }
                }

                if (isVtol && LandingCircleCard != null)
                {
                    var fullMission = GetFullMission();
                    int landSeq = fullMission.Count - 1;
                    LandingCircleCard.BorderThickness = new Thickness(seq == landSeq ? 3 : 2);
                }
            });
        }

        private double CalculateDistance(WaypointItem wp1, WaypointItem wp2)
        {
            const double R = 6371000;
            double dLat = ToRadians(wp2.Latitude - wp1.Latitude);
            double dLon = ToRadians(wp2.Longitude - wp1.Longitude);

            double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                       Math.Cos(ToRadians(wp1.Latitude)) * Math.Cos(ToRadians(wp2.Latitude)) *
                       Math.Sin(dLon / 2) * Math.Sin(dLon / 2);

            double c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
            return R * c;
        }

        private double ToRadians(double degrees) => degrees * Math.PI / 180.0;

        private void UpdateWaypointsList()
        {
            WaypointsListPanel.Children.Clear();
            WaypointsCountText.Text = Fmt("Wp_Count", _waypoints.Count);

            if (ArrowBeforeRtl != null)
                ArrowBeforeRtl.Visibility = _waypoints.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            foreach (var wp in _waypoints)
            {

                if (wp.Number > 1)
                {
                    WaypointsListPanel.Children.Add(new TextBlock
                    {
                        Text = "▼",
                        Foreground = new SolidColorBrush(Color.FromRgb(152, 240, 25)),
                        FontSize = 10,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Margin = new Thickness(0, 1, 0, 1)
                    });
                }

                WaypointsListPanel.Children.Add(CreateWaypointCard(wp));
            }
        }

        private Border CreateWaypointCard(WaypointItem wp)
        {
            var card = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(20, 30, 50)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(50, 70, 100)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(8, 5, 8, 5),
                Margin = new Thickness(0, 0, 0, 2),
                Cursor = Cursors.Hand,
                Tag = wp.Number
            };

            var mainGrid = new Grid();
            mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var numBorder = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(152, 240, 25)),
                CornerRadius = new CornerRadius(10),
                Width = 20,
                Height = 20,
                Margin = new Thickness(0, 0, 5, 0)
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
            Grid.SetColumn(numBorder, 0);
            mainGrid.Children.Add(numBorder);

            bool isVtol = _currentVehicleType == VehicleType.QuadPlane;
            var (badge, cmdName, badgeColor) = GetCommandBadgeInfo(wp.CommandType, isVtol);

            var typeBadge = new Border
            {
                Background = new SolidColorBrush(badgeColor),
                CornerRadius = new CornerRadius(6),
                Width = 14,
                Height = 14,
                Margin = new Thickness(0, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            typeBadge.Child = new TextBlock
            {
                Text = badge,
                Foreground = Brushes.White,
                FontSize = 7,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(typeBadge, 1);
            mainGrid.Children.Add(typeBadge);

            var infoPanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            infoPanel.Children.Add(new TextBlock
            {
                Text = cmdName,
                Foreground = Brushes.White,
                FontSize = 10,
                FontWeight = FontWeights.SemiBold
            });
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            infoPanel.Children.Add(new TextBlock
            {
                Text = $"{wp.Latitude.ToString("F2", inv)} , {wp.Longitude.ToString("F2", inv)}",
                Foreground = new SolidColorBrush(Color.FromRgb(140, 150, 170)),
                FontSize = 9,
                Margin = new Thickness(0, 1, 0, 0)
            });
            Grid.SetColumn(infoPanel, 2);
            mainGrid.Children.Add(infoPanel);

            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            var locBtn = new Border
            {
                Background = Brushes.Transparent,
                CornerRadius = new CornerRadius(6),
                Width = 18,
                Height = 18,
                Cursor = Cursors.Hand,
                ToolTip = Get("Tip_GoToWaypoint")
            };
            locBtn.Child = new TextBlock { Text = "📍", FontSize = 9, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            locBtn.MouseEnter += (s, e) => locBtn.Background = new SolidColorBrush(Color.FromArgb(50, 96, 165, 250));
            locBtn.MouseLeave += (s, e) => locBtn.Background = Brushes.Transparent;
            locBtn.MouseLeftButtonDown += (s, e) =>
            {
                e.Handled = true;
                SelectWaypoint(wp);
                PlanMap.Position = new PointLatLng(wp.Latitude, wp.Longitude);
            };
            btnPanel.Children.Add(locBtn);

            var delBtn = new Border
            {
                Background = Brushes.Transparent,
                CornerRadius = new CornerRadius(6),
                Width = 18,
                Height = 18,
                Cursor = Cursors.Hand,
                Margin = new Thickness(2, 0, 0, 0),
                ToolTip = Get("Tip_DeleteWaypoint")
            };
            delBtn.Child = new TextBlock
            {
                Text = "✕",
                FontSize = 9,
                Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            delBtn.MouseEnter += (s, e) => delBtn.Background = new SolidColorBrush(Color.FromArgb(50, 239, 68, 68));
            delBtn.MouseLeave += (s, e) => delBtn.Background = Brushes.Transparent;
            delBtn.MouseLeftButtonDown += (s, e) =>
            {
                e.Handled = true;
                RemoveWaypoint(wp);
            };
            btnPanel.Children.Add(delBtn);

            Grid.SetColumn(btnPanel, 3);
            mainGrid.Children.Add(btnPanel);

            card.Child = mainGrid;
            card.MouseLeftButtonDown += (s, e) =>
            {
                if (e.Handled) return;
                SelectWaypoint(wp);
                PlanMap.Position = new PointLatLng(wp.Latitude, wp.Longitude);
                OpenWaypointEditDialog(wp);
            };

            return card;
        }

        private (string badge, string name, Color color) GetCommandBadgeInfo(string commandType, bool isVtol = false)
        {
            string prefix = isVtol ? "Q_" : "";

            return commandType switch
            {
                "WAYPOINT" => ("W", $"{prefix}{Get("CmdShort_Waypoint")}", Color.FromRgb(34, 197, 94)),
                "LOITER_UNLIM" => ("L", $"{prefix}{Get("CmdShort_Loiter")}", Color.FromRgb(59, 130, 246)),
                "LOITER_TIME" => ("LT", $"{prefix}{Get("CmdShort_LoiterTime")}", Color.FromRgb(59, 130, 246)),
                "LOITER_TURNS" => ("LR", $"{prefix}{Get("CmdShort_LoiterTurns")}", Color.FromRgb(59, 130, 246)),
                "TAKEOFF" => ("T", Get("CmdShort_Takeoff"), Color.FromRgb(16, 185, 129)),
                "LAND" => ("LD", $"{prefix}{Get("CmdShort_Land")}", Color.FromRgb(249, 115, 22)),
                "DELAY" => ("D", $"{prefix}{Get("CmdShort_Delay")}", Color.FromRgb(139, 92, 246)),
                "CHANGE_SPEED" => ("S", $"{prefix}{Get("CmdShort_Speed")}", Color.FromRgb(234, 179, 8)),
                "RETURN_TO_LAUNCH" => ("R", Get("CmdShort_RTL"), Color.FromRgb(239, 68, 68)),
                "SPLINE_WP" => ("SP", Get("CmdShort_Spline"), Color.FromRgb(20, 184, 166)),
                _ => ("W", $"{prefix}{Get("CmdShort_Waypoint")}", Color.FromRgb(34, 197, 94))
            };
        }

        private void OpenWaypointEditDialog(WaypointItem wp)
        {
            bool isVtol = _currentVehicleType == VehicleType.QuadPlane;

            var dialog = new WaypointEditDialog(
                wp.Number,
                wp.Latitude,
                wp.Longitude,
                wp.Altitude,
                wp.Radius,
                wp.Delay,
                wp.LoiterTurns,
                wp.Speed,
                wp.AutoNext,
                wp.Clockwise,
                wp.CommandType,
                isVtol
            );

            dialog.Owner = Window.GetWindow(this);

            if (dialog.ShowDialog() == true)
            {
                bool positionChanged = (wp.Latitude != dialog.Latitude || wp.Longitude != dialog.Longitude);

                wp.Latitude = dialog.Latitude;
                wp.Longitude = dialog.Longitude;
                wp.Altitude = dialog.Altitude;
                wp.Radius = dialog.Radius;
                wp.Delay = dialog.Delay;
                wp.LoiterTurns = dialog.LoiterTurns;
                wp.Speed = dialog.Speed;
                wp.AutoNext = dialog.AutoNext;
                wp.Clockwise = dialog.Clockwise;
                wp.CommandType = dialog.CommandType;

                if (positionChanged && wp.Marker != null)
                {
                    wp.Marker.Position = new PointLatLng(wp.Latitude, wp.Longitude);

                    if (_resizeHandles.ContainsKey(wp))
                    {
                        var handlePos = CalculatePointAtDistance(wp.Latitude, wp.Longitude, 90, wp.Radius / 1000.0);
                        _resizeHandles[wp].Position = new PointLatLng(handlePos.Lat, handlePos.Lng);
                    }
                }

                UpdateWaypointRadiusVisual(wp);
                UpdateRoute();
                UpdateWaypointsList();
                UpdateStatistics();

                TryRealTimeMissionUpdate(wp);

                System.Diagnostics.Debug.WriteLine($"[WP EDIT] Точка {wp.Number} обновлена: {wp.Latitude:F6}, {wp.Longitude:F6}, Alt={wp.Altitude}м, Cmd={wp.CommandType}");
            }
        }

        private async void TryRealTimeMissionUpdate(WaypointItem changedWp = null,
            int deletedMissionSeq = -1, bool isNewWaypoint = false)
        {
            try
            {
                if (_mavlinkService == null || !_mavlinkService.IsConnected) return;

                var telem = _mavlinkService.CurrentTelemetry;
                if (telem == null || !telem.Armed) return;

                string mode = telem.FlightMode?.ToUpper() ?? "";
                if (mode != "AUTO" && mode != "MISSION" && mode != "LOITER" && mode != "QLOITER") return;
                if (_homePosition == null) return;

                int currentSeq = _mavlinkService.CurrentMissionSeq;
                bool isVtol = _currentVehicleType == VehicleType.QuadPlane;
                int offset = isVtol ? 3 : 2;
                int changedSeq = -1;
                if (isNewWaypoint && changedWp != null)
                    changedSeq = changedWp.Number - 1 + offset;
                else if (deletedMissionSeq > 0)
                    changedSeq = deletedMissionSeq;
                bool changeIsDefinitelyAhead = changedSeq > currentSeq && currentSeq > 0;
                if (changedWp != null && !isNewWaypoint && deletedMissionSeq < 0
                    && _waypoints.Contains(changedWp))
                {
                    int missionSeq = changedWp.Number - 1 + offset;
                    bool ok = await _mavlinkService.ModifyWaypointInFlight(changedWp, missionSeq);
                    if (ok)
                    {
                        System.Diagnostics.Debug.WriteLine($"[REALTIME] ✅ Partial WP{changedWp.Number} seq={missionSeq}");
                        return;
                    }
                    System.Diagnostics.Debug.WriteLine("[REALTIME] Partial failed, fallback full upload");
                }
                if (_isMissionFrozen)
                {
                    NotifyMissionChangedIfArmed();
                    return;
                }
                int debounceMs = changeIsDefinitelyAhead ? 50 : 200;
                _realtimeUploadCts?.Cancel();
                _realtimeUploadCts = new System.Threading.CancellationTokenSource();
                var uploadToken = _realtimeUploadCts.Token;
                try { await Task.Delay(debounceMs, uploadToken); }
                catch (OperationCanceledException) { return; }
                if (uploadToken.IsCancellationRequested) return;
                if (!_mavlinkService.IsConnected) return;
                telem = _mavlinkService.CurrentTelemetry;
                if (telem == null || !telem.Armed) return;
                int seqBeforeUpload = _mavlinkService.CurrentMissionSeq;

                var fullMission = GetFullMission();
                _mavlinkService.SavePlannedMission(fullMission);

                bool success = await _mavlinkService.UploadPlannedMission();
                if (!success) { System.Diagnostics.Debug.WriteLine("[REALTIME] ❌ Upload failed"); return; }

                _mavlinkService.SetActiveMission(fullMission);

                if (!_mavlinkService.CurrentTelemetry.Armed) return;

                int seqAfterUpload = _mavlinkService.CurrentMissionSeq;
                int resumeSeq = seqAfterUpload > 0 ? seqAfterUpload : seqBeforeUpload;

                if (deletedMissionSeq > 0)
                {
                    if (deletedMissionSeq < seqAfterUpload)
                        resumeSeq = seqAfterUpload - 1;
                    else if (deletedMissionSeq == seqAfterUpload)
                        resumeSeq = seqAfterUpload;
                }

                int newTotal = fullMission.Count;
                bool isVtolResume = _currentVehicleType == VehicleType.QuadPlane;
                int maxSafeSeq = isVtolResume ? newTotal - 3 : newTotal - 2;
                int minSafeSeq = isVtolResume ? 3 : 2;
                resumeSeq = Math.Max(minSafeSeq, Math.Min(resumeSeq, maxSafeSeq));
                if (!changeIsDefinitelyAhead)
                {
                    await _mavlinkService.SetCurrentWaypointVerified((ushort)resumeSeq);
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"[REALTIME] ⚡ Пропуск SetCurrentWP — изменение после seq={currentSeq}");
                }

                NotifyMissionProgress(resumeSeq);

                System.Diagnostics.Debug.WriteLine(
                    $"[REALTIME] ✅ Done, seqBefore={seqBeforeUpload} seqAfter={seqAfterUpload} resume={resumeSeq}");
            }
            catch (OperationCanceledException) {  }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[REALTIME] Ошибка: {ex.Message}");
            }
        }

        

        private void AddHomeMarkerToMap(WaypointItem home)
        {

            if (home.Marker != null)
            {
                home.Marker.Shape = null;
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

        private void DataTabButton_Click(object sender, MouseButtonEventArgs e)
        {
            if (_isDataTabActive) return;
            _isDataTabActive = true;

            DataTabBorder.Background = (Brush)FindResource("AcidGreen");
            DataTabText.Foreground = new SolidColorBrush(Color.FromRgb(6, 11, 26));
            MissionTabBorder.Background = Brushes.Transparent;
            MissionTabText.Foreground = new SolidColorBrush(Color.FromRgb(107, 114, 128));

            DataPanel.Visibility = Visibility.Visible;
            MissionPanel.Visibility = Visibility.Collapsed;
        }

        private void MissionTabButton_Click(object sender, MouseButtonEventArgs e)
        {
            if (!_isDataTabActive) return;
            _isDataTabActive = false;

            MissionTabBorder.Background = (Brush)FindResource("AcidGreen");
            MissionTabText.Foreground = new SolidColorBrush(Color.FromRgb(6, 11, 26));
            DataTabBorder.Background = Brushes.Transparent;
            DataTabText.Foreground = new SolidColorBrush(Color.FromRgb(107, 114, 128));

            DataPanel.Visibility = Visibility.Collapsed;
            MissionPanel.Visibility = Visibility.Visible;
        }

        private void DrawCompassTicks()
        {
            if (CompassTickCanvas == null) return;
            CompassTickCanvas.Children.Clear();

            double cx = 75, cy = 75;
            double tickR = 70;

            for (int deg = 0; deg < 360; deg += 10)
            {
                if (deg == 0 || deg == 90 || deg == 180 || deg == 270) continue;

                bool isMajor = (deg % 30 == 0);
                double len = isMajor ? 8 : 4;
                double rad = deg * Math.PI / 180.0;

                var line = new Line
                {
                    X1 = cx + tickR * Math.Sin(rad),
                    Y1 = cy - tickR * Math.Cos(rad),
                    X2 = cx + (tickR - len) * Math.Sin(rad),
                    Y2 = cy - (tickR - len) * Math.Cos(rad),
                    Stroke = isMajor
                        ? new SolidColorBrush(Color.FromRgb(55, 75, 100))
                        : new SolidColorBrush(Color.FromRgb(30, 42, 60)),
                    StrokeThickness = isMajor ? 1.5 : 1
                };
                CompassTickCanvas.Children.Add(line);
            }

            var north = new Line
            {
                X1 = cx,
                Y1 = cy - tickR,
                X2 = cx,
                Y2 = cy - tickR + 9,
                Stroke = new SolidColorBrush(Color.FromRgb(152, 240, 25)),
                StrokeThickness = 2
            };
            CompassTickCanvas.Children.Add(north);
        }

        private void SetHomeButton_Click(object sender, RoutedEventArgs e)
        {

            _isSettingHomeMode = true;
            PlanMap.Cursor = Cursors.Cross;

            AppMessageBox.ShowInfo(
                Get("Msg_ClickMapHome"),
                owner: OwnerWindow,
                subtitle: Get("Msg_HomeSetupSub")
            );
        }

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

        

        private void TakeoffAltitudeBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (double.TryParse(TakeoffAltitudeBox.Text,
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out double alt) && alt > 0)
            {
                if (_currentVehicleType == VehicleType.QuadPlane)
                    _vtolTakeoffAltitude = alt;
                else
                    _takeoffAltitude = alt;
            }
            else
            {
                double current = _currentVehicleType == VehicleType.QuadPlane
                    ? _vtolTakeoffAltitude : _takeoffAltitude;
                TakeoffAltitudeBox.Text = current.ToString("F0");
            }
        }

        private void RtlAltitudeBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (double.TryParse(RtlAltitudeBox.Text,
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out double alt) && alt > 0)
                _rtlAltitude = alt;
            else
                RtlAltitudeBox.Text = _rtlAltitude.ToString("F0");
        }

        private void ZoomSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (PlanMap != null)
            {
                PlanMap.Zoom = e.NewValue;

                RefreshMarkers();
            }
        }

        private void RefreshMarkers()
        {
            if (_waypoints == null || _waypoints.Count == 0 || PlanMap == null) return;

            System.Diagnostics.Debug.WriteLine($" RefreshMarkers: обновляем {_waypoints.Count} меток, текущий zoom={PlanMap.Zoom:F1}");

            foreach (var wp in _waypoints)
            {

                if (wp.ShapeGrid != null && wp.RadiusCircle != null)
                {
                    double radiusInPixels = MetersToPixels(wp.Radius, wp.Latitude, PlanMap.Zoom);

                    System.Diagnostics.Debug.WriteLine($"   WP{wp.Number}: Radius={wp.Radius:F0}м → radiusInPixels = {radiusInPixels:F2}px (zoom={PlanMap.Zoom:F1})");

                    radiusInPixels = Math.Min(5000, radiusInPixels);

                    radiusInPixels = Math.Max(3, radiusInPixels);

                    double diameter = radiusInPixels * 2;

                    System.Diagnostics.Debug.WriteLine($"   WP{wp.Number}: radiusInPixels ПОСЛЕ clamp = {radiusInPixels:F0}px (диаметр: {diameter:F0}px)");

                    wp.ShapeGrid.Width = diameter;
                    wp.ShapeGrid.Height = diameter;

                    wp.RadiusCircle.Width = diameter;
                    wp.RadiusCircle.Height = diameter;

                    if (wp.Marker != null)
                    {
                        wp.Marker.Offset = new Point(-diameter / 2, -diameter / 2);
                    }

                    wp.ShapeGrid.InvalidateVisual();
                    wp.RadiusCircle.InvalidateVisual();
                }
                else
                {

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

            if (_currentVehicleType == VehicleType.QuadPlane)
            {
            }

            UpdateRoute();

            PlanMap.InvalidateVisual();

            System.Diagnostics.Debug.WriteLine($" RefreshMarkers: завершено\n");
        }

        private void MotorTestButton_Click(object sender, RoutedEventArgs e)
        {
            if (_mavlinkService?.IsConnected != true)
            {
                ShowNotConnectedMessage();
                return;
            }

            var dialog = new SimpleDroneGCS.UI.Dialogs.MotorTestDialog(_mavlinkService, _currentVehicleType)
            {
                Owner = OwnerWindow
            };
            dialog.ShowDialog();
        }

        private void UploadButton_Click(object sender, RoutedEventArgs e)
        {
            if (_waypoints == null || _waypoints.Count == 0)
            {
                AppMessageBox.ShowWarning(
                    Get("Msg_EmptyMission"),
                    owner: OwnerWindow,
                    subtitle: Get("Msg_EmptyMissionSub"),
                    hint: Get("Msg_AddPointsHint")
                );
                return;
            }

            try
            {
                System.Diagnostics.Debug.WriteLine($" Начало сохранения миссии: {_waypoints.Count} точек");

                SaveMissionToFile("mission_planned.txt");

                string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                string fullPath = System.IO.Path.Combine(desktopPath, "mission_planned.txt");

                System.Diagnostics.Debug.WriteLine($" Файл сохранён: {fullPath}");

                if (_mavlinkService != null)
                {
                    _mavlinkService.SavePlannedMission(GetFullMission());
                    System.Diagnostics.Debug.WriteLine($" Миссия сохранена в MAVLink");

                    if (_currentVehicleType == VehicleType.QuadPlane && _mavlinkService.IsConnected)
                    {

                        System.Diagnostics.Debug.WriteLine($" VTOL: переходы заданы в миссии явно");
                    }

                    MissionStore.Set((int)_currentVehicleType, GetFullMission());

                    string successMsg = Fmt("Msg_MissionSavedShort", _waypoints.Count);

                    AppMessageBox.ShowSuccess(
                        successMsg,
                        owner: OwnerWindow,
                        subtitle: Get("Msg_MissionSavedSub")
                    );
                }
                else
                {
                    MissionStore.Set((int)_currentVehicleType, GetFullMission());

                    AppMessageBox.ShowSuccess(
                        Fmt("Msg_MissionSavedShort", _waypoints.Count),
                        owner: OwnerWindow,
                        subtitle: Get("Msg_MissionSavedSub")
                    );
                }
            }
            catch (Exception ex)
            {
                AppMessageBox.ShowError(
                    Fmt("Msg_ErrorSave", ex.Message),
                    owner: OwnerWindow,
                    subtitle: Get("Msg_MissionWriteErrorSub"),
                    hint: Get("Msg_CheckFolderAccess")
                );
                System.Diagnostics.Debug.WriteLine($" Ошибка: {ex.Message}\n{ex.StackTrace}");
            }

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
                    Get("Msg_ConnectToReadMission"),
                    owner: OwnerWindow,
                    subtitle: Get("Msg_NoConnectionSub"),
                    hint: Get("Msg_ConnectViaSerialOrUdp")
                );
                return;
            }

            try
            {
                System.Diagnostics.Debug.WriteLine("[DOWNLOAD] Начало чтения миссии с дрона...");

                var items = await _mavlinkService.DownloadMission(15000);

                if (items == null || items.Count == 0)
                {
                    AppMessageBox.ShowWarning(Get("Msg_MissionEmptyDrone"), owner: OwnerWindow, subtitle: Get("Msg_EmptyMissionSub"));
                    return;
                }

                if (_waypoints.Count > 0)
                {
                    if (!AppMessageBox.ShowConfirm(
                        Fmt("Msg_ConfirmReplace", _waypoints.Count, items.Count),
                        owner: OwnerWindow,
                        subtitle: Get("Msg_ReplaceMissionSub")))
                    {
                        return;
                    }
                }

                PlanMap.Markers.Clear();
                _droneMarker = null;
                _navBearingMarker = null;
                _trackMarkers.Clear();
                _waypoints.Clear();
                _homePosition = null;
                _resizeHandles.Clear();
                _isMissionFrozen = false;

                bool isVtolMission = items.Any(it => it.command == 84 || it.command == 85 || it.command == 3000);

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

                    var takeoff = items.FirstOrDefault(it => it.command == 84);
                    if (takeoff.z > 0) _vtolTakeoffAltitude = takeoff.z;

                    var vtolLand = items.FirstOrDefault(it => it.command == 85);
                    if (vtolLand.z > 0) _vtolLandAltitude = vtolLand.z;

                    var navItems = items.Where(it =>
                        it.seq > 0 &&
                        (it.command == 16 || it.command == 17 || it.command == 18 || it.command == 19 ||
                         it.command == 82 || it.command == 93 || it.command == 178 || it.command == 21) &&
                        (it.x != 0 || it.y != 0 || it.command == 93 || it.command == 178)
                    ).ToList();

                    var transitionFw = items.FirstOrDefault(it => it.command == 3000 && it.param1 == 4);
                    var transitionMcList = items.Where(it => it.command == 3000 && it.param1 == 3).ToList();
                    var transitionMc = transitionMcList.Count > 0 ? transitionMcList[transitionMcList.Count - 1] : default;

                    ushort startCircleSeq = 0;
                    ushort landCircleSeq = 0;

                    if (transitionFw.command == 3000)
                    {
                        ushort expectedSeq = (ushort)(transitionFw.seq + 1);
                        var candidate = navItems.FirstOrDefault(it => it.seq == expectedSeq);
                        if (candidate.command == 17 || candidate.command == 18)
                            startCircleSeq = expectedSeq;
                    }

                    if (transitionMc.command == 3000)
                    {
                        ushort expectedSeq = (ushort)(transitionMc.seq - 1);
                        var candidate = navItems.FirstOrDefault(it => it.seq == expectedSeq);
                        if (candidate.command == 17 || candidate.command == 18)
                            landCircleSeq = expectedSeq;
                    }

                    if (startCircleSeq > 0 && startCircleSeq == landCircleSeq)
                        landCircleSeq = 0;

                    foreach (var nav in navItems)
                    {
                        if (nav.seq == startCircleSeq && startCircleSeq > 0)
                        {
                            continue;
                        }

                        if (nav.seq == landCircleSeq && landCircleSeq > 0)
                        {
                            continue;
                        }

                        var wp = new WaypointItem
                        {
                            Number = _waypoints.Count + 1,
                            Latitude = nav.x / 1e7,
                            Longitude = nav.y / 1e7,
                            Altitude = nav.z,
                            Radius = Math.Abs(nav.param3) > 0 ? Math.Abs(nav.param3) : 80,
                            Clockwise = nav.param3 >= 0,
                            CommandType = ConvertMAVCmdToCommandTypeWithParam(nav.command, nav.param1),
                            AutoNext = nav.autocontinue == 1,
                            Delay = nav.command == 178 ? 0 : (nav.command == 16 || nav.command == 93 || nav.command == 19 || nav.command == 82) ? nav.param1 : 0,
                            Speed = nav.command == 178 ? nav.param2 : 10,
                            LoiterTurns = (nav.command == 18) ? (int)nav.param1 : 0
                        };
                        _waypoints.Add(wp);
                        AddMarkerToMap(wp);
                    }
                }
                else
                {

                    var takeoff = items.FirstOrDefault(it => it.command == 22);
                    if (takeoff.z > 0) _takeoffAltitude = takeoff.z;

                    var rtlItem = items.FirstOrDefault(it => it.command == 20);
                    if (rtlItem.z > 0) _rtlAltitude = rtlItem.z;

                    var navItems = items.Where(it =>
                        it.seq > 0 &&
                        it.command != 22 && it.command != 20 &&
                        (it.command == 16 || it.command == 17 || it.command == 18 || it.command == 19 ||
                         it.command == 82 || it.command == 93 || it.command == 178 || it.command == 21) &&
                        (it.x != 0 || it.y != 0 || it.command == 93 || it.command == 178)
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
                            Delay = nav.command == 178 ? 0 : (nav.command == 16 || nav.command == 93 || nav.command == 19 || nav.command == 82) ? nav.param1 : 0,
                            Speed = nav.command == 178 ? nav.param2 : 10,
                            LoiterTurns = (nav.command == 18) ? (int)nav.param1 : 0
                        };
                        _waypoints.Add(wp);
                        AddMarkerToMap(wp);
                    }
                }

                UpdateRoute();
                UpdateWaypointsList();
                UpdateStatistics();

                string typeStr = isVtolMission ? "VTOL" : "Copter";
                AppMessageBox.ShowSuccess(
                    Fmt("Msg_MissionReadDrone", _waypoints.Count, typeStr),
                    owner: OwnerWindow,
                    subtitle: Get("Msg_MissionLoadedSub")
                );

                System.Diagnostics.Debug.WriteLine($"[DOWNLOAD] ✅ Миссия прочитана: {_waypoints.Count} WPs, VTOL={isVtolMission}");
            }
            catch (Exception ex)
            {
                AppMessageBox.ShowError(Fmt("Msg_ErrorRead", ex.Message), owner: OwnerWindow, subtitle: Get("Msg_ErrorSub"));
                System.Diagnostics.Debug.WriteLine($"[DOWNLOAD] Ошибка: {ex.Message}\n{ex.StackTrace}");
            }
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            if (_waypoints == null || _waypoints.Count == 0)
            {
                AppMessageBox.ShowWarning(Get("Msg_EmptyMission"), owner: OwnerWindow, subtitle: Get("Msg_EmptyMissionSub"));
                return;
            }

            try
            {
                var dlg = new Microsoft.Win32.SaveFileDialog
                {
                    Title = Get("Dlg_SaveMission"),
                    Filter = "Mission files (*.txt;*.waypoints)|*.txt;*.waypoints|All files (*.*)|*.*",
                    DefaultExt = ".txt",
                    FileName = $"mission_{DateTime.Now:yyyyMMdd_HHmm}.txt"
                };

                if (dlg.ShowDialog() != true) return;

                SaveMissionToPath(dlg.FileName);

                AppMessageBox.ShowSuccess(
                    Fmt("Msg_MissionSaved", _waypoints.Count, System.IO.Path.GetFileName(dlg.FileName)),
                    owner: OwnerWindow,
                    subtitle: Get("Msg_FileSavedSub")
                );
            }
            catch (Exception ex)
            {
                AppMessageBox.ShowError(Fmt("Msg_ErrorSave", ex.Message), owner: OwnerWindow, subtitle: Get("Msg_ErrorSub"));
            }
        }

        private void LoadButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Title = Get("Dlg_LoadMission"),
                    Filter = "Mission files (*.txt;*.waypoints)|*.txt;*.waypoints|All files (*.*)|*.*"
                };

                if (dlg.ShowDialog() != true) return;

                var lines = System.IO.File.ReadAllLines(dlg.FileName);
                if (lines.Length < 2 || !lines[0].StartsWith("QGC WPL"))
                {
                    AppMessageBox.ShowError(Get("Msg_BadFormat"), owner: OwnerWindow, subtitle: Get("Msg_BadFormatSub"));
                    return;
                }

                PlanMap.Markers.Clear();
                _droneMarker = null;
                _navBearingMarker = null;
                _trackMarkers.Clear();
                _waypoints.Clear();
                _homePosition = null;
                _resizeHandles.Clear();
                _isMissionFrozen = false;

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

                bool isVtolMission = parsedItems.Any(p => p.cmd == 84 || p.cmd == 85 || p.cmd == 3000);

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

                    var takeoff = parsedItems.FirstOrDefault(p => p.cmd == 84);
                    if (takeoff.alt > 0) _vtolTakeoffAltitude = takeoff.alt;

                    var vtolLand = parsedItems.FirstOrDefault(p => p.cmd == 85);
                    if (vtolLand.alt > 0) _vtolLandAltitude = vtolLand.alt;

                    var navItems = parsedItems.Where(p =>
                        p.seq > 0 &&
                        (p.cmd == 16 || p.cmd == 17 || p.cmd == 18 || p.cmd == 19 ||
                         p.cmd == 82 || p.cmd == 93 || p.cmd == 178 || p.cmd == 21) &&
                        (p.lat != 0 || p.lon != 0 || p.cmd == 93 || p.cmd == 178)
                    ).ToList();

                    var transFw = parsedItems.FirstOrDefault(p => p.cmd == 3000 && p.p1 == 4);
                    var transMcList = parsedItems.Where(p => p.cmd == 3000 && p.p1 == 3).ToList();
                    var transMc = transMcList.Count > 0 ? transMcList[transMcList.Count - 1] : default;


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
                            Delay = nav.cmd == 178 ? 0 : (nav.cmd == 16 || nav.cmd == 93 || nav.cmd == 19 || nav.cmd == 82) ? nav.p1 : 0,
                            Speed = nav.cmd == 178 ? nav.p2 : 10,
                            LoiterTurns = (nav.cmd == 18) ? (int)nav.p1 : 0
                        };
                        _waypoints.Add(wp);
                        AddMarkerToMap(wp);
                    }
                }
                else
                {

                    var takeoff = parsedItems.FirstOrDefault(p => p.cmd == 22);
                    if (takeoff.alt > 0) _takeoffAltitude = takeoff.alt;

                    var rtlItem = parsedItems.FirstOrDefault(p => p.cmd == 20);
                    if (rtlItem.alt > 0) _rtlAltitude = rtlItem.alt;

                    var navItems = parsedItems.Where(p =>
                        p.seq > 0 &&
                        p.cmd != 22 && p.cmd != 20 &&
                        (p.cmd == 16 || p.cmd == 17 || p.cmd == 18 || p.cmd == 19 ||
                         p.cmd == 82 || p.cmd == 93 || p.cmd == 178 || p.cmd == 21) &&
                        (p.lat != 0 || p.lon != 0 || p.cmd == 93 || p.cmd == 178)
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
                            Delay = nav.cmd == 178 ? 0 : (nav.cmd == 16 || nav.cmd == 93 || nav.cmd == 19 || nav.cmd == 82) ? nav.p1 : 0,
                            Speed = nav.cmd == 178 ? nav.p2 : 10,
                            LoiterTurns = (nav.cmd == 18) ? (int)nav.p1 : 0
                        };
                        _waypoints.Add(wp);
                        AddMarkerToMap(wp);
                    }
                }

                UpdateRoute();
                UpdateWaypointsList();
                UpdateStatistics();

                string typeStr = isVtolMission ? "VTOL" : "Copter";
                AppMessageBox.ShowSuccess(
                    Fmt("Msg_MissionLoaded", _waypoints.Count, typeStr, System.IO.Path.GetFileName(dlg.FileName)),
                    owner: OwnerWindow,
                    subtitle: Get("Msg_MissionLoadedSub")
                );

                System.Diagnostics.Debug.WriteLine($"[LOAD] Миссия загружена из {dlg.FileName}: {_waypoints.Count} WPs, VTOL={isVtolMission}");
            }
            catch (Exception ex)
            {
                AppMessageBox.ShowError(Fmt("Msg_ErrorLoad", ex.Message), owner: OwnerWindow, subtitle: Get("Msg_ErrorSub"));
                System.Diagnostics.Debug.WriteLine($"[LOAD] Ошибка: {ex.Message}\n{ex.StackTrace}");
            }
        }

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

        
        private string ConvertMAVCmdToCommandTypeWithParam(ushort cmd, double param1)
        {
            if (cmd == 3000)
                return (param1 == 3) ? "VTOL_TRANSITION_MC" : "VTOL_TRANSITION_FW";
            return ConvertMAVCmdToCommandType(cmd);
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            if (AppMessageBox.ShowConfirm(
                Get("Msg_ClearAllWaypoints"),
                owner: OwnerWindow,
                subtitle: Get("Msg_ConfirmClearSub"),
                hint: Get("Msg_ActionIrreversible")
            ))
            {

                PlanMap.Markers.Clear();
                _droneMarker = null;
                _navBearingMarker = null;
                _trackMarkers.Clear();

                _waypoints.Clear();

                _homePosition = null;

                _resizeHandles.Clear();

                _isMissionFrozen = false;

                UpdateWaypointsList();
                UpdateStatistics();

                System.Diagnostics.Debug.WriteLine("Все waypoints, HOME, S/L удалены");
            }
        }

        private void SaveMissionToFile(string filename)
        {
            string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            string fullPath = System.IO.Path.Combine(desktopPath, filename);
            SaveMissionToPath(fullPath);
        }

        private void SaveMissionToPath(string fullPath)
        {
            System.Diagnostics.Debug.WriteLine($" Сохранение миссии в: {fullPath}");

            var lines = new List<string>();
            lines.Add("QGC WPL 110");

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

                var inv = System.Globalization.CultureInfo.InvariantCulture;
                lines.Add(string.Format(inv, "{0}\t{1}\t{2}\t{3}\t{4}\t{5}\t{6}\t{7}\t{8:F7}\t{9:F7}\t{10:F2}\t{11}",
                    i, current, frame, mavCmd, p1, p2, p3, p4, wp.Latitude, wp.Longitude, wp.Altitude, autoContinue));
            }

            System.IO.File.WriteAllLines(fullPath, lines);

            System.Diagnostics.Debug.WriteLine($" Миссия сохранена в {fullPath}");
            System.Diagnostics.Debug.WriteLine($"   Всего строк: {lines.Count}");
        }

        private (double p1, double p2, double p3, double p4) GetCommandParams(WaypointItem wp)
        {

            double signedRadius = wp.Clockwise ? Math.Abs(wp.Radius) : -Math.Abs(wp.Radius);

            switch (wp.CommandType)
            {
                case "VTOL_TRANSITION_FW":
                    return (4, 0, 0, 0);
                case "VTOL_TRANSITION_MC":
                    return (3, 0, 0, 0);
                case "LOITER_TIME":
                    return (wp.Delay, 0, signedRadius, 0);
                case "LOITER_TURNS":
                    return (wp.LoiterTurns, 0, signedRadius, 0);
                case "LOITER_UNLIM":
                    return (0, 0, signedRadius, 0);
                case "CHANGE_SPEED":
                    return (1, wp.Speed, -1, 0);
                case "SPLINE_WP":
                    return (wp.Delay, 0, 0, 0);
                case "WAYPOINT":
                    return (wp.Delay, 0, 0, 0);
                case "DELAY":
                    return (wp.Delay, -1, -1, -1);
                default:
                    return (wp.Delay, 0, 0, 0);
            }
        }

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
                case "HOME": result = 16; break;
                default:
                    System.Diagnostics.Debug.WriteLine($"⚠️ Неизвестный тип команды: '{commandType}', использую WAYPOINT");
                    result = 16;
                    break;
            }

            return result;
        }

        private void PlanMap_MouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
        {
            if (PlanMap == null) return;

            double newZoom = PlanMap.Zoom + (e.Delta > 0 ? 1 : -1);

            if (newZoom >= PlanMap.MinZoom && newZoom <= PlanMap.MaxZoom)
            {
                PlanMap.Zoom = newZoom;

                if (ZoomSlider != null)
                {
                    ZoomSlider.Value = newZoom;
                }

                System.Diagnostics.Debug.WriteLine($" Plan Map Zoom: {newZoom}");
            }

            e.Handled = true;
        }

        private GMapMarker CreateDroneMarker(PointLatLng position)
        {
            var grid = new Grid
            {
                Width = 4000,
                Height = 4000
            };

            var headingLine = new Line
            {
                X1 = 2000,
                Y1 = 2000,
                X2 = 2000,
                Y2 = 0,
                Stroke = new SolidColorBrush(Color.FromRgb(239, 68, 68)),
                StrokeThickness = 2,
                StrokeEndLineCap = PenLineCap.Triangle,
                Name = "HeadingLine"
            };
            var gpsTrackLine = new Line
            {
                X1 = 2000,
                Y1 = 2000,
                X2 = 2000,
                Y2 = 0,
                Stroke = new SolidColorBrush(Color.FromRgb(20, 20, 20)),
                StrokeThickness = 2,
                StrokeEndLineCap = PenLineCap.Triangle,
                Name = "GpsTrackLine"
            };

            var droneIcon = new Image
            {
                Source = new System.Windows.Media.Imaging.BitmapImage(
                    new Uri("pack://application:,,,/Images/pl.png")),
                Width = 48,
                Height = 48,
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

            grid.Children.Add(gpsTrackLine);
            grid.Children.Add(headingLine);
            grid.Children.Add(droneIcon);

            var marker = new GMapMarker(position)
            {
                Shape = grid,
                Offset = new Point(-2000, -2000),
                ZIndex = 1000,
                Tag = grid
            };

            return marker;
        }

        private void UpdateNavBearingLine(PointLatLng dronePos, Telemetry telemetry)
        {
            if (!telemetry.HasNavBearing || _mavlinkService?.ActiveMission == null)
            {
                RemoveNavBearingMarker();
                return;
            }
            int seq = telemetry.CurrentWaypoint;
            var mission = _mavlinkService.ActiveMission;
            if (seq <= 0 || seq >= mission.Count)
            {
                RemoveNavBearingMarker();
                return;
            }

            var targetWp = mission[seq];
            if (targetWp.Latitude == 0 && targetWp.Longitude == 0)
            {
                RemoveNavBearingMarker();
                return;
            }
            var droneScreen = PlanMap.FromLatLngToLocal(dronePos);
            var wpScreen = PlanMap.FromLatLngToLocal(
                new PointLatLng(targetWp.Latitude, targetWp.Longitude));
            double dx = wpScreen.X - droneScreen.X;
            double dy = wpScreen.Y - droneScreen.Y;

            if (_navBearingMarker == null)
            {
                var canvas = new System.Windows.Controls.Canvas
                {
                    Width = 4000,
                    Height = 4000,
                    IsHitTestVisible = false
                };
                var line = new Line
                {
                    X1 = 2000,
                    Y1 = 2000,
                    X2 = 2000 + dx,
                    Y2 = 2000 + dy,
                    Stroke = new SolidColorBrush(Color.FromRgb(251, 146, 60)),
                    StrokeThickness = 2,
                    StrokeDashArray = new System.Windows.Media.DoubleCollection { 6, 3 },
                    StrokeEndLineCap = PenLineCap.Triangle
                };
                canvas.Children.Add(line);

                _navBearingMarker = new GMapMarker(dronePos)
                {
                    Shape = canvas,
                    Offset = new Point(-2000, -2000),
                    ZIndex = 990
                };
                PlanMap.Markers.Add(_navBearingMarker);
            }
            else
            {
                _navBearingMarker.Position = dronePos;
                if (_navBearingMarker.Shape is System.Windows.Controls.Canvas cv)
                {
                    var line = cv.Children.OfType<Line>().FirstOrDefault();
                    if (line != null) { line.X2 = 2000 + dx; line.Y2 = 2000 + dy; }
                }
            }
        }

        private void RemoveNavBearingMarker()
        {
            if (_navBearingMarker == null) return;
            PlanMap.Markers.Remove(_navBearingMarker);
            _navBearingMarker = null;
        }

        private void AddTrackPoint(PointLatLng pos)
        {
            const int MAX_TRACK = 500;
            if (_trackMarkers.Count >= MAX_TRACK)
            {
                PlanMap.Markers.Remove(_trackMarkers[0]);
                _trackMarkers.RemoveAt(0);
            }

            var dot = new System.Windows.Shapes.Ellipse
            {
                Width = 5,
                Height = 5,
                Fill = new SolidColorBrush(Color.FromRgb(168, 85, 247)),
                Opacity = 0.85
            };
            var marker = new GMapMarker(pos)
            {
                Shape = dot,
                Offset = new Point(-2, -2),
                ZIndex = 50
            };
            PlanMap.Markers.Add(marker);
            _trackMarkers.Add(marker);
        }

        private void ClearTrack()
        {
            foreach (var m in _trackMarkers)
                PlanMap.Markers.Remove(m);
            _trackMarkers.Clear();
        }

        private void UpdateDronePosition(object sender, EventArgs e)
        {
            if (_mavlinkService == null || PlanMap == null) return;
            if (!_mavlinkService.IsConnected) return;

            var telemetry = _mavlinkService.CurrentTelemetry;

            if (telemetry.Armed && !_wasArmed)
            {

                if (!_homeLockedFromArm && telemetry.Latitude != 0 && telemetry.Longitude != 0 && telemetry.GpsFixType >= 2 && telemetry.RelativeAltitude < 2.0)
                {
                    SetHomeFromDronePosition(telemetry.Latitude, telemetry.Longitude);
                    _homeLockedFromArm = true;
                }
                _wasArmed = true;
            }
            else if (!telemetry.Armed)
            {
                _wasArmed = false;
                _homeLockedFromArm = false;
            }

            if (telemetry.Latitude != 0 && telemetry.Longitude != 0)
            {
                var dronePosition = new PointLatLng(telemetry.Latitude, telemetry.Longitude);

                if (_droneMarker == null)
                {
                    _droneMarker = CreateDroneMarker(dronePosition);
                    PlanMap.Markers.Add(_droneMarker);

                    if (_droneMarker.Tag is Grid grid)
                        grid.RenderTransform = new RotateTransform(telemetry.Heading, 2000, 2000);
                }
                else
                {
                    _droneMarker.Position = dronePosition;

                    if (_droneMarker.Tag is Grid grid)
                        grid.RenderTransform = new RotateTransform(telemetry.Heading, 2000, 2000);
                }
                if (_droneMarker.Tag is Grid dGrid)
                {
                    var trackLine = dGrid.Children.OfType<Line>()
                        .FirstOrDefault(l => l.Name == "GpsTrackLine");
                    if (trackLine != null && telemetry.Speed > 0.5)
                    {
                        double relAngle = (telemetry.GpsTrack - telemetry.Heading) * Math.PI / 180.0;
                        trackLine.X2 = 2000 + Math.Sin(relAngle) * 500;
                        trackLine.Y2 = 2000 - Math.Cos(relAngle) * 500;
                    }
                }
                UpdateNavBearingLine(dronePosition, telemetry);
                if (telemetry.Armed)
                {
                    if ((DateTime.Now - _lastTrackTime).TotalSeconds >= 1.0)
                    {
                        AddTrackPoint(dronePosition);
                        _lastTrackTime = DateTime.Now;
                    }
                }
                else if (_trackMarkers.Count > 0)
                {
                    ClearTrack();
                }
            }
            else if (_droneMarker != null && !_mavlinkService.IsConnected)
            {
                _droneMarker.Shape = null;
                PlanMap.Markers.Remove(_droneMarker);
                _droneMarker = null;
                ClearTrack();
                RemoveNavBearingMarker();
                _homeLockedFromArm = false;
                _lastDisplayedArmed = null;
                _lastNextBtnVisible = null;
                _lastDisplayedMode = null;
                _lastGpsFixType = 255;
                _lastSpeedLabelType = null;
            }
            UpdateDroneInfoPanel(telemetry);
        }

        private void UpdateDroneInfoPanel(Telemetry telemetry)
        {

            if (telemetry.Latitude != 0 || telemetry.Longitude != 0)
            {
                PlanDroneLatText.Text = telemetry.Latitude.ToString("F6");
                PlanDroneLonText.Text = telemetry.Longitude.ToString("F6");
            }

            PlanHeadingRotation.Angle = telemetry.Heading;
            PlanHeadingText.Text = $"{telemetry.Heading:F0}°";

            if (_mavlinkService.HasHomePosition)
            {

                PlanHomeLatText.Text = _mavlinkService.HomeLat.Value.ToString("F6");
                PlanHomeLonText.Text = _mavlinkService.HomeLon.Value.ToString("F6");
            }
            else if (_homePosition != null)
            {

                PlanHomeLatText.Text = _homePosition.Latitude.ToString("F6");
                PlanHomeLonText.Text = _homePosition.Longitude.ToString("F6");
            }
            else
            {

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

        private void SetHomeFromDronePosition(double lat, double lon)
        {

            if (_homePosition?.Marker != null)
            {
                _homePosition.Marker.Shape = null;
                PlanMap.Markers.Remove(_homePosition.Marker);
            }

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

            _mavlinkService?.SendSetHome(useCurrentLocation: true);

            UpdateRoute();

            MissionStore.SetHome((int)_currentVehicleType, _homePosition);

            System.Diagnostics.Debug.WriteLine($"[HOME] Автоматически установлен при армировании: {lat:F6}, {lon:F6}");
        }

        public List<WaypointItem> GetFullMission()
        {
            var mission = new List<WaypointItem>();
            bool isVTOL = _currentVehicleType == VehicleType.QuadPlane;

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

                mission.Add(new WaypointItem
                {
                    Number = mission.Count,
                    Latitude = _homePosition?.Latitude ?? 0,
                    Longitude = _homePosition?.Longitude ?? 0,
                    Altitude = _vtolTakeoffAltitude,
                    CommandType = "VTOL_TAKEOFF"
                });

                mission.Add(new WaypointItem
                {
                    Number = mission.Count,
                    Latitude = 0,
                    Longitude = 0,
                    Altitude = 0,
                    CommandType = "VTOL_TRANSITION_FW"
                });

                foreach (var wp in _waypoints)
                {
                    string cmdType = (wp.CommandType == "LAND") ? "VTOL_LAND" : wp.CommandType;
                    mission.Add(new WaypointItem
                    {
                        Number = mission.Count,
                        Latitude = wp.Latitude,
                        Longitude = wp.Longitude,
                        Altitude = wp.Altitude,
                        CommandType = cmdType,
                        Delay = wp.Delay,
                        Speed = wp.Speed,
                        Radius = wp.Radius,
                        Clockwise = wp.Clockwise,
                        AutoNext = wp.AutoNext,
                        LoiterTurns = wp.LoiterTurns
                    });
                }

                mission.Add(new WaypointItem
                {
                    Number = mission.Count,
                    Latitude = 0,
                    Longitude = 0,
                    Altitude = _vtolLandAltitude > 0 ? _vtolLandAltitude : 30,
                    CommandType = "VTOL_TRANSITION_MC"
                });

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

                if (_homePosition != null)
                {
                    mission.Add(new WaypointItem
                    {
                        Number = mission.Count,
                        Latitude = 0,
                        Longitude = 0,
                        Altitude = _takeoffAltitude,
                        CommandType = "TAKEOFF"
                    });
                }

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
                        Speed = wp.Speed,
                        Radius = wp.Radius,
                        Clockwise = wp.Clockwise,
                        AutoNext = wp.AutoNext,
                        LoiterTurns = wp.LoiterTurns
                    });
                }

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

        private void SaveCurrentMissionForType()
        {

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
                    Speed = wp.Speed,
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

        private void LoadMissionForType(VehicleType type)
        {
            System.Diagnostics.Debug.WriteLine($"[LoadMission] Загрузка миссии для {type}...");

            foreach (var wp in _waypoints)
            {
                if (wp.Marker != null)
                {
                    wp.Marker.Shape = null;
                    PlanMap.Markers.Remove(wp.Marker);
                    wp.Marker = null;
                }
            }

            foreach (var handle in _resizeHandles.Values)
            {
                handle.Shape = null;
                PlanMap.Markers.Remove(handle);
            }
            _resizeHandles.Clear();

            if (_homePosition?.Marker != null)
            {
                _homePosition.Marker.Shape = null;
                PlanMap.Markers.Remove(_homePosition.Marker);
                _homePosition.Marker = null;
            }

            var oldRoutes = PlanMap.Markers.OfType<GMapRoute>().ToList();
            foreach (var r in oldRoutes)
            {
                r.Shape = null;
                PlanMap.Markers.Remove(r);
            }

            var tempCollection = _waypoints;
            _waypoints = new ObservableCollection<WaypointItem>();
            tempCollection.Clear();

            _waypoints.CollectionChanged += (s, e) =>
            {
                UpdateStatistics();
                UpdateWaypointsList();
            };

            _homePosition = null;

            var vtolMarkers = PlanMap.Markers
                .Where(m => m.Tag?.ToString()?.StartsWith("vtol_") == true)
                .ToList();
            foreach (var m in vtolMarkers) { m.Shape = null; PlanMap.Markers.Remove(m); }

            System.Diagnostics.Debug.WriteLine($"[LoadMission] Карта очищена. Загружаем тип {type}...");

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
                        Speed = wp.Speed,
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



            UpdateRoute();
            UpdateStatistics();
            UpdateWaypointsList();

            System.Diagnostics.Debug.WriteLine($"[LoadMission] Завершено: {_waypoints.Count} точек, HOME: {_homePosition != null}");
        }

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
                    Fmt("Msg_VehicleMenuError", ex.Message),
                    owner: OwnerWindow,
                    subtitle: Get("Msg_ErrorSub"),
                    hint: Get("Msg_RetryCheckLogs")
                );
            }
        }

        private ContextMenu BuildVehicleTypeMenu()
        {
            var darkBg = (SolidColorBrush)new BrushConverter().ConvertFromString("#0D1733");
            var borderColor = (SolidColorBrush)new BrushConverter().ConvertFromString("#2A4361");

            var menu = new ContextMenu
            {
                Background = darkBg,
                BorderBrush = borderColor,
                BorderThickness = new Thickness(2),
                Padding = new Thickness(6),
                HasDropShadow = true
            };

            var ctxTemplate = new ControlTemplate(typeof(ContextMenu));
            var outerBorder = new FrameworkElementFactory(typeof(Border));
            outerBorder.SetValue(Border.BackgroundProperty, darkBg);
            outerBorder.SetValue(Border.BorderBrushProperty, borderColor);
            outerBorder.SetValue(Border.BorderThicknessProperty, new Thickness(2));
            outerBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
            outerBorder.SetValue(Border.PaddingProperty, new Thickness(4));
            var itemsPresenter = new FrameworkElementFactory(typeof(ItemsPresenter));
            outerBorder.AppendChild(itemsPresenter);
            ctxTemplate.VisualTree = outerBorder;
            menu.Template = ctxTemplate;

            var itemStyle = new Style(typeof(MenuItem));
            itemStyle.Setters.Add(new Setter(MenuItem.ForegroundProperty, Brushes.White));
            itemStyle.Setters.Add(new Setter(MenuItem.BackgroundProperty, Brushes.Transparent));
            itemStyle.Setters.Add(new Setter(MenuItem.PaddingProperty, new Thickness(10, 8, 10, 8)));
            itemStyle.Setters.Add(new Setter(MenuItem.CursorProperty, Cursors.Hand));
            itemStyle.Setters.Add(new Setter(MenuItem.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));

            var menuTemplate = new ControlTemplate(typeof(MenuItem));
            var borderFactory = new FrameworkElementFactory(typeof(Border));
            borderFactory.Name = "menuBorder";
            borderFactory.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            borderFactory.SetValue(Border.PaddingProperty, new Thickness(10, 8, 10, 8));
            var contentFactory = new FrameworkElementFactory(typeof(ContentPresenter));
            contentFactory.SetValue(ContentPresenter.ContentSourceProperty, "Header");
            contentFactory.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            borderFactory.AppendChild(contentFactory);
            menuTemplate.VisualTree = borderFactory;
            var highlightTrigger = new Trigger { Property = MenuItem.IsHighlightedProperty, Value = true };
            highlightTrigger.Setters.Add(new Setter(Border.BackgroundProperty,
                (SolidColorBrush)new BrushConverter().ConvertFromString("#1A2433"), "menuBorder"));
            menuTemplate.Triggers.Add(highlightTrigger);
            itemStyle.Setters.Add(new Setter(MenuItem.TemplateProperty, menuTemplate));

            menu.Resources[typeof(MenuItem)] = itemStyle;

            var copter = new MenuItem
            {
                Header = BuildVehicleMenuHeader("/Images/drone_icon.png", Get("Vehicle_Multicopter"), "MC"),
                Tag = VehicleType.Copter
            };
            copter.Click += VehicleTypeMenuItem_Click;

            var vtol = new MenuItem
            {
                Header = BuildVehicleMenuHeader("/Images/pl.png", Get("Vehicle_VTOL"), "VTOL"),
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
                Get("Msg_SwitchVehicleConfirm"),
                owner: OwnerWindow,
                subtitle: Get("Msg_VehicleChangeSub")
            );

            if (!ok) return;

            try
            {

                SaveCurrentMissionForType();

                VehicleManager.Instance.SetVehicleType(newType);
                _currentVehicleType = newType;

                if (_mavlinkService != null)
                {
                    var mavType = (byte)VehicleManager.Instance.CurrentProfile.MavType;
                    _mavlinkService.SetVehicleType(mavType);
                }

                LoadMissionForType(newType);

                UpdateVehicleTypeDisplay();
            }
            catch (Exception ex)
            {
                AppMessageBox.ShowError(
                    $"{Get("MsgBox_Error")}: {ex.Message}",
                    owner: OwnerWindow,
                    subtitle: Get("Msg_SwitchErrorSub")
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
                    VehicleTypeFullText.SetResourceReference(TextBlock.TextProperty, profile.Type == VehicleType.Copter ? "Vehicle_Multicopter" : "Vehicle_VTOL");

                if (profile.Type == VehicleType.QuadPlane)
                {
                    if (TakeoffLabel != null) TakeoffLabel.SetResourceReference(TextBlock.TextProperty, "VTOL_Takeoff");
                    if (RtlLabel != null) RtlLabel.SetResourceReference(TextBlock.TextProperty, "VTOL_Landing");
                }
                else
                {
                    if (TakeoffLabel != null) TakeoffLabel.SetResourceReference(TextBlock.TextProperty, "Copter_Takeoff");
                    if (RtlLabel != null) RtlLabel.SetResourceReference(TextBlock.TextProperty, "Copter_RTL");
                }

                UpdateQuickModeButtons();
                if (TakeoffAltitudeBox != null)
                    TakeoffAltitudeBox.Text = (_currentVehicleType == VehicleType.QuadPlane
                        ? _vtolTakeoffAltitude : _takeoffAltitude).ToString("F0");

                UpdateWaypointsList();
            }
            catch
            {
                if (VehicleTypeShortText != null) VehicleTypeShortText.Text = "MC";
                if (VehicleTypeFullText != null) VehicleTypeFullText.SetResourceReference(TextBlock.TextProperty, "Vehicle_Multicopter");
            }
        }

        private void UpdateQuickModeButtons()
        {
            if (_currentVehicleType == VehicleType.QuadPlane)
            {

                if (QuickModeBtn1 != null) { QuickModeBtn1.SetResourceReference(ContentControl.ContentProperty, "Mode_QHold"); QuickModeBtn1.Tag = "QLOITER"; }
                if (QuickModeBtn2 != null) { QuickModeBtn2.SetResourceReference(ContentControl.ContentProperty, "Mode_QAltHold"); QuickModeBtn2.Tag = "QHOVER"; }
                if (QuickModeBtn4 != null) { QuickModeBtn4.SetResourceReference(ContentControl.ContentProperty, "Mode_QStabilize"); QuickModeBtn4.Tag = "QSTABILIZE"; }
                if (QuickModeBtn5 != null) { QuickModeBtn5.SetResourceReference(ContentControl.ContentProperty, "Mode_Home"); QuickModeBtn5.Tag = "QRTL"; }
            }
            else
            {

                if (QuickModeBtn1 != null) { QuickModeBtn1.SetResourceReference(ContentControl.ContentProperty, "Mode_Hold"); QuickModeBtn1.Tag = "LOITER"; }
                if (QuickModeBtn2 != null) { QuickModeBtn2.SetResourceReference(ContentControl.ContentProperty, "Mode_AltHold"); QuickModeBtn2.Tag = "ALT_HOLD"; }
                if (QuickModeBtn4 != null) { QuickModeBtn4.SetResourceReference(ContentControl.ContentProperty, "Mode_Stabilize"); QuickModeBtn4.Tag = "STABILIZE"; }
                if (QuickModeBtn5 != null) { QuickModeBtn5.SetResourceReference(ContentControl.ContentProperty, "Mode_Home"); QuickModeBtn5.Tag = "RTL"; }
            }
        }

        private void DownloadSRTM_Click(object sender, RoutedEventArgs e)
        {

            var center = PlanMap.Position;

            var dialog = new UI.Dialogs.SRTMDownloadDialog(center.Lat, center.Lng);
            dialog.Owner = Window.GetWindow(this);
            dialog.ShowDialog();
        }

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

        private void PlanMap_OnMapDrag()
        {
            if (_droneMarker != null && _mavlinkService?.IsConnected == true)
            {
                var telem = _mavlinkService.CurrentTelemetry;
                if (telem != null && telem.Latitude != 0)
                    UpdateNavBearingLine(
                        new PointLatLng(telem.Latitude, telem.Longitude), telem);
            }
        }

        private void PlanMap_OnMapZoomChanged()
        {
            if (_droneMarker != null && _mavlinkService?.IsConnected == true)
            {
                var telem = _mavlinkService.CurrentTelemetry;
                if (telem != null && telem.Latitude != 0)
                    UpdateNavBearingLine(
                        new PointLatLng(telem.Latitude, telem.Longitude), telem);
            }

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

            if (_currentVehicleType == VehicleType.QuadPlane)
            {
            }
        }

        private void PlanMap_MouseMove(object sender, MouseEventArgs e)
        {

            if (_isRadiusDragging && _radiusDragWaypoint != null)
            {
                var dragPoint = e.GetPosition(PlanMap);
                var dragLatLng = PlanMap.FromLocalToLatLng((int)dragPoint.X, (int)dragPoint.Y);

                double newRadius = CalculateDistanceLatLng(
                    _radiusDragWaypoint.Latitude, _radiusDragWaypoint.Longitude,
                    dragLatLng.Lat, dragLatLng.Lng);

                double minRadius = GetMinRadius();
                newRadius = Math.Max(minRadius, Math.Min(500, newRadius));

                _radiusDragWaypoint.Radius = newRadius;

                UpdateWaypointRadiusVisual(_radiusDragWaypoint);

                UpdateRadiusTooltip(dragLatLng, newRadius);

                return;
            }

            var point = e.GetPosition(PlanMap);
            var cursorLatLng = PlanMap.FromLocalToLatLng((int)point.X, (int)point.Y);

            if (CursorLatText != null)
                CursorLatText.Text = cursorLatLng.Lat.ToString("F6");

            if (CursorLngText != null)
                CursorLngText.Text = cursorLatLng.Lng.ToString("F6");

            if (CursorAltText != null)
            {
                double? elevation = _elevationProvider.GetElevation(cursorLatLng.Lat, cursorLatLng.Lng);
                CursorAltText.Text = elevation.HasValue ? $"{elevation.Value:F0} м" : "— м";
            }

            if (_waypoints.Count > 0 && CursorDistanceFromLast != null)
            {
                var lastWp = _waypoints[_waypoints.Count - 1];
                double dist = CalculateDistanceLatLng(lastWp.Latitude, lastWp.Longitude,
                                                       cursorLatLng.Lat, cursorLatLng.Lng);
                CursorDistanceFromLast.Text = $"{Get("FromWP_Label")} WP{lastWp.Number}: {FormatDistance(dist)}";
            }
            else if (CursorDistanceFromLast != null)
            {
                CursorDistanceFromLast.Text = $"{Get("FromWP_Label")} WP: —";
            }

            if (_homePosition != null && CursorDistanceFromHome != null)
            {
                double dist = CalculateDistanceLatLng(_homePosition.Latitude, _homePosition.Longitude,
                                                       cursorLatLng.Lat, cursorLatLng.Lng);
                CursorDistanceFromHome.Text = $"{Get("FromHOME_Label")}: {FormatDistance(dist)}";
            }
            else if (CursorDistanceFromHome != null)
            {
                CursorDistanceFromHome.Text = $"{Get("FromHOME_Label")}: —";
            }
        }

        private System.Threading.CancellationTokenSource _hudCts;

        private void OnHudRequested(string message, NotificationType type)
        {
            Dispatcher.BeginInvoke(() => ShowHudBanner(message, type));
        }

        private async void ShowHudBanner(string message, NotificationType type)
        {
            var (bg, fg, border, icon, shadow) = type switch
            {
                NotificationType.Error => ("#CC1C0A0A", "#EF4444", "#7F1D1D", "✗", "#EF4444"),
                NotificationType.Warning => ("#CC1C1400", "#F59E0B", "#78350F", "⚠", "#F59E0B"),
                NotificationType.Success => ("#CC0A1C0A", "#22C55E", "#14532D", "✓", "#22C55E"),
                _ => ("#CC060B1A", "#60A5FA", "#1E3A5F", "●", "#60A5FA"),
            };

            HudBanner.Background = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(bg));
            HudBanner.BorderBrush = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(border));
            HudBanner.BorderThickness = new Thickness(1);
            HudBannerText.Text = message;
            HudBannerText.Foreground = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(fg));
            HudBannerIcon.Text = icon;
            HudBannerIcon.Foreground = HudBannerText.Foreground;

            if (HudBanner.Effect is System.Windows.Media.Effects.DropShadowEffect dse)
                dse.Color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(shadow);

            HudBanner.Visibility = Visibility.Visible;

            _hudCts?.Cancel();
            _hudCts = new System.Threading.CancellationTokenSource();
            var token = _hudCts.Token;

            try
            {
                await System.Threading.Tasks.Task.Delay(4000, token);
                if (!token.IsCancellationRequested)
                    HudBanner.Visibility = Visibility.Collapsed;
            }
            catch (System.Threading.Tasks.TaskCanceledException) { }
        }

        private void ShowNotConnectedMessage()
        {
            AppMessageBox.ShowWarning(Get("Msg_DroneNotConnected"), owner: OwnerWindow);
        }

        private void OnTelemetryReceived(object sender, Telemetry telemetry)
        {

        }

        private void UpdateTelemetryUI(object sender, EventArgs e)
        {
            if (_mavlinkService == null) return;

            var telemetry = _mavlinkService.CurrentTelemetry;

            if (AltitudeValue != null)
                AltitudeValue.Text = $"{telemetry.RelativeAltitude:F1} м";
            if (AltitudeMslValue != null)
                AltitudeMslValue.Text = $"{telemetry.GpsAltitude:F1} м";

            if (SecondarySpeedValue != null && SecondarySpeedLabel != null)
            {
                if (_currentVehicleType == VehicleType.QuadPlane)
                {
                    if (_lastSpeedLabelType != VehicleType.QuadPlane)
                    {
                        SecondarySpeedLabel.SetResourceReference(TextBlock.TextProperty, "Airspeed");
                        SecondarySpeedValue.Foreground = new SolidColorBrush(Color.FromRgb(34, 211, 238));
                        _lastSpeedLabelType = VehicleType.QuadPlane;
                    }
                    SecondarySpeedValue.Text = $"{telemetry.Airspeed:F1} м/с";
                }
                else
                {
                    if (_lastSpeedLabelType != VehicleType.Copter)
                    {
                        SecondarySpeedLabel.SetResourceReference(TextBlock.TextProperty, "VertSpeed");
                        _lastSpeedLabelType = VehicleType.Copter;
                    }
                    string sign = telemetry.ClimbRate >= 0 ? "+" : "";
                    SecondarySpeedValue.Text = $"{sign}{telemetry.ClimbRate:F1} м/с";
                    SecondarySpeedValue.Foreground = new SolidColorBrush(
                        telemetry.ClimbRate > 0.5 ? Color.FromRgb(74, 222, 128) :
                        telemetry.ClimbRate < -0.5 ? Color.FromRgb(251, 146, 60) :
                        Color.FromRgb(156, 163, 175));
                }
            }

            if (SpeedValue != null)
                SpeedValue.Text = $"{telemetry.GroundSpeed:F1} м/с";

            if (AttitudeIndicator != null)
            {
                AttitudeIndicator.Roll = telemetry.Roll;
                AttitudeIndicator.Pitch = telemetry.Pitch;
            }

            UpdateGpsStatus(telemetry);

            if (BatteryVoltage != null)
                BatteryVoltage.Text = $"{telemetry.BatteryVoltage:F1}V";
            if (BatteryPercent != null)
                BatteryPercent.Text = $"{telemetry.BatteryPercent}%";

            if (SatellitesValue != null)
                SatellitesValue.Text = telemetry.SatellitesVisible.ToString();

            if (FlightModeValue != null)
                FlightModeValue.Text = telemetry.FlightMode ?? "UNKNOWN";

            UpdateFlightModeOverlay(telemetry);

            UpdateMotorValues(telemetry);

            UpdateArmButton(telemetry);

            UpdateNextWaypointButtonVisibility(telemetry);

            _notifier.Check(
                telemetry,
                _mavlinkService.IsConnected,
                _mavlinkService.DroneStatus.LastHeartbeat,
                VehicleManager.Instance.CurrentVehicleType);
        }

        private void UpdateGpsStatus(Telemetry telemetry)
        {
            if (GpsIndicator == null || GpsStatusText == null) return;

            byte category = (byte)(telemetry.GpsFixType >= 3 ? 3 : telemetry.GpsFixType >= 2 ? 2 : 0);
            if (_lastGpsFixType == category) return;
            _lastGpsFixType = category;

            if (category >= 3)
            {
                GpsIndicator.Fill = new SolidColorBrush(Color.FromRgb(34, 197, 94));
                GpsStatusText.Text = "GPS OK";
                GpsStatusText.Foreground = new SolidColorBrush(Color.FromRgb(34, 197, 94));
            }
            else if (category >= 2)
            {
                GpsIndicator.Fill = new SolidColorBrush(Color.FromRgb(251, 191, 36));
                GpsStatusText.Text = "GPS 2D";
                GpsStatusText.Foreground = new SolidColorBrush(Color.FromRgb(251, 191, 36));
            }
            else
            {
                GpsIndicator.Fill = new SolidColorBrush(Color.FromRgb(239, 68, 68));
                GpsStatusText.Text = "NO GPS";
                GpsStatusText.Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68));
            }
        }

        private void UpdateMotorValues(Telemetry telemetry)
        {

            if (MultirotorValue != null)
            {
                int avgMotor = (telemetry.Motor1Percent + telemetry.Motor2Percent + telemetry.Motor3Percent + telemetry.Motor4Percent) / 4;
                MultirotorValue.Text = $"{avgMotor}%";
            }
            if (PusherMotorValue != null) PusherMotorValue.Text = $"{telemetry.PusherPercent}%";
        }

        private void UpdateFlightModeOverlay(Telemetry telemetry)
        {
            if (FlightModeOverlay == null || FlightModeOverlayText == null) return;

            string mode = telemetry.FlightMode?.ToUpper() ?? "UNKNOWN";

            if (_lastDisplayedMode == mode) return;
            _lastDisplayedMode = mode;

            FlightModeOverlay.Visibility = Visibility.Visible;
            FlightModeOverlayText.Text = mode;

            Color dotColor = mode switch
            {
                "AUTO" or "GUIDED" or "QRTL" => Color.FromRgb(74, 222, 128),
                "STABILIZE" or "QSTABILIZE" or "QHOVER" or "QLOITER" or "FBWA"
                    => Color.FromRgb(250, 204, 21),
                "LAND" or "QLAND" or "RTL" => Color.FromRgb(251, 146, 60),
                "UNKNOWN" => Color.FromRgb(107, 114, 128),
                _ => Color.FromRgb(96, 165, 250)
            };

            if (FlightModeIndicator != null)
                FlightModeIndicator.Fill = new SolidColorBrush(dotColor);
        }

        private void UpdateArmButton(Telemetry telemetry)
        {
            if (ArmButton == null) return;

            if (_lastDisplayedArmed == telemetry.Armed) return;
            _lastDisplayedArmed = telemetry.Armed;

            if (telemetry.Armed)
            {
                ArmButton.SetResourceReference(ContentControl.ContentProperty, "Disarm");
                ArmButton.Background = new SolidColorBrush(Color.FromRgb(127, 29, 29));
                ArmButton.BorderBrush = new SolidColorBrush(Color.FromRgb(239, 68, 68));
            }
            else
            {
                ArmButton.SetResourceReference(ContentControl.ContentProperty, "Arm");
                ArmButton.Background = new SolidColorBrush(Color.FromRgb(22, 101, 52));
                ArmButton.BorderBrush = new SolidColorBrush(Color.FromRgb(34, 197, 94));
            }
        }

        private void UpdateNextWaypointButtonVisibility(Telemetry telemetry)
        {
            if (NextWaypointBtn == null) return;

            string mode = telemetry.FlightMode?.ToUpper() ?? "";
            bool show = telemetry.Armed && (mode == "AUTO" || mode == "LOITER" || mode == "QLOITER" || mode == "GUIDED");

            if (_lastNextBtnVisible == show) return;
            _lastNextBtnVisible = show;

            NextWaypointBtn.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdateConnectionTimer(object sender, EventArgs e)
        {
            if (_mavlinkService == null || ConnectionTimerText == null) return;

            bool reallyConnected = _mavlinkService.IsConnected
                && _mavlinkService.DroneStatus.LastHeartbeat != DateTime.MinValue;

            if (reallyConnected)
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
                _isMissionFrozen = false;
                ConnectionTimerText.Text = "00:00:00";
                _notifier.Reset();
            }
        }

        private void PopulateFlightModes()
        {
            if (FlightModeCombo == null) return;

            FlightModeCombo.SelectionChanged -= FlightModeCombo_SelectionChanged;
            FlightModeCombo.Items.Clear();
            FlightModeCombo.Items.Add(new ComboBoxItem { Content = Get("FlightModes"), IsSelected = true });

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

        private void ArmButton_Click(object sender, RoutedEventArgs e)
        {
            if (_mavlinkService?.IsConnected != true)
            {
                ShowNotConnectedMessage();
                return;
            }

            bool isArmed = _mavlinkService.CurrentTelemetry.Armed;

            if (isArmed)
            {
                double alt = _mavlinkService.CurrentTelemetry.RelativeAltitude;
                if (alt > 1.0)
                {
                    if (!AppMessageBox.ShowConfirm(
                        Get("Msg_ForceDisarmInFlight"),
                        owner: OwnerWindow,
                        subtitle: Get("Msg_Warning")))
                        return;

                    _mavlinkService.SetArm(false, true);
                }
                else
                {
                    _mavlinkService.SetArm(false, false);
                }
                return;
            }

            var checklist = new PreflightChecklistDialog(_mavlinkService, _currentVehicleType)
            {
                Owner = OwnerWindow
            };

            if (checklist.ShowDialog() != true) return;

            ArmWithFeedbackAsync(checklist.ForceArm);
        }

        private async void ArmWithFeedbackAsync(bool force)
        {
            _mavlinkService.SetArm(true, force);
            for (int i = 0; i < 16; i++)
            {
                await System.Threading.Tasks.Task.Delay(500);
                if (_mavlinkService.CurrentTelemetry.Armed) return;
            }
            AppMessageBox.ShowError(Get("Msg_ArmFailed"), owner: OwnerWindow);
        }

        private void QuickMode_Click(object sender, RoutedEventArgs e)
        {
            if (_mavlinkService?.IsConnected != true)
            {
                ShowNotConnectedMessage();
                return;
            }

            if (sender is Button btn && btn.Tag is string mode)
            {
                if (mode == "AUTO" && !_mavlinkService.CurrentTelemetry.Armed)
                {
                    AppMessageBox.ShowWarning(Get("Msg_DroneNotArmed"), owner: OwnerWindow);
                    return;
                }

                _mavlinkService.SetFlightMode(mode);
            }
        }

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
                var owner = Window.GetWindow(this);

                if (calibType == Get("Gyroscope"))
                {
                    var dialog = new GyroCalibrationDialog(_mavlinkService);
                    dialog.Owner = owner;
                    dialog.ShowDialog();
                }
                else if (calibType == Get("Compass"))
                {
                    var dialog = new CompassCalibrationDialog(_mavlinkService);
                    dialog.Owner = owner;
                    dialog.ShowDialog();
                }
                else if (calibType == Get("Accelerometer"))
                {
                    var dialog = new AccelCalibrationDialog(_mavlinkService);
                    dialog.Owner = owner;
                    dialog.ShowDialog();
                }
                else if (calibType == Get("Calibrations"))
                {
                    AppMessageBox.ShowInfo(Get("Msg_SelectCalibration"), owner: OwnerWindow);
                }
            }
        }

        private async void ActivateMissionButton_Click(object sender, RoutedEventArgs e)
        {
            if (_mavlinkService?.IsConnected != true)
            {
                ShowNotConnectedMessage();
                return;
            }

            if (_waypoints.Count == 0)
            {
                AppMessageBox.ShowWarning(Get("Msg_MissionEmptyAddPoints"), owner: OwnerWindow);
                return;
            }

            if (_homePosition == null)
            {
                AppMessageBox.ShowError(Get("Msg_SetHome"), owner: OwnerWindow);
                return;
            }

            var telem = _mavlinkService.CurrentTelemetry;
            if (telem.GpsFixType < 3)
            {
                bool proceed = AppMessageBox.ShowConfirm(
                    Get("Msg_NoGpsActivateWarning"),
                    owner: OwnerWindow,
                    subtitle: Get("Msg_Warning"));
                if (!proceed) return;
            }

            bool alreadyArmed = _mavlinkService.CurrentTelemetry.Armed;
            bool forceArm = false;

            if (!alreadyArmed)
            {
                var checklist = new PreflightChecklistDialog(_mavlinkService, _currentVehicleType)
                { Owner = OwnerWindow };
                if (checklist.ShowDialog() != true) return;
                forceArm = checklist.ForceArm;
            }

            bool isVtol = _currentVehicleType == VehicleType.QuadPlane;
            string confirmMsg = isVtol
                ? Fmt("Msg_ActivateVtolMission", _waypoints.Count)
                : Fmt("Msg_ActivateCopterMission", _waypoints.Count);

            if (!AppMessageBox.ShowConfirm(confirmMsg, OwnerWindow, subtitle: Get("MsgBox_Confirm")))
                return;

            try
            {
                _mavlinkService.SavePlannedMission(GetFullMission());
                bool uploadSuccess = await _mavlinkService.UploadPlannedMission();

                if (!uploadSuccess)
                {
                    AppMessageBox.ShowError(Get("Msg_MissionUploadError"), owner: OwnerWindow);
                    return;
                }

                await System.Threading.Tasks.Task.Delay(500);

                if (!_mavlinkService.CurrentTelemetry.Armed)
                {
                    _mavlinkService.SetArm(true, forceArm);

                    bool armed = false;
                    for (int i = 0; i < 16; i++)
                    {
                        await System.Threading.Tasks.Task.Delay(500);
                        if (_mavlinkService.CurrentTelemetry.Armed) { armed = true; break; }
                    }

                    if (!armed)
                    {
                        AppMessageBox.ShowError(Get("Msg_ArmFailed"), owner: OwnerWindow);
                        return;
                    }
                }

                _mavlinkService.SetActiveMission(GetFullMission());
                _mavlinkService.StartMission();

                AppMessageBox.ShowSuccess(Get("Msg_MissionActivated"), owner: OwnerWindow);
            }
            catch (Exception ex)
            {
                AppMessageBox.ShowError($"{Get("MsgBox_Error")}: {ex.Message}", owner: OwnerWindow);
            }
        }

        private string FormatDistance(double meters)
        {
            return $"{meters:F0} м";
        }



        private async void FreezeResumeMission_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_mavlinkService == null || !_mavlinkService.IsConnected)
                {
                    ShowStatusMessage(Get("Msg_DroneNotConnected"));
                    return;
                }

                var telem = _mavlinkService.CurrentTelemetry;
                if (telem == null || !telem.Armed)
                {
                    ShowStatusMessage(Get("Msg_DroneNotArmed"));
                    return;
                }

                string mode = telem.FlightMode?.ToUpper() ?? "";

                if (!_isMissionFrozen)
                {

                    int currentSeq = _mavlinkService.CurrentMissionSeq;

                    int minFreezeSeq = (_currentVehicleType == VehicleType.QuadPlane) ? 3 : 2;
                    if (currentSeq < minFreezeSeq)
                    {
                        ShowStatusMessage(Get("Msg_CantFreezeTakeoff"));
                        return;
                    }

                    if (_currentVehicleType == VehicleType.QuadPlane)
                    {

                        int totalItems = GetFullMission().Count;
                        int transitionMcSeq = totalItems - 2;
                        int landSeq = totalItems - 1;

                        if (currentSeq >= transitionMcSeq)
                        {
                            ShowStatusMessage(Get("Msg_CantFreezeLanding"));
                            return;
                        }
                    }
                    string freezeMode = (_currentVehicleType == VehicleType.QuadPlane) ? "QLOITER" : "LOITER";
                    _mavlinkService.SetFlightMode(freezeMode);
                    _isMissionFrozen = true;
                    _missionModifiedInFlight = false;

                    if (sender is Button btn)
                    {
                        btn.Content = Get("Msg_ResumeMission");
                        btn.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(22, 163, 74));
                    }

                    System.Diagnostics.Debug.WriteLine($"[FREEZE] Миссия заморожена на seq={currentSeq}");
                    ShowStatusMessage(Get("Msg_MissionFrozen"));
                }
                else
                {
                    ushort resumeSeq = (ushort)_mavlinkService.CurrentMissionSeq;
                    int _minResume = (_currentVehicleType == VehicleType.QuadPlane) ? 3 : 2;
                    if (resumeSeq < _minResume) resumeSeq = (ushort)_minResume;

                    if (!_mavlinkService.CurrentTelemetry.Armed)
                    {
                        _isMissionFrozen = false;
                        ShowStatusMessage(Get("Msg_ResumeNotArmed"));
                        return;
                    }
                    if (_missionModifiedInFlight)
                    {
                        var fullMission = GetFullMission();
                        _mavlinkService.SavePlannedMission(fullMission);
                        bool success = await _mavlinkService.UploadPlannedMission();
                        if (!success) { ShowStatusMessage(Get("Msg_UploadError")); return; }
                        _mavlinkService.SetActiveMission(fullMission);
                        _missionModifiedInFlight = false;

                        if (!_mavlinkService.CurrentTelemetry.Armed)
                        {
                            _isMissionFrozen = false;
                            ShowStatusMessage(Get("Msg_ResumeNotArmed"));
                            return;
                        }
                        resumeSeq = (ushort)_mavlinkService.CurrentMissionSeq;
                        bool isVtolR = _currentVehicleType == VehicleType.QuadPlane;
                        int _minResumeUpd = isVtolR ? 3 : 2;
                        if (resumeSeq < _minResumeUpd) resumeSeq = (ushort)_minResumeUpd;
                        int maxSafe = fullMission.Count - (isVtolR ? 3 : 2);
                        resumeSeq = (ushort)Math.Max(isVtolR ? 3 : 2, Math.Min(resumeSeq, maxSafe));
                    }
                    await _mavlinkService.SetCurrentWaypointVerified(resumeSeq);
                    _mavlinkService.SetFlightMode("AUTO");

                    _isMissionFrozen = false;
                    _missionModifiedInFlight = false;

                    if (sender is Button btn)
                    {
                        btn.Content = Get("Msg_FreezeMission");
                        btn.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(37, 99, 235));
                    }

                    System.Diagnostics.Debug.WriteLine($"[RESUME] Миссия продолжена с seq={resumeSeq}");
                    ShowStatusMessage(Fmt("Msg_MissionResumed", resumeSeq));
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[FREEZE/RESUME] Ошибка: {ex.Message}");
                ShowStatusMessage($"{Get("MsgBox_Error")}: {ex.Message}");
            }
        }

        private void ShowStatusMessage(string message)
        {
            System.Diagnostics.Debug.WriteLine($"[STATUS] {message}");
            AppMessageBox.ShowInfo(message, owner: OwnerWindow);
        }
        private bool _missionModifiedInFlight = false;
        private void NotifyMissionChangedIfArmed()
        {
            if (_mavlinkService == null || !_mavlinkService.IsConnected) return;
            var telem = _mavlinkService.CurrentTelemetry;
            if (telem == null || !telem.Armed) return;

            _missionModifiedInFlight = true;

            string mode = telem.FlightMode?.ToUpper() ?? "";
            if (mode == "AUTO" || mode == "MISSION")
            {
                NotificationService.Instance.HudOnly(
                    Get("Msg_MissionChangedFreeze"), NotificationType.Warning);
            }
        }

        private void ResumeMissionButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_mavlinkService == null || !_mavlinkService.IsConnected)
                {
                    AppMessageBox.ShowWarning(Get("Msg_DroneNotConnected"), owner: OwnerWindow);
                    return;
                }
                if (_mavlinkService.IsUploadingMission)
                {
                    AppMessageBox.ShowWarning(Get("Msg_MissionUploading"), owner: OwnerWindow);
                    return;
                }

                var telem = _mavlinkService.CurrentTelemetry;
                if (telem == null || !telem.Armed)
                {
                    AppMessageBox.ShowWarning(Get("Msg_DroneNotArmed"), owner: OwnerWindow);
                    return;
                }

                ushort currentSeq = (ushort)(telem.CurrentWaypoint);
                ushort nextSeq = (ushort)(currentSeq + 1);

                int totalItems = GetFullMission().Count;
                if (nextSeq >= totalItems)
                {
                    AppMessageBox.ShowInfo(Get("Msg_DroneLastWaypoint"), owner: OwnerWindow);
                    return;
                }
                int minUserSeq = (_currentVehicleType == VehicleType.QuadPlane) ? 3 : 2;
                if (nextSeq < minUserSeq)
                {
                    AppMessageBox.ShowWarning(Get("Msg_CantSkipTakeoff"), owner: OwnerWindow);
                    return;
                }

                if (_currentVehicleType == VehicleType.QuadPlane)
                {
                    int transitionMcSeq = totalItems - 2;

                    if (nextSeq >= transitionMcSeq)
                    {
                        AppMessageBox.ShowWarning(Get("Msg_NextWpVtolDanger"), owner: OwnerWindow);
                        return;
                    }
                }

                _mavlinkService.SetCurrentWaypoint(nextSeq);

                System.Diagnostics.Debug.WriteLine($"[RESUME] Отправлен MISSION_SET_CURRENT seq={nextSeq}");
            }
            catch (Exception ex)
            {
                AppMessageBox.ShowError($"{Get("MsgBox_Error")}: {ex.Message}", owner: OwnerWindow);
            }
        }

        private void OnMissionProgressUpdated(object sender, int seq)
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (_mavlinkService?.CurrentTelemetry?.Armed == true)
                    HighlightMissionSeq(seq);

                NotifyMissionProgress(seq);
            });
        }

        private void NotifyMissionProgress(int seq)
        {
            if (_mavlinkService == null) return;
            var t = _mavlinkService.CurrentTelemetry;
            if (t == null || !t.Armed) return;
            string mode = t.FlightMode?.ToUpper() ?? "";
            if (mode != "AUTO" && mode != "GUIDED") return;
            if (_suppressMissionNotify) return;

            int total = _mavlinkService.PlannedMissionCount > 0
                ? _mavlinkService.PlannedMissionCount
                : (_mavlinkService.ActiveMission?.Count ?? 0);
            if (total <= 0) return;

            if (seq <= 0) return;
            if (seq == 1)
            {
                NotificationService.Instance.Hud(Get("Notif_MissionStarted"), NotificationType.Success);
                return;
            }
            bool isVtolNotify = _currentVehicleType == VehicleType.QuadPlane;
            int missionCompleteSeq = isVtolNotify ? total - 2 : total - 1;

            if (seq >= missionCompleteSeq)
            {
                NotificationService.Instance.Hud(Get("Notif_MissionComplete"), NotificationType.Success);
                return;
            }
            int wpOffset = isVtolNotify ? 3 : 2;
            int rtlItems = isVtolNotify ? 2 : 1;

            int userSeq = seq - wpOffset + 1;
            int userTotal = total - wpOffset - rtlItems;

            if (userSeq > 0 && userTotal > 0)
            {
                NotificationService.Instance.HudOnly(
                    Fmt("Notif_WaypointReached", userSeq, userTotal), NotificationType.Info);
            }
        }
    }

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

        private double _speed = 10;
        public double Speed
        {
            get => _speed;
            set { _speed = value; OnPropertyChanged(); }
        }

        private int _loiterTurns;
        public int LoiterTurns
        {
            get => _loiterTurns;
            set { _loiterTurns = value; OnPropertyChanged(); }
        }

        private bool _autoNext = true;

        public bool AutoNext
        {
            get => _autoNext;
            set { _autoNext = value; OnPropertyChanged(); }
        }

        private bool _clockwise = true;

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