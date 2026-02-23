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
        private Dictionary<VehicleType, WaypointItem> _startByType = new();
        private Dictionary<VehicleType, WaypointItem> _landingByType = new();
        private DispatcherTimer _droneUpdateTimer; 
        private bool _isSettingHomeMode = false; 
        private double _takeoffAltitude = 10;  
        private double _rtlAltitude = 15;      
        private bool _wasArmed = false; 
        private SrtmElevationProvider _elevationProvider = new(); 
        private Dictionary<WaypointItem, GMapMarker> _resizeHandles = new(); 

        private DispatcherTimer _telemetryTimer;      
        private DispatcherTimer _connectionTimer;     
        private DateTime _connectionStartTime;        
        private bool _wasConnected = false;           

        private WaypointItem _startCircle;            
        private WaypointItem _landingCircle;          
        private double _vtolTakeoffAltitude = 30;     
        private double _vtolLandAltitude = 30;        
        private bool _isMissionFrozen = false;        
        private bool _isDataTabActive = true;          

        public FlightPlanView(MAVLinkService mavlinkService = null)
        {
            InitializeComponent();
            DrawCompassTicks();

            Services.LocalizationService.Instance.LanguageChanged += (s, e) =>
                Dispatcher.Invoke(() => UpdateVehicleTypeDisplay());
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

                        if (_currentVehicleType == VehicleType.QuadPlane)
                            
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

            this.MouseLeftButtonUp += (s, e) => EndRadiusDrag();
            this.MouseLeave += (s, e) => EndRadiusDrag();
        }

        private void InitializePlanMap()
        {
            try
            {
                System.Diagnostics.Debug.WriteLine("Начало инициализации карты планирования...");

                string cacheFolder = System.IO.Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, "MapCache");

                if (!System.IO.Directory.Exists(cacheFolder))
                    System.IO.Directory.CreateDirectory(cacheFolder);

                bool hasInternet = CheckInternetConnection();

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

                System.Diagnostics.Debug.WriteLine("Карта планирования инициализирована");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Ошибка инициализации карты: {ex.Message}");
            }
        }

        private bool CheckInternetConnection()
        {
            
            try
            {
                using (var client = new System.Net.WebClient())
                using (client.OpenRead("http://www.google.com"))
                    return true;
            }
            catch { }

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
                    System.Diagnostics.Debug.WriteLine("1. _waypoints.Add - OK");

                    AddMarkerToMap(waypoint);
                    System.Diagnostics.Debug.WriteLine("2. AddMarkerToMap - OK");

                    UpdateRoute();
                    System.Diagnostics.Debug.WriteLine("3. UpdateRoute - OK");
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

            System.Diagnostics.Debug.WriteLine($"Waypoint удалён, осталось: {_waypoints.Count}");
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
                
                if (_currentVehicleType == VehicleType.QuadPlane)
                    
                System.Diagnostics.Debug.WriteLine($"UpdateRoute() - Точек: 0, HOME: {_homePosition != null}");
                UpdateStatistics();
                return;
            }

            var entryPoints = new Dictionary<int, PointLatLng>();
            var exitPoints = new Dictionary<int, PointLatLng>();

            bool isVTOL = _currentVehicleType == VehicleType.QuadPlane;

            if (isVTOL)
            {
                
            }

            if (isVTOL && _startCircle != null)
            {
                var firstWp = _waypoints[0];
                var tangent = GetExternalTangentPoints(
                    _startCircle.Latitude, _startCircle.Longitude, _startCircle.Radius, _startCircle.Clockwise,
                    firstWp.Latitude, firstWp.Longitude, firstWp.Radius, firstWp.Clockwise);
                entryPoints[0] = tangent.Item2;  
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

            if (isVTOL && _landingCircle != null && _waypoints.Count > 0)
            {
                int lastIdx = _waypoints.Count - 1;
                var lastWp = _waypoints[lastIdx];
                var tangent = GetExternalTangentPoints(
                    lastWp.Latitude, lastWp.Longitude, lastWp.Radius, lastWp.Clockwise,
                    _landingCircle.Latitude, _landingCircle.Longitude, _landingCircle.Radius, _landingCircle.Clockwise);
                exitPoints[lastIdx] = tangent.Item1;  
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

            for (int i = 0; i < _waypoints.Count; i++)
            {
                if (entryPoints.ContainsKey(i) && exitPoints.ContainsKey(i))
                {
                    DrawArcOnWaypoint(_waypoints[i], entryPoints[i], exitPoints[i]);
                }
            }

            if (isVTOL)
            {
                
            }

            System.Diagnostics.Debug.WriteLine($"UpdateRoute() - Точек: {_waypoints.Count}, HOME: {_homePosition != null}, VTOL: {isVTOL}");
        }

        private void DrawVtolSpecialPoints()
        {
            
            var vtolLines = PlanMap.Markers
                .Where(m => m.Tag?.ToString()?.StartsWith("vtol_line") == true ||
                            m.Tag?.ToString()?.StartsWith("vtol_arc") == true)
                .ToList();
            foreach (var m in vtolLines) { m.Shape = null; PlanMap.Markers.Remove(m); }

            if (_startCircle == null) InitializeStartCircle();
            if (_landingCircle == null) InitializeLandingCircle();
            if (_startCircle == null || _landingCircle == null) return;

            var cyan = Color.FromRgb(0, 168, 143);     
            var orange = Color.FromRgb(255, 159, 26);  
            var green = Color.FromRgb(152, 240, 25);

            if (_startCircle.Marker == null || !PlanMap.Markers.Contains(_startCircle.Marker))
            {
                AddSpecialCircleMarker(_startCircle, "S", cyan, Color.FromRgb(0, 168, 143));
            }
            else
            {
                UpdateSpecialCircleSize(_startCircle, "S");
            }

            if (_landingCircle.Marker == null || !PlanMap.Markers.Contains(_landingCircle.Marker))
            {
                AddSpecialCircleMarker(_landingCircle, "L", orange, Color.FromRgb(255, 159, 26));
            }
            else
            {
                UpdateSpecialCircleSize(_landingCircle, "L");
            }

            PointLatLng? sEntry = null, sExit = null;
            PointLatLng? lEntry = null, lExit = null;

            if (_homePosition != null)
            {
                var homePos = new PointLatLng(_homePosition.Latitude, _homePosition.Longitude);
                var tangent = GetExternalTangentPoints(
                    _homePosition.Latitude, _homePosition.Longitude, 0, true,
                    _startCircle.Latitude, _startCircle.Longitude, _startCircle.Radius, _startCircle.Clockwise);
                sEntry = tangent.Item2;
                DrawVtolLine(homePos, tangent.Item2, cyan, true);
            }

            if (_waypoints.Count > 0)
            {
                var wp1 = _waypoints[0];
                var tangent = GetExternalTangentPoints(
                    _startCircle.Latitude, _startCircle.Longitude, _startCircle.Radius, _startCircle.Clockwise,
                    wp1.Latitude, wp1.Longitude, wp1.Radius, wp1.Clockwise);
                sExit = tangent.Item1;
                DrawVtolLine(tangent.Item1, tangent.Item2, green, false);
            }

            if (_waypoints.Count > 0)
            {
                var wpN = _waypoints[^1];
                var tangent = GetExternalTangentPoints(
                    wpN.Latitude, wpN.Longitude, wpN.Radius, wpN.Clockwise,
                    _landingCircle.Latitude, _landingCircle.Longitude, _landingCircle.Radius, _landingCircle.Clockwise);
                lEntry = tangent.Item2;
                DrawVtolLine(tangent.Item1, tangent.Item2, orange, false);
            }

            if (_homePosition != null)
            {
                var homePos = new PointLatLng(_homePosition.Latitude, _homePosition.Longitude);
                var tangent = GetExternalTangentPoints(
                    _landingCircle.Latitude, _landingCircle.Longitude, _landingCircle.Radius, _landingCircle.Clockwise,
                    _homePosition.Latitude, _homePosition.Longitude, 0, true);
                lExit = tangent.Item1;
                DrawVtolLine(tangent.Item1, homePos, orange, true);
            }

            if (sEntry.HasValue && sExit.HasValue)
                DrawArcOnSpecialCircle(_startCircle, sEntry.Value, sExit.Value, Color.FromRgb(0, 130, 110));
            if (lEntry.HasValue && lExit.HasValue)
                DrawArcOnSpecialCircle(_landingCircle, lEntry.Value, lExit.Value, Color.FromRgb(200, 125, 16));
        }

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
                arcRoute.Tag = "vtol_arc"; 
                PlanMap.Markers.Add(arcRoute);
            }
        }

        private void AddSpecialCircleMarker(WaypointItem wp, string label, Color dotColor, Color circleColor)
        {
            if (wp == null) return;

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

            double radiusInPixels = MetersToPixels(wp.Radius, wp.Latitude, PlanMap.Zoom);
            radiusInPixels = Math.Max(3, Math.Min(5000, radiusInPixels));
            double gridSize = Math.Max(60, radiusInPixels * 2);

            var grid = new Grid
            {
                Width = gridSize,
                Height = gridSize,
                Cursor = Cursors.Hand
            };

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

            var halo = new Ellipse
            {
                Width = 34,
                Height = 34,
                Fill = new SolidColorBrush(Color.FromArgb(89, 0, 0, 0)), 
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            grid.Children.Add(halo);

            var strokeColor = (label == "S")
                ? Color.FromRgb(255, 255, 255)   
                : Color.FromRgb(42, 22, 0);      

            var centerPoint = new Ellipse
            {
                Width = 28,
                Height = 28,
                Fill = new SolidColorBrush(dotColor),
                Stroke = new SolidColorBrush(strokeColor),
                StrokeThickness = 3,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            grid.Children.Add(centerPoint);

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

            grid.ToolTip = CreateCircleTooltip(wp, label);
            ToolTipService.SetInitialShowDelay(grid, 300);
            grid.ToolTipOpening += (s, args) =>
            {
                if (s is Grid g) g.ToolTip = CreateCircleTooltip(wp, label);
            };

            var marker = new GMapMarker(new PointLatLng(wp.Latitude, wp.Longitude))
            {
                Shape = grid,
                Offset = new Point(-gridSize / 2, -gridSize / 2),
                ZIndex = label == "S" ? 95 : 90,
                Tag = $"vtol_{label}"
            };

            wp.Marker = marker;
            PlanMap.Markers.Add(marker);

            grid.MouseLeftButtonDown += (s, e) =>
            {
                if (e.ClickCount == 2)
                {
                    
                    string title = label == "S" ? Get("StartCircle") : Get("LandingCircle");
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

            grid.MouseRightButtonDown += (s, e) =>
            {
                string name = label == "S" ? Get("StartCircle") : Get("LandingCircle");
                if (AppMessageBox.ShowConfirm(Fmt("Msg_ConfirmDelete", name), OwnerWindow, subtitle: Get("Msg_ConfirmDeleteSub")))
                {
                    if (label == "S") { _startCircle = null; }
                    else { _landingCircle = null; }
                    UpdateRoute();
                }
                e.Handled = true;
            };
        }

        private ToolTip CreateCircleTooltip(WaypointItem wp, string label)
        {
            var tooltip = new ToolTip
            {
                Background = new SolidColorBrush(Color.FromRgb(13, 23, 51)),
                BorderBrush = new SolidColorBrush(label == "S"
                    ? Color.FromRgb(250, 204, 21)  
                    : Color.FromRgb(249, 115, 22)), 
                BorderThickness = new Thickness(2),
                Padding = new Thickness(10, 6, 10, 6)
            };

            var stack = new StackPanel();
            stack.Children.Add(new TextBlock
            {
                Text = label == "S" ? Get("StartCircle") : Get("LandingCircle"),
                Foreground = tooltip.BorderBrush,
                FontWeight = FontWeights.Bold,
                FontSize = 12
            });
            stack.Children.Add(new TextBlock
            {
                Text = $"{Get("WpEdit_RadiusM")} {wp.Radius:F0}\n" +
                       $"{Get("WpEdit_AltitudeM")} {wp.Altitude:F0}\n" +
                       $"{Get("WpEdit_Direction")} {(wp.Clockwise ? "CW ↻" : "CCW ↺")}\n" +
                       $"{Get("WpEdit_AutoNext")}: {(wp.AutoNext ? Get("Yes") : Get("No_Loiter"))}",
                Foreground = Brushes.White,
                FontSize = 11
            });

            tooltip.Content = stack;
            return tooltip;
        }

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
            route.Tag = "vtol_line"; 
            PlanMap.Markers.Add(route);
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

        private (PointLatLng, PointLatLng) GetExternalTangentPoints(
            double lat1, double lon1, double r1,
            double lat2, double lon2, double r2)
        {
            return GetExternalTangentPoints(lat1, lon1, r1, true, lat2, lon2, r2, true);
        }

        private PointLatLng GetNearEdgePoint(double circleLat, double circleLon, double circleRadius,
                                              double pointLat, double pointLon)
        {
            double bearing = CalculateBearing(circleLat, circleLon, pointLat, pointLon);
            return CalculatePointAtDistance(circleLat, circleLon, bearing, circleRadius / 1000.0);
        }

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

            if (isVTOL)
            {
                
            }

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

            for (int i = 0; i < _waypoints.Count; i++)
            {
                if (entryPoints.ContainsKey(i) && exitPoints.ContainsKey(i))
                {
                    DrawArcOnWaypoint(_waypoints[i], entryPoints[i], exitPoints[i]);
                }
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

            if (StartCircleCard != null) StartCircleCard.Visibility = Visibility.Collapsed;
            if (LandingCircleCard != null) LandingCircleCard.Visibility = Visibility.Collapsed;
            if (ArrowAfterTakeoff != null) ArrowAfterTakeoff.Visibility = isVtol ? Visibility.Collapsed : Visibility.Visible;
            if (ArrowAfterTakeoffVtol != null) ArrowAfterTakeoffVtol.Visibility = Visibility.Collapsed;
            if (ArrowAfterStart != null) ArrowAfterStart.Visibility = Visibility.Collapsed;
            if (ArrowBeforeLand != null) ArrowBeforeLand.Visibility = Visibility.Collapsed;

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

            bool hasWp = _waypoints.Count > 0;
            if (ArrowBeforeRtl != null) ArrowBeforeRtl.Visibility = hasWp ? Visibility.Visible : Visibility.Collapsed;
            if (RtlCard != null) RtlCard.Visibility = hasWp ? Visibility.Visible : Visibility.Collapsed;
        }

        public void HighlightMissionSeq(int seq)
        {
            Dispatcher.BeginInvoke(() =>
            {
                
                if (WaypointsListPanel == null) return;
                foreach (var child in WaypointsListPanel.Children)
                {
                    if (child is Border border && border.Tag is int wpNum)
                    {
                        bool isVtol = _currentVehicleType == VehicleType.QuadPlane;
                        int expectedSeq = isVtol ? wpNum + 3 : wpNum; 

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

                {
                    int landSeq = _waypoints.Count + 4;
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

        private dynamic[] GetCommandsForVehicleType()
        {
            if (_currentVehicleType == VehicleType.QuadPlane)
            {
                
                return new dynamic[]
                {
            new { Name = Get("CmdShort_Waypoint"), Value = "WAYPOINT" },
            new { Name = Get("CmdShort_Loiter"), Value = "LOITER_UNLIM" },
            new { Name = Get("CmdShort_LoiterTime"), Value = "LOITER_TIME" },
            new { Name = Get("CmdShort_LoiterTurns"), Value = "LOITER_TURNS" },
            new { Name = Get("CmdShort_Land"), Value = "LAND" },
            new { Name = Get("CmdShort_Delay"), Value = "DELAY" },
            new { Name = Get("CmdShort_Speed"), Value = "CHANGE_SPEED" }
                };
            }

            return new dynamic[]
            {
        new { Name = Get("CmdShort_Waypoint"), Value = "WAYPOINT" },
        new { Name = Get("CmdShort_Loiter"), Value = "LOITER_UNLIM" },
        new { Name = Get("CmdShort_LoiterTime"), Value = "LOITER_TIME" },
        new { Name = Get("CmdShort_LoiterTurns"), Value = "LOITER_TURNS" },
        new { Name = Get("CmdShort_Takeoff"), Value = "TAKEOFF" },
        new { Name = Get("CmdShort_Land"), Value = "LAND" },
        new { Name = Get("CmdShort_Delay"), Value = "DELAY" },
        new { Name = Get("CmdShort_Speed"), Value = "CHANGE_SPEED" },
        new { Name = Get("CmdShort_RTL"), Value = "RETURN_TO_LAUNCH" },
        new { Name = Get("CmdShort_Spline"), Value = "SPLINE_WP" }
            };
        }

        private void AddHomePosition()
        {
            if (_mavlinkService == null || _mavlinkService.CurrentTelemetry.Latitude == 0)
            {
                AppMessageBox.ShowWarning(
                    Get("Msg_NoGpsSignal"),
                    owner: OwnerWindow,
                    subtitle: Get("Msg_CannotSetHomeSub"),
                    hint: Get("Msg_WaitGpsFix")
                );
                return;
            }

            if (_homePosition != null)
            {
                if (_homePosition.Marker != null)
                    _homePosition.Marker.Shape = null;
                PlanMap.Markers.Remove(_homePosition.Marker);
            }

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
            UpdateRoute(); 

            MissionStore.SetHome((int)_currentVehicleType, _homePosition);

            PlanHomeLatText.Text = _homePosition.Latitude.ToString("F6");
            PlanHomeLonText.Text = _homePosition.Longitude.ToString("F6");

            System.Diagnostics.Debug.WriteLine($" HOME установлена: {telemetry.Latitude:F6}, {telemetry.Longitude:F6}");
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

        private ControlTemplate CreateRoundedTextBoxTemplate()
        {
            var template = new ControlTemplate(typeof(TextBox));

            var border = new FrameworkElementFactory(typeof(Border));
            border.Name = "border";
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(TextBox.BackgroundProperty));
            border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(TextBox.BorderBrushProperty));
            border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(TextBox.BorderThicknessProperty));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6)); 

            var scrollViewer = new FrameworkElementFactory(typeof(ScrollViewer));
            scrollViewer.Name = "PART_ContentHost";
            scrollViewer.SetValue(ScrollViewer.FocusableProperty, false);
            scrollViewer.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
            scrollViewer.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);

            border.AppendChild(scrollViewer);
            template.VisualTree = border;

            return template;
        }

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
                
                textBox.Text = wp?.Altitude.ToString("F0") ?? "100";
            }
        }

        private void DelayBox_LostFocus(object sender, RoutedEventArgs e)
        {
            var textBox = sender as TextBox;
            var wp = textBox?.Tag as WaypointItem;
            if (wp != null && double.TryParse(textBox.Text, out double newDelay))
            {
                wp.Delay = newDelay;
            }
        }

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
                UpdateWaypointsList(); 
            }
        }

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
                UpdateWaypointsList(); 
            }
        }

        private void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            var waypoint = (sender as Button)?.Tag as WaypointItem;
            if (waypoint != null)
            {
                RemoveWaypoint(waypoint);
            }
        }

        private void ZoomSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (PlanMap != null)
            {
                PlanMap.Zoom = e.NewValue;

                RefreshMarkers();
            }
        }

        private void MissionScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            
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
                UpdateSpecialCircleSize(_startCircle, "S");
                UpdateSpecialCircleSize(_landingCircle, "L");
            }

            UpdateRoute();

            PlanMap.InvalidateVisual();

            System.Diagnostics.Debug.WriteLine($" RefreshMarkers: завершено\n");
        }

        private void UpdateSpecialCircleSize(WaypointItem wp, string label)
        {
            if (wp == null || wp.ShapeGrid == null || wp.RadiusCircle == null) return;

            double radiusInPixels = MetersToPixels(wp.Radius, wp.Latitude, PlanMap.Zoom);
            radiusInPixels = Math.Max(3, Math.Min(5000, radiusInPixels));
            double diameter = radiusInPixels * 2;
            double gridSize = Math.Max(60, diameter);

            wp.ShapeGrid.Width = gridSize;
            wp.ShapeGrid.Height = gridSize;
            wp.RadiusCircle.Width = diameter;
            wp.RadiusCircle.Height = diameter;

            if (wp.Marker != null)
            {
                wp.Marker.Offset = new Point(-gridSize / 2, -gridSize / 2);
            }

            wp.ShapeGrid.InvalidateVisual();
            wp.RadiusCircle.InvalidateVisual();

            System.Diagnostics.Debug.WriteLine($"   {label}: Radius={wp.Radius:F0}м → {radiusInPixels:F0}px");
        }

        private void AddWaypointButton_Click(object sender, RoutedEventArgs e)
        {
            AppMessageBox.ShowInfo(
                Get("Msg_AddPointInDev"),
                owner: OwnerWindow,
                subtitle: Get("Msg_InDevelopmentSub")
            );
        }

        private void ExecuteButton_Click(object sender, RoutedEventArgs e)
        {
            AppMessageBox.ShowInfo(Get("Msg_InDevelopment"), owner: OwnerWindow, subtitle: Get("Msg_InDevelopmentSub"));
        }

        private void LoiterButton_Click(object sender, RoutedEventArgs e)
        {
            AppMessageBox.ShowInfo(Get("Msg_InDevelopment"), owner: OwnerWindow, subtitle: Get("Msg_InDevelopmentSub"));
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            AppMessageBox.ShowInfo(Get("Msg_InDevelopment"), owner: OwnerWindow, subtitle: Get("Msg_InDevelopmentSub"));
        }

        private void ToggleButton_Click(object sender, RoutedEventArgs e)
        {
            AppMessageBox.ShowInfo(Get("Msg_InDevelopment"), owner: OwnerWindow, subtitle: Get("Msg_InDevelopmentSub"));
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
                        _mavlinkService.SetVTOLAutoTransition(true);
                        System.Diagnostics.Debug.WriteLine($" VTOL: Q_OPTIONS=128 (автопереход после взлёта)");
                    }

                    MissionStore.Set((int)_currentVehicleType, GetFullMission());

                    string successMsg = Fmt("Msg_MissionSavedShort", _waypoints.Count);
                    if (_currentVehicleType == VehicleType.QuadPlane)
                    {
                        successMsg += "\n✈️ " + Get("Msg_VtolSequence");
                    }

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
                _waypoints.Clear();
                _startCircle = null;
                _landingCircle = null;
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

                    var navItems = items.Where(it =>
                        it.seq > 0 &&
                        (it.command == 16 || it.command == 17 || it.command == 18 || it.command == 19) &&
                        (it.x != 0 || it.y != 0)
                    ).ToList();

                    if (navItems.Count >= 3)
                    {
                        
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
                _waypoints.Clear();
                _startCircle = null;
                _landingCircle = null;
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

                    var navItems = parsedItems.Where(p =>
                        p.seq > 0 && 
                        (p.cmd == 16 || p.cmd == 17 || p.cmd == 18 || p.cmd == 19) &&
                        (p.lat != 0 || p.lon != 0) 
                    ).ToList();

                    if (navItems.Count >= 3)
                    {
                        
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

                    }
                    else
                    {
                        
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
                                AutoNext = nav.autoCont == 1
                            };
                            _waypoints.Add(wp);
                            AddMarkerToMap(wp);
                        }
                    }
                }
                else
                {
                    
                    var takeoff = parsedItems.FirstOrDefault(p => p.cmd == 22);
                    if (takeoff.alt > 0) _takeoffAltitude = takeoff.alt;

                    var navItems = parsedItems.Where(p =>
                        p.seq > 0 &&
                        p.cmd != 22 && p.cmd != 20 && 
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

                _waypoints.Clear();

                _startCircle = null;
                _landingCircle = null;

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

                lines.Add($"{i}\t{current}\t{frame}\t{mavCmd}\t{p1}\t{p2}\t{p3}\t{p4}\t{wp.Latitude:F7}\t{wp.Longitude:F7}\t{wp.Altitude:F2}\t{autoContinue}");
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
                case "WAYPOINT":
                    return (wp.Delay, 0, 0, 0);  
                case "DELAY":
                    return (wp.Delay, 0, 0, 0);
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
                case "START_CIRCLE": result = 17; break; 
                case "LANDING_CIRCLE": result = 17; break; 
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

        private void UpdateDronePosition(object sender, EventArgs e)
        {
            if (_mavlinkService == null || PlanMap == null) return;
            if (!_mavlinkService.IsConnected) return;

            var telemetry = _mavlinkService.CurrentTelemetry;

            if (telemetry.Armed && !_wasArmed)
            {
                
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
                        Latitude = _homePosition.Latitude,
                        Longitude = _homePosition.Longitude,
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
            _startCircle = null;
            _landingCircle = null;

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
                if (QuickModeBtn3 != null) { QuickModeBtn3.SetResourceReference(ContentControl.ContentProperty, "Execute"); QuickModeBtn3.Tag = "AUTO"; }
                if (QuickModeBtn4 != null) { QuickModeBtn4.SetResourceReference(ContentControl.ContentProperty, "Mode_QStabilize"); QuickModeBtn4.Tag = "QSTABILIZE"; }
                if (QuickModeBtn5 != null) { QuickModeBtn5.SetResourceReference(ContentControl.ContentProperty, "Mode_Home"); QuickModeBtn5.Tag = "QRTL"; }
            }
            else
            {
                
                if (QuickModeBtn1 != null) { QuickModeBtn1.SetResourceReference(ContentControl.ContentProperty, "Mode_Hold"); QuickModeBtn1.Tag = "LOITER"; }
                if (QuickModeBtn2 != null) { QuickModeBtn2.SetResourceReference(ContentControl.ContentProperty, "Mode_AltHold"); QuickModeBtn2.Tag = "ALT_HOLD"; }
                if (QuickModeBtn3 != null) { QuickModeBtn3.SetResourceReference(ContentControl.ContentProperty, "Execute"); QuickModeBtn3.Tag = "AUTO"; }
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

        private void PlanMap_OnMapZoomChanged()
        {
            
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
                UpdateSpecialCircleSize(_startCircle, "S");
                UpdateSpecialCircleSize(_landingCircle, "L");
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

            System.Diagnostics.Debug.WriteLine($"[MouseMove] Lat={cursorLatLng.Lat:F4}, Lng={cursorLatLng.Lng:F4}");

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

        private void LoiterBtn_Click(object sender, MouseButtonEventArgs e)
        {
            if (_mavlinkService?.IsConnected != true)
            {
                ShowNotConnectedMessage();
                return;
            }

            var mode = VehicleManager.Instance.CurrentVehicleType == Models.VehicleType.QuadPlane
                ? "QLOITER" : "LOITER";
            _mavlinkService.SetFlightMode(mode);
        }

        private void ResumeBtn_Click(object sender, MouseButtonEventArgs e)
        {
            if (_mavlinkService?.IsConnected != true)
            {
                ShowNotConnectedMessage();
                return;
            }

            _mavlinkService.SetFlightMode("AUTO");
        }

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

        private void RtlBtn_Click(object sender, MouseButtonEventArgs e)
        {
            if (_mavlinkService?.IsConnected != true)
            {
                ShowNotConnectedMessage();
                return;
            }

            var mode = VehicleManager.Instance.CurrentVehicleType == Models.VehicleType.QuadPlane
                ? "QRTL" : "RTL";
            _mavlinkService.SetFlightMode(mode);
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
                AltitudeValue.Text = $"{telemetry.Altitude:F1} м";
            if (AltitudeMslValue != null)
                AltitudeMslValue.Text = $"{telemetry.Altitude:F1} м";

            if (SecondarySpeedValue != null && SecondarySpeedLabel != null)
            {
                if (_currentVehicleType == VehicleType.QuadPlane)
                {
                    SecondarySpeedLabel.SetResourceReference(TextBlock.TextProperty, "Airspeed");
                    SecondarySpeedValue.Text = $"{telemetry.Airspeed:F1} м/с";
                    SecondarySpeedValue.Foreground = new SolidColorBrush(Color.FromRgb(34, 211, 238)); 
                }
                else
                {
                    SecondarySpeedLabel.SetResourceReference(TextBlock.TextProperty, "VertSpeed");
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
        }

        private void UpdateGpsStatus(Telemetry telemetry)
        {
            if (GpsIndicator == null || GpsStatusText == null) return;

            if (telemetry.GpsFixType >= 3)
            {
                GpsIndicator.Fill = new SolidColorBrush(Color.FromRgb(34, 197, 94)); 
                GpsStatusText.Text = "GPS OK";
                GpsStatusText.Foreground = new SolidColorBrush(Color.FromRgb(34, 197, 94));
            }
            else if (telemetry.GpsFixType >= 2)
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

        private void PopulateFlightModes()
        {
            if (FlightModeCombo == null) return;

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
            _mavlinkService.SetArm(!isArmed, true);
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

            bool isVtol = _currentVehicleType == VehicleType.QuadPlane;
            string msg = isVtol
                ? Fmt("Msg_ActivateVtolMission", _waypoints.Count)
                : Fmt("Msg_ActivateCopterMission", _waypoints.Count);

            var result = AppMessageBox.ShowConfirm(msg, OwnerWindow, subtitle: Get("MsgBox_Confirm"));
            if (!result) return;

            try
            {
                bool uploadSuccess;

                if (isVtol)
                {
                    
                    if (_homePosition == null) { AppMessageBox.ShowError(Get("Msg_SetHome"), owner: OwnerWindow); return; }
                    
                    uploadSuccess = await _mavlinkService.UploadVtolMission(
                        _homePosition, _startCircle, _waypoints.ToList(), _landingCircle,
                        _vtolTakeoffAltitude, _vtolLandAltitude);
                }
                else
                {
                    
                    _mavlinkService.SavePlannedMission(_waypoints.ToList());
                    uploadSuccess = await _mavlinkService.UploadPlannedMission();
                }

                if (!uploadSuccess)
                {
                    AppMessageBox.ShowError(Get("Msg_MissionUploadError"), owner: OwnerWindow);
                    return;
                }

                await System.Threading.Tasks.Task.Delay(500);

                _mavlinkService.SetArm(true, true);
                await System.Threading.Tasks.Task.Delay(1000);

                if (!_mavlinkService.CurrentTelemetry.Armed)
                {
                    AppMessageBox.ShowError(Get("Msg_ArmFailed"), owner: OwnerWindow);
                    return;
                }

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
                
                baseLat = PlanMap.Position.Lat;
                baseLon = PlanMap.Position.Lng;
            }

            var pos = CalculatePointAtDistance(baseLat, baseLon, 45, 0.3); 
            _startCircle = new WaypointItem
            {
                Number = 0,
                Latitude = pos.Lat,
                Longitude = pos.Lng,
                Altitude = _vtolTakeoffAltitude,
                Radius = 150,
                CommandType = "START_CIRCLE",
                AutoNext = false, 
                LoiterTurns = 1,
                Clockwise = true,
                Delay = 0
            };
            System.Diagnostics.Debug.WriteLine($"[VTOL] StartCircle создан: {_startCircle.Latitude:F6}, {_startCircle.Longitude:F6}");
        }

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

            var pos = CalculatePointAtDistance(baseLat, baseLon, 225, 0.3); 
            _landingCircle = new WaypointItem
            {
                Number = -1,
                Latitude = pos.Lat,
                Longitude = pos.Lng,
                Altitude = _vtolLandAltitude,
                Radius = 150,
                CommandType = "LANDING_CIRCLE",
                AutoNext = false, 
                LoiterTurns = 1,
                Clockwise = true,
                Delay = 0
            };
            System.Diagnostics.Debug.WriteLine($"[VTOL] LandingCircle создан: {_landingCircle.Latitude:F6}, {_landingCircle.Longitude:F6}");
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

                    if (currentSeq <= 2)
                    {
                        ShowStatusMessage(Get("Msg_CantFreezeTakeoff"));
                        return;
                    }

                    if (_currentVehicleType == VehicleType.QuadPlane)
                    {
                        
                        int totalItems = _waypoints.Count + 7; 
                        int transitionMcSeq = totalItems - 2;  
                        int landSeq = totalItems - 1;          

                        if (currentSeq >= transitionMcSeq)
                        {
                            ShowStatusMessage(Get("Msg_CantFreezeLanding"));
                            return;
                        }
                    }

                    _mavlinkService.SetFlightMode("LOITER");
                    _isMissionFrozen = true;

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
                    
                    int currentSeq = _mavlinkService.CurrentMissionSeq;

                    if (_currentVehicleType == VehicleType.QuadPlane && _startCircle != null && _landingCircle != null)
                    {
                        
                        if (_homePosition == null) { ShowStatusMessage(Get("Msg_NoHomePos")); return; }

                        bool success = await _mavlinkService.UploadVtolMission(
                            _homePosition, _startCircle, _waypoints.ToList(), _landingCircle,
                            _vtolTakeoffAltitude, _vtolLandAltitude);

                        if (!success) { ShowStatusMessage(Get("Msg_UploadError")); return; }
                    }
                    else
                    {
                        
                        _mavlinkService.SavePlannedMission(_waypoints.ToList());
                        bool success = await _mavlinkService.UploadPlannedMission();
                        if (!success) { ShowStatusMessage(Get("Msg_UploadError")); return; }
                    }

                    await System.Threading.Tasks.Task.Delay(500);

                    ushort nextSeq = (ushort)(currentSeq + 1);
                    _mavlinkService.SetCurrentWaypoint(nextSeq);

                    await System.Threading.Tasks.Task.Delay(300);
                    _mavlinkService.SetFlightMode("AUTO");

                    _isMissionFrozen = false;

                    if (sender is Button btn)
                    {
                        btn.Content = Get("Msg_FreezeMission");
                        btn.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(37, 99, 235)); 
                    }

                    System.Diagnostics.Debug.WriteLine($"[RESUME] Миссия продолжена с seq={nextSeq}");
                    ShowStatusMessage(Fmt("Msg_MissionResumed", nextSeq));
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

        private void ResumeMissionButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_mavlinkService == null || !_mavlinkService.IsConnected)
                {
                    AppMessageBox.ShowWarning(Get("Msg_DroneNotConnected"), owner: OwnerWindow);
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

                _mavlinkService.SetCurrentWaypoint(nextSeq);

                System.Diagnostics.Debug.WriteLine($"[RESUME] Отправлен MISSION_SET_CURRENT seq={nextSeq}");
            }
            catch (Exception ex)
            {
                AppMessageBox.ShowError($"{Get("MsgBox_Error")}: {ex.Message}", owner: OwnerWindow);
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