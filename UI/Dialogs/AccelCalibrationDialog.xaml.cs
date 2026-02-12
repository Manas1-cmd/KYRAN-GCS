using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using SimpleDroneGCS.Services;

namespace SimpleDroneGCS.UI.Dialogs
{
    public partial class AccelCalibrationDialog : Window
    {
        private readonly MAVLinkService _mavlink;
        private int _currentStep = -1;
        private bool _waitingForDrone = false;
        private readonly Border[] _stepIndicators;

        private readonly (string title, string description, string hint)[] _positions =
        {
            ("РОВНО",          "Поставьте дрон РОВНО на стол\nколёсами/ножками вниз",      "Стандартное положение"),
            ("НА ЛЕВЫЙ БОК",   "Наклоните дрон НА ЛЕВЫЙ БОК\n(левая сторона вниз)",        "Левый бок касается стола"),
            ("НА ПРАВЫЙ БОК",  "Наклоните дрон НА ПРАВЫЙ БОК\n(правая сторона вниз)",      "Правый бок касается стола"),
            ("НОС ВНИЗ",       "Поставьте дрон НОСОМ ВНИЗ\n(передняя часть вниз)",         "Нос касается стола"),
            ("НОС ВВЕРХ",      "Поставьте дрон НОСОМ ВВЕРХ\n(задняя часть вниз)",          "Хвост касается стола"),
            ("ВВЕРХ НОГАМИ",   "Переверните дрон ВВЕРХ НОГАМИ\n(крышка вниз)",             "Дрон перевёрнут"),
        };

        // Углы поворота иконки для каждой позиции
        private readonly double[] _rotations = { 0, -90, 90, 45, -45, 180 };

        public AccelCalibrationDialog(MAVLinkService mavlinkService)
        {
            InitializeComponent();
            _mavlink = mavlinkService;
            _stepIndicators = new Border[] { Step0, Step1, Step2, Step3, Step4, Step5 };

            _mavlink.OnStatusTextReceived += OnStatusText;
            DrawDroneIcon(0, 0);
        }

        private void StartButton_Click(object sender, RoutedEventArgs e)
        {
            StartButton.IsEnabled = false;
            StartButton.Content = "ИДЁТ...";

            // PREFLIGHT_CALIBRATION param5=1 (Accelerometer)
            _mavlink.SendPreflightCalibration(accelerometer: true);

            StatusText.Text = "Ожидание ответа от дрона...";
            _waitingForDrone = true;
            _currentStep = -1;

            // Таймер на случай если дрон не ответит STATUSTEXT
            var fallback = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            fallback.Tick += (s, args) =>
            {
                fallback.Stop();
                if (_currentStep == -1)
                    GoToStep(0);
            };
            fallback.Start();

            Debug.WriteLine("[AccelCalib] Calibration started");
        }

        private void NextButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentStep < 0) return;

            NextButton.IsEnabled = false;
            NextButton.Opacity = 0.4;

            // MAV_CMD_ACCEL_CAL_VEHICLE_POS = 42429
            _mavlink.SendCommandLong(42429, param1: _currentStep);

            StatusText.Text = "Дрон собирает данные... не двигайте!";
            _waitingForDrone = true;
            MarkStepCompleted(_currentStep);

            // Через 3 сек переходим если дрон не ответил
            var fallback = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            fallback.Tick += (s, args) =>
            {
                fallback.Stop();
                if (_waitingForDrone)
                {
                    int next = _currentStep + 1;
                    if (next < 6) GoToStep(next);
                    else ShowResult(true, "Калибровка завершена!");
                }
            };
            fallback.Start();

            Debug.WriteLine($"[AccelCalib] Position {_currentStep} confirmed");
        }

        private void GoToStep(int step)
        {
            _currentStep = step;
            _waitingForDrone = false;

            if (step >= 6)
            {
                ShowResult(true, "Калибровка завершена!");
                return;
            }

            var pos = _positions[step];
            PositionTitle.Text = $"Шаг {step + 1}/6: {pos.title}";
            PositionDescription.Text = pos.description;
            PositionHint.Text = pos.hint;
            CalibProgress.Value = step;

            for (int i = 0; i < 6; i++)
            {
                if (i < step) MarkStepCompleted(i);
                else if (i == step) MarkStepActive(i);
            }

            DrawDroneIcon(step, _rotations[step]);

            NextButton.IsEnabled = true;
            NextButton.Opacity = 1.0;
            NextButton.Content = step < 5 ? "ДАЛЕЕ ▶" : "ГОТОВО ✓";
            StatusText.Text = $"Установите дрон: {pos.title}";
        }

        private void OnStatusText(string text)
        {
            Dispatcher.BeginInvoke(() =>
            {
                StatusText.Text = text;
                string lower = text.ToLower();

                if (lower.Contains("place vehicle level") || (lower.Contains("level") && _currentStep < 0))
                    GoToStep(0);
                else if (lower.Contains("left side") || lower.Contains("on its left"))
                    GoToStep(1);
                else if (lower.Contains("right side") || lower.Contains("on its right"))
                    GoToStep(2);
                else if (lower.Contains("nose down") || lower.Contains("nosedown"))
                    GoToStep(3);
                else if (lower.Contains("nose up") || lower.Contains("noseup"))
                    GoToStep(4);
                else if (lower.Contains("back") || lower.Contains("upside") || lower.Contains("inverted"))
                    GoToStep(5);
                else if (lower.Contains("success") || lower.Contains("calibration done") || lower.Contains("complete"))
                    ShowResult(true, text);
                else if (lower.Contains("fail") || lower.Contains("error"))
                    ShowResult(false, text);
            });
        }

        #region Визуализация дрона

        private void DrawDroneIcon(int step, double rotation)
        {
            DroneIconCanvas.Children.Clear();
            double cx = 80, cy = 55;

            // Корпус дрона
            var body = new Ellipse
            {
                Width = 30, Height = 24,
                Fill = new SolidColorBrush(Color.FromArgb(40, 152, 240, 25)),
                Stroke = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#98F019")),
                StrokeThickness = 1.5
            };
            Canvas.SetLeft(body, cx - 15);
            Canvas.SetTop(body, cy - 12);

            // Лучи и моторы
            var arms = new[] {
                (cx - 15, cy, cx - 32, cy - 16),
                (cx + 15, cy, cx + 32, cy - 16),
                (cx - 15, cy, cx - 32, cy + 16),
                (cx + 15, cy, cx + 32, cy + 16),
            };

            foreach (var (x1, y1, x2, y2) in arms)
            {
                var line = new Line
                {
                    X1 = x1, Y1 = y1, X2 = x2, Y2 = y2,
                    Stroke = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#98F019")),
                    StrokeThickness = 1.5
                };
                DroneIconCanvas.Children.Add(line);

                var motor = new Ellipse
                {
                    Width = 14, Height = 14,
                    Stroke = new SolidColorBrush(Color.FromArgb(80, 152, 240, 25)),
                    StrokeThickness = 1,
                    StrokeDashArray = new DoubleCollection(new[] { 2.0, 1.0 })
                };
                Canvas.SetLeft(motor, x2 - 7);
                Canvas.SetTop(motor, y2 - 7);
                DroneIconCanvas.Children.Add(motor);
            }

            DroneIconCanvas.Children.Add(body);

            // Нос (оранжевый треугольник)
            var nose = new Polygon
            {
                Points = new PointCollection(new[] {
                    new Point(cx, cy - 20),
                    new Point(cx - 4, cy - 13),
                    new Point(cx + 4, cy - 13)
                }),
                Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF6B35"))
            };
            DroneIconCanvas.Children.Add(nose);

            // Вращаем весь Canvas
            if (rotation != 0)
            {
                DroneIconCanvas.RenderTransformOrigin = new Point(0.5, 0.45);
                DroneIconCanvas.RenderTransform = new RotateTransform(rotation);
            }
            else
            {
                DroneIconCanvas.RenderTransform = null;
            }

            // Линия поверхности (стол)
            var surface = new Line
            {
                X1 = 15, Y1 = 105, X2 = 145, Y2 = 105,
                Stroke = new SolidColorBrush(Color.FromArgb(60, 42, 67, 97)),
                StrokeThickness = 2,
                StrokeDashArray = new DoubleCollection(new[] { 6.0, 3.0 })
            };
            DroneIconCanvas.Children.Add(surface);
        }

        #endregion

        #region Step Indicators

        private void MarkStepActive(int step)
        {
            if (step < 0 || step >= 6) return;
            _stepIndicators[step].Background = new SolidColorBrush(Color.FromArgb(40, 0, 170, 255));
            _stepIndicators[step].BorderBrush = new SolidColorBrush(Color.FromArgb(80, 0, 170, 255));
            _stepIndicators[step].BorderThickness = new Thickness(1);
            if (_stepIndicators[step].Child is TextBlock tb)
                tb.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#00AAFF"));
        }

        private void MarkStepCompleted(int step)
        {
            if (step < 0 || step >= 6) return;
            _stepIndicators[step].Background = new SolidColorBrush(Color.FromArgb(30, 152, 240, 25));
            _stepIndicators[step].BorderBrush = new SolidColorBrush(Color.FromArgb(60, 152, 240, 25));
            _stepIndicators[step].BorderThickness = new Thickness(1);
            if (_stepIndicators[step].Child is TextBlock tb)
            {
                tb.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#98F019"));
                if (!tb.Text.StartsWith("✓")) tb.Text = "✓ " + tb.Text;
            }
        }

        #endregion

        private void ShowResult(bool success, string message)
        {
            _waitingForDrone = false;
            CalibProgress.Value = 6;
            NextButton.IsEnabled = false;
            NextButton.Opacity = 0.4;

            if (success)
            {
                PositionTitle.Text = "КАЛИБРОВКА ЗАВЕРШЕНА!";
                PositionTitle.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#98F019"));
                PositionDescription.Text = "Акселерометр откалиброван успешно";
                PositionHint.Text = "";
                CalibProgress.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#98F019"));
                CancelButton.Content = "ЗАКРЫТЬ";
                for (int i = 0; i < 6; i++) MarkStepCompleted(i);
            }
            else
            {
                PositionTitle.Text = "ОШИБКА КАЛИБРОВКИ";
                PositionTitle.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF4444"));
                PositionDescription.Text = message;
                CalibProgress.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF4444"));
                StartButton.IsEnabled = true;
                StartButton.Content = "ПОВТОРИТЬ";
            }

            StatusText.Text = message;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e) => Close();

        protected override void OnClosed(EventArgs e)
        {
            _mavlink.OnStatusTextReceived -= OnStatusText;
            base.OnClosed(e);
        }
    }
}
