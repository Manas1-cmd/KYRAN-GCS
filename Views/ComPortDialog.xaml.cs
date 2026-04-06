using SimpleDroneGCS.UI.Dialogs;
using System.IO.Ports;
using System.Windows;

namespace SimpleDroneGCS.Views
{
    public partial class ComPortDialog : Window
    {
        public string SelectedPort { get; private set; }
        public int SelectedBaudRate { get; private set; } = 57600;
        public bool IsConfirmed { get; private set; }

        private readonly int[] _baudRates = { 9600, 19200, 38400, 57600, 115200, 230400, 460800, 921600 };

        public ComPortDialog()
        {
            InitializeComponent();
            LoadPorts();
            LoadBaudRates();
        }

        private void LoadPorts()
        {
            ComPortComboBox.Items.Clear();
            var ports = SerialPort.GetPortNames();
            foreach (var port in ports)
            {
                ComPortComboBox.Items.Add(port);
            }
            if (ports.Length > 0)
                ComPortComboBox.SelectedIndex = 0;
        }

        private void LoadBaudRates()
        {
            BaudRateComboBox.Items.Clear();
            foreach (var baud in _baudRates)
            {
                BaudRateComboBox.Items.Add(baud);
            }
            BaudRateComboBox.SelectedItem = 57600;
        }

        private void RefreshPorts_Click(object sender, RoutedEventArgs e)
        {
            LoadPorts();
        }

        private void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            if (ComPortComboBox.SelectedItem == null)
            {
                AppMessageBox.ShowWarning("Выберите COM порт");
                return;
            }

            SelectedPort = ComPortComboBox.SelectedItem.ToString();

            if (int.TryParse(BaudRateComboBox.Text, out int baud) && baud > 0)
            {
                SelectedBaudRate = baud;
            }
            else
            {
                AppMessageBox.ShowWarning("Введите корректный Baud Rate");
                return;
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
