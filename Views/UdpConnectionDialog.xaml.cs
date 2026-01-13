using SimpleDroneGCS.Controls;
using SimpleDroneGCS.UI.Dialogs;
using System.Net;
using System.Windows;

namespace SimpleDroneGCS.Views
{
    public partial class UdpConnectionDialog : Window
    {
        public string LocalIp { get; private set; } = "0.0.0.0";
        public int LocalPort { get; private set; } = 14550;
        public string? HostIp { get; private set; }
        public int? HostPort { get; private set; }
        public bool IsConfirmed { get; private set; }

        public UdpConnectionDialog()
        {
            InitializeComponent();
        }

        private void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            // Валидация локального IP
            string localIp = LocalIpTextBox.Text.Trim();
            if (string.IsNullOrEmpty(localIp) || !IPAddress.TryParse(localIp, out _))
            {
                AppMessageBox.ShowWarning("Введите корректный локальный IP (например 0.0.0.0)");
                return;
            }
            LocalIp = localIp;

            // Валидация локального порта
            if (!int.TryParse(LocalPortTextBox.Text, out int localPort) || localPort < 1 || localPort > 65535)
            {
                AppMessageBox.ShowWarning("Введите корректный локальный порт (1-65535)");
                return;
            }
            LocalPort = localPort;

            // IP дрона (опционально)
            string hostIp = HostIpTextBox.Text.Trim();
            if (!string.IsNullOrEmpty(hostIp))
            {
                if (!IPAddress.TryParse(hostIp, out _))
                {
                    AppMessageBox.ShowWarning("Введите корректный IP адрес дрона");
                    return;
                }
                HostIp = hostIp;

                // Если указан IP дрона, порт тоже нужен
                if (!int.TryParse(HostPortTextBox.Text, out int hostPort) || hostPort < 1 || hostPort > 65535)
                {
                    AppMessageBox.ShowWarning("Введите корректный порт дрона (1-65535)");
                    return;
                }
                HostPort = hostPort;
            }

            IsConfirmed = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            IsConfirmed = false;
            Close();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            IsConfirmed = false;
            Close();
        }
    }
}
