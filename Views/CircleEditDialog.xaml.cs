using System.Windows;
using System.Windows.Input;

namespace SimpleDroneGCS.Views
{
    public partial class CircleEditDialog : Window
    {
        public double Radius { get; private set; }
        public double Altitude { get; private set; }
        public bool AutoNext { get; private set; }
        public bool Clockwise { get; private set; }

        private const double MIN_RADIUS = 150.0;

        public CircleEditDialog(string title, double radius, double altitude, bool autoNext, bool clockwise)
        {
            InitializeComponent();

            TitleText.Text = title;
            TitleText.Foreground = title == "СТАРТ" 
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(250, 204, 21))  // Yellow
                : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(249, 115, 22)); // Orange

            RadiusBox.Text = radius.ToString("F0");
            AltitudeBox.Text = altitude.ToString("F0");
            AutoNextCheck.IsChecked = autoNext;
            DirectionCombo.SelectedIndex = clockwise ? 0 : 1;

            Radius = radius;
            Altitude = altitude;
            AutoNext = autoNext;
            Clockwise = clockwise;
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 1)
                DragMove();
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            if (!double.TryParse(RadiusBox.Text, out double radius))
            {
                MessageBox.Show("Введите корректный радиус", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (radius < MIN_RADIUS)
            {
                MessageBox.Show($"Минимальный радиус: {MIN_RADIUS} м", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
                RadiusBox.Text = MIN_RADIUS.ToString("F0");
                return;
            }

            if (!double.TryParse(AltitudeBox.Text, out double altitude))
            {
                MessageBox.Show("Введите корректную высоту", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (altitude < 10 || altitude > 5000)
            {
                MessageBox.Show("Высота должна быть от 10 до 5000 м", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Radius = radius;
            Altitude = altitude;
            AutoNext = AutoNextCheck.IsChecked == true;
            Clockwise = DirectionCombo.SelectedIndex == 0;

            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
