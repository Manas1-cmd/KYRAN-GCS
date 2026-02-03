using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace SimpleDroneGCS.Services
{
    public class SRTMService
    {
        private static SRTMService _instance;
        public static SRTMService Instance => _instance ??= new SRTMService();

        private readonly string _cacheFolder;
        private readonly HttpClient _httpClient;

        // Источник SRTM данных (бесплатный, без регистрации)
        private const string SRTM_URL = "https://elevation-tiles-prod.s3.amazonaws.com/skadi";

        private SRTMService()
        {
            _cacheFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Elevation");
            Directory.CreateDirectory(_cacheFolder);

            _httpClient = new HttpClient();
            _httpClient.Timeout = TimeSpan.FromSeconds(30);
        }

        /// <summary>
        /// Получить высоту в метрах для координаты
        /// </summary>
        public async Task<double?> GetElevationAsync(double lat, double lon)
        {
            try
            {
                string fileName = GetTileFileName(lat, lon);
                string filePath = Path.Combine(_cacheFolder, fileName);

                // Скачиваем если нет
                if (!File.Exists(filePath))
                {
                    bool downloaded = await DownloadTileAsync(lat, lon, filePath);
                    if (!downloaded) return null;
                }

                // Читаем высоту
                return ReadElevation(filePath, lat, lon);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"SRTM Error: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Синхронная версия (для UI)
        /// </summary>
        public double? GetElevation(double lat, double lon)
        {
            return GetElevationAsync(lat, lon).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Предзагрузка тайлов для региона
        /// </summary>
        public async Task PreloadRegionAsync(double minLat, double maxLat, double minLon, double maxLon,
            IProgress<(int current, int total, string status)> progress = null,
            CancellationToken cancellationToken = default)
        {
            int startLat = (int)Math.Floor(minLat);
            int endLat = (int)Math.Floor(maxLat);
            int startLon = (int)Math.Floor(minLon);
            int endLon = (int)Math.Floor(maxLon);

            int total = (endLat - startLat + 1) * (endLon - startLon + 1);
            int current = 0;

            for (int lat = startLat; lat <= endLat; lat++)
            {
                for (int lon = startLon; lon <= endLon; lon++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    current++;
                    string fileName = GetTileFileName(lat, lon);
                    string filePath = Path.Combine(_cacheFolder, fileName);

                    if (!File.Exists(filePath))
                    {
                        progress?.Report((current, total, $"Загрузка {fileName}..."));
                        await DownloadTileAsync(lat, lon, filePath, cancellationToken);
                    }
                    else
                    {
                        progress?.Report((current, total, $"{fileName} уже есть"));
                    }
                }
            }

            progress?.Report((total, total, "Готово!"));
        }

        private string GetTileFileName(double lat, double lon)
        {
            int latInt = (int)Math.Floor(lat);
            int lonInt = (int)Math.Floor(lon);

            string ns = latInt >= 0 ? "N" : "S";
            string ew = lonInt >= 0 ? "E" : "W";

            return $"{ns}{Math.Abs(latInt):D2}{ew}{Math.Abs(lonInt):D3}.hgt";
        }

        private async Task<bool> DownloadTileAsync(double lat, double lon, string filePath, CancellationToken cancellationToken = default)
        {
            try
            {
                int latInt = (int)Math.Floor(lat);
                int lonInt = (int)Math.Floor(lon);

                string ns = latInt >= 0 ? "N" : "S";
                string ew = lonInt >= 0 ? "E" : "W";
                string tileName = $"{ns}{Math.Abs(latInt):D2}{ew}{Math.Abs(lonInt):D3}";
                string folder = $"{ns}{Math.Abs(latInt):D2}";

                // URL: https://elevation-tiles-prod.s3.amazonaws.com/skadi/N43/N43E076.hgt.gz
                string url = $"{SRTM_URL}/{folder}/{tileName}.hgt.gz";

                System.Diagnostics.Debug.WriteLine($"📥 Downloading: {url}");

                var response = await _httpClient.GetAsync(url, cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    System.Diagnostics.Debug.WriteLine($"❌ SRTM not found: {tileName}");
                    return false;
                }

                // Распаковываем gzip
                using var compressedStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var gzipStream = new GZipStream(compressedStream, CompressionMode.Decompress);
                using var fileStream = File.Create(filePath);
                await gzipStream.CopyToAsync(fileStream, cancellationToken);

                System.Diagnostics.Debug.WriteLine($"✅ Downloaded: {tileName}");
                return true;
            }
            catch (OperationCanceledException)
            {
                System.Diagnostics.Debug.WriteLine($"⏹️ Download cancelled");
                throw;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"❌ Download error: {ex.Message}");
                return false;
            }
        }

        private double? ReadElevation(string filePath, double lat, double lon)
        {
            // SRTM3: 1201x1201 точек, 2 байта на точку
            const int size = 1201;

            double latFrac = lat - Math.Floor(lat);
            double lonFrac = lon - Math.Floor(lon);

            int row = (int)((1 - latFrac) * (size - 1));
            int col = (int)(lonFrac * (size - 1));

            row = Math.Clamp(row, 0, size - 1);
            col = Math.Clamp(col, 0, size - 1);

            int offset = (row * size + col) * 2;

            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            fs.Seek(offset, SeekOrigin.Begin);

            byte[] buffer = new byte[2];
            fs.Read(buffer, 0, 2);

            // Big-endian signed 16-bit
            short elevation = (short)((buffer[0] << 8) | buffer[1]);

            // -32768 означает нет данных
            if (elevation == -32768) return null;

            return elevation;
        }

        /// <summary>
        /// Проверить есть ли тайл в кэше
        /// </summary>
        public bool IsTileCached(double lat, double lon)
        {
            string fileName = GetTileFileName(lat, lon);
            return File.Exists(Path.Combine(_cacheFolder, fileName));
        }

        /// <summary>
        /// Размер кэша
        /// </summary>
        public long GetCacheSize()
        {
            if (!Directory.Exists(_cacheFolder)) return 0;
            return new DirectoryInfo(_cacheFolder)
                .GetFiles("*.hgt")
                .Sum(f => f.Length);
        }

        /// <summary>
        /// Очистить кэш
        /// </summary>
        public void ClearCache()
        {
            if (Directory.Exists(_cacheFolder))
            {
                foreach (var file in Directory.GetFiles(_cacheFolder, "*.hgt"))
                    File.Delete(file);
            }
        }
    }
}