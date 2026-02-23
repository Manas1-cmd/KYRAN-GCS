using System;
using System.Collections.Generic;
using System.IO;

namespace SimpleDroneGCS.Services
{
    
    public class SrtmElevationProvider
    {
        private readonly string _srtmFolder;
        private readonly Dictionary<string, short[]> _cache = new();

        private const int SRTM3_SIZE = 1201;
        private const int SRTM1_SIZE = 3601;

        public SrtmElevationProvider()
        {
            _srtmFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Elevation");

            if (!Directory.Exists(_srtmFolder))
                Directory.CreateDirectory(_srtmFolder);

            System.Diagnostics.Debug.WriteLine($"[SRTM] Папка: {_srtmFolder}");
        }

        public double? GetElevation(double lat, double lng)
        {
            try
            {
                string fileName = GetFileName(lat, lng);
                string filePath = Path.Combine(_srtmFolder, fileName);

                if (!File.Exists(filePath))
                {
                    System.Diagnostics.Debug.WriteLine($"[SRTM] Файл не найден: {fileName}");
                    return null;
                }

                short[] data = LoadFile(filePath);
                if (data == null)
                    return null;

                int size = data.Length == SRTM1_SIZE * SRTM1_SIZE ? SRTM1_SIZE : SRTM3_SIZE;

                double latFrac = lat - Math.Floor(lat);
                double lngFrac = lng - Math.Floor(lng);

                int row = (int)((1 - latFrac) * (size - 1));
                int col = (int)(lngFrac * (size - 1));

                row = Math.Max(0, Math.Min(size - 1, row));
                col = Math.Max(0, Math.Min(size - 1, col));

                short elevation = data[row * size + col];

                if (elevation == -32768)
                    return null;

                return elevation;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SRTM] Ошибка: {ex.Message}");
                return null;
            }
        }

        private string GetFileName(double lat, double lng)
        {
            int latInt = (int)Math.Floor(lat);
            int lngInt = (int)Math.Floor(lng);

            char latDir = latInt >= 0 ? 'N' : 'S';
            char lngDir = lngInt >= 0 ? 'E' : 'W';

            return $"{latDir}{Math.Abs(latInt):D2}{lngDir}{Math.Abs(lngInt):D3}.hgt";
        }

        private short[] LoadFile(string filePath)
        {
            string key = Path.GetFileName(filePath);

            if (_cache.TryGetValue(key, out short[] cached))
                return cached;

            System.Diagnostics.Debug.WriteLine($"[SRTM] Загружаем: {key}");

            byte[] bytes = File.ReadAllBytes(filePath);
            short[] data = new short[bytes.Length / 2];

            for (int i = 0; i < data.Length; i++)
            {
                data[i] = (short)((bytes[i * 2] << 8) | bytes[i * 2 + 1]);
            }

            _cache[key] = data;

            System.Diagnostics.Debug.WriteLine($"[SRTM] Загружено: {key} ({data.Length} точек)");
            return data;
        }

        public bool HasDataFor(double lat, double lng)
        {
            string fileName = GetFileName(lat, lng);
            string filePath = Path.Combine(_srtmFolder, fileName);
            return File.Exists(filePath);
        }

        public string[] GetAvailableFiles()
        {
            if (!Directory.Exists(_srtmFolder))
                return Array.Empty<string>();

            return Directory.GetFiles(_srtmFolder, "*.hgt");
        }
    }
}