using GMap.NET.MapProviders;
using SimpleDroneGCS.Controls;
using SimpleDroneGCS.UI.Dialogs;
using System.Net;
using System.Windows;
using static SimpleDroneGCS.Helpers.Loc;

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

            string localIp = LocalIpTextBox.Text.Trim();
            if (string.IsNullOrEmpty(localIp) || !IPAddress.TryParse(localIp, out _))
            {
                AppMessageBox.ShowWarning(Get("Udp_ErrLocalIp"));
                return;
            }
            LocalIp = localIp;

            if (!int.TryParse(LocalPortTextBox.Text, out int localPort) || localPort < 1 || localPort > 65535)
            {
                AppMessageBox.ShowWarning(Get("Udp_ErrLocalPort"));
                return;
            }
            LocalPort = localPort;

            string hostIp = HostIpTextBox.Text.Trim();
            if (!string.IsNullOrEmpty(hostIp))
            {
                if (!IPAddress.TryParse(hostIp, out _))
                {
                    AppMessageBox.ShowWarning(Get("Udp_ErrHostIp"));
                    return;
                }
                HostIp = hostIp;

                if (!int.TryParse(HostPortTextBox.Text, out int hostPort) || hostPort < 1 || hostPort > 65535)
                {
                    AppMessageBox.ShowWarning(Get("Udp_ErrHostPort"));
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

        private void PresetDrone_Click(object sender, RoutedEventArgs e)
        {
            HostIpTextBox.Text = "192.168.1.236";
            HostPortTextBox.Text = "15000";
            LocalIpTextBox.Text = "192.168.1.33";
            LocalPortTextBox.Text = "15000";
        }

        private void PresetSimulator_Click(object sender, RoutedEventArgs e)
        {
            HostIp = null;
            HostPort = null;
            LocalIp = "0.0.0.0";
            LocalPort = 14550;
            IsConfirmed = true;
            Close();
        }

        private void PresetListen_Click(object sender, RoutedEventArgs e)
        { 
            HostIpTextBox.Text = "";
            HostPortTextBox.Text = "";
            LocalIpTextBox.Text = "0.0.0.0";
            LocalPortTextBox.Text = "14550";
        }
    }
}