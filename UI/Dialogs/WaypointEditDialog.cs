using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SimpleDroneGCS.UI.Dialogs
{
    /// <summary>
    /// Диалог редактирования параметров waypoint
    /// </summary>
    public class WaypointEditDialog : Window
    {
        // Результаты
        public double Latitude { get; private set; }
        public double Longitude { get; private set; }
        public double Altitude { get; private set; }
        public double Radius { get; private set; }
        public double Delay { get; private set; }
        public int LoiterTurns { get; private set; }

        // Поля ввода
        private TextBox _latBox, _lngBox, _altBox, _radBox, _delayBox, _turnsBox;

        public WaypointEditDialog(int waypointNumber, double lat, double lng, double alt, double radius, double delay, int turns)
        {
            Latitude = lat;
            Longitude = lng;
            Altitude = alt;
            Radius = radius;
            Delay = delay;
            LoiterTurns = turns;

            Title = $"Точка #{waypointNumber}";
            Width = 450;
            Height = 520;  // УВЕЛИЧЕНО чтобы кнопки поместились
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
            var headerGrid = new Grid { Margin = new Thickness(0, 0, 0, 20) };
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

            // === Кнопки ===
            var btnGrid = new Grid { Margin = new Thickness(0, 24, 0, 0) };
            btnGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            btnGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // Кнопка Отмена
            var cancelBtn = new Border
            {
                Height = 44,
                Cursor = Cursors.Hand,
                Background = new SolidColorBrush(Color.FromRgb(55, 65, 81)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(107, 114, 128)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Margin = new Thickness(0, 0, 8, 0)
            };
            cancelBtn.Child = new TextBlock
            {
                Text = "Отмена",
                Foreground = Brushes.White,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            cancelBtn.MouseLeftButtonDown += (s, e) => { DialogResult = false; Close(); };
            cancelBtn.MouseEnter += (s, e) => cancelBtn.Opacity = 0.85;
            cancelBtn.MouseLeave += (s, e) => cancelBtn.Opacity = 1.0;
            Grid.SetColumn(cancelBtn, 0);
            btnGrid.Children.Add(cancelBtn);

            // Кнопка Сохранить
            var saveBtn = new Border
            {
                Height = 44,
                Cursor = Cursors.Hand,
                Background = new SolidColorBrush(Color.FromRgb(22, 101, 52)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(34, 197, 94)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Margin = new Thickness(8, 0, 0, 0)
            };
            saveBtn.Child = new TextBlock
            {
                Text = "Сохранить",
                Foreground = Brushes.White,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            saveBtn.MouseLeftButtonDown += SaveButton_Click;
            saveBtn.MouseEnter += (s, e) => saveBtn.Opacity = 0.85;
            saveBtn.MouseLeave += (s, e) => saveBtn.Opacity = 1.0;
            Grid.SetColumn(saveBtn, 1);
            btnGrid.Children.Add(saveBtn);

            mainStack.Children.Add(btnGrid);

            mainBorder.Child = mainStack;
            Content = mainBorder;

            // Перетаскивание окна
            mainBorder.MouseLeftButtonDown += (s, e) =>
            {
                if (e.ButtonState == MouseButtonState.Pressed)
                    DragMove();
            };
        }

        private StackPanel CreateInputRow(string label, string value, TextBox textBox)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };

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
            textBox.Height = 36;
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

            DialogResult = true;
            Close();
        }
    }
}
