using System;
using System.Windows;
using System.Windows.Threading;

namespace SimpleDroneGCS.Views
{
    public partial class SplashScreen : Window
    {
        public SplashScreen()
        {
            InitializeComponent();

            // Автоматически закрываем через 3 секунды
            var timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(3.5)
            };
            timer.Tick += (s, e) =>
            {
                timer.Stop();

                // Fade out эффект
                var fadeOut = new System.Windows.Media.Animation.DoubleAnimation(1, 0, TimeSpan.FromSeconds(0.5));
                fadeOut.Completed += (sender, args) => this.Close();
                this.BeginAnimation(OpacityProperty, fadeOut);
            };
            timer.Start();
        }
    }
}