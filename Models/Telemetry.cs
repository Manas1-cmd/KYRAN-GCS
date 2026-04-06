using System;

namespace SimpleDroneGCS.Models
{

    public class Telemetry
    {

        public double Roll { get; set; }
        public double Pitch { get; set; }
        public double Yaw { get; set; }

        public double Altitude { get; set; }
        public double RelativeAltitude { get; set; }
        public double Speed { get; set; }
        public double GroundSpeed => Speed;
        public double Airspeed { get; set; }
        public double ClimbRate { get; set; }
        public double Heading { get; set; }
        public double Throttle { get; set; }
        public double GpsTrack { get; set; } = 0;  // фактическое направление движения (из vx/vy)
        public double NavBearing { get; set; } = 0;
        public bool HasNavBearing { get; set; } = false; // получен ли NAV_CONTROLLER_OUTPUT      

        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public double GpsAltitude { get; set; }
        public int SatellitesVisible { get; set; }
        public int GpsFixType { get; set; }

        public bool IsEkfOk { get; set; } = false;

        public double BatteryVoltage { get; set; }
        public double BatteryCurrent { get; set; }
        public int BatteryPercent { get; set; }

        public int Motor1Percent { get; set; }
        public int Motor2Percent { get; set; }
        public int Motor3Percent { get; set; }
        public int Motor4Percent { get; set; }
        public int PusherPercent { get; set; }

        public string FlightMode { get; set; }
        public bool Armed { get; set; }
        public bool IsArmed => Armed;
        public byte SystemStatus { get; set; }
        public byte BaseMode { get; set; }
        public uint CustomMode { get; set; }

        public ushort CurrentWaypoint { get; set; }

        public int SignalStrength { get; set; }
        public double DistanceFromHome { get; set; }

        public byte SystemId { get; set; }
        public byte ComponentId { get; set; }

        public DateTime LastUpdate { get; set; }

        public Telemetry()
        {
            FlightMode = "UNKNOWN";
            LastUpdate = DateTime.Now;
            SignalStrength = 0;
            DistanceFromHome = 0;
        }

        public bool IsStale()
        {
            return (DateTime.Now - LastUpdate).TotalSeconds > 3;
        }
    }
}