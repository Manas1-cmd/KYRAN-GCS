using SimpleDroneGCS.Services;
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace SimpleDroneGCS.UI.Dialogs
{
    public partial class SRTMDownloadDialog : Window
    {
        private CancellationTokenSource _cts;
        private double _currentLat;
        private double _currentLon;

        // Границы регионов
        private readonly (double minLat, double maxLat, double minLon, double maxLon) _kzBounds = (40, 56, 46, 88);
        private readonly (double minLat, double maxLat, double minLon, double maxLon) _almatyBounds = (43, 44, 76, 78);
        private readonly (double minLat, double maxLat, double minLon, double maxLon) _astanaBounds = (51, 52, 71, 72);
        private readonly (double minLat, double maxLat, double minLon, double maxLon) _shymkentBounds = (42, 43, 69, 70);

        public SRTMDownloadDialog(double currentLat = 43.238, double currentLon = 76.945)
        {
            InitializeComponent();
            _currentLat = currentLat;
            _currentLon = currentLon;
            UpdateCacheInfo();
        }

        private void UpdateCacheInfo()
        {
            long cacheSize = SRTMService.Instance.GetCacheSize();
            CacheInfoText.Text = $"В кэше: {cacheSize / 1024.0 / 1024.0:F1} MB";
        }

        private void RegionComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded || TilesInfoText == null) return;  // ← добавь эту строку

            if (RegionComboBox.SelectedItem is ComboBoxItem)
            {
                (double, double, double, double) selectedBounds = GetSelectedBounds();
                int num = CalculateTiles(selectedBounds);
                double value = (double)num * 2.8;
                TilesInfoText.Text = $"Тайлов: ~{num}";
                SizeInfoText.Text = $"Примерный размер: ~{value:F0} MB";
            }
        }

        private (double minLat, double maxLat, double minLon, double maxLon) GetSelectedBounds()
        {
            if (RegionComboBox.SelectedItem is ComboBoxItem item)
            {
                return item.Tag?.ToString() switch
                {
                    "KZ" => _kzBounds,
                    "Almaty" => _almatyBounds,
                    "Astana" => _astanaBounds,
                    "Shymkent" => _shymkentBounds,
                    "Current" => ((int)_currentLat, (int)_currentLat + 1, (int)_currentLon, (int)_currentLon + 1),
                    _ => ((int)_currentLat, (int)_currentLat + 1, (int)_currentLon, (int)_currentLon + 1)
                };
            }
            return ((int)_currentLat, (int)_currentLat + 1, (int)_currentLon, (int)_currentLon + 1);
        }

        private int CalculateTiles((double minLat, double maxLat, double minLon, double maxLon) bounds)
        {
            int latTiles = (int)(bounds.maxLat - bounds.minLat);
            int lonTiles = (int)(bounds.maxLon - bounds.minLon);
            return Math.Max(1, latTiles * lonTiles);
        }

        private async void DownloadButton_Click(object sender, RoutedEventArgs e)
        {
            var bounds = GetSelectedBounds();

            DownloadButton.IsEnabled = false;
            RegionComboBox.IsEnabled = false;
            ProgressPanel.Visibility = Visibility.Visible;
            CancelButton.Content = "Остановить";

            _cts = new CancellationTokenSource();

            var progress = new Progress<(int current, int total, string status)>(p =>
            {
                ProgressBar.Maximum = p.total;
                ProgressBar.Value = p.current;
                ProgressText.Text = $"Загружено: {p.current} / {p.total}";
                ProgressDetailText.Text = p.status;
            });

            try
            {
                await SRTMService.Instance.PreloadRegionAsync(
                    bounds.minLat, bounds.maxLat,
                    bounds.minLon, bounds.maxLon,
                    progress);

                UpdateCacheInfo();
                ProgressText.Text = "✅ Загрузка завершена!";
                ProgressDetailText.Text = "";
                CancelButton.Content = "Закрыть";
            }
            catch (OperationCanceledException)
            {
                ProgressText.Text = "⏹️ Загрузка отменена";
            }
            catch (Exception ex)
            {
                ProgressText.Text = $"❌ Ошибка: {ex.Message}";
            }
            finally
            {
                DownloadButton.IsEnabled = true;
                RegionComboBox.IsEnabled = true;
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            if (_cts != null && !_cts.IsCancellationRequested)
            {
                _cts.Cancel();
            }
            else
            {
                DialogResult = true;
                Close();
            }
        }
    }
}