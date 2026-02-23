using System.Collections.Generic;

namespace SimpleDroneGCS.Models
{
    public enum VehicleType
    {
        Copter,        
        Plane,         
        QuadPlane,     
        Rover,         
        Boat           
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

        public byte MavType { get; set; }  
        public bool SupportsVTOL { get; set; }
        public bool RequiresAirspeed { get; set; }
        public bool SupportsHover { get; set; }
    }

    public class TelemetryConfiguration
    {
        
        public bool ShowAirspeed { get; set; }
        public bool ShowGroundSpeed { get; set; }
        public bool ShowClimbRate { get; set; }
        public bool ShowThrottle { get; set; }
        public bool ShowRPM { get; set; }
        public bool ShowVTOLStatus { get; set; }
        public bool ShowFlaps { get; set; }

        public double MaxAirspeed { get; set; }  
        public double StallSpeed { get; set; }   
        public double CruiseSpeed { get; set; }  
        public double MaxAltitude { get; set; }  
        public double MaxRange { get; set; }     
    }

    public class ParameterSet
    {
        public string Name { get; set; }
        public Dictionary<string, float> Parameters { get; set; }
    }
}