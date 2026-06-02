using System.Windows;
using System.Windows.Input;

namespace SimpleDroneGCS.UI.Dialogs
{
    public partial class PusherTestDetailsDialog : Window
    {
        public PusherTestDetailsDialog()
        {
            InitializeComponent();
        }

        private void CloseBtn_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape || e.Key == Key.Enter)
                Close();
        }
    }
}
