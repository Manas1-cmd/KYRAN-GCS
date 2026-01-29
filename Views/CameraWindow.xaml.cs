using SimpleDroneGCS.Services;
using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Diagnostics;
using LibVLCSharp.Shared;
using LibVLCSharp.WPF;

namespace SimpleDroneGCS.Views
{
    public partial class CameraWindow : Window
    {
        private readonly Z30TCameraService _camera;
        private readonly MAVLinkService _mavlink;
        private readonly CameraConnectionSettings _settings;
        private DispatcherTimer _gimbalTimer;
        private bool _isPanning = false;
        private bool _isTilting = false;
        private bool _isZooming = false;
        private int _panDirection = 0;
        private int _tiltDirection = 0;
        private int _zoomDirection = 0;

        // LibVLCSharp для видеопотока
        private LibVLC _libVLC;
        private LibVLCSharp.Shared.MediaPlayer _mediaPlayer;
        private VideoView _videoView;

        public CameraWindow(CameraConnectionSettings settings, MAVLinkService mavlink = null)
        {
            InitializeComponent();

            _settings = settings;
            _camera = Z30TCameraService.Instance;
            _mavlink = mavlink;

            // Инициализация LibVLC
            InitializeVideoPlayer();

            InitializeCamera();
            InitializeHUD();
            SetupTimers();
            InitializeGimbalSliders();

            if (_mavlink != null)
            {
                _mavlink.TelemetryReceived += OnMavlinkTelemetry;
            }

            // Позиционируем на второй монитор после загрузки
            Loaded += (s, e) => PositionOnSecondaryMonitor();

            // Автоподключение к камере
            Loaded += async (s, e) => await ConnectToCameraAsync();
        }

        #region Initialization

        private void InitializeVideoPlayer()
        {
            try
            {
                // Инициализация LibVLC (один раз)
                Core.Initialize();

                // Параметры для минимальной задержки
                _libVLC = new LibVLC(
                    "--network-caching=100",      // Минимальный сетевой буфер (мс)
                    "--live-caching=100",         // Буфер для live потока
                    "--clock-jitter=0",
                    "--clock-synchro=0",
                    "--no-audio"                  // Без звука для меньшей задержки
                );

                _mediaPlayer = new LibVLCSharp.Shared.MediaPlayer(_libVLC);

                // Создаём VideoView и добавляем в контейнер
                _videoView = new VideoView
                {
                    MediaPlayer = _mediaPlayer,
                    Background = Brushes.Black
                };

                // Добавляем VideoView в ContentControl
                if (VideoViewHost != null)
                {
                    VideoViewHost.Content = _videoView;
                }

                Debug.WriteLine("[Video] LibVLC initialized");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Video] LibVLC init error: {ex.Message}");
            }
        }

        private void StartVideoStream(string rtspUrl)
        {
            try
            {
                if (_mediaPlayer == null || _libVLC == null)
                {
                    Debug.WriteLine("[Video] MediaPlayer not initialized");
                    return;
                }

                // Создаём медиа с RTSP URL
                using var media = new Media(_libVLC, new Uri(rtspUrl));

                // Дополнительные опции для низкой задержки
                media.AddOption(":rtsp-tcp");              // TCP вместо UDP (надёжнее)
                media.AddOption(":network-caching=100");
                media.AddOption(":live-caching=100");

                _mediaPlayer.Play(media);

                // Скрываем placeholder
                if (NoVideoText != null)
                    NoVideoText.Visibility = Visibility.Collapsed;

                Debug.WriteLine($"[Video] Started stream: {rtspUrl}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Video] Start stream error: {ex.Message}");
                if (NoVideoText != null)
                {
                    NoVideoText.Text = $"❌ Ошибка видео: {ex.Message}";
                    NoVideoText.Visibility = Visibility.Visible;
                }
            }
        }

        private void StopVideoStream()
        {
            try
            {
                _mediaPlayer?.Stop();
                Debug.WriteLine("[Video] Stream stopped");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Video] Stop error: {ex.Message}");
            }
        }

        private void InitializeCamera()
        {
            if (_camera == null) return;

            _camera.ConnectionChanged += OnCameraConnectionChanged;
            _camera.StatusMessage += OnCameraStatusMessage;

            UpdateConnectionUI(false);
        }

        private async System.Threading.Tasks.Task ConnectToCameraAsync()
        {
            if (_settings == null || _camera == null) return;

            if (CameraStatusText != null)
                CameraStatusText.Text = $"Подключение к {_settings.CameraIP}:{_settings.TcpPort}...";

            bool connected = await _camera.ConnectAsync(_settings.CameraIP, _settings.TcpPort);

            if (connected)
            {
                UpdateConnectionUI(true);

                // Запускаем RTSP видеопоток
                if (!string.IsNullOrEmpty(_settings.RtspUrl))
                {
                    StartVideoStream(_settings.RtspUrl);
                }
            }
            else
            {
                if (CameraStatusText != null)
                    CameraStatusText.Text = $"Ошибка подключения к {_settings.CameraIP}";

                if (NoVideoText != null)
                    NoVideoText.Text = $"❌ Камера недоступна\n{_settings.CameraIP}:{_settings.TcpPort}";
            }
        }

        private void InitializeHUD()
        {
            Loaded += (s, e) => DrawHUD();
            SizeChanged += (s, e) => DrawHUD();
        }

        private void SetupTimers()
        {
            _gimbalTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(100)
            };
            _gimbalTimer.Tick += GimbalTimer_Tick;
            _gimbalTimer.Start();
        }

        #region Multi-Monitor Support (WPF Native - No Windows.Forms)

        [DllImport("user32.dll")]
        private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip,
            MonitorEnumProc lpfnEnum, IntPtr dwData);

        private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor,
            ref RECT lprcMonitor, IntPtr dwData);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        private System.Collections.Generic.List<RECT> _monitors = new();

        private void PositionOnSecondaryMonitor()
        {
            try
            {
                _monitors.Clear();

                EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
                    (IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData) =>
                    {
                        _monitors.Add(lprcMonitor);
                        return true;
                    }, IntPtr.Zero);

                if (_monitors.Count > 1)
                {
                    // Берём второй монитор
                    var secondary = _monitors[1];

                    this.WindowStartupLocation = WindowStartupLocation.Manual;
                    this.Left = secondary.Left;
                    this.Top = secondary.Top;
                    this.Width = secondary.Right - secondary.Left;
                    this.Height = secondary.Bottom - secondary.Top;
                    this.WindowState = WindowState.Maximized;

                    Debug.WriteLine($"[CameraWindow] Positioned on secondary monitor: {secondary.Left},{secondary.Top}");
                }
                else
                {
                    // Один монитор - по центру
                    this.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                    Debug.WriteLine("[CameraWindow] Single monitor - centered");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CameraWindow] Monitor detection error: {ex.Message}");
                this.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }
        }

        #endregion

        #endregion

        #region HUD Drawing

        private void DrawHUD()
        {
            if (HudCanvas == null) return;

            HudCanvas.Children.Clear();

            double centerX = HudCanvas.ActualWidth / 2;
            double centerY = HudCanvas.ActualHeight / 2;

            if (centerX <= 0 || centerY <= 0) return;

            var hudColor = new SolidColorBrush(Color.FromRgb(152, 240, 25));
            hudColor.Opacity = 0.8;

            // Центральное перекрестье
            var hLine = new Line
            {
                X1 = centerX - 40,
                Y1 = centerY,
                X2 = centerX + 40,
                Y2 = centerY,
                Stroke = hudColor,
                StrokeThickness = 1.5
            };
            HudCanvas.Children.Add(hLine);

            var vLine = new Line
            {
                X1 = centerX,
                Y1 = centerY - 40,
                X2 = centerX,
                Y2 = centerY + 40,
                Stroke = hudColor,
                StrokeThickness = 1.5
            };
            HudCanvas.Children.Add(vLine);

            var centerDot = new Ellipse
            {
                Width = 6,
                Height = 6,
                Fill = hudColor
            };
            Canvas.SetLeft(centerDot, centerX - 3);
            Canvas.SetTop(centerDot, centerY - 3);
            HudCanvas.Children.Add(centerDot);

            // Диагональные маркеры
            double markerSize = 20;
            double offset = 60;
            var dashStyle = new DoubleCollection { 4, 4 };

            AddDashedLine(centerX - offset, centerY - offset, centerX - offset - markerSize, centerY - offset - markerSize, hudColor, dashStyle);
            AddDashedLine(centerX + offset, centerY - offset, centerX + offset + markerSize, centerY - offset - markerSize, hudColor, dashStyle);
            AddDashedLine(centerX - offset, centerY + offset, centerX - offset - markerSize, centerY + offset + markerSize, hudColor, dashStyle);
            AddDashedLine(centerX + offset, centerY + offset, centerX + offset + markerSize, centerY + offset + markerSize, hudColor, dashStyle);
        }

        private void AddDashedLine(double x1, double y1, double x2, double y2, Brush stroke, DoubleCollection dashArray)
        {
            if (HudCanvas == null) return;

            var line = new Line
            {
                X1 = x1,
                Y1 = y1,
                X2 = x2,
                Y2 = y2,
                Stroke = stroke,
                StrokeThickness = 1,
                StrokeDashArray = dashArray
            };
            HudCanvas.Children.Add(line);
        }

        #endregion

        #region Connection

        private async void BtnConnect_Click(object sender, RoutedEventArgs e)
        {
            if (_camera == null) return;

            if (_camera.IsConnected)
            {
                _camera.Disconnect();
                StopVideoStream();

                if (NoVideoText != null)
                {
                    NoVideoText.Text = "📹 Видеопоток остановлен";
                    NoVideoText.Visibility = Visibility.Visible;
                }
            }
            else
            {
                if (_settings == null) return;

                if (BtnConnect != null) BtnConnect.IsEnabled = false;
                if (ConnectButtonText != null) ConnectButtonText.Text = "Connecting...";

                bool success = await _camera.ConnectAsync(_settings.CameraIP, _settings.TcpPort);

                if (BtnConnect != null) BtnConnect.IsEnabled = true;
                UpdateConnectionUI(success);

                if (success && !string.IsNullOrEmpty(_settings.RtspUrl))
                {
                    StartVideoStream(_settings.RtspUrl);
                }
            }
        }

        private void OnCameraConnectionChanged(object sender, bool connected)
        {
            Dispatcher.Invoke(() => UpdateConnectionUI(connected));
        }

        private void OnCameraStatusMessage(object sender, string message)
        {
            Dispatcher.Invoke(() =>
            {
                if (CameraStatusText != null)
                    CameraStatusText.Text = message;
                Debug.WriteLine($"[Camera] {message}");
            });
        }

        private void UpdateConnectionUI(bool connected)
        {
            if (connected)
            {
                if (CameraConnectionIndicator != null)
                    CameraConnectionIndicator.Fill = new SolidColorBrush(Color.FromRgb(152, 240, 25));
                if (CameraStatusText != null)
                    CameraStatusText.Text = "Камера подключена";
                if (ConnectButtonText != null)
                    ConnectButtonText.Text = "Disconnect";
                if (BtnConnect != null)
                    BtnConnect.Background = new SolidColorBrush(Color.FromRgb(239, 68, 68));
            }
            else
            {
                if (CameraConnectionIndicator != null)
                    CameraConnectionIndicator.Fill = Brushes.Red;
                if (CameraStatusText != null)
                    CameraStatusText.Text = "Камера не подключена";
                if (ConnectButtonText != null)
                    ConnectButtonText.Text = "Connect";
                if (BtnConnect != null)
                    BtnConnect.Background = new SolidColorBrush(Color.FromRgb(26, 39, 68));
            }
        }

        #endregion

        #region Camera Mode

        private void BtnEO_Click(object sender, RoutedEventArgs e)
        {
            _camera?.SwitchToEO();
            if (BtnEO != null) BtnEO.Style = FindResource("CameraButtonActiveStyle") as Style;
            if (BtnIR != null) BtnIR.Style = FindResource("CameraButtonStyle") as Style;
        }

        private void BtnIR_Click(object sender, RoutedEventArgs e)
        {
            _camera?.SwitchToIR();
            if (BtnIR != null) BtnIR.Style = FindResource("CameraButtonActiveStyle") as Style;
            if (BtnEO != null) BtnEO.Style = FindResource("CameraButtonStyle") as Style;
        }

        private void PaletteComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (PaletteComboBox?.SelectedIndex >= 0)
            {
                _camera?.SetIRPalette(PaletteComboBox.SelectedIndex);
            }
        }

        #endregion

        #region Gimbal Control

        private void GimbalTimer_Tick(object sender, EventArgs e)
        {
            if (_camera == null) return;

            if (_isPanning && _panDirection != 0)
            {
                if (_panDirection < 0) _camera.PanLeft();
                else _camera.PanRight();
            }

            if (_isTilting && _tiltDirection != 0)
            {
                if (_tiltDirection > 0) _camera.TiltUp();
                else _camera.TiltDown();
            }

            if (_isZooming && _zoomDirection != 0)
            {
                if (_zoomDirection > 0) _camera.ZoomIn();
                else _camera.ZoomOut();
            }
        }

        private void BtnPanLeft_MouseDown(object sender, MouseButtonEventArgs e)
        {
            _isPanning = true;
            _panDirection = -1;
            _camera?.PanLeft();
        }

        private void BtnPanRight_MouseDown(object sender, MouseButtonEventArgs e)
        {
            _isPanning = true;
            _panDirection = 1;
            _camera?.PanRight();
        }

        private void BtnPan_MouseUp(object sender, MouseButtonEventArgs e)
        {
            _isPanning = false;
            _panDirection = 0;
        }

        private void BtnTiltUp_MouseDown(object sender, MouseButtonEventArgs e)
        {
            _isTilting = true;
            _tiltDirection = 1;
            _camera?.TiltUp();
        }

        private void BtnTiltDown_MouseDown(object sender, MouseButtonEventArgs e)
        {
            _isTilting = true;
            _tiltDirection = -1;
            _camera?.TiltDown();
        }

        private void BtnTilt_MouseUp(object sender, MouseButtonEventArgs e)
        {
            _isTilting = false;
            _tiltDirection = 0;
        }

        private void BtnZoomIn_MouseDown(object sender, MouseButtonEventArgs e)
        {
            _isZooming = true;
            _zoomDirection = 1;
            _camera?.ZoomIn();
        }

        private void BtnZoomOut_MouseDown(object sender, MouseButtonEventArgs e)
        {
            _isZooming = true;
            _zoomDirection = -1;
            _camera?.ZoomOut();
        }

        private void BtnZoom_MouseUp(object sender, MouseButtonEventArgs e)
        {
            _isZooming = false;
            _zoomDirection = 0;
            _camera?.ZoomStop();  // Отправляем команду остановки!
        }

        private void BtnHome_Click(object sender, RoutedEventArgs e)
        {
            _camera?.ReturnToHome();
        }

        #endregion

        #region Tracking

        private void BtnTracking_Click(object sender, RoutedEventArgs e)
        {
            _camera?.ToggleTracking();

            if (_camera?.IsTracking == true)
            {
                if (BtnTracking != null) BtnTracking.Style = FindResource("CameraButtonActiveStyle") as Style;
                if (TrackStatusText != null)
                {
                    TrackStatusText.Text = "ON";
                    TrackStatusText.Foreground = new SolidColorBrush(Color.FromRgb(74, 222, 128));
                }
            }
            else
            {
                if (BtnTracking != null) BtnTracking.Style = FindResource("CameraButtonStyle") as Style;
                if (TrackStatusText != null)
                {
                    TrackStatusText.Text = "OFF";
                    TrackStatusText.Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68));
                }
            }
        }

        #endregion

        // Добавь в CameraWindow.xaml.cs

        #region Gimbal Slider Control

        private bool _isUpdatingSliders = false;
        private DispatcherTimer _sliderDebounceTimer;
        private double _pendingYaw = 0;
        private double _pendingPitch = 0;

        /// <summary>
        /// Инициализация слайдеров gimbal (вызови в конструкторе)
        /// </summary>
        private void InitializeGimbalSliders()
        {
            _sliderDebounceTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(150)  // Каждые 150мс
            };
            _sliderDebounceTimer.Tick += SliderDebounceTimer_Tick;
            _sliderDebounceTimer.Start();  // Запускаем сразу и работает всегда
        }

        /// <summary>
        /// Изменение Yaw (горизонталь)
        /// </summary>
        private void YawSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isUpdatingSliders) return;
            _pendingYaw = e.NewValue;
        }

        private void PitchSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isUpdatingSliders) return;
            _pendingPitch = e.NewValue;
        }

        /// <summary>
        /// Отправка команды после паузы (debounce)
        /// </summary>
        private void SliderDebounceTimer_Tick(object sender, EventArgs e)
        {
            // Не останавливаем таймер - пусть тикает непрерывно

            if (_camera == null || !_camera.IsConnected) return;

            // Отправляем команды только если слайдер не в центре
            if (Math.Abs(_pendingYaw) > 5)
                _camera.SetYawAngle(_pendingYaw);

            if (Math.Abs(_pendingPitch) > 5)
                _camera.SetPitchAngle(_pendingPitch);
        }

        /// <summary>
        /// Сброс gimbal в домашнюю позицию
        /// </summary>
        private void BtnGimbalHome_Click(object sender, RoutedEventArgs e)
        {
            _isUpdatingSliders = true;

            // Устанавливаем слайдеры в 0
            if (YawSlider != null) YawSlider.Value = 0;
            if (PitchSlider != null) PitchSlider.Value = 0;

            _isUpdatingSliders = false;

            // Отправляем команду Home
            _camera?.ReturnToHome();

            Debug.WriteLine("[Gimbal] Home position");
        }

        /// <summary>
        /// Обновить слайдеры из текущих значений (если получаем телеметрию от камеры)
        /// </summary>
        private void UpdateSlidersFromTelemetry(double yaw, double pitch)
        {
            _isUpdatingSliders = true;

            if (YawSlider != null)
            {
                YawSlider.Value = Math.Clamp(yaw, -180, 180);
            }

            if (PitchSlider != null)
            {
                PitchSlider.Value = Math.Clamp(pitch, -90, 90);
            }

            _isUpdatingSliders = false;
        }

        #endregion

        // НЕ ЗАБУДЬ:
        // 1. Добавить вызов InitializeGimbalSliders() в конструктор CameraWindow
        // 2. Добавить _sliderDebounceTimer?.Stop() в Window_Closing


        #region Other Functions

        private void BtnOSD_Click(object sender, RoutedEventArgs e)
        {
            _camera?.ToggleOSD();

            if (BtnOSD != null)
            {
                // Зелёный когда OSD выключен (чистое видео), обычный когда включен
                BtnOSD.Background = _camera?.IsOSDOn == false
                    ? new SolidColorBrush(Color.FromRgb(152, 240, 25))
                    : new SolidColorBrush(Color.FromRgb(26, 39, 68));
            }
        }

        private void BtnSnapshot_Click(object sender, RoutedEventArgs e)
        {
            _camera?.TakeSnapshot();
        }
        private void BtnRecord_Click(object sender, RoutedEventArgs e)
        {
            _camera?.ToggleRecord();

            if (_camera?.IsRecording == true)
            {
                if (BtnRecord != null) BtnRecord.Background = new SolidColorBrush(Color.FromRgb(239, 68, 68));
                if (RecordStatusText != null)
                {
                    RecordStatusText.Text = "REC";
                    RecordStatusText.Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68));
                }
            }
            else
            {
                if (BtnRecord != null) BtnRecord.Background = new SolidColorBrush(Color.FromRgb(26, 39, 68));
                if (RecordStatusText != null)
                {
                    RecordStatusText.Text = "OFF";
                    RecordStatusText.Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68));
                }
            }
        }

        private void BtnLaser_Click(object sender, RoutedEventArgs e)
        {
            _camera?.ToggleLaser();

            if (BtnLaser != null)
            {
                BtnLaser.Background = _camera?.IsLaserOn == true
                    ? new SolidColorBrush(Color.FromRgb(239, 68, 68))
                    : new SolidColorBrush(Color.FromRgb(26, 39, 68));
            }
        }

        private void BtnFillLight_Click(object sender, RoutedEventArgs e)
        {
            _camera?.ToggleFillLight();

            if (BtnFillLight != null)
            {
                BtnFillLight.Background = _camera?.IsLightOn == true
                    ? new SolidColorBrush(Color.FromRgb(250, 204, 21))
                    : new SolidColorBrush(Color.FromRgb(26, 39, 68));
            }
        }

        private void BtnTemp_Click(object sender, RoutedEventArgs e)
        {
            _camera?.MeasureTemperature();
        }

        #endregion

        #region MAVLink Telemetry

        private void OnMavlinkTelemetry(object sender, EventArgs e)
        {
            if (_mavlink?.CurrentTelemetry == null) return;

            Dispatcher.Invoke(() =>
            {
                var tel = _mavlink.CurrentTelemetry;

                if (VehicleLatText != null) VehicleLatText.Text = tel.Latitude.ToString("F6");
                if (VehicleAltText != null) VehicleAltText.Text = tel.Altitude.ToString("F1");
                if (VehicleHeadingText != null) VehicleHeadingText.Text = tel.Heading.ToString("F1");
            });
        }

        #endregion

        #region Window Chrome

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                MaximizeButton_Click(null, null);
            }
            else
            {
                DragMove();
            }
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void MaximizeButton_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            _gimbalTimer?.Stop();

            // Останавливаем видеопоток
            StopVideoStream();

            // Освобождаем ресурсы LibVLC
            _mediaPlayer?.Dispose();
            _libVLC?.Dispose();

            if (_mavlink != null)
            {
                _mavlink.TelemetryReceived -= OnMavlinkTelemetry;
            }

            if (_camera != null)
            {
                _camera.ConnectionChanged -= OnCameraConnectionChanged;
                _camera.StatusMessage -= OnCameraStatusMessage;
            }

            Debug.WriteLine("[CameraWindow] Closed");
        }

        #endregion
    }
}
