using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SimpleDroneGCS.Helpers;
using SimpleDroneGCS.Models;
using SimpleDroneGCS.Services;

namespace SimpleDroneGCS.UI.Dialogs
{
    public partial class PreflightChecklistDialog : Window
    {
        public bool ArmConfirmed { get; private set; }
        public bool ForceArm { get; private set; }

        private readonly MAVLinkService _mav;
        private readonly VehicleType _vehicleType;

        private bool _motorTestPassed = false;
        private CheckItem _motorItem;

        private readonly List<CheckItem> _items = new();

        private static readonly SolidColorBrush BrushGreen = new(Color.FromRgb(152, 240, 25));
        private static readonly SolidColorBrush BrushRed = new(Color.FromRgb(239, 68, 68));
        private static readonly SolidColorBrush BrushYellow = new(Color.FromRgb(234, 179, 8));
        private static readonly SolidColorBrush BrushGray = new(Color.FromRgb(107, 114, 128));
        private static readonly SolidColorBrush BrushDim = new(Color.FromRgb(156, 163, 175));

        public PreflightChecklistDialog(MAVLinkService mav, VehicleType vehicleType)
        {
            _mav = mav ?? throw new ArgumentNullException(nameof(mav));
            _vehicleType = vehicleType;
            InitializeComponent();
            BuildChecklist();
            RunAutoChecks();
        }

        private void BuildChecklist()
        {
            ChecklistPanel.Children.Clear();
            _items.Clear();

            AddAutoItem("Preflight_Check_GPS", CheckGps, critical: true);
            AddAutoItem("Preflight_Check_Sats", CheckSats, critical: true);
            AddAutoItem("Preflight_Check_BattPct", CheckBattPct, critical: true);
            AddAutoItem("Preflight_Check_BattV", CheckBattV, critical: false); // warn только — порог зависит от LiPo типа
            AddAutoItem("Preflight_Check_Mode", CheckMode, critical: false);
            AddAutoItem("Preflight_Check_EKF", CheckEkf, critical: false);

            AddMotorTestItem();
        }

        private void RunAutoChecks()
        {
            var t = _mav.CurrentTelemetry;
            foreach (var item in _items)
            {
                if (item.CheckFunc != null)
                    item.Apply(item.CheckFunc(t));
            }
            UpdateSummary();
        }


        private static CheckResult CheckGps(Telemetry t)
        {
            if (t.GpsFixType >= 3) return CheckResult.Pass(Loc.Get("Preflight_Val_3DFix"));
            if (t.GpsFixType == 2) return CheckResult.Fail(Loc.Get("Preflight_Val_2DFix"));
            return CheckResult.Fail(Loc.Get("Preflight_Val_NoFix"));
        }

        private static CheckResult CheckSats(Telemetry t)
        {
            if (t.SatellitesVisible >= 6)
                return CheckResult.Pass(t.SatellitesVisible.ToString());
            return CheckResult.Fail(Loc.Fmt("Preflight_Val_SatsLow", t.SatellitesVisible));
        }

        private static CheckResult CheckBattPct(Telemetry t)
        {
            string val = $"{t.BatteryPercent}%";
            if (t.BatteryPercent <= 0)
                return CheckResult.Warn(Loc.Get("Preflight_Val_BattUnknown")); // нет данных
            if (t.BatteryPercent >= 20)
                return CheckResult.Pass(val);
            return CheckResult.Fail(Loc.Fmt("Preflight_Val_BattLow", val));
        }

        private static CheckResult CheckBattV(Telemetry t)
        {
            if (t.BatteryVoltage <= 0.1)
                return CheckResult.Warn(Loc.Get("Preflight_Val_VoltUnknown"));
            if (t.BatteryVoltage >= 10.5)
                return CheckResult.Pass($"{t.BatteryVoltage:F1}V");
            return CheckResult.Warn(Loc.Fmt("Preflight_Val_VoltLow", $"{t.BatteryVoltage:F1}V"));
        }

        private static CheckResult CheckMode(Telemetry t)
        {
            if (!string.IsNullOrEmpty(t.FlightMode) && t.FlightMode != "UNKNOWN")
                return CheckResult.Pass(t.FlightMode);
            return CheckResult.Warn(Loc.Get("Preflight_Val_UnknownMode"));
        }

        private static CheckResult CheckEkf(Telemetry t)
        {
            if (t.IsEkfOk)
                return CheckResult.Pass(Loc.Get("Preflight_Val_EkfOk"));
            return CheckResult.Warn(Loc.Get("Preflight_Val_EkfWarn"));
        }

        private void UpdateSummary()
        {
            bool allCriticalOk = true;
            bool hasWarnings = false;

            foreach (var item in _items)
            {
                if (item.Critical && (item.State == CheckState.Fail || item.State == CheckState.Pending))
                    allCriticalOk = false;
                if (item.State == CheckState.Warn)
                    hasWarnings = true;
            }

            ArmBtn.IsEnabled = allCriticalOk;
            ForceArmBtn.Visibility = allCriticalOk ? Visibility.Collapsed : Visibility.Visible;

            if (allCriticalOk && !hasWarnings)
            {
                StatusText.Text = Loc.Get("Preflight_Status_Ready");
                StatusText.Foreground = BrushGreen;
                StatusBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(50, 152, 240, 25));
            }
            else if (allCriticalOk)
            {
                StatusText.Text = Loc.Get("Preflight_Status_Warn");
                StatusText.Foreground = BrushYellow;
                StatusBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(50, 234, 179, 8));
            }
            else
            {
                StatusText.Text = Loc.Get("Preflight_Status_Fail");
                StatusText.Foreground = BrushRed;
                StatusBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(50, 239, 68, 68));
            }
        }

        private void AddAutoItem(string labelKey, Func<Telemetry, CheckResult> checkFunc, bool critical)
        {
            var item = new CheckItem { LabelKey = labelKey, CheckFunc = checkFunc, Critical = critical };
            _items.Add(item);
            ChecklistPanel.Children.Add(BuildRow(item, motorTestButton: false));
        }

        private void AddMotorTestItem()
        {
            _motorItem = new CheckItem
            {
                LabelKey = "Preflight_Check_Motors",
                CheckFunc = null,
                Critical = true,
                State = CheckState.Pending
            };
            _items.Add(_motorItem);
            ChecklistPanel.Children.Add(BuildRow(_motorItem, motorTestButton: true));
        }

        private Border BuildRow(CheckItem item, bool motorTestButton)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

           
            var icon = new TextBlock
            {
                Text = "○",
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = BrushGray
            };
            Grid.SetColumn(icon, 0);
            item.IconBlock = icon;

            // Название + значение
            var nameBlock = new TextBlock
            {
                Text = Loc.Get(item.LabelKey),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                Foreground = BrushDim,
                VerticalAlignment = VerticalAlignment.Center
            };
            var valueBlock = new TextBlock
            {
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11,
                Foreground = BrushGray,
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            item.ValueBlock = valueBlock;

            var labelStack = new StackPanel { Orientation = Orientation.Horizontal };
            labelStack.Children.Add(nameBlock);
            labelStack.Children.Add(valueBlock);
            Grid.SetColumn(labelStack, 1);

            grid.Children.Add(icon);
            grid.Children.Add(labelStack);

            if (motorTestButton)
            {
                var btn = new Button
                {
                    Content = Loc.Get("Preflight_MotorTest_Btn"),
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 11,
                    Height = 26,
                    Padding = new Thickness(10, 0, 10, 0),
                    Background = new SolidColorBrush(Color.FromRgb(13, 23, 51)),
                    Foreground = BrushGreen,
                    BorderBrush = new SolidColorBrush(Color.FromRgb(42, 67, 97)),
                    BorderThickness = new Thickness(1),
                    Cursor = Cursors.Hand,
                    Template = BuildFlatTemplate()
                };
                btn.Click += MotorTestBtn_Click;
                Grid.SetColumn(btn, 2);
                grid.Children.Add(btn);
                item.TestButton = btn;
            }

            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(13, 23, 51)),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 8, 10, 8),
                Child = grid
            };

            item.RefreshUI = () =>
            {
                switch (item.State)
                {
                    case CheckState.Pass:
                        icon.Text = "✓"; icon.Foreground = BrushGreen;
                        valueBlock.Foreground = BrushGreen; break;
                    case CheckState.Fail:
                        icon.Text = "✗"; icon.Foreground = BrushRed;
                        valueBlock.Foreground = BrushRed; break;
                    case CheckState.Warn:
                        icon.Text = "⚠"; icon.Foreground = BrushYellow;
                        valueBlock.Foreground = BrushYellow; break;
                    default:
                        icon.Text = "○"; icon.Foreground = BrushGray;
                        valueBlock.Foreground = BrushGray; break;
                }
                valueBlock.Text = item.ValueHint ?? "";
            };

            return border;
        }

        private void MotorTestBtn_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new MotorTestDialog(_mav, _vehicleType)
            {
                Owner = this
            };
            dialog.ShowDialog();
            if (dialog.AnyMotorTested)
            {
                _motorTestPassed = true;
                _motorItem.Apply(CheckResult.Pass(Loc.Get("Preflight_Val_MotorDone")));
            }
            else
            {
                _motorItem.Apply(CheckResult.Warn(Loc.Get("Preflight_Val_MotorSkipped")));
            }

            if (sender is Button btn)
                btn.IsEnabled = false;

            UpdateSummary();
        }

        private void RefreshBtn_Click(object sender, RoutedEventArgs e)
        {
            var t = _mav.CurrentTelemetry;
            foreach (var item in _items)
                if (item.CheckFunc != null)
                    item.Apply(item.CheckFunc(t));
            UpdateSummary();
        }

        private void ArmBtn_Click(object sender, RoutedEventArgs e)
        {
            ArmConfirmed = true;
            ForceArm = false;
            DialogResult = true;
        }

        private void ForceArmBtn_Click(object sender, RoutedEventArgs e)
        {
            if (!AppMessageBox.ShowConfirm(
                Loc.Get("Msg_ArmForceConfirm"),
                owner: this,
                subtitle: Loc.Get("Msg_Warning")))
                return;

            ArmConfirmed = true;
            ForceArm = true;
            DialogResult = true;
        }

        private void CancelBtn_Click(object sender, RoutedEventArgs e) => DialogResult = false;

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) DialogResult = false;
        }

        private static ControlTemplate BuildFlatTemplate()
        {
            var tpl = new ControlTemplate(typeof(Button));
            var bdFact = new FrameworkElementFactory(typeof(Border));
            bdFact.SetBinding(Border.BackgroundProperty,
                new System.Windows.Data.Binding("Background")
                { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
            bdFact.SetBinding(Border.BorderBrushProperty,
                new System.Windows.Data.Binding("BorderBrush")
                { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
            bdFact.SetBinding(Border.BorderThicknessProperty,
                new System.Windows.Data.Binding("BorderThickness")
                { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
            bdFact.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            cp.SetBinding(ContentPresenter.MarginProperty,
                new System.Windows.Data.Binding("Padding")
                { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
            bdFact.AppendChild(cp);
            tpl.VisualTree = bdFact;
            return tpl;
        }

        private enum CheckState { Pending, Pass, Fail, Warn }

        private sealed class CheckResult
        {
            public CheckState State { get; }
            public string Hint { get; }
            private CheckResult(CheckState s, string h) { State = s; Hint = h; }
            public static CheckResult Pass(string h) => new(CheckState.Pass, h);
            public static CheckResult Fail(string h) => new(CheckState.Fail, h);
            public static CheckResult Warn(string h) => new(CheckState.Warn, h);
        }

        private sealed class CheckItem
        {
            public string LabelKey { get; set; }
            public Func<Telemetry, CheckResult> CheckFunc { get; set; }
            public bool Critical { get; set; } = true;
            public CheckState State { get; set; } = CheckState.Pending;
            public string ValueHint { get; set; }

            public TextBlock IconBlock { get; set; }
            public TextBlock ValueBlock { get; set; }
            public Button TestButton { get; set; }
            public Action RefreshUI { get; set; }

            public void Apply(CheckResult r)
            {
                State = r.State;
                ValueHint = r.Hint;
                RefreshUI?.Invoke();
            }
        }
    }
}