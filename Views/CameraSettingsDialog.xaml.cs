using SimpleDroneGCS.Services;
using System.Windows;
using System.Windows.Input;

namespace SimpleDroneGCS.Views
{
    public partial class CameraSettingsDialog : Window
    {
        private readonly ViewProCameraService _camera;

        public CameraSettingsDialog(ViewProCameraService camera)
        {
            InitializeComponent();
            _camera = camera;
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 1)
                DragMove();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void GimbalCenter_Click(object sender, RoutedEventArgs e)
        {
            _camera?.GimbalCenter();
        }

        private void GimbalLookDown_Click(object sender, RoutedEventArgs e)
        {
            _camera?.GimbalLookDown();
        }

        private void VideoEO_Click(object sender, RoutedEventArgs e)
        {
            _camera?.SetVideoEO();
        }

        private void VideoIR_Click(object sender, RoutedEventArgs e)
        {
            _camera?.SetVideoIR();
        }

        private void VideoPiP_Click(object sender, RoutedEventArgs e)
        {
            _camera?.SetVideoEO_IR_PiP();
        }

        private void IrZoom_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn && btn.Tag is string tag)
            {
                if (int.TryParse(tag, out int zoom))
                {
                    _camera?.SetIrDigitalZoom(zoom);
                }
            }
        }
    }
}
