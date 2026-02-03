using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace SimpleDroneGCS.Controls
{
    /// <summary>
    /// Кастомный Attitude Indicator (искусственный горизонт) с плавной интерполяцией lololo
    /// 450x250 прямоугольный формат sgdsfdaasfdfddfgs
    /// </summary>
    public class AttitudeIndicator : UserControl
    {
        private Canvas _canvas;
        private Image _rollIndicatorImage;
        private Canvas _pitchLadder;

        // Трансформации
        private RotateTransform _backgroundRollTransform;
        private TranslateTransform _backgroundPitchTransform;
        private TransformGroup _backgroundTransformGroup;
        private RotateTransform _pitchLadderRollTransform;
        private TranslateTransform _pitchLadderPitchTransform;

        // Целевые и текущие значения для плавности
        private double _currentRoll = 0;
        private double _currentPitch = 0;
        private double _targetRoll = 0;
        private double _targetPitch = 0;
        private readonly object _lockObject = new object();

        // Rendering для 60 FPS
        private bool _isRendering = false;

        // Dependency Properties
        public static readonly DependencyProperty RollProperty =
            DependencyProperty.Register("Roll", typeof(double), typeof(AttitudeIndicator),
                new PropertyMetadata(0.0, OnAttitudeChanged));

        public static readonly DependencyProperty PitchProperty =
            DependencyProperty.Register("Pitch", typeof(double), typeof(AttitudeIndicator),
                new PropertyMetadata(0.0, OnAttitudeChanged));

        public double Roll
        {
            get => (double)GetValue(RollProperty);
            set => SetValue(RollProperty, value);
        }

        public double Pitch
        {
            get => (double)GetValue(PitchProperty);
            set => SetValue(PitchProperty, value);
        }

        public AttitudeIndicator()
        {
            Width = 450;
            Height = 250;

            // GPU ускорение
            RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.LowQuality);
            RenderOptions.SetCachingHint(this, CachingHint.Cache);

            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            InitializeIndicator();
            StartRendering();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            StopRendering();
        }

        /// <summary>
        /// Запуск rendering loop на частоте монитора (~60 FPS)
        /// </summary>
        private void StartRendering()
        {
            if (_isRendering) return;
            _isRendering = true;
            CompositionTarget.Rendering += OnRendering;
        }

        private void StopRendering()
        {
            _isRendering = false;
            CompositionTarget.Rendering -= OnRendering;
        }

        /// <summary>
        /// Rendering callback - вызывается на частоте монитора
        /// </summary>
        private void OnRendering(object sender, EventArgs e)
        {
            if (!_isRendering) return;

            // Плавное движение к целевым значениям
            const double smoothing = 0.15;

            lock (_lockObject)
            {
                _currentRoll = Lerp(_currentRoll, _targetRoll, smoothing);
                _currentPitch = Lerp(_currentPitch, _targetPitch, smoothing);
            }

            ApplyTransforms();
        }

        /// <summary>
        /// Линейная интерполяция
        /// </summary>
        private double Lerp(double current, double target, double amount)
        {
            return current + (target - current) * amount;
        }

        /// <summary>
        /// Применение трансформаций к элементам
        /// </summary>
        private void ApplyTransforms()
        {
            if (_backgroundRollTransform == null || _backgroundPitchTransform == null) return;

            double pixelsPerDegree = 2.5; // для 250px высоты

            // ФОН
            _backgroundPitchTransform.Y = _currentPitch * pixelsPerDegree;
            _backgroundRollTransform.Angle = -_currentRoll;

            // PITCH LADDER
            if (_pitchLadderPitchTransform != null && _pitchLadderRollTransform != null)
            {
                _pitchLadderPitchTransform.Y = _currentPitch * pixelsPerDegree;
                _pitchLadderRollTransform.Angle = -_currentRoll;
            }

            // attitude_roll СТАТИЧЕН - не применяем трансформации
            // Обновляем pitch ladder при необходимости
            UpdatePitchLadder(_currentPitch);
        }

        private void InitializeIndicator()
        {
            _canvas = new Canvas
            {
                Width = Width,
                Height = Height,
                ClipToBounds = true,
                Background = new SolidColorBrush(Color.FromRgb(13, 23, 51))
            };

            RenderOptions.SetBitmapScalingMode(_canvas, BitmapScalingMode.LowQuality);
            RenderOptions.SetEdgeMode(_canvas, EdgeMode.Aliased);

            _canvas.Clip = new RectangleGeometry
            {
                Rect = new Rect(0, 0, Width, Height),
                RadiusX = 8,
                RadiusY = 8
            };

            // === ФОН 2000x2000 (гарантированно покрывает pitch ±90° + roll ±180°) ===
            var bg = new Canvas { Width = 2000, Height = 2000 };

            // НЕБО тёмное (верх)
            bg.Children.Add(new Rectangle
            {
                Width = 2000,
                Height = 650,
                Fill = new SolidColorBrush(Color.FromRgb(12, 45, 85))
            });

            // НЕБО градиент
            var skyGrad = new LinearGradientBrush { StartPoint = new Point(0.5, 0), EndPoint = new Point(0.5, 1) };
            skyGrad.GradientStops.Add(new GradientStop(Color.FromRgb(12, 45, 85), 0.0));
            skyGrad.GradientStops.Add(new GradientStop(Color.FromRgb(40, 95, 145), 0.3));
            skyGrad.GradientStops.Add(new GradientStop(Color.FromRgb(78, 145, 200), 0.6));
            skyGrad.GradientStops.Add(new GradientStop(Color.FromRgb(125, 190, 235), 1.0));
            var skyRect = new Rectangle { Width = 2000, Height = 350, Fill = skyGrad };
            Canvas.SetTop(skyRect, 650);
            bg.Children.Add(skyRect);

            // ЗЕМЛЯ градиент
            var gndGrad = new LinearGradientBrush { StartPoint = new Point(0.5, 0), EndPoint = new Point(0.5, 1) };
            gndGrad.GradientStops.Add(new GradientStop(Color.FromRgb(252, 238, 190), 0.0));
            gndGrad.GradientStops.Add(new GradientStop(Color.FromRgb(220, 200, 150), 0.3));
            gndGrad.GradientStops.Add(new GradientStop(Color.FromRgb(180, 160, 120), 0.6));
            gndGrad.GradientStops.Add(new GradientStop(Color.FromRgb(22, 17, 10), 1.0));
            var gndRect = new Rectangle { Width = 2000, Height = 350, Fill = gndGrad };
            Canvas.SetTop(gndRect, 1000);
            bg.Children.Add(gndRect);

            // ЗЕМЛЯ тёмная (низ)
            var gndDark = new Rectangle
            {
                Width = 2000,
                Height = 650,
                Fill = new SolidColorBrush(Color.FromRgb(22, 17, 10))
            };
            Canvas.SetTop(gndDark, 1350);
            bg.Children.Add(gndDark);

            // Линия горизонта
            bg.Children.Add(new Line
            {
                X1 = 0,
                Y1 = 1000,
                X2 = 2000,
                Y2 = 1000,
                Stroke = Brushes.White,
                StrokeThickness = 2,
                Opacity = 0.9
            });

            // Трансформации (центр 1000,1000)
            _backgroundPitchTransform = new TranslateTransform(0, 0);
            _backgroundRollTransform = new RotateTransform(0, 1000, 1000);
            _backgroundTransformGroup = new TransformGroup();
            _backgroundTransformGroup.Children.Add(_backgroundPitchTransform);
            _backgroundTransformGroup.Children.Add(_backgroundRollTransform);
            bg.RenderTransform = _backgroundTransformGroup;

            Canvas.SetLeft(bg, -775);  // (450-2000)/2
            Canvas.SetTop(bg, -875);   // (250-2000)/2
            _canvas.Children.Add(bg);

            // === PITCH LADDER ===
            _pitchLadder = new Canvas { Width = Width * 2, Height = Height * 2 };
            RenderOptions.SetBitmapScalingMode(_pitchLadder, BitmapScalingMode.LowQuality);
            UpdatePitchLadder(0);

            _pitchLadderPitchTransform = new TranslateTransform(0, 0);
            _pitchLadderRollTransform = new RotateTransform(0, Width, Height);
            var plTransform = new TransformGroup();
            plTransform.Children.Add(_pitchLadderPitchTransform);
            plTransform.Children.Add(_pitchLadderRollTransform);
            _pitchLadder.RenderTransform = plTransform;

            Canvas.SetLeft(_pitchLadder, -Width / 2);
            Canvas.SetTop(_pitchLadder, -Height / 2);
            _canvas.Children.Add(_pitchLadder);

            // === ROLL INDICATOR (статичный) ===
            _rollIndicatorImage = LoadImageLayer("Assets/attitude_roll.png");
            if (_rollIndicatorImage != null)
            {
                _rollIndicatorImage.Width = 450;
                _rollIndicatorImage.Height = 158;
                _rollIndicatorImage.Stretch = Stretch.Fill;
                RenderOptions.SetBitmapScalingMode(_rollIndicatorImage, BitmapScalingMode.HighQuality);
                Canvas.SetLeft(_rollIndicatorImage, 0);
                Canvas.SetTop(_rollIndicatorImage, (Height - 158) / 2);
                _canvas.Children.Add(_rollIndicatorImage);
            }

            this.Content = _canvas;
        }

        private Image LoadImageLayer(string path)
        {
            try
            {
                string uriString = $"pack://application:,,,/{path}";
                var uri = new Uri(uriString, UriKind.Absolute);
                var bitmap = new BitmapImage();

                bitmap.BeginInit();
                bitmap.UriSource = uri;
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.EndInit();

                System.Diagnostics.Debug.WriteLine($"✅ Загружено: {path} ({bitmap.PixelWidth}x{bitmap.PixelHeight})");

                return new Image
                {
                    Source = bitmap,
                    Stretch = Stretch.None
                };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"❌ Ошибка {path}: {ex.Message}");
                return null;
            }
        }

        private void UpdatePitchLadder(double currentPitch)
        {
            // Обновляем только если pitch изменился больше чем на 2°

            _pitchLadder.Children.Clear(); // Очищаем старые элементы

            double centerX = _pitchLadder.Width / 2;   // 450 (центр canvas)
            double centerY = _pitchLadder.Height / 2;  // 250
            double pixelsPerDegree = 2.5;

            // Видимая область: 158px / 2.5 = ~63° по вертикали
            // Генерируем ±40° от текущего pitch с запасом
            int startAngle = (int)(currentPitch - 32);
            int endAngle = (int)(currentPitch + 32);

            // Округляем до ближайших 5°
            startAngle = (startAngle / 5) * 5;
            endAngle = (endAngle / 5) * 5;

            // Ограничиваем -90 до +90
            startAngle = Math.Max(-90, startAngle);
            endAngle = Math.Min(90, endAngle);

            for (int angle = startAngle; angle <= endAngle; angle += 5)
            {
                if (angle == 0) continue; // пропускаем горизонт

                double yPos = centerY - (angle * pixelsPerDegree);

                // ЧЕРЕДОВАНИЕ: каждые 10° - длинная, остальные - короткая
                bool isLong = angle % 10 == 0;
                double lineWidth = isLong ? 100 : 50;
                double thickness = isLong ? 3 : 2;

                // ПОЛНАЯ линия по центру
                var line = new Line
                {
                    X1 = centerX - lineWidth / 2,
                    Y1 = yPos,
                    X2 = centerX + lineWidth / 2,
                    Y2 = yPos,
                    Stroke = Brushes.White,
                    StrokeThickness = thickness,
                    SnapsToDevicePixels = true,
                    Opacity = 0.8
                };
                _pitchLadder.Children.Add(line);

                // Цифры ТОЛЬКО каждые 30° и ТОЛЬКО слева
                if (angle % 30 == 0)
                {
                    var textLeft = new TextBlock
                    {
                        Text = Math.Abs(angle).ToString(),
                        Foreground = Brushes.White,
                        FontSize = 16,
                        FontWeight = FontWeights.Bold,
                        Opacity = 0.9
                    };
                    Canvas.SetLeft(textLeft, centerX - lineWidth / 2 - 30);
                    Canvas.SetTop(textLeft, yPos - 12);
                    _pitchLadder.Children.Add(textLeft);
                }
            }
        }

        /// <summary>
        /// Обновление целевых значений
        /// </summary>
        private static void OnAttitudeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is AttitudeIndicator indicator)
            {
                lock (indicator._lockObject)
                {
                    indicator._targetRoll = indicator.Roll;
                    indicator._targetPitch = indicator.Pitch;
                }
            }
        }
    }
}