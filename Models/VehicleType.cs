using System.Collections.Generic;

namespace SimpleDroneGCS.Models
{
    public enum VehicleType
    {
        Copter,        // Multirotor (Quad, Hexa, Octo)
        Plane,         // Fixed Wing
        QuadPlane,     // VTOL (Vertical Takeoff and Landing)
        Rover,         // Ground Vehicle (future)
        Boat           // Water Vehicle (future)
    }

    public class VehicleProfile
    {
        public VehicleType Type { get; set; }
        public string DisplayName { get; set; }
        public string Description { get; set; }
        public string IconPath { get; set; }
        public string[] SupportedFlightModes { get; set; }
        public TelemetryConfiguration TelemetryConfig { get; set; }
        public ParameterSet DefaultParameters { get; set; }

        // MAVLink specific
        public byte MavType { get; set; }  // MAV_TYPE from MAVLink
        public bool SupportsVTOL { get; set; }
        public bool RequiresAirspeed { get; set; }
        public bool SupportsHover { get; set; }
    }

    public class TelemetryConfiguration
    {
        // Which telemetry items to show/hide
        public bool ShowAirspeed { get; set; }
        public bool ShowGroundSpeed { get; set; }
        public bool ShowClimbRate { get; set; }
        public bool ShowThrottle { get; set; }
        public bool ShowRPM { get; set; }
        public bool ShowVTOLStatus { get; set; }
        public bool ShowFlaps { get; set; }

        // Units and ranges
        public double MaxAirspeed { get; set; }  // m/s
        public double StallSpeed { get; set; }   // m/s
        public double CruiseSpeed { get; set; }  // m/s
        public double MaxAltitude { get; set; }  // meters
        public double MaxRange { get; set; }     // km
    }

    public class ParameterSet
    {
        public string Name { get; set; }
        public Dictionary<string, float> Parameters { get; set; }
    }
}