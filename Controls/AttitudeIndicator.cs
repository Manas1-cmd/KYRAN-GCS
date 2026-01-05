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
    /// Кастомный Attitude Indicator (искусственный горизонт) с плавной интерполяцией
    /// 450x250 прямоугольный формат
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
            // Главный Canvas
            _canvas = new Canvas
            {
                Width = Width,
                Height = Height,
                ClipToBounds = true,
                Background = new SolidColorBrush(Color.FromRgb(13, 23, 51))
            };

            RenderOptions.SetBitmapScalingMode(_canvas, BitmapScalingMode.LowQuality);
            RenderOptions.SetEdgeMode(_canvas, EdgeMode.Aliased);

            // Прямоугольный клиппинг
            var clipGeometry = new RectangleGeometry
            {
                Rect = new Rect(0, 0, Width, Height),
                RadiusX = 8,
                RadiusY = 8
            };
            _canvas.Clip = clipGeometry;

            // === СЛОЙ 1: ФОН (программный Canvas с небом/землёй, двигается + вращается) ===
            var backgroundCanvas = new Canvas
            {
                Width = 1000,
                Height = 1000
            };

            // === НЕБО: градиент только в 125px у горизонта, остальное - тёмный фон ===
            // Тёмный фон вверху (375px)
            var skyDark = new Rectangle
            {
                Width = 1000,
                Height = 375,
                Fill = new SolidColorBrush(Color.FromRgb(12, 45, 85))  // Самый тёмный синий
            };
            Canvas.SetLeft(skyDark, 0);
            Canvas.SetTop(skyDark, 0);
            backgroundCanvas.Children.Add(skyDark);

            // Градиент неба (125px у горизонта) - от тёмного к светлому
            var skyGradient = new LinearGradientBrush
            {
                StartPoint = new Point(0.5, 0),    // Верх градиента (тёмный)
                EndPoint = new Point(0.5, 1)       // Горизонт (светлый)
            };
            skyGradient.GradientStops.Add(new GradientStop(Color.FromRgb(12, 45, 85), 0.0));       // Тёмно-синий
            skyGradient.GradientStops.Add(new GradientStop(Color.FromRgb(25, 70, 115), 0.15));
            skyGradient.GradientStops.Add(new GradientStop(Color.FromRgb(40, 95, 145), 0.3));
            skyGradient.GradientStops.Add(new GradientStop(Color.FromRgb(58, 120, 175), 0.5));
            skyGradient.GradientStops.Add(new GradientStop(Color.FromRgb(78, 145, 200), 0.7));
            skyGradient.GradientStops.Add(new GradientStop(Color.FromRgb(100, 170, 220), 0.85));
            skyGradient.GradientStops.Add(new GradientStop(Color.FromRgb(125, 190, 235), 1.0));    // Светло-голубой у горизонта

            var skyGradientRect = new Rectangle
            {
                Width = 1000,
                Height = 125,
                Fill = skyGradient
            };
            Canvas.SetLeft(skyGradientRect, 0);
            Canvas.SetTop(skyGradientRect, 375);  // После тёмного фона
            backgroundCanvas.Children.Add(skyGradientRect);

            // === ЗЕМЛЯ: градиент только в 125px у горизонта, остальное - тёмный фон ===
            // Градиент земли (125px у горизонта) - от светлого к тёмному
            var groundGradient = new LinearGradientBrush
            {
                StartPoint = new Point(0.5, 0),    // Горизонт (светлый)
                EndPoint = new Point(0.5, 1)       // Низ градиента (тёмный)
            };
            groundGradient.GradientStops.Add(new GradientStop(Color.FromRgb(252, 238, 190), 0.0));  // Почти свет
            groundGradient.GradientStops.Add(new GradientStop(Color.FromRgb(250, 235, 185), 0.08));
            groundGradient.GradientStops.Add(new GradientStop(Color.FromRgb(247, 232, 180), 0.16));
            groundGradient.GradientStops.Add(new GradientStop(Color.FromRgb(244, 228, 175), 0.24));
            groundGradient.GradientStops.Add(new GradientStop(Color.FromRgb(240, 223, 170), 0.32));
            groundGradient.GradientStops.Add(new GradientStop(Color.FromRgb(235, 218, 165), 0.40));
            groundGradient.GradientStops.Add(new GradientStop(Color.FromRgb(230, 212, 160), 0.48));
            groundGradient.GradientStops.Add(new GradientStop(Color.FromRgb(224, 205, 155), 0.56));
            groundGradient.GradientStops.Add(new GradientStop(Color.FromRgb(218, 198, 150), 0.64));
            groundGradient.GradientStops.Add(new GradientStop(Color.FromRgb(212, 192, 145), 0.72));
            groundGradient.GradientStops.Add(new GradientStop(Color.FromRgb(206, 186, 140), 0.80));
            groundGradient.GradientStops.Add(new GradientStop(Color.FromRgb(200, 180, 135), 0.88));
            groundGradient.GradientStops.Add(new GradientStop(Color.FromRgb(195, 175, 130), 1.0));  // Очень мягкий низ




            var groundGradientRect = new Rectangle
            {
                Width = 1000,
                Height = 125,
                Fill = groundGradient
            };
            Canvas.SetLeft(groundGradientRect, 0);
            Canvas.SetTop(groundGradientRect, 500);  // Сразу после горизонта
            backgroundCanvas.Children.Add(groundGradientRect);

            // Тёмный фон внизу (375px)
            var groundDark = new Rectangle
            {
                Width = 1000,
                Height = 375,
                Fill = new SolidColorBrush(Color.FromRgb(22, 17, 10))  // Самый тёмный коричневый
            };
            Canvas.SetLeft(groundDark, 0);
            Canvas.SetTop(groundDark, 625);  // После градиента
            backgroundCanvas.Children.Add(groundDark);

            // === ЛИНИЯ ГОРИЗОНТА с лёгким свечением ===
            var horizonGlow = new Rectangle
            {
                Width = 1000,
                Height = 8,
                Fill = new LinearGradientBrush
                {
                    StartPoint = new Point(0.5, 0),
                    EndPoint = new Point(0.5, 1),
                    GradientStops = new GradientStopCollection
                    {
                        new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.0),
                        new GradientStop(Color.FromArgb(60, 255, 255, 255), 0.5),
                        new GradientStop(Color.FromArgb(0, 255, 255, 255), 1.0)
                    }
                }
            };
            Canvas.SetLeft(horizonGlow, 0);
            Canvas.SetTop(horizonGlow, 496);
            backgroundCanvas.Children.Add(horizonGlow);

            // Основная линия горизонта
            var horizonLine = new Line
            {
                X1 = 0,
                Y1 = 500,
                X2 = 1000,
                Y2 = 500,
                Stroke = Brushes.White,
                StrokeThickness = 2,
                Opacity = 0.9
            };
            backgroundCanvas.Children.Add(horizonLine);

            // Трансформации для фона (вращение вокруг центра 500,500)
            _backgroundRollTransform = new RotateTransform(0, 500, 500);
            _backgroundPitchTransform = new TranslateTransform(0, 0);
            _backgroundTransformGroup = new TransformGroup();
            _backgroundTransformGroup.Children.Add(_backgroundPitchTransform);
            _backgroundTransformGroup.Children.Add(_backgroundRollTransform);
            backgroundCanvas.RenderTransform = _backgroundTransformGroup;

            // Центрируем фон: (450 - 1000) / 2 = -275, (250 - 1000) / 2 = -375
            Canvas.SetLeft(backgroundCanvas, -275);
            Canvas.SetTop(backgroundCanvas, -375);
            _canvas.Children.Add(backgroundCanvas);

            // === СЛОЙ 2: PITCH LADDER (программный, двигается + вращается) ===
            _pitchLadder = new Canvas
            {
                Width = Width * 2,      // 900x500
                Height = Height * 2
            };

            RenderOptions.SetBitmapScalingMode(_pitchLadder, BitmapScalingMode.LowQuality);
            RenderOptions.SetCachingHint(_pitchLadder, CachingHint.Cache);

            // КЛИППИНГ для pitch ladder - ограничиваем видимую область
            


            UpdatePitchLadder(0); // Инициализация с pitch = 0

            // Центр вращения pitch ladder = его центр (450, 250)
            _pitchLadderRollTransform = new RotateTransform(0, Width, Height);
            _pitchLadderPitchTransform = new TranslateTransform(0, 0);
            var pitchLadderTransformGroup = new TransformGroup();
            pitchLadderTransformGroup.Children.Add(_pitchLadderPitchTransform);
            pitchLadderTransformGroup.Children.Add(_pitchLadderRollTransform);
            _pitchLadder.RenderTransform = pitchLadderTransformGroup;

            // Позиция pitch ladder
            Canvas.SetLeft(_pitchLadder, -Width / 2);   // -225
            Canvas.SetTop(_pitchLadder, -Height / 2);   // -125
            _canvas.Children.Add(_pitchLadder);

            // === СЛОЙ 3: ROLL INDICATOR (attitude_roll.png = ЦЕНТРАЛЬНЫЙ МАРКЕР) ===
            // СТАТИЧНЫЙ! НЕ вращается, НЕ двигается - представляет крылья самолета
            _rollIndicatorImage = LoadImageLayer("Assets/attitude_roll.png");
            if (_rollIndicatorImage != null)
            {
                // Масштабируем: 450x158
                _rollIndicatorImage.Width = 450;
                _rollIndicatorImage.Height = 158;
                _rollIndicatorImage.Stretch = Stretch.Fill;

                RenderOptions.SetBitmapScalingMode(_rollIndicatorImage, BitmapScalingMode.HighQuality);

                // СТАТИЧНЫЙ - НЕ применяем трансформации!

                Canvas.SetLeft(_rollIndicatorImage, 0);
                Canvas.SetTop(_rollIndicatorImage, (Height - 158) / 2); // = 46
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