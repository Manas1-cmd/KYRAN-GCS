using System;
using System.Collections.Generic;
using SimpleDroneGCS.Models;
using static SimpleDroneGCS.Helpers.Loc;

namespace SimpleDroneGCS.Services
{
    public sealed class TelemetryNotifier
    {
        private readonly INotificationService _notifier;
        private readonly MAVLinkService _mav;

        private bool _batteryLowFired = false;
        private bool _batteryCritFired = false;
        private byte _lastGpsFix = byte.MaxValue;
        private bool? _lastArmed = null;
        private string _lastMode = null;
        private bool _altDropActive = false;
        private DateTime _altDropCooldown = DateTime.MinValue;
        private bool _inFixedWingMode = false;
        private bool _heartbeatLostFired = false;

        private const int BatteryResetPct = 25;
        private const double HeartbeatTimeoutSec = 5.0;
        private const double AltDropThresholdMps = -3.0;
        private const double AltDropMinAltitudeM = 5.0;
        private const int AltDropCooldownSec = 10;

        private static readonly HashSet<string> FixedWingModes = new(StringComparer.OrdinalIgnoreCase)
        {
            "FBWA", "FBWB", "CRUISE", "AUTO", "GUIDED",
            "TAKEOFF", "LOITER", "AUTOTUNE", "MANUAL", "TRAINING"
        };

        private static readonly HashSet<string> VtolModes = new(StringComparer.OrdinalIgnoreCase)
        {
            "QSTABILIZE", "QHOVER", "QLOITER", "QLAND", "QRTL", "QACRO", "QAUTOTUNE"
        };

        public TelemetryNotifier(INotificationService notifications, MAVLinkService mav = null)
        {
            _notifier = notifications ?? throw new ArgumentNullException(nameof(notifications));
            _mav = mav;
        }

        public void Check(Telemetry telemetry, bool isConnected, DateTime lastHeartbeat, VehicleType vehicleType)
        {
            if (telemetry == null) return;

            CheckHeartbeat(isConnected, lastHeartbeat);
            if (!isConnected) return;

            CheckBattery(telemetry);
            CheckGps(telemetry);
            CheckArmed(telemetry);
            CheckFlightMode(telemetry, vehicleType);
            CheckAltitudeDrop(telemetry);

            if (vehicleType == VehicleType.QuadPlane)
                CheckVtolTransition(telemetry);
        }

        public void Reset()
        {
            _batteryLowFired = false;
            _batteryCritFired = false;
            _lastGpsFix = byte.MaxValue;
            _lastArmed = null;
            _lastMode = null;
            _altDropActive = false;
            _altDropCooldown = DateTime.MinValue;
            _inFixedWingMode = false;
            _heartbeatLostFired = false;
            _notifier.ClearSpamCache();
        }

        private void CheckHeartbeat(bool isConnected, DateTime lastHeartbeat)
        {
            if (!isConnected) return;

            bool timedOut = lastHeartbeat != DateTime.MinValue &&
                            (DateTime.Now - lastHeartbeat).TotalSeconds > HeartbeatTimeoutSec;

            if (timedOut && !_heartbeatLostFired)
            {
                _notifier.Hud(Get("Notif_HeartbeatLost"), NotificationType.Error);
                _heartbeatLostFired = true;
            }
            else if (!timedOut && _heartbeatLostFired)
            {
                _notifier.Hud(Get("Notif_HeartbeatBack"), NotificationType.Success);
                _heartbeatLostFired = false;
            }
        }

        private void CheckBattery(Telemetry t)
        {
            if (t.BatteryPercent <= 0) return;

            if (t.BatteryPercent <= 10 && !_batteryCritFired)
            {
                _notifier.Hud(Fmt("Notif_BatteryCrit", t.BatteryPercent), NotificationType.Error);
                _batteryCritFired = true;
                _batteryLowFired = true;
            }
            else if (t.BatteryPercent <= 20 && !_batteryLowFired)
            {
                _notifier.Hud(Fmt("Notif_BatteryLow", t.BatteryPercent), NotificationType.Warning);
                _batteryLowFired = true;
            }

            if (t.BatteryPercent > BatteryResetPct)
            {
                _batteryLowFired = false;
                _batteryCritFired = false;
            }
        }

        private void CheckGps(Telemetry t)
        {
            byte current = ClassifyGpsFix(t.GpsFixType);
            if (current == _lastGpsFix) return;

            byte previous = _lastGpsFix;
            _lastGpsFix = current;

            if (previous == byte.MaxValue) return;

            if (current == 0)
                _notifier.Hud(Get("Notif_GpsLost"), NotificationType.Error);
            else if (current == 2 && previous >= 3)
                _notifier.Warning(Fmt("Notif_Gps2D", t.SatellitesVisible));
            else if (current >= 3 && previous < 3)
                _notifier.Success(Fmt("Notif_GpsOk", t.SatellitesVisible));
        }

        private void CheckArmed(Telemetry t)
        {
            if (_lastArmed == t.Armed) return;

            bool wasEverChecked = _lastArmed.HasValue;
            _lastArmed = t.Armed;

            if (!wasEverChecked) return;

            if (t.Armed) _notifier.Hud(Get("Notif_Armed"), NotificationType.Error);
            else _notifier.Hud(Get("Notif_Disarmed"), NotificationType.Success);
        }

        private void CheckFlightMode(Telemetry t, VehicleType vehicleType)
        {
            string mode = t.FlightMode?.ToUpperInvariant() ?? "UNKNOWN";
            if (string.Equals(mode, _lastMode, StringComparison.Ordinal)) return;

            bool first = _lastMode == null;
            _lastMode = mode;

            if (first) return;

            string msg = Fmt("Notif_ModeChanged", mode);

            if (mode == "RTL" && vehicleType == VehicleType.QuadPlane)
            {
                _notifier.Hud(Get("Notif_RtlToQrtl"), NotificationType.Warning);
                _mav?.SetFlightMode("QRTL");
                return;
            }

            if (mode is "LAND" or "QLAND" or "RTL" or "QRTL")
                _notifier.Hud(msg, NotificationType.Warning);
            else if (mode is "AUTO" or "GUIDED")
                _notifier.Hud(msg, NotificationType.Success);
            else
                _notifier.Info(msg);
        }

        private void CheckAltitudeDrop(Telemetry t)
        {
            bool dropping = t.ClimbRate < AltDropThresholdMps
                         && t.RelativeAltitude > AltDropMinAltitudeM;

            if (dropping && !_altDropActive && DateTime.Now > _altDropCooldown)
            {
                _notifier.Warning(Fmt("Notif_AltDrop", t.ClimbRate.ToString("F1")));
                _altDropActive = true;
                _altDropCooldown = DateTime.Now.AddSeconds(AltDropCooldownSec);
            }
            else if (!dropping)
            {
                _altDropActive = false;
            }
        }

        private void CheckVtolTransition(Telemetry t)
        {
            string mode = t.FlightMode?.ToUpperInvariant() ?? string.Empty;

            bool nowFixedWing = FixedWingModes.Contains(mode);
            bool nowVtol = VtolModes.Contains(mode);
            bool isActuallyFlying = t.Armed && t.RelativeAltitude > 10.0;

            if (nowFixedWing && !_inFixedWingMode)
            {
                _inFixedWingMode = true;
                if (isActuallyFlying)
                    _notifier.Hud(Get("Notif_ToFixedWing"), NotificationType.Info);
            }
            else if (nowVtol && _inFixedWingMode)
            {
                _inFixedWingMode = false;
                if (isActuallyFlying)
                    _notifier.Hud(Get("Notif_ToMulticopter"), NotificationType.Info);
            }
        }

        private static byte ClassifyGpsFix(int fixType) =>
            fixType >= 3 ? (byte)3 :
            fixType >= 2 ? (byte)2 : (byte)0;
    }
}