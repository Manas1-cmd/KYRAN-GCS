using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using SimpleDroneGCS.Models;
using SimpleDroneGCS.Services;
using static SimpleDroneGCS.Helpers.Loc;

namespace SimpleDroneGCS.UI.Dialogs
{
    public partial class MotorTestDialog : Window
    {
        private readonly MAVLinkService _mav;
        private readonly VehicleType _vehicleType;

        private int _motorCount = 4;
        private int _activeMotor = 0;
        private bool _isTestingAll = false;
        private bool _isClosed = false;
        private System.Threading.CancellationTokenSource _testCts;
        private readonly HashSet<int> _testedMotors = new();
        private readonly HashSet<int> _failedMotors = new();
        private DispatcherTimer _pollTimer;
        public bool AnyMotorTested => _testedMotors.Count > 0;

        private System.Threading.Tasks.TaskCompletionSource<bool> _motorAckTcs;

        private static readonly SolidColorBrush BrushGreen = new(Color.FromRgb(152, 240, 25));
        private static readonly SolidColorBrush BrushRed = new(Color.FromRgb(239, 68, 68));
        private static readonly SolidColorBrush BrushBlue = new(Color.FromRgb(96, 165, 250));
        private static readonly SolidColorBrush BrushGray = new(Color.FromRgb(42, 67, 97));
        private static readonly SolidColorBrush BrushDim = new(Color.FromRgb(107, 114, 128));
        private static readonly SolidColorBrush BrushYellow = new(Color.FromRgb(234, 179, 8));

        private readonly Dictionary<int, Button> _motorButtons = new();

        private const double CX = 130;
        private const double CY = 95;
        private const double BtnSize = 50;

        private static readonly Dictionary<int, Dictionary<int, (double x, double y, string label)>> Layouts = new()
        {
            [4] = new()
            {
                [1] = (185, 18, "M1\nFR"),
                [2] = (35, 132, "M2\nRL"),
                [3] = (35, 18, "M3\nFL"),
                [4] = (185, 132, "M4\nRR"),
            },
            [6] = new()
            {
                [1] = (175, 18, "M1\nFR"),
                [2] = (45, 132, "M2\nRL"),
                [3] = (45, 18, "M3\nFL"),
                [4] = (175, 132, "M4\nRR"),
                [5] = (210, 73, "M5\nR"),
                [6] = (10, 73, "M6\nL"),
            },
            [8] = new()
            {
                [1] = (180, 22, "M1\nFR"),
                [2] = (40, 128, "M2\nRL"),
                [3] = (40, 22, "M3\nFL"),
                [4] = (180, 128, "M4\nRR"),
                [5] = (208, 75, "M5\nR"),
                [6] = (12, 75, "M6\nL"),
                [7] = (110, 10, "M7\nF"),
                [8] = (110, 130, "M8\nB"),
            },
        };

        public MotorTestDialog(MAVLinkService mav, VehicleType vehicleType)
        {
            _mav = mav ?? throw new ArgumentNullException(nameof(mav));
            _vehicleType = vehicleType;
            InitializeComponent();

            if (_vehicleType == VehicleType.QuadPlane)
            {
                ConfigSelectorRow.Visibility = Visibility.Collapsed;
                DroneIcon.Text = "✈";
                _motorCount = 4;
                PusherRow.Visibility = Visibility.Visible;
                var pusherStack = new System.Windows.Controls.StackPanel
                { HorizontalAlignment = System.Windows.HorizontalAlignment.Center };
                pusherStack.Children.Add(new System.Windows.Controls.TextBlock
                {
                    Text = Get("MotorTest_TestMotor"),
                    FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                    FontSize = 11,
                    FontWeight = System.Windows.FontWeights.Bold,
                    Foreground = BrushDim,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center
                });
                PusherBtn.Content = pusherStack;
            }
            else
            {
                ConfigSelectorRow.Visibility = Visibility.Visible;
                DroneIcon.Text = "✦";
                _motorCount = 4;
            }

            BuildLayout(_motorCount);
            UpdateConfigButtons(_motorCount);
            UpdateArmedState();
            AddStatusLine(Get("MotorTest_StatusReady"), BrushDim);

            _mav.MotorTestAckReceived += OnMotorTestAck;

            _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _pollTimer.Tick += (_, _) => UpdateArmedState();
            _pollTimer.Start();
        }

        private void CfgBtn_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not string tag) return;
            if (!int.TryParse(tag, out int count)) return;
            if (count == _motorCount) return;

            _motorCount = count;
            _testedMotors.Clear();
            _activeMotor = 0;

            BuildLayout(_motorCount);
            UpdateConfigButtons(_motorCount);
            AddStatusLine(Fmt("MotorTest_ConfigChanged", _motorCount), BrushDim);
        }

        private void UpdateConfigButtons(int active)
        {
            SetCfgBtnStyle(Cfg4Btn, active == 4);
            SetCfgBtnStyle(Cfg6Btn, active == 6);
            SetCfgBtnStyle(Cfg8Btn, active == 8);
        }

        private static void SetCfgBtnStyle(Button btn, bool selected)
        {
            if (selected)
            {
                btn.Background = new SolidColorBrush(Color.FromArgb(60, 152, 240, 25));
                btn.BorderBrush = BrushGreen;
                btn.Foreground = BrushGreen;
            }
            else
            {
                btn.Background = new SolidColorBrush(Color.FromRgb(13, 23, 51));
                btn.BorderBrush = new SolidColorBrush(Color.FromRgb(42, 67, 97));
                btn.Foreground = new SolidColorBrush(Color.FromRgb(107, 114, 128));
            }
        }

        private void BuildLayout(int count)
        {
            MotorButtonsCanvas.Children.Clear();
            ArmLinesCanvas.Children.Clear();
            _motorButtons.Clear();

            if (!Layouts.TryGetValue(count, out var positions)) return;

            foreach (var (_, (x, y, _)) in positions)
            {
                double motorCX = x + BtnSize / 2;
                double motorCY = y + BtnSize / 2;

                var line = new Line
                {
                    X1 = CX,
                    Y1 = CY,
                    X2 = motorCX,
                    Y2 = motorCY,
                    Stroke = new SolidColorBrush(Color.FromArgb(40, 42, 67, 97)),
                    StrokeThickness = 1.5
                };
                ArmLinesCanvas.Children.Add(line);
            }

            foreach (var (motorNum, (x, y, label)) in positions)
            {
                var btn = CreateMotorButton(motorNum, label);
                Canvas.SetLeft(btn, x);
                Canvas.SetTop(btn, y);
                MotorButtonsCanvas.Children.Add(btn);
                _motorButtons[motorNum] = btn;
            }

            if (_vehicleType == VehicleType.QuadPlane)
                _motorButtons[5] = PusherBtn;

            UpdateArmedState();
        }

        private Button CreateMotorButton(int motorNum, string label)
        {
            var btn = new Button
            {
                Width = BtnSize,
                Height = BtnSize,
                Tag = motorNum,
                Cursor = Cursors.Hand,
                Background = new SolidColorBrush(Color.FromRgb(13, 23, 51)),
                BorderBrush = BrushGray,
                BorderThickness = new Thickness(2),
                Template = BuildCircleTemplate(),
            };

            var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            foreach (var line in label.Split('\n'))
            {
                stack.Children.Add(new TextBlock
                {
                    Text = line,
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = line.StartsWith("M") ? 11 : 9,
                    FontWeight = line.StartsWith("M") ? FontWeights.Bold : FontWeights.Normal,
                    Foreground = BrushDim,
                    HorizontalAlignment = HorizontalAlignment.Center
                });
            }

            btn.Content = stack;
            btn.Click += MotorBtn_Click;
            return btn;
        }

        private static ControlTemplate BuildCircleTemplate()
        {
            var tpl = new ControlTemplate(typeof(Button));
            var bd = new FrameworkElementFactory(typeof(Border));
            bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(25));
            bd.SetBinding(Border.BackgroundProperty,
                new System.Windows.Data.Binding("Background")
                { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
            bd.SetBinding(Border.BorderBrushProperty,
                new System.Windows.Data.Binding("BorderBrush")
                { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
            bd.SetBinding(Border.BorderThicknessProperty,
                new System.Windows.Data.Binding("BorderThickness")
                { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            bd.AppendChild(cp);
            tpl.VisualTree = bd;
            return tpl;
        }

        private void UpdateArmedState()
        {
            bool armed = _mav.CurrentTelemetry?.Armed == true;
            ArmedWarningBorder.Visibility = armed ? Visibility.Visible : Visibility.Collapsed;

            bool canTest = !armed && _mav.IsConnected && !_isTestingAll;
            foreach (var btn in _motorButtons.Values)
                btn.IsEnabled = canTest;
            TestAllBtn.IsEnabled = canTest;
            StopBtn.IsEnabled = _mav.IsConnected;

            bool configEnabled = canTest && _activeMotor == 0;
            Cfg4Btn.IsEnabled = configEnabled;
            Cfg6Btn.IsEnabled = configEnabled;
            Cfg8Btn.IsEnabled = configEnabled;
        }

        private void OnMotorTestAck(int motorNum, bool accepted)
        {
            _motorAckTcs?.TrySetResult(accepted);
        }

        private void MotorBtn_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn) return;
            int motorNum = btn.Tag switch
            {
                int i => i,
                string s => int.TryParse(s, out int n) ? n : 0,
                _ => 0
            };
            if (motorNum <= 0) return;
            RunMotorTest(motorNum);
        }

        private async void RunMotorTest(int motorNum)
        {
            if (!_mav.IsConnected || _mav.CurrentTelemetry?.Armed == true) return;
            if (_activeMotor != 0) return;

            int duration = (int)DurationSlider.Value;
            float throttle = (float)ThrottleSlider.Value;

            SetMotorActive(motorNum);
            AddStatusLine(Fmt("MotorTest_Testing", motorNum), BrushBlue);

            bool accepted = await SendMotorTestWithAck(motorNum, throttle, duration);

            if (_isClosed) return;

            if (_activeMotor == motorNum)
            {
                if (accepted)
                    SetMotorDone(motorNum);
                else
                    SetMotorFailed(motorNum);
            }
        }

        private async System.Threading.Tasks.Task<bool> SendMotorTestWithAck(
            int motorNum, float throttle, int duration,
            System.Threading.CancellationToken ct = default)
        {
            var tcs = new System.Threading.Tasks.TaskCompletionSource<bool>();
            _motorAckTcs = tcs;

            _mav.SendMotorTest(motorNum, throttle, duration);

            var ackTimeout = System.Threading.Tasks.Task.Delay(3000, ct);
            var completed = await System.Threading.Tasks.Task.WhenAny(tcs.Task, ackTimeout);

            bool accepted;
            if (completed == ackTimeout)
            {
                accepted = true;
                AddStatusLine(Fmt("MotorTest_AckTimeout", motorNum), BrushYellow);
            }
            else
            {
                accepted = tcs.Task.Result;
            }

            if (accepted && !ct.IsCancellationRequested)
            {
                try { await System.Threading.Tasks.Task.Delay(duration * 1000, ct); }
                catch (System.Threading.Tasks.TaskCanceledException) { }
            }

            return accepted;
        }

        private async void TestAllBtn_Click(object sender, RoutedEventArgs e)
        {
            _isTestingAll = true;
            _testCts?.Cancel();
            _testCts = new System.Threading.CancellationTokenSource();
            var token = _testCts.Token;

            TestAllBtn.IsEnabled = false;
            Cfg4Btn.IsEnabled = false;
            Cfg6Btn.IsEnabled = false;
            Cfg8Btn.IsEnabled = false;
            foreach (var btn in _motorButtons.Values)
                btn.IsEnabled = false;

            int duration = (int)DurationSlider.Value;
            float throttle = (float)ThrottleSlider.Value;
            int totalMotors = _vehicleType == VehicleType.QuadPlane ? 5 : _motorCount;

            bool aborted = false;
            for (int m = 1; m <= totalMotors; m++)
            {
                if (token.IsCancellationRequested || _isClosed) { aborted = true; break; }

                if (!_mav.IsConnected || _mav.CurrentTelemetry?.Armed == true)
                {
                    AddStatusLine(Get("MotorTest_StoppedArmed"), BrushRed);
                    aborted = true;
                    break;
                }

                SetMotorActive(m);
                AddStatusLine(Fmt("MotorTest_Testing", m), BrushBlue);

                bool accepted;
                try { accepted = await SendMotorTestWithAck(m, throttle, duration, token); }
                catch (System.Threading.Tasks.TaskCanceledException) { aborted = true; break; }

                if (_isClosed) return;

                if (_activeMotor == m)
                {
                    if (accepted) SetMotorDone(m);
                    else SetMotorFailed(m);
                }
            }

            if (_isClosed) return;
            if (!aborted)
                AddStatusLine(Get("MotorTest_AllDone"), BrushGreen);

            _isTestingAll = false;
            UpdateArmedState();
        }

        private void StopBtn_Click(object sender, RoutedEventArgs e)
        {
            _testCts?.Cancel();
            _isTestingAll = false;
            int totalMotors = _vehicleType == VehicleType.QuadPlane ? 5 : _motorCount;
            for (int m = 1; m <= totalMotors; m++)
                _mav.SendMotorTest(m, 0f, 0f);

            _activeMotor = 0;
            foreach (var (mn, btn) in _motorButtons)
                ResetMotorButton(mn, btn);

            AddStatusLine(Get("MotorTest_Stopped"), BrushYellow);
            UpdateArmedState();
        }

        private void SetMotorActive(int motorNum)
        {
            if (!_motorButtons.TryGetValue(motorNum, out var btn)) return;
            _activeMotor = motorNum;
            btn.Background = new SolidColorBrush(Color.FromArgb(80, 96, 165, 250));
            btn.BorderBrush = BrushBlue;
            btn.BorderThickness = new Thickness(2.5);
            SetButtonTextColor(btn, BrushBlue);
        }

        private void SetMotorFailed(int motorNum)
        {
            if (!_motorButtons.TryGetValue(motorNum, out var btn)) return;
            _failedMotors.Add(motorNum);
            if (_activeMotor == motorNum) _activeMotor = 0;
            btn.Background = new SolidColorBrush(Color.FromArgb(60, 239, 68, 68));
            btn.BorderBrush = BrushRed;
            btn.BorderThickness = new Thickness(2.5);
            SetButtonTextColor(btn, BrushRed);
            AddStatusLine(Fmt("MotorTest_Failed", motorNum), BrushRed);
        }

        private void SetMotorDone(int motorNum)
        {
            if (!_motorButtons.TryGetValue(motorNum, out var btn)) return;
            _testedMotors.Add(motorNum);
            if (_activeMotor == motorNum) _activeMotor = 0;
            btn.Background = new SolidColorBrush(Color.FromArgb(60, 152, 240, 25));
            btn.BorderBrush = BrushGreen;
            btn.BorderThickness = new Thickness(2.5);
            SetButtonTextColor(btn, BrushGreen);
            AddStatusLine(Fmt("MotorTest_Done", motorNum), BrushGreen);
        }

        private void ResetMotorButton(int motorNum, Button btn)
        {
            if (_testedMotors.Contains(motorNum))
            {
                btn.Background = new SolidColorBrush(Color.FromRgb(13, 23, 51));
                btn.BorderBrush = BrushGreen;
                btn.BorderThickness = new Thickness(2.5);
                SetButtonTextColor(btn, BrushGreen);
            }
            else if (_failedMotors.Contains(motorNum))
            {
                btn.Background = new SolidColorBrush(Color.FromRgb(13, 23, 51));
                btn.BorderBrush = BrushRed;
                btn.BorderThickness = new Thickness(2.5);
                SetButtonTextColor(btn, BrushRed);
            }
            else
            {
                btn.Background = new SolidColorBrush(Color.FromRgb(13, 23, 51));
                btn.BorderBrush = BrushGray;
                btn.BorderThickness = new Thickness(2);
                SetButtonTextColor(btn, BrushDim);
            }
        }

        private static void SetButtonTextColor(Button btn, SolidColorBrush color)
        {
            if (btn.Content is not StackPanel sp) return;
            foreach (var child in sp.Children)
                if (child is TextBlock tb) tb.Foreground = color;
        }

        private void ThrottleSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (ThrottleValueText != null)
                ThrottleValueText.Text = $"{(int)e.NewValue}%";
        }

        private void DurationSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (DurationValueText != null)
                DurationValueText.Text = $"{(int)e.NewValue}{Get("MotorTest_SecSuffix")}";
        }

        private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) Close();
        }

        private void AddStatusLine(string text, SolidColorBrush color)
        {
            StatusLog.Children.Insert(0, new TextBlock
            {
                Text = $"{DateTime.Now:HH:mm:ss}  {text}",
                FontFamily = new FontFamily("Consolas"),
                FontSize = 10,
                Foreground = color,
                Margin = new Thickness(0, 1, 0, 1)
            });

            while (StatusLog.Children.Count > 20)
                StatusLog.Children.RemoveAt(StatusLog.Children.Count - 1);
        }

        protected override void OnClosed(EventArgs e)
        {
            _isClosed = true;
            _testCts?.Cancel();
            _motorAckTcs?.TrySetCanceled();
            _mav.MotorTestAckReceived -= OnMotorTestAck;
            _pollTimer?.Stop();
            base.OnClosed(e);
        }
    }
}