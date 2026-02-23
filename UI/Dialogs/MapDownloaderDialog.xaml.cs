using GMap.NET;
using GMap.NET.MapProviders;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SimpleDroneGCS.UI.Dialogs
{
    public partial class MapDownloaderDialog : Window
    {
        private CancellationTokenSource _cts;
        private bool _isDownloading = false;
        private PointLatLng? _currentPosition = null;

        private static readonly RectLatLng KazakhstanBounds = new RectLatLng(
            55.45, 46.49, 14.89, 40.82  
        );

        private static readonly RectLatLng AlmatyBounds = new RectLatLng(
            44.0, 76.0, 1.5, 2.0  
        );

        private static readonly RectLatLng AstanaBounds = new RectLatLng(
            51.5, 71.0, 1.0, 1.5  
        );

        public MapDownloaderDialog(PointLatLng? currentPosition = null)
        {
            InitializeComponent();
            _currentPosition = currentPosition;

            RegionCombo.SelectionChanged += (s, e) => UpdateEstimate();
            ZoomFromCombo.SelectionChanged += (s, e) => UpdateEstimate();
            ZoomToCombo.SelectionChanged += (s, e) => UpdateEstimate();

            if (_currentPosition == null)
            {
                RegionCombo.Items.RemoveAt(3); 
            }

            UpdateEstimate();
        }

        private RectLatLng GetSelectedBounds()
        {
            var selected = (ComboBoxItem)RegionCombo.SelectedItem;
            var tag = selected?.Tag?.ToString() ?? "KZ";

            return tag switch
            {
                "ALMATY" => AlmatyBounds,
                "ASTANA" => AstanaBounds,
                "CURRENT" => CreateBoundsAroundPoint(_currentPosition ?? new PointLatLng(43.238, 76.945), 50),
                _ => KazakhstanBounds
            };
        }

        private RectLatLng CreateBoundsAroundPoint(PointLatLng center, double radiusKm)
        {
            
            double degreeSpan = radiusKm / 111.0;
            return new RectLatLng(
                center.Lat + degreeSpan,  
                center.Lng - degreeSpan,  
                degreeSpan * 2,           
                degreeSpan * 2            
            );
        }

        private void UpdateEstimate()
        {
            try
            {
                int zoomFrom = GetZoomFrom();
                int zoomTo = GetZoomTo();
                var bounds = GetSelectedBounds();

                long totalTiles = 0;
                for (int z = zoomFrom; z <= zoomTo; z++)
                {
                    var tiles = GMapProviders.GoogleSatelliteMap.Projection.GetAreaTileList(bounds, z, 0);
                    totalTiles += tiles.Count;
                }

                TileCountText.Text = $"~{totalTiles:N0}";

                double estimatedMB = totalTiles * 0.025;
                EstimatedSizeText.Text = estimatedMB < 1000
                    ? $"~{estimatedMB:N0} MB"
                    : $"~{estimatedMB / 1000:N1} GB";

                double estimatedMinutes = totalTiles / 30.0 / 60.0;
                EstimatedTimeText.Text = estimatedMinutes < 1
                    ? "< 1 мин"
                    : $"~{estimatedMinutes:N0} мин";
            }
            catch
            {
                TileCountText.Text = "—";
                EstimatedSizeText.Text = "—";
                EstimatedTimeText.Text = "—";
            }
        }

        private int GetZoomFrom()
        {
            var item = (ComboBoxItem)ZoomFromCombo.SelectedItem;
            return int.Parse(item.Content.ToString());
        }

        private int GetZoomTo()
        {
            var item = (ComboBoxItem)ZoomToCombo.SelectedItem;
            return int.Parse(item.Content.ToString());
        }

        private async void StartButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isDownloading)
            {
                
                _cts?.Cancel();
                return;
            }

            int zoomFrom = GetZoomFrom();
            int zoomTo = GetZoomTo();

            if (zoomFrom > zoomTo)
            {
                StatusText.Text = "❌ 'От' должен быть меньше 'До'";
                StatusText.Foreground = System.Windows.Media.Brushes.OrangeRed;
                return;
            }

            _isDownloading = true;
            _cts = new CancellationTokenSource();

            StartButton.Content = "⏹ Стоп";
            StartButton.Background = System.Windows.Media.Brushes.OrangeRed;
            RegionCombo.IsEnabled = false;
            ZoomFromCombo.IsEnabled = false;
            ZoomToCombo.IsEnabled = false;
            StatusText.Foreground = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#98F019"));

            try
            {
                await DownloadTilesAsync(zoomFrom, zoomTo, _cts.Token);
                StatusText.Text = "✅ Скачивание завершено!";
            }
            catch (OperationCanceledException)
            {
                StatusText.Text = "⏹ Скачивание остановлено";
                StatusText.Foreground = System.Windows.Media.Brushes.Orange;
            }
            catch (Exception ex)
            {
                StatusText.Text = $"❌ Ошибка: {ex.Message}";
                StatusText.Foreground = System.Windows.Media.Brushes.OrangeRed;
            }
            finally
            {
                _isDownloading = false;
                StartButton.Content = "▶ Скачать";
                StartButton.Background = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#98F019"));
                RegionCombo.IsEnabled = true;
                ZoomFromCombo.IsEnabled = true;
                ZoomToCombo.IsEnabled = true;
            }
        }

        private async Task DownloadTilesAsync(int zoomFrom, int zoomTo, CancellationToken ct)
        {
            
            string cacheFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "MapCache");
            if (!Directory.Exists(cacheFolder))
                Directory.CreateDirectory(cacheFolder);

            GMaps.Instance.Mode = AccessMode.ServerAndCache;

            var bounds = GetSelectedBounds();

            long totalTiles = 0;
            for (int z = zoomFrom; z <= zoomTo; z++)
            {
                totalTiles += GMapProviders.GoogleSatelliteMap.Projection
                    .GetAreaTileList(bounds, z, 0).Count;
            }

            long downloaded = 0;
            int errors = 0;
            int skipped = 0;
            var startTime = DateTime.Now;

            for (int zoom = zoomFrom; zoom <= zoomTo; zoom++)
            {
                ct.ThrowIfCancellationRequested();

                var tiles = GMapProviders.GoogleSatelliteMap.Projection
                    .GetAreaTileList(bounds, zoom, 0);

                StatusText.Text = $"Зум {zoom}: {tiles.Count:N0} тайлов...";

                foreach (var tile in tiles)
                {
                    ct.ThrowIfCancellationRequested();

                    try
                    {
                        
                        var img = GMaps.Instance.GetImageFrom(
                            GMapProviders.GoogleSatelliteMap, tile, zoom, out var ex);

                        if (ex != null)
                        {
                            errors++;
                        }
                        else if (img == null)
                        {
                            skipped++;
                        }
                    }
                    catch
                    {
                        errors++;
                    }

                    downloaded++;

                    if (downloaded % 20 == 0 || downloaded == totalTiles)
                    {
                        int percent = (int)((downloaded * 100) / totalTiles);
                        DownloadProgress.Value = percent;
                        ProgressPercent.Text = $"{percent}%";

                        var elapsed = DateTime.Now - startTime;
                        double speed = downloaded / elapsed.TotalSeconds;
                        double remaining = (totalTiles - downloaded) / speed;

                        ProgressText.Text = $"Зум {zoom} | {downloaded:N0}/{totalTiles:N0} | " +
                                          $"Ошибок: {errors} | " +
                                          $"~{TimeSpan.FromSeconds(remaining):mm\\:ss} осталось";

                        await Task.Delay(1, ct);
                    }

                    await Task.Delay(25, ct);
                }
            }

            ProgressText.Text = $"Готово: {downloaded:N0} тайлов | Ошибок: {errors} | Пропущено: {skipped}";
            DownloadProgress.Value = 100;
            ProgressPercent.Text = "100%";
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            this.Close();
        }

        private void CloseButton_Click(object sender, MouseButtonEventArgs e)
        {
            _cts?.Cancel();
            this.Close();
        }
    }
}
