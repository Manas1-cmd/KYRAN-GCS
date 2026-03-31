using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace SimpleDroneGCS.Controls
{
    public class AttitudeIndicator : UserControl
    {
        private Canvas _canvas;
        private Canvas _pitchLadder;
        private Canvas _rollArc;
        private RotateTransform _rollArcTransform;

        private RotateTransform _backgroundRollTransform;
        private TranslateTransform _backgroundPitchTransform;
        private TransformGroup _backgroundTransformGroup;
        private RotateTransform _pitchLadderRollTransform;
        private TranslateTransform _pitchLadderPitchTransform;

        private double _currentRoll = 0;
        private double _currentPitch = 0;
        private double _targetRoll = 0;
        private double _targetPitch = 0;
        private double _lastPitchForLadder = double.NaN;
        private readonly object _lockObject = new object();

        private bool _isRendering = false;

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

        private void OnRendering(object sender, EventArgs e)
        {
            if (!_isRendering) return;

            const double smoothing = 0.15;

            lock (_lockObject)
            {
                _currentRoll = Lerp(_currentRoll, _targetRoll, smoothing);
                _currentPitch = Lerp(_currentPitch, _targetPitch, smoothing);
            }

            ApplyTransforms();
        }

        private double Lerp(double current, double target, double amount)
        {
            return current + (target - current) * amount;
        }

        private void ApplyTransforms()
        {
            if (_backgroundRollTransform == null || _backgroundPitchTransform == null) return;

            double pixelsPerDegree = 3.5;

            _backgroundPitchTransform.Y = _currentPitch * pixelsPerDegree;
            _backgroundRollTransform.Angle = -_currentRoll;

            if (_pitchLadderPitchTransform != null && _pitchLadderRollTransform != null)
            {
                _pitchLadderPitchTransform.Y = _currentPitch * pixelsPerDegree;
                _pitchLadderRollTransform.Angle = -_currentRoll;
            }

            if (_rollArcTransform != null)
                _rollArcTransform.Angle = -_currentRoll;

            if (double.IsNaN(_lastPitchForLadder) || Math.Abs(_currentPitch - _lastPitchForLadder) > 1.0)
            {
                _lastPitchForLadder = _currentPitch;
                UpdatePitchLadder(_currentPitch);
            }
        }

        private void InitializeIndicator()
        {
            double cx = Width / 2;
            double cy = Height / 2;

            _canvas = new Canvas
            {
                Width = Width,
                Height = Height,
                ClipToBounds = true,
                Background = new SolidColorBrush(Color.FromRgb(6, 11, 26))
            };

            RenderOptions.SetBitmapScalingMode(_canvas, BitmapScalingMode.LowQuality);

            _canvas.Clip = new RectangleGeometry
            {
                Rect = new Rect(0, 0, Width, Height),
                RadiusX = 8,
                RadiusY = 8
            };
          
            var bg = new Canvas { Width = 2000, Height = 2000 };

            bg.Children.Add(new Rectangle
            {
                Width = 2000,
                Height = 650,
                Fill = new SolidColorBrush(Color.FromRgb(18, 38, 70))
            });

            var skyGrad = new LinearGradientBrush
            {
                StartPoint = new Point(0.5, 0),
                EndPoint = new Point(0.5, 1)
            };
            skyGrad.GradientStops.Add(new GradientStop(Color.FromRgb(18, 38, 70), 0.0));
            skyGrad.GradientStops.Add(new GradientStop(Color.FromRgb(30, 60, 110), 0.3));
            skyGrad.GradientStops.Add(new GradientStop(Color.FromRgb(38, 78, 132), 0.6));
            skyGrad.GradientStops.Add(new GradientStop(Color.FromRgb(44, 92, 156), 1.0));
            var skyRect = new Rectangle { Width = 2000, Height = 350, Fill = skyGrad };
            Canvas.SetTop(skyRect, 650);
            bg.Children.Add(skyRect);

            var gndGrad = new LinearGradientBrush
            {
                StartPoint = new Point(0.5, 0),
                EndPoint = new Point(0.5, 1)
            };
            gndGrad.GradientStops.Add(new GradientStop(Color.FromRgb(42, 52, 66), 0.0));
            gndGrad.GradientStops.Add(new GradientStop(Color.FromRgb(36, 44, 56), 0.3));
            gndGrad.GradientStops.Add(new GradientStop(Color.FromRgb(28, 35, 45), 0.6));
            gndGrad.GradientStops.Add(new GradientStop(Color.FromRgb(18, 22, 30), 1.0));
            var gndRect = new Rectangle { Width = 2000, Height = 350, Fill = gndGrad };
            Canvas.SetTop(gndRect, 1000);
            bg.Children.Add(gndRect);

            var gndDark = new Rectangle
            {
                Width = 2000,
                Height = 650,
                Fill = new SolidColorBrush(Color.FromRgb(14, 18, 24))
            };
            Canvas.SetTop(gndDark, 1350);
            bg.Children.Add(gndDark);

            bg.Children.Add(new Line
            {
                X1 = 0,
                Y1 = 1000,
                X2 = 2000,
                Y2 = 1000,
                Stroke = new SolidColorBrush(Color.FromRgb(200, 200, 200)),
                StrokeThickness = 1.2,
                Opacity = 0.5
            });

            _backgroundPitchTransform = new TranslateTransform(0, 0);
            _backgroundRollTransform = new RotateTransform(0, 1000, 1000);
            _backgroundTransformGroup = new TransformGroup();
            _backgroundTransformGroup.Children.Add(_backgroundPitchTransform);
            _backgroundTransformGroup.Children.Add(_backgroundRollTransform);
            bg.RenderTransform = _backgroundTransformGroup;

            Canvas.SetLeft(bg, -775);
            Canvas.SetTop(bg, -875);
            _canvas.Children.Add(bg);

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

          
            double rollRadius = 180;
            double rollCenterY = cy + 65;

            _rollArc = new Canvas { Width = Width, Height = Height, IsHitTestVisible = false };

            _rollArc.Children.Add(new Path
            {
                Stroke = Brushes.White,
                StrokeThickness = 1.5,
                Opacity = 0.7,
                Data = CreateArcGeometry(cx, rollCenterY, rollRadius, -50, 50),
                IsHitTestVisible = false
            });

            int[] rollAngles = { -60, -45, -30, -20, -10, 0, 10, 20, 30, 45, 60 };

            foreach (int angle in rollAngles)
            {
                double rad = (angle - 90) * Math.PI / 180;
                double outerX = cx + rollRadius * Math.Cos(rad);
                double outerY = rollCenterY + rollRadius * Math.Sin(rad);

                double tickLen, thickness;
                if (angle == 0) { tickLen = 18; thickness = 2.5; }
                else if (Math.Abs(angle) == 30 || Math.Abs(angle) == 60) { tickLen = 16; thickness = 2; }
                else if (Math.Abs(angle) == 45) { tickLen = 13; thickness = 1.5; }
                else { tickLen = 9; thickness = 1.2; }

                double innerX = cx + (rollRadius - tickLen) * Math.Cos(rad);
                double innerY = rollCenterY + (rollRadius - tickLen) * Math.Sin(rad);

                if (outerY > cy + 20) continue;

                _rollArc.Children.Add(new Line
                {
                    X1 = outerX,
                    Y1 = outerY,
                    X2 = innerX,
                    Y2 = innerY,
                    Stroke = Brushes.White,
                    StrokeThickness = thickness,
                    Opacity = 0.8
                });
            }

            int[] labelAngles = { -30, 30 };
            foreach (int angle in labelAngles)
            {
                double rad = (angle - 90) * Math.PI / 180;
                double labelR = rollRadius + 14;
                double lx = cx + labelR * Math.Cos(rad);
                double ly = rollCenterY + labelR * Math.Sin(rad);

                var label = new TextBlock
                {
                    Text = "30",
                    Foreground = Brushes.White,
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold,
                    Opacity = 0.75
                };
                label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Canvas.SetLeft(label, lx - label.DesiredSize.Width / 2);
                Canvas.SetTop(label, ly - label.DesiredSize.Height / 2);
                _rollArc.Children.Add(label);
            }

            double triY = rollCenterY - rollRadius;
            _rollArc.Children.Add(new Polygon
            {
                Points = new PointCollection
                {
                    new Point(cx, triY + 2),
                    new Point(cx - 5, triY - 8),
                    new Point(cx + 5, triY - 8)
                },
                Fill = Brushes.White,
                Opacity = 0.85
            });

            _rollArcTransform = new RotateTransform(0, cx, rollCenterY);
            _rollArc.RenderTransform = _rollArcTransform;
            _canvas.Children.Add(_rollArc);

            double ptrY = rollCenterY - rollRadius;
            _canvas.Children.Add(new Polygon
            {
                Points = new PointCollection
                {
                    new Point(cx, ptrY + 2),
                    new Point(cx - 8, ptrY - 11),
                    new Point(cx + 8, ptrY - 11)
                },
                Fill = new SolidColorBrush(Color.FromRgb(152, 240, 25)),
                IsHitTestVisible = false
            });

            var planeColor = new SolidColorBrush(Color.FromRgb(230, 150, 30));

            _canvas.Children.Add(new Line
            {
                X1 = cx - 70,
                Y1 = cy,
                X2 = cx - 8,
                Y2 = cy,
                Stroke = planeColor,
                StrokeThickness = 3,
                IsHitTestVisible = false
            });
            _canvas.Children.Add(new Line
            {
                X1 = cx - 70,
                Y1 = cy,
                X2 = cx - 70,
                Y2 = cy + 8,
                Stroke = planeColor,
                StrokeThickness = 2.5,
                IsHitTestVisible = false
            });

            _canvas.Children.Add(new Line
            {
                X1 = cx + 8,
                Y1 = cy,
                X2 = cx + 70,
                Y2 = cy,
                Stroke = planeColor,
                StrokeThickness = 3,
                IsHitTestVisible = false
            });
            _canvas.Children.Add(new Line
            {
                X1 = cx + 70,
                Y1 = cy,
                X2 = cx + 70,
                Y2 = cy + 8,
                Stroke = planeColor,
                StrokeThickness = 2.5,
                IsHitTestVisible = false
            });

            _canvas.Children.Add(new Line
            {
                X1 = cx,
                Y1 = cy - 10,
                X2 = cx,
                Y2 = cy - 2,
                Stroke = planeColor,
                StrokeThickness = 2.5,
                IsHitTestVisible = false
            });

            var dot = new Ellipse
            {
                Width = 6,
                Height = 6,
                Fill = planeColor
            };
            Canvas.SetLeft(dot, cx - 3);
            Canvas.SetTop(dot, cy - 3);
            _canvas.Children.Add(dot);

            this.Content = _canvas;
        }

        private void UpdatePitchLadder(double currentPitch)
        {
            _pitchLadder.Children.Clear();

            double centerX = _pitchLadder.Width / 2;
            double centerY = _pitchLadder.Height / 2;
            double pixelsPerDegree = 3.5;

            int startAngle = ((int)(currentPitch - 25) / 5) * 5;
            int endAngle = ((int)(currentPitch + 25) / 5) * 5;
            startAngle = Math.Max(-90, startAngle);
            endAngle = Math.Min(90, endAngle);

            for (int angle = startAngle; angle <= endAngle; angle += 5)
            {
                if (angle == 0) continue;

                double yPos = centerY - (angle * pixelsPerDegree);
                bool isMajor = angle % 10 == 0;
                double halfLen = isMajor ? 55 : 20;
                double thickness = isMajor ? 2 : 1;
                double opacity = isMajor ? 0.75 : 0.35;

                _pitchLadder.Children.Add(new Line
                {
                    X1 = centerX - halfLen,
                    Y1 = yPos,
                    X2 = centerX + halfLen,
                    Y2 = yPos,
                    Stroke = Brushes.White,
                    StrokeThickness = thickness,
                    Opacity = opacity
                });

                if (isMajor)
                {
                    var text = new TextBlock
                    {
                        Text = Math.Abs(angle).ToString(),
                        Foreground = Brushes.White,
                        FontSize = 13,
                        FontWeight = FontWeights.SemiBold,
                        Opacity = 0.8
                    };
                    Canvas.SetLeft(text, centerX - halfLen - 24);
                    Canvas.SetTop(text, yPos - 9);
                    _pitchLadder.Children.Add(text);
                }
            }
        }

        private Geometry CreateArcGeometry(double cx, double cy, double r, double startAngleDeg, double endAngleDeg)
        {
            double startRad = (startAngleDeg - 90) * Math.PI / 180;
            double endRad = (endAngleDeg - 90) * Math.PI / 180;

            var start = new Point(cx + r * Math.Cos(startRad), cy + r * Math.Sin(startRad));
            var end = new Point(cx + r * Math.Cos(endRad), cy + r * Math.Sin(endRad));

            bool isLargeArc = Math.Abs(endAngleDeg - startAngleDeg) > 180;

            var figure = new PathFigure { StartPoint = start };
            figure.Segments.Add(new ArcSegment
            {
                Point = end,
                Size = new Size(r, r),
                IsLargeArc = isLargeArc,
                SweepDirection = SweepDirection.Clockwise
            });

            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);
            return geometry;
        }

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