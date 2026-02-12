using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SimpleDroneGCS.UI.Dialogs
{
    public class WaypointEditDialog : Window
    {
        // === РЕЗУЛЬТАТЫ ===
        public double Latitude { get; private set; }
        public double Longitude { get; private set; }
        public double Altitude { get; private set; }
        public double Radius { get; private set; }
        public double Delay { get; private set; }
        public int LoiterTurns { get; private set; }
        public bool AutoNext { get; private set; }
        public bool Clockwise { get; private set; }
        public string CommandType { get; private set; }  // НОВОЕ

        // === ПОЛЯ ВВОДА ===
        private TextBox _latBox, _lngBox, _altBox, _radBox, _delayBox, _turnsBox;
        private CheckBox _autoNextBox;
        private Border _cwButton, _ccwButton;
        private ComboBox _commandCombo;  // НОВОЕ
        private bool _isClockwise;
        private bool _isVtol;  // НОВОЕ

        public WaypointEditDialog(int waypointNumber, double lat, double lng, double alt,
                                  double radius, double delay, int turns, bool autoNext, bool clockwise,
                                  string commandType = "WAYPOINT", bool isVtol = false)  // НОВЫЕ ПАРАМЕТРЫ
        {
            Latitude = lat;
            Longitude = lng;
            Altitude = alt;
            Radius = radius;
            Delay = delay;
            LoiterTurns = turns;
            AutoNext = autoNext;
            Clockwise = clockwise;
            CommandType = commandType;  // НОВОЕ
            _isClockwise = clockwise;
            _isVtol = isVtol;  // НОВОЕ

            Title = $"Точка #{waypointNumber}";
            Width = 450;
            Height = 720;  // Увеличено для команды
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            Background = new SolidColorBrush(Color.FromRgb(10, 14, 26));
            Foreground = Brushes.White;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;

            BuildUI(waypointNumber);
        }

        private void BuildUI(int wpNum)
        {
            var mainBorder = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(13, 23, 51)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(152, 240, 25)),
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(28)
            };

            var mainStack = new StackPanel();

            // === Заголовок ===
            var headerGrid = new Grid { Margin = new Thickness(0, 0, 0, 16) };
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var titleStack = new StackPanel { Orientation = Orientation.Horizontal };
            var numCircle = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(152, 240, 25)),
                CornerRadius = new CornerRadius(20),
                Width = 40,
                Height = 40,
                Margin = new Thickness(0, 0, 14, 0)
            };
            numCircle.Child = new TextBlock
            {
                Text = wpNum.ToString(),
                Foreground = Brushes.Black,
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            titleStack.Children.Add(numCircle);
            titleStack.Children.Add(new TextBlock
            {
                Text = "Редактирование точки",
                Foreground = Brushes.White,
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            });
            Grid.SetColumn(titleStack, 0);
            headerGrid.Children.Add(titleStack);

            // Кнопка закрытия
            var closeBtn = new TextBlock
            {
                Text = "✕",
                FontSize = 22,
                Foreground = new SolidColorBrush(Color.FromRgb(156, 163, 175)),
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center
            };
            closeBtn.MouseLeftButtonDown += (s, e) => { DialogResult = false; Close(); };
            closeBtn.MouseEnter += (s, e) => closeBtn.Foreground = Brushes.White;
            closeBtn.MouseLeave += (s, e) => closeBtn.Foreground = new SolidColorBrush(Color.FromRgb(156, 163, 175));
            Grid.SetColumn(closeBtn, 1);
            headerGrid.Children.Add(closeBtn);

            mainStack.Children.Add(headerGrid);

            // === ВЫБОР КОМАНДЫ ===
            var cmdPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
            cmdPanel.Children.Add(new TextBlock
            {
                Text = "Команда:",
                Foreground = new SolidColorBrush(Color.FromRgb(156, 163, 175)),
                FontSize = 14,
                Width = 115,
                VerticalAlignment = VerticalAlignment.Center
            });

            _commandCombo = new ComboBox
            {
                Width = 255,
                Height = 34,
                FontSize = 13,
                Background = new SolidColorBrush(Color.FromRgb(26, 36, 51)),
                Foreground = Brushes.Black,
                BorderBrush = new SolidColorBrush(Color.FromRgb(55, 65, 81))
            };

            var commands = _isVtol ? new[]
            {
                ("📍 Q-ТОЧКА", "WAYPOINT"),
                ("🔄 Q-КРУГ", "LOITER_UNLIM"),
                ("⏱️ Q-КРУГ(время)", "LOITER_TIME"),
                ("🔁 Q-КРУГ(обор)", "LOITER_TURNS"),
                ("🛬 Q-ПОСАДКА", "LAND"),
                ("⏸️ Q-ЗАДЕРЖКА", "DELAY"),
                ("⚡ Q-СКОРОСТЬ", "CHANGE_SPEED")
            } : new[]
            {
                ("📍 ТОЧКА", "WAYPOINT"),
                ("🔄 КРУГ", "LOITER_UNLIM"),
                ("⏱️ КРУГ(время)", "LOITER_TIME"),
                ("🔁 КРУГ(обор)", "LOITER_TURNS"),
                ("🛫 ВЗЛЁТ", "TAKEOFF"),
                ("🛬 ПОСАДКА", "LAND"),
                ("⏸️ ЗАДЕРЖКА", "DELAY"),
                ("⚡ СКОРОСТЬ", "CHANGE_SPEED"),
                ("🏠 ВОЗВРАТ", "RETURN_TO_LAUNCH"),
                ("〰️ СПЛАЙН", "SPLINE_WP")
            };

            int selIdx = 0;
            for (int i = 0; i < commands.Length; i++)
            {
                var item = new ComboBoxItem { Content = commands[i].Item1, Tag = commands[i].Item2, Foreground = Brushes.Black };
                _commandCombo.Items.Add(item);
                if (commands[i].Item2 == CommandType) selIdx = i;
            }
            _commandCombo.SelectedIndex = selIdx;

            cmdPanel.Children.Add(_commandCombo);
            mainStack.Children.Add(cmdPanel);

            // === Поля ввода ===
            _latBox = new TextBox();
            mainStack.Children.Add(CreateInputRow("Широта:", Latitude.ToString("F7"), _latBox));

            _lngBox = new TextBox();
            mainStack.Children.Add(CreateInputRow("Долгота:", Longitude.ToString("F7"), _lngBox));

            _altBox = new TextBox();
            mainStack.Children.Add(CreateInputRow("Высота (м):", Altitude.ToString("F0"), _altBox));

            _radBox = new TextBox();
            mainStack.Children.Add(CreateInputRow("Радиус (м):", Radius.ToString("F0"), _radBox));

            _delayBox = new TextBox();
            mainStack.Children.Add(CreateInputRow("Задержка (с):", Delay.ToString("F0"), _delayBox));

            _turnsBox = new TextBox();
            mainStack.Children.Add(CreateInputRow("Кругов:", LoiterTurns.ToString(), _turnsBox));

            // === НАПРАВЛЕНИЕ КРУЖЕНИЯ ===
            var directionPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 12, 0, 0)
            };

            directionPanel.Children.Add(new TextBlock
            {
                Text = "Направление:",
                Foreground = new SolidColorBrush(Color.FromRgb(156, 163, 175)),
                FontSize = 14,
                Width = 115,
                VerticalAlignment = VerticalAlignment.Center
            });

            _cwButton = CreateDirectionButton("↻ CW", true);
            _cwButton.MouseLeftButtonDown += (s, e) => SetDirection(true);
            directionPanel.Children.Add(_cwButton);

            _ccwButton = CreateDirectionButton("↺ CCW", false);
            _ccwButton.MouseLeftButtonDown += (s, e) => SetDirection(false);
            _ccwButton.Margin = new Thickness(8, 0, 0, 0);
            directionPanel.Children.Add(_ccwButton);

            mainStack.Children.Add(directionPanel);

            // Подсказка направления
            var dirHintPanel = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(30, 41, 59)),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 6, 10, 6),
                Margin = new Thickness(0, 6, 0, 0)
            };
            dirHintPanel.Child = new TextBlock
            {
                Text = "🔄 CW = по часовой стрелке, CCW = против часовой",
                Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184)),
                FontSize = 10,
                TextWrapping = TextWrapping.Wrap
            };
            mainStack.Children.Add(dirHintPanel);

            // === АВТОПЕРЕХОД ===
            var autoPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 16, 0, 0)
            };
            _autoNextBox = new CheckBox
            {
                IsChecked = AutoNext,
                Style = CreateCheckBoxStyle(),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            };
            autoPanel.Children.Add(_autoNextBox);
            autoPanel.Children.Add(new TextBlock
            {
                Text = "Автопереход к следующей точке",
                Foreground = Brushes.White,
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center
            });
            mainStack.Children.Add(autoPanel);

            // === КНОПКА СОХРАНИТЬ ===
            var saveBtn = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(22, 101, 52)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(74, 222, 128)),
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(8),
                Margin = new Thickness(0, 24, 0, 0),
                Cursor = Cursors.Hand,
                Height = 44
            };
            saveBtn.Child = new TextBlock
            {
                Text = "💾 СОХРАНИТЬ",
                Foreground = Brushes.White,
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            saveBtn.MouseLeftButtonDown += SaveButton_Click;
            saveBtn.MouseEnter += (s, e) => saveBtn.Background = new SolidColorBrush(Color.FromRgb(34, 139, 34));
            saveBtn.MouseLeave += (s, e) => saveBtn.Background = new SolidColorBrush(Color.FromRgb(22, 101, 52));
            mainStack.Children.Add(saveBtn);

            mainBorder.Child = mainStack;
            Content = mainBorder;

            // Перетаскивание окна
            mainBorder.MouseLeftButtonDown += (s, e) =>
            {
                if (e.ButtonState == MouseButtonState.Pressed)
                    DragMove();
            };

            UpdateDirectionButtons();
        }

        private Border CreateDirectionButton(string text, bool isCw)
        {
            var btn = new Border
            {
                Height = 32,
                MinWidth = 70,
                Cursor = Cursors.Hand,
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 0, 12, 0)
            };
            btn.Child = new TextBlock
            {
                Text = text,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            return btn;
        }

        private void SetDirection(bool clockwise)
        {
            _isClockwise = clockwise;
            UpdateDirectionButtons();
        }

        private void UpdateDirectionButtons()
        {
            if (_isClockwise)
            {
                _cwButton.Background = new SolidColorBrush(Color.FromRgb(22, 101, 52));
                _cwButton.BorderBrush = new SolidColorBrush(Color.FromRgb(74, 222, 128));
                _cwButton.BorderThickness = new Thickness(2);
                ((TextBlock)_cwButton.Child).Foreground = new SolidColorBrush(Color.FromRgb(74, 222, 128));
            }
            else
            {
                _cwButton.Background = new SolidColorBrush(Color.FromRgb(26, 36, 51));
                _cwButton.BorderBrush = new SolidColorBrush(Color.FromRgb(55, 65, 81));
                _cwButton.BorderThickness = new Thickness(1);
                ((TextBlock)_cwButton.Child).Foreground = new SolidColorBrush(Color.FromRgb(156, 163, 175));
            }

            if (!_isClockwise)
            {
                _ccwButton.Background = new SolidColorBrush(Color.FromRgb(127, 29, 29));
                _ccwButton.BorderBrush = new SolidColorBrush(Color.FromRgb(248, 113, 113));
                _ccwButton.BorderThickness = new Thickness(2);
                ((TextBlock)_ccwButton.Child).Foreground = new SolidColorBrush(Color.FromRgb(248, 113, 113));
            }
            else
            {
                _ccwButton.Background = new SolidColorBrush(Color.FromRgb(26, 36, 51));
                _ccwButton.BorderBrush = new SolidColorBrush(Color.FromRgb(55, 65, 81));
                _ccwButton.BorderThickness = new Thickness(1);
                ((TextBlock)_ccwButton.Child).Foreground = new SolidColorBrush(Color.FromRgb(156, 163, 175));
            }
        }

        private StackPanel CreateInputRow(string label, string value, TextBox textBox)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };

            row.Children.Add(new TextBlock
            {
                Text = label,
                Foreground = new SolidColorBrush(Color.FromRgb(156, 163, 175)),
                FontSize = 14,
                Width = 115,
                VerticalAlignment = VerticalAlignment.Center
            });

            textBox.Text = value;
            textBox.Width = 255;
            textBox.Height = 34;
            textBox.FontSize = 14;
            textBox.Background = new SolidColorBrush(Color.FromRgb(26, 36, 51));
            textBox.Foreground = Brushes.White;
            textBox.BorderBrush = new SolidColorBrush(Color.FromRgb(55, 65, 81));
            textBox.BorderThickness = new Thickness(1);
            textBox.Padding = new Thickness(10, 6, 10, 6);
            textBox.VerticalContentAlignment = VerticalAlignment.Center;

            var normalBrush = new SolidColorBrush(Color.FromRgb(55, 65, 81));
            var focusBrush = new SolidColorBrush(Color.FromRgb(152, 240, 25));
            textBox.GotFocus += (s, e) => textBox.BorderBrush = focusBrush;
            textBox.LostFocus += (s, e) => textBox.BorderBrush = normalBrush;

            row.Children.Add(textBox);

            return row;
        }

        private Style CreateCheckBoxStyle()
        {
            var style = new Style(typeof(CheckBox));

            var template = new ControlTemplate(typeof(CheckBox));
            var factory = new FrameworkElementFactory(typeof(Border));
            factory.Name = "border";
            factory.SetValue(Border.WidthProperty, 20.0);
            factory.SetValue(Border.HeightProperty, 20.0);
            factory.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
            factory.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(26, 36, 51)));
            factory.SetValue(Border.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(55, 65, 81)));
            factory.SetValue(Border.BorderThicknessProperty, new Thickness(2));

            var checkMark = new FrameworkElementFactory(typeof(TextBlock));
            checkMark.SetValue(TextBlock.TextProperty, "✓");
            checkMark.SetValue(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(152, 240, 25)));
            checkMark.SetValue(TextBlock.FontSizeProperty, 14.0);
            checkMark.SetValue(TextBlock.FontWeightProperty, FontWeights.Bold);
            checkMark.SetValue(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            checkMark.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
            checkMark.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);
            checkMark.Name = "checkMark";

            factory.AppendChild(checkMark);
            template.VisualTree = factory;

            var trigger = new Trigger { Property = CheckBox.IsCheckedProperty, Value = true };
            trigger.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible, "checkMark"));
            trigger.Setters.Add(new Setter(Border.BorderBrushProperty,
                new SolidColorBrush(Color.FromRgb(152, 240, 25)), "border"));
            template.Triggers.Add(trigger);

            style.Setters.Add(new Setter(Control.TemplateProperty, template));
            return style;
        }

        private void SaveButton_Click(object sender, MouseButtonEventArgs e)
        {
            if (!double.TryParse(_latBox.Text.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double lat) ||
                !double.TryParse(_lngBox.Text.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double lng) ||
                !double.TryParse(_altBox.Text, out double alt) ||
                !double.TryParse(_radBox.Text, out double rad) ||
                !double.TryParse(_delayBox.Text, out double delay) ||
                !int.TryParse(_turnsBox.Text, out int turns))
            {
                MessageBox.Show("Проверьте правильность введённых данных", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (lat < -90 || lat > 90)
            {
                MessageBox.Show("Широта должна быть от -90 до 90", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (lng < -180 || lng > 180)
            {
                MessageBox.Show("Долгота должна быть от -180 до 180", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Latitude = lat;
            Longitude = lng;
            Altitude = Math.Max(0, alt);
            Radius = Math.Max(5, Math.Min(500, rad));
            Delay = Math.Max(0, delay);
            LoiterTurns = Math.Max(0, turns);
            AutoNext = _autoNextBox.IsChecked ?? true;
            Clockwise = _isClockwise;

            // Сохраняем команду
            if (_commandCombo.SelectedItem is ComboBoxItem cmdItem)
                CommandType = cmdItem.Tag?.ToString() ?? "WAYPOINT";

            DialogResult = true;
            Close();
        }
    }
}