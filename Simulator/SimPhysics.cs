using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace SimpleDroneGCS.Simulator
{
    public enum SimState { Disarmed, Armed, Takeoff, Mission, Rtl, Landing }

    public class SimPhysics
    {
        public double Lat { get; private set; }
        public double Lon { get; private set; }
        public double AltRel { get; private set; }
        public double AltMsl { get; private set; }
        public double Speed { get; private set; }
        public double Heading { get; private set; }
        public float Roll { get; private set; }
        public float Pitch { get; private set; }
        public double ClimbRate { get; private set; }
        public double BattPct { get; private set; } = 100.0;
        public double Voltage { get; private set; } = 12.6;
        public byte GpsFixType { get; private set; } = 3;
        public byte SatCount { get; private set; } = 12;
        public SimState State { get; private set; } = SimState.Disarmed;
        public string FlightMode { get; private set; } = "STABILIZE";
        public bool Armed { get; private set; }
        public int CurrentWpIndex { get; private set; } = -1;
        public int TotalWpCount => _waypoints.Count;

        public short NavBearing
        {
            get
            {
                if (CurrentWpIndex < 0 || CurrentWpIndex >= _waypoints.Count)
                    return (short)Math.Round(Heading);
                var wp = _waypoints[CurrentWpIndex];
                double b = Bearing(Lat, Lon, wp.Lat, wp.Lon);
                if (b > 180) b -= 360;
                return (short)Math.Round(b);
            }
        }

        public double CruiseSpeed { get; set; } = 8.0;
        public bool IsVtol { get; set; } = false;
        public bool ScenarioGpsLoss { get; set; }
        public bool ScenarioBattDrain { get; set; }
        public bool ScenarioRcFailsafe { get; set; }

        public event Action<string>? StatusTextEvent;

        private readonly double _homeLat, _homeLon, _homeAltMsl;
        private List<SimWaypoint> _waypoints = new();
        private double _targetAlt;
        private double _currentSpeed;
        private double _prevAltRel;
        private double _elapsedSec;
        private bool _missionPaused; private bool _gpsFailing;
        private bool _rcFailsafeFired;
        private bool _battLow20Fired;
        private bool _battLow10Fired;

        private const double Accel = 3.0;
        private const double Decel = 4.0;
        private const double VertSpeedUp = 3.0;
        private const double VertSpeedDn = 1.5;
        private const double AcceptRadius = 8.0;

        public SimPhysics(double homeLat, double homeLon, double homeAltMsl)
        {
            _homeLat = homeLat;
            _homeLon = homeLon;
            _homeAltMsl = homeAltMsl;
            Lat = homeLat; Lon = homeLon;
            AltMsl = homeAltMsl; AltRel = 0;
        }

        public void SetMission(List<SimWaypoint> waypoints)
        {
            int prevIndex = CurrentWpIndex; _waypoints = new List<SimWaypoint>(waypoints);
            _missionPaused = false;
            _wpWaiting = false;
            _wpWaitRemaining = 0;

            if (State == SimState.Mission && prevIndex >= 0 && prevIndex < _waypoints.Count)
                CurrentWpIndex = prevIndex;
            else
                CurrentWpIndex = -1;
        }

        public void UpdateWaypointAt(int seq, SimWaypoint updated)
        {
            if (seq >= 0 && seq < _waypoints.Count)
                _waypoints[seq] = updated;
        }

        public void Tick(double dt, ConcurrentQueue<SimCommand> commands)
        {
            _elapsedSec += dt;
            _prevAltRel = AltRel;

            while (commands.TryDequeue(out var cmd))
                ProcessCommand(cmd);

            double drainRate = Armed
                ? (ScenarioBattDrain ? 0.3 : 0.025)
                : 0.003;
            BattPct = Math.Max(0, BattPct - drainRate * dt);
            Voltage = 12.6 - (1.0 - BattPct / 100.0) * 2.1;

            if (BattPct <= 20.0 && !_battLow20Fired)
            {
                _battLow20Fired = true;
                StatusTextEvent?.Invoke("Low Battery");
            }
            if (BattPct <= 10.0 && !_battLow10Fired
                && State is not (SimState.Rtl or SimState.Landing or SimState.Disarmed))
            {
                _battLow10Fired = true;
                StatusTextEvent?.Invoke("Battery Failsafe - RTL");
                BeginRtl();
            }

            if (ScenarioGpsLoss)
            {
                if (!_gpsFailing) { StatusTextEvent?.Invoke("GPS: No Fix"); _gpsFailing = true; }
                GpsFixType = 0; SatCount = 0;
            }
            else if (_gpsFailing)
            {
                _gpsFailing = false;
                GpsFixType = 3; SatCount = 12;
                StatusTextEvent?.Invoke("GPS: 3D Fix restored");
            }

            if (ScenarioRcFailsafe && _elapsedSec > 20 && !_rcFailsafeFired && Armed)
            {
                _rcFailsafeFired = true;
                StatusTextEvent?.Invoke("Radio failsafe - RTL");
                BeginRtl();
            }

            switch (State)
            {
                case SimState.Takeoff: TickTakeoff(dt); break;
                case SimState.Mission: TickMission(dt); break;
                case SimState.Rtl: TickRtl(dt); break;
                case SimState.Landing: TickLanding(dt); break;
            }

            ClimbRate = (AltRel - _prevAltRel) / dt;
            UpdateAttitude();
        }


        private void ProcessCommand(SimCommand cmd)
        {
            switch (cmd.Type)
            {
                case SimCommandType.Arm:
                    if (State == SimState.Disarmed)
                    {
                        Armed = true;
                        State = SimState.Armed;
                        StatusTextEvent?.Invoke("Armed");
                    }
                    break;

                case SimCommandType.Disarm:
                    if (State is SimState.Disarmed or SimState.Armed)
                    {
                        Armed = false; State = SimState.Disarmed;
                        Speed = 0; _currentSpeed = 0;
                        FlightMode = "STABILIZE";
                        // Сбрасываем сценарные флаги для повторного запуска
                        _rcFailsafeFired = false;
                        _battLow20Fired = false;
                        _battLow10Fired = false;
                        _elapsedSec = 0;
                        StatusTextEvent?.Invoke("Disarmed");
                    }
                    break;

                case SimCommandType.SetMode:
                    FlightMode = cmd.ModeName ?? "STABILIZE";
                    if (cmd.ModeName == "RTL") BeginRtl();
                    if (cmd.ModeName == "LAND") BeginLanding();
                    if (cmd.ModeName == "LOITER" && State == SimState.Mission)
                    {
                        _missionPaused = true;
                        Speed = 0; _currentSpeed = 0;
                        StatusTextEvent?.Invoke("Mission paused (LOITER)");
                    }
                    if (cmd.ModeName == "AUTO" && _missionPaused)
                    {
                        _missionPaused = false;
                        StatusTextEvent?.Invoke("Mission resumed (AUTO)");
                    }
                    break;

                case SimCommandType.MissionStart:
                    if (Armed && _waypoints.Count > 0)
                        BeginMission();
                    break;

                case SimCommandType.MissionClearAll:
                    _waypoints.Clear();
                    CurrentWpIndex = -1;
                    break;

                case SimCommandType.SetCurrentWaypoint:
                    if (cmd.WpSeq >= 0 && cmd.WpSeq < _waypoints.Count)
                    {
                        CurrentWpIndex = cmd.WpSeq;
                        _missionPaused = false;
                        _wpWaiting = false;
                        _wpWaitRemaining = 0;
                        int _offset = IsVtol ? 3 : 2;
                        int _userNum = Math.Max(1, CurrentWpIndex - _offset + 1);
                        StatusTextEvent?.Invoke($"Next WP {_userNum}");
                    }
                    break;
            }
        }


        private void BeginMission()
        {
            FlightMode = "AUTO";
            State = SimState.Takeoff;
            _targetAlt = 30;
            _missionPaused = false;

            // cmd 22 = TAKEOFF (copter), cmd 84 = VTOL_TAKEOFF
            for (int i = 0; i < _waypoints.Count; i++)
            {
                var wp = _waypoints[i];
                if (wp.Command == 22 || wp.Command == 84 || wp.Alt > 5f)
                {
                    _targetAlt = wp.Alt;
                    CurrentWpIndex = i;
                    break;
                }
            }
            if (CurrentWpIndex < 0) CurrentWpIndex = 0;
            StatusTextEvent?.Invoke($"Mission: Takeoff -> {_targetAlt:F0} m");
        }

        private void BeginRtl()
        {
            if (State is SimState.Rtl or SimState.Landing or SimState.Disarmed) return;
            FlightMode = "RTL";
            State = SimState.Rtl;
            _missionPaused = false;
            _currentSpeed = Speed;
            StatusTextEvent?.Invoke("RTL initiated");
        }

        private void BeginLanding()
        {
            if (State is SimState.Landing or SimState.Disarmed) return;
            FlightMode = "LAND";
            State = SimState.Landing;
            Speed = 0;
            _currentSpeed = 0;
            StatusTextEvent?.Invoke("Landing");
        }


        private void TickTakeoff(double dt)
        {
            AltRel = Math.Min(AltRel + VertSpeedUp * dt, _targetAlt);
            AltMsl = _homeAltMsl + AltRel;

            if (AltRel >= _targetAlt - 0.3)
            {
                CurrentWpIndex++;
                if (CurrentWpIndex >= _waypoints.Count)
                    BeginRtl();
                else
                {
                    State = SimState.Mission;
                    int userWpNum = Math.Max(1, CurrentWpIndex - (IsVtol ? 3 : 2) + 1);
                    StatusTextEvent?.Invoke($"Takeoff complete -> WP {userWpNum}");
                }
            }
        }

        private bool _wpWaiting = false;
        private double _wpWaitRemaining = 0;
        private double _wpOrbitAngle = 0;
        private double _wpOrbitDone = 0;

        public bool UploadInProgress { get; set; } = false;

        private void TickMission(double dt)
        {
            if (_missionPaused) return;

            if (_waypoints.Count == 0) return;

            if (CurrentWpIndex < 0) return;

            if (CurrentWpIndex >= _waypoints.Count)
            {
                StatusTextEvent?.Invoke("Mission complete");
                BeginRtl();
                return;
            }

            var wp = _waypoints[CurrentWpIndex];

            if (wp.Command == 22) { CurrentWpIndex++; return; }   // TAKEOFF - пропускаем
            if (wp.Command == 84) { CurrentWpIndex++; return; }   // VTOL_TAKEOFF - пропускаем
            if (wp.Command == 85) { BeginLanding(); return; }     // VTOL_LAND
            if (wp.Command == 3000) { CurrentWpIndex++; return; } // VTOL_TRANSITION - пропускаем
            if (wp.Command == 20) { BeginRtl(); return; }
            if (wp.Command == 178)
            {
                if (wp.Param2 > 0) CruiseSpeed = wp.Param2;
                CurrentWpIndex++;
                return;
            }

            if (_wpWaiting)
            {
                bool isLoiter = wp.Command == 19 || wp.Command == 18 || wp.Command == 17;
                if (isLoiter && wp.Alt > 1f)
                {
                    double radius = Math.Max(20, Math.Abs(wp.Param3) > 0 ? Math.Abs(wp.Param3) : 80);
                    bool cw = wp.Param3 >= 0;
                    double angSpeed = (CruiseSpeed / radius) * (180.0 / Math.PI); _wpOrbitAngle += (cw ? angSpeed : -angSpeed) * dt;
                    double rad = _wpOrbitAngle * Math.PI / 180.0;
                    Lat = wp.Lat + Math.Cos(rad) * radius / 111111.0;
                    Lon = wp.Lon + Math.Sin(rad) * radius / (111111.0 * Math.Cos(wp.Lat * Math.PI / 180.0));
                    Heading = (_wpOrbitAngle + (cw ? 90 : -90) + 360) % 360;
                    Speed = CruiseSpeed;
                    _wpOrbitDone = Math.Abs(_wpOrbitAngle) / 360.0;
                }
                else
                {
                    Speed = 0;
                }

                if (wp.Command == 18)
                {
                    double targetTurns = wp.Param1 > 0 ? wp.Param1 : 1;
                    if (_wpOrbitDone >= targetTurns)
                        AdvanceWaypoint();
                }
                else
                {
                    _wpWaitRemaining -= dt;
                    if (_wpWaitRemaining <= 0)
                        AdvanceWaypoint();
                }
                return;
            }

            double altDiff = wp.Alt - AltRel;
            if (Math.Abs(altDiff) > 0.5)
            {
                double vs = altDiff > 0 ? VertSpeedUp : -VertSpeedDn;
                AltRel = Math.Max(0, AltRel + vs * dt);
                AltMsl = _homeAltMsl + AltRel;
            }

            double distToCenter = Haversine(Lat, Lon, wp.Lat, wp.Lon);
            double bearingToCenter = Bearing(Lat, Lon, wp.Lat, wp.Lon);

            if (distToCenter < AcceptRadius)
            {
                StatusTextEvent?.Invoke($"WP {Math.Max(1, CurrentWpIndex - (IsVtol ? 3 : 2) + 1)} reached");

                double delay = GetWpDelay(wp);
                if (delay > 0 || wp.Command == 18 || wp.Command == 17)
                {
                    _wpWaiting = true;
                    _wpWaitRemaining = delay;
                    _wpOrbitAngle = bearingToCenter + 90;
                    _wpOrbitDone = 0;
                    if (delay > 0)
                        StatusTextEvent?.Invoke($"WP {Math.Max(1, CurrentWpIndex - (IsVtol ? 3 : 2) + 1)}: delay {delay:F0}s");
                    return;
                }

                AdvanceWaypoint();
                return;
            }

            MoveTowards(bearingToCenter, distToCenter, dt);
        }

        private void AdvanceWaypoint()
        {
            if (UploadInProgress) return;

            _wpWaiting = false;
            _wpWaitRemaining = 0;
            _wpOrbitAngle = 0;
            _wpOrbitDone = 0;

            int nextIdx = CurrentWpIndex + 1;

            if (nextIdx < _waypoints.Count)
            {
                var next = _waypoints[nextIdx];
                if (next.Command == 20 && nextIdx < _waypoints.Count - 1)
                {
                    System.Diagnostics.Debug.WriteLine($"[SIM] RTL at non-last seq={nextIdx}, holding");
                    return;
                }
            }

            CurrentWpIndex = nextIdx;
            if (CurrentWpIndex >= _waypoints.Count)
            {
                StatusTextEvent?.Invoke("Mission complete");
                BeginRtl();
            }
        }

        private static double GetWpDelay(SimWaypoint wp)
        {
            return wp.Command switch
            {
                16 => wp.Param1 > 0 ? wp.Param1 : 0,
                82 => wp.Param1 > 0 ? wp.Param1 : 0,
                19 => wp.Param1 > 0 ? wp.Param1 : 0,
                17 => 99999,
                93 => wp.Param1 > 0 ? wp.Param1 : 0,
                _ => 0
            };
        }

        private void TickRtl(double dt)
        {
            const double RtlAlt = 30.0;
            if (AltRel < RtlAlt - 0.5)
            {
                AltRel = Math.Min(AltRel + VertSpeedUp * dt, RtlAlt);
                AltMsl = _homeAltMsl + AltRel;
            }

            double dist = Haversine(Lat, Lon, _homeLat, _homeLon);
            double bearing = Bearing(Lat, Lon, _homeLat, _homeLon);

            if (dist > AcceptRadius)
                MoveTowards(bearing, dist, dt);
            else
            {
                Lat = _homeLat; Lon = _homeLon;
                Speed = 0; _currentSpeed = 0;
                BeginLanding();
            }
        }

        private void TickLanding(double dt)
        {
            Speed = 0; _currentSpeed = 0;
            AltRel = Math.Max(0, AltRel - VertSpeedDn * dt);
            AltMsl = _homeAltMsl + AltRel;

            if (AltRel < 0.3)
            {
                AltRel = 0; AltMsl = _homeAltMsl;
                Armed = false;
                State = SimState.Disarmed;
                FlightMode = "STABILIZE";
                StatusTextEvent?.Invoke("Landed. Disarmed.");
            }
        }


        private void MoveTowards(double bearing, double dist, double dt)
        {
            double hdgDiff = bearing - Heading;
            if (hdgDiff > 180) hdgDiff -= 360;
            if (hdgDiff < -180) hdgDiff += 360;
            double maxTurn = 90 * dt;
            Heading = (Heading + Math.Sign(hdgDiff) * Math.Min(Math.Abs(hdgDiff), maxTurn) + 360) % 360;

            double brakingDist = (_currentSpeed * _currentSpeed) / (2 * Decel);
            double targetSpd = dist < brakingDist
                ? Math.Sqrt(2 * Decel * Math.Max(1.0, dist))
                : CruiseSpeed;
            targetSpd = Math.Clamp(targetSpd, 0.3, CruiseSpeed);

            if (_currentSpeed < targetSpd)
                _currentSpeed = Math.Min(_currentSpeed + Accel * dt, targetSpd);
            else
                _currentSpeed = Math.Max(_currentSpeed - Decel * dt, targetSpd);

            Speed = _currentSpeed;

            double brgRad = Heading * Math.PI / 180.0;
            double dMeters = _currentSpeed * dt;
            Lat += Math.Cos(brgRad) * dMeters / 111111.0;
            Lon += Math.Sin(brgRad) * dMeters / (111111.0 * Math.Cos(Lat * Math.PI / 180.0));
        }

        private void UpdateAttitude()
        {
            float targetRoll = 0f, targetPitch = 0f;

            if (State == SimState.Takeoff)
                targetPitch = 0.18f;
            else if (State == SimState.Landing)
                targetPitch = -0.12f;
            else if (State is SimState.Mission or SimState.Rtl && Speed > 0.5)
                targetPitch = 0.05f;

            Roll += (targetRoll - Roll) * 0.17f;
            Pitch += (targetPitch - Pitch) * 0.17f;
        }


        private static double Haversine(double lat1, double lon1, double lat2, double lon2)
        {
            const double R = 6371000.0;
            double dLat = (lat2 - lat1) * Math.PI / 180.0;
            double dLon = (lon2 - lon1) * Math.PI / 180.0;
            double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                     + Math.Cos(lat1 * Math.PI / 180.0) * Math.Cos(lat2 * Math.PI / 180.0)
                     * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
            return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        }

        private static double Bearing(double lat1, double lon1, double lat2, double lon2)
        {
            double lat1R = lat1 * Math.PI / 180.0;
            double lat2R = lat2 * Math.PI / 180.0;
            double dLon = (lon2 - lon1) * Math.PI / 180.0;
            double y = Math.Sin(dLon) * Math.Cos(lat2R);
            double x = Math.Cos(lat1R) * Math.Sin(lat2R)
                     - Math.Sin(lat1R) * Math.Cos(lat2R) * Math.Cos(dLon);
            return (Math.Atan2(y, x) * 180.0 / Math.PI + 360) % 360;
        }
    }
}