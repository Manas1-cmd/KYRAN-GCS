using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using SimpleDroneGCS.Helpers;
using SimpleDroneGCS.Models;

namespace SimpleDroneGCS.Services
{
    public sealed class FlightLogService : IFlightLogService, IDisposable
    {
        private const int CsvIntervalMs = 1000;
        private const long MaxFileSizeBytes = 50L * 1024 * 1024;
        private static readonly string[] CsvHeader =
        {
            "Timestamp","Mode","Armed",
            "Lat","Lon","AltRel","AltAbs",
            "Heading","Speed","ClimbRate","Roll","Pitch","Throttle",
            "BattV","BattA","BattPct",
            "Sats","GPSFix","DistHome","WP"
        };

        private readonly INotificationService _notif;
        private readonly string _logDirectory;

        private MAVLinkService _mavLink;
        private bool _wasArmed;
        private Telemetry _lastTelemetry;
        private readonly object _lock = new();

        private FileStream _tlogStream;

        private StreamWriter _csvWriter;
        private Timer _csvTimer;

        public bool IsRecording { get; private set; }
        public string TlogPath { get; private set; }
        public string CsvPath { get; private set; }

        public FlightLogService(INotificationService notif)
        {
            _notif = notif ?? throw new ArgumentNullException(nameof(notif));

            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var logDir = AppSettings.Instance.GetSettings().LogDirectory;
            _logDirectory = Path.Combine(appData, "SQK_GCS", logDir);
        }

        public void Attach(MAVLinkService mavLink)
        {
            _mavLink = mavLink ?? throw new ArgumentNullException(nameof(mavLink));
            _mavLink.TelemetryUpdated += OnTelemetry;
            _mavLink.RawPacketReceived += OnRawPacket;
            _mavLink.ConnectionStatusChanged_Bool += OnConnectionChanged;
        }

        public void Detach()
        {
            if (_mavLink is null) return;
            _mavLink.TelemetryUpdated -= OnTelemetry;
            _mavLink.RawPacketReceived -= OnRawPacket;
            _mavLink.ConnectionStatusChanged_Bool -= OnConnectionChanged;
            _mavLink = null;
            ForceStop();
        }

        public void ForceStop() => StopRecording(forced: true);

        public string[] GetLogFiles()
        {
            if (!Directory.Exists(_logDirectory))
                return Array.Empty<string>();
            return Directory.GetFiles(_logDirectory, "KYRAN_*.tlog");
        }

        private void OnConnectionChanged(object sender, bool connected)
        {
            if (!connected && IsRecording)
                StopRecording(forced: false);
        }

        private void OnTelemetry(object sender, Telemetry t)
        {
            lock (_lock)
            {
                _lastTelemetry = t;

                if (!_wasArmed && t.Armed)
                {
                    _wasArmed = true;
                    StartRecording(t);
                }
                else if (_wasArmed && !t.Armed)
                {
                    _wasArmed = false;
                    StopRecording(forced: false);
                }
            }
        }

        private void OnRawPacket(byte[] rawPacket)
        {
            try
            {
                long us = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L;

                Span<byte> ts = stackalloc byte[8];
                ts[0] = (byte)(us >> 56); ts[1] = (byte)(us >> 48);
                ts[2] = (byte)(us >> 40); ts[3] = (byte)(us >> 32);
                ts[4] = (byte)(us >> 24); ts[5] = (byte)(us >> 16);
                ts[6] = (byte)(us >> 8); ts[7] = (byte)(us);

                lock (_lock)
                {
                    if (!IsRecording || _tlogStream is null) return;                    _tlogStream.Write(ts);
                    _tlogStream.Write(rawPacket, 0, rawPacket.Length);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[FlightLog] tlog write error: {ex.Message}");
            }
        }

        private void StartRecording(Telemetry t)
        {
            if (IsRecording) return;
            if (!AppSettings.Instance.GetSettings().EnableLogging) return;

            try
            {
                EnsureDirectory();

                var stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
                TlogPath = Path.Combine(_logDirectory, $"KYRAN_{stamp}.tlog");
                CsvPath = Path.Combine(_logDirectory, $"KYRAN_{stamp}.csv");

                _tlogStream = new FileStream(TlogPath, FileMode.Create,
                                             FileAccess.Write, FileShare.Read,
                                             bufferSize: 65536);

                _csvWriter = new StreamWriter(CsvPath, append: false, Encoding.UTF8);
                _csvWriter.WriteLine(string.Join(",", CsvHeader));
                _csvWriter.Flush();

                IsRecording = true;

                TryWriteCsvRow(t);

                _csvTimer = new Timer(CsvTimerCallback, null, CsvIntervalMs, CsvIntervalMs);

                _notif.Info(Loc.Fmt("Log_Started", Path.GetFileNameWithoutExtension(TlogPath)));
            }
            catch (Exception ex)
            {
                _notif.Error(Loc.Fmt("Log_ErrorCreate", ex.Message));
                CloseFiles();
            }
        }

        private void StopRecording(bool forced)
        {
            if (!IsRecording) return;

            string name;
            lock (_lock)
            {
                IsRecording = false;
                name = Path.GetFileNameWithoutExtension(TlogPath ?? "");

                _csvTimer?.Dispose();
                _csvTimer = null;

                if (_lastTelemetry is not null)
                    TryWriteCsvRow(_lastTelemetry);

                CloseFiles();
            }

            if (forced)
                _notif.Warning(Loc.Fmt("Log_Stopped", name));
            else
                _notif.Info(Loc.Fmt("Log_Saved", name));
        }

        private void CsvTimerCallback(object state)
        {
            Telemetry snapshot;
            lock (_lock)
            {
                if (!IsRecording || _lastTelemetry is null) return;
                snapshot = _lastTelemetry;

                try
                {
                    if (_tlogStream is not null && _tlogStream.Length >= MaxFileSizeBytes)
                    {
                        _notif.Warning(Loc.Get("Log_SizeLimit"));
                        StopRecording(forced: true);
                        return;
                    }
                }
                catch { }
            }

            lock (_lock)
            {
                if (IsRecording)
                    TryWriteCsvRow(snapshot);
            }
        }

        private void TryWriteCsvRow(Telemetry t)
        {
            if (_csvWriter is null) return;
            try
            {
                var sb = new StringBuilder(256);
                sb.Append(t.LastUpdate.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture)); sb.Append(',');
                sb.Append(t.FlightMode); sb.Append(',');
                sb.Append(t.Armed ? '1' : '0'); sb.Append(',');
                D(sb, t.Latitude, 7); sb.Append(',');
                D(sb, t.Longitude, 7); sb.Append(',');
                D(sb, t.RelativeAltitude, 2); sb.Append(',');
                D(sb, t.Altitude, 2); sb.Append(',');
                D(sb, t.Heading, 1); sb.Append(',');
                D(sb, t.Speed, 2); sb.Append(',');
                D(sb, t.ClimbRate, 2); sb.Append(',');
                D(sb, t.Roll, 2); sb.Append(',');
                D(sb, t.Pitch, 2); sb.Append(',');
                D(sb, t.Throttle, 1); sb.Append(',');
                D(sb, t.BatteryVoltage, 2); sb.Append(',');
                D(sb, t.BatteryCurrent, 2); sb.Append(',');
                sb.Append(t.BatteryPercent); sb.Append(',');
                sb.Append(t.SatellitesVisible); sb.Append(',');
                sb.Append(t.GpsFixType); sb.Append(',');
                D(sb, t.DistanceFromHome, 1); sb.Append(',');
                sb.Append(t.CurrentWaypoint);

                _csvWriter.WriteLine(sb);
                _csvWriter.Flush();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[FlightLog] csv write error: {ex.Message}");
            }
        }

        private static void D(StringBuilder sb, double v, int dec)
            => sb.Append(v.ToString($"F{dec}", CultureInfo.InvariantCulture));

        private void CloseFiles()
        {
            try { _csvWriter?.Flush(); _csvWriter?.Dispose(); } catch { }
            try { _tlogStream?.Flush(); _tlogStream?.Dispose(); } catch { }
            _csvWriter = null;
            _tlogStream = null;
        }

        private void EnsureDirectory()
        {
            if (!Directory.Exists(_logDirectory))
                Directory.CreateDirectory(_logDirectory);
        }

        public void Dispose() => Detach();
    }
}