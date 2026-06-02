using System.Windows;
using System.Windows.Input;

namespace SimpleDroneGCS.UI.Dialogs
{
    public partial class PusherTestWarningDialog : Window
    {
        public bool HideWarningInFuture => HideWarningCheckBox.IsChecked == true;

        public PusherTestWarningDialog(int servoNumber, int originalFunction)
        {
            InitializeComponent();

            FunctionParamLabel.Text = $"SERVO{servoNumber}_FUNCTION";
            TrimParamLabel.Text = $"SERVO{servoNumber}_TRIM";
            FunctionOldText.Text = originalFunction.ToString();
            FunctionRestoreText.Text = originalFunction.ToString();
        }

        private void StartBtn_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }

        private void CancelBtn_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void DetailsBtn_Click(object sender, RoutedEventArgs e)
        {
            var details = new PusherTestDetailsDialog
            {
                Owner = this
            };
            details.ShowDialog();
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                DialogResult = false;
                Close();
            }
            else if (e.Key == Key.Enter)
            {
                DialogResult = true;
                Close();
            }
        }
    }
}
