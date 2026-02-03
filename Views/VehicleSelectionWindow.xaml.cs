using SimpleDroneGCS.Models;
using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace SimpleDroneGCS.Views
{
    public partial class VehicleSelectionWindow : Window
    {
        public VehicleType SelectedVehicle { get; private set; }
        public bool SelectionMade { get; private set; }

        public VehicleSelectionWindow()
        {
            InitializeComponent();
            Debug.WriteLine("[VehicleSelection] Window created");
            SelectionMade = false;
        }

        private void VehicleCard_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border border && border.Tag is string vehicleTag)
            {
                // Парсим выбранный тип
                if (Enum.TryParse<VehicleType>(vehicleTag, out var vehicleType))
                {
                    SelectedVehicle = vehicleType;
                    SelectionMade = true;
                    Debug.WriteLine($"[VehicleSelection] {vehicleType} selected");

                    // Закрываем окно с результатом
                    DialogResult = true;
                    Close();
                }
            }
        }

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Позволяет перетаскивать окно
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                DragMove();
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Debug.WriteLine("[VehicleSelection] Close button clicked");
            SelectionMade = false;
            DialogResult = false;
            Close();
        }
    }
}