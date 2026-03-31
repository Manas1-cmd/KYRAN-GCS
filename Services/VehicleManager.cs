using System;
using System.Collections.Generic;
using SimpleDroneGCS.Models;

namespace SimpleDroneGCS.Services
{
    public class VehicleManager
    {
        private static readonly Lazy<VehicleManager> _lazy =
            new Lazy<VehicleManager>(() => new VehicleManager());
        public static VehicleManager Instance => _lazy.Value;

        private VehicleType _currentVehicleType = VehicleType.Copter;
        private VehicleProfile _currentProfile;
        private readonly object _typeLock = new object();

        public VehicleType CurrentVehicleType
        {
            get { lock (_typeLock) return _currentVehicleType; }
        }

        public VehicleProfile CurrentProfile
        {
            get { lock (_typeLock) return _currentProfile; }
        }

        private readonly Dictionary<VehicleType, VehicleProfile> _profiles;

        private VehicleManager()
        {
            _profiles = InitializeProfiles();
            _currentVehicleType = VehicleType.Copter;
            _currentProfile = _profiles[_currentVehicleType];
        }

        public void SetVehicleType(VehicleType type)
        {
            VehicleProfile profile;
            lock (_typeLock)
            {
                _currentVehicleType = type;
                _currentProfile = _profiles[type];
                profile = _currentProfile;
            }
            VehicleTypeChanged?.Invoke(this, profile);
        }

        public event EventHandler<VehicleProfile> VehicleTypeChanged;

        private Dictionary<VehicleType, VehicleProfile> InitializeProfiles()
        {
            return new Dictionary<VehicleType, VehicleProfile>
            {
                [VehicleType.Copter] = new VehicleProfile
                {
                    Type = VehicleType.Copter,
                    DisplayName = "Multicopter",
                    Description = "Quadcopter, Hexacopter, Octocopter",
                    IconPath = "/Resources/Icons/copter_icon.png",
                    MavType = 2,
                    SupportsVTOL = false,
                    RequiresAirspeed = false,
                    SupportsHover = true,

                    SupportedFlightModes = new[]
                    {
                        "STABILIZE",
                        "ALT_HOLD",
                        "LOITER",
                        "AUTO",
                        "GUIDED",
                        "LAND",
                        "RTL",
                        "CIRCLE",
                        "POSITION",
                        "ACRO",
                        "SPORT",
                        "DRIFT",
                        "FLIP",
                        "BRAKE",
                        "SMART_RTL"
                    },

                    TelemetryConfig = new TelemetryConfiguration
                    {
                        ShowAirspeed = false,
                        ShowGroundSpeed = true,
                        ShowClimbRate = true,
                        ShowThrottle = true,
                        ShowRPM = false,
                        ShowVTOLStatus = false,
                        ShowFlaps = false,
                        MaxAltitude = 500,
                        MaxRange = 5,
                        CruiseSpeed = 15
                    },

                    DefaultParameters = new ParameterSet
                    {
                        Name = "Copter_Defaults",
                        Parameters = new Dictionary<string, float>
                        {
                            ["WPNAV_SPEED"] = 500,
                            ["WPNAV_SPEED_UP"] = 250,
                            ["WPNAV_SPEED_DN"] = 150,
                            ["ANGLE_MAX"] = 3000,
                            ["PILOT_SPEED_UP"] = 250,
                            ["RTL_ALT"] = 3000,
                            ["FENCE_TYPE"] = 3,
                            ["FENCE_ALT_MAX"] = 100,
                            ["FENCE_RADIUS"] = 300
                        }
                    }
                },

                [VehicleType.Plane] = new VehicleProfile
                {
                    Type = VehicleType.Plane,
                    DisplayName = "Fixed Wing",
                    Description = "Traditional airplane",
                    IconPath = "/Resources/Icons/plane_icon.png",
                    MavType = 1,
                    SupportsVTOL = false,
                    RequiresAirspeed = true,
                    SupportsHover = false,

                    SupportedFlightModes = new[]
                    {
                        "MANUAL",
                        "STABILIZE",
                        "FLY_BY_WIRE_A",
                        "FLY_BY_WIRE_B",
                        "AUTOTUNE",
                        "TRAINING",
                        "ACRO",
                        "CRUISE",
                        "AUTO",
                        "RTL",
                        "LOITER",
                        "CIRCLE",
                        "GUIDED",
                        "TAKEOFF",
                        "QSTABILIZE",
                        "QHOVER",
                        "QLOITER",
                        "QLAND",
                        "QRTL"
                    },

                    TelemetryConfig = new TelemetryConfiguration
                    {
                        ShowAirspeed = true,
                        ShowGroundSpeed = true,
                        ShowClimbRate = true,
                        ShowThrottle = true,
                        ShowRPM = false,
                        ShowVTOLStatus = false,
                        ShowFlaps = true,
                        MaxAirspeed = 30,
                        StallSpeed = 12,
                        CruiseSpeed = 20,
                        MaxAltitude = 1000,
                        MaxRange = 50
                    },

                    DefaultParameters = new ParameterSet
                    {
                        Name = "Plane_Defaults",
                        Parameters = new Dictionary<string, float>
                        {
                            ["TRIM_ARSPD_CM"] = 1500,
                            ["ARSPD_FBW_MIN"] = 12,
                            ["ARSPD_FBW_MAX"] = 25,
                            ["WP_RADIUS"] = 90,
                            ["WP_LOITER_RAD"] = 60,
                            ["RTL_RADIUS"] = 60,
                            ["ALT_HOLD_RTL"] = 100,
                            ["LIM_ROLL_CD"] = 4500,
                            ["LIM_PITCH_MAX"] = 2000,
                            ["LIM_PITCH_MIN"] = -2500
                        }
                    }
                },

                [VehicleType.QuadPlane] = new VehicleProfile
                {
                    Type = VehicleType.QuadPlane,
                    DisplayName = "VTOL",
                    Description = "Vertical Takeoff and Landing aircraft",
                    IconPath = "/Resources/Icons/vtol_icon.png",
                    MavType = 20,
                    SupportsVTOL = true,
                    RequiresAirspeed = true,
                    SupportsHover = true,

                    SupportedFlightModes = new[]
                    {

                        "MANUAL", "STABILIZE", "FLY_BY_WIRE_A", "FLY_BY_WIRE_B",
                        "CRUISE", "AUTOTUNE", "AUTO", "RTL", "LOITER", "CIRCLE",

                        "QSTABILIZE", "QHOVER", "QLOITER", "QLAND", "QRTL", "QACRO",

                        "QAUTOTUNE", "GUIDED", "TAKEOFF", "VTOL_TAKEOFF", "VTOL_LAND"
                    },

                    TelemetryConfig = new TelemetryConfiguration
                    {
                        ShowAirspeed = true,
                        ShowGroundSpeed = true,
                        ShowClimbRate = true,
                        ShowThrottle = true,
                        ShowRPM = false,
                        ShowVTOLStatus = true,
                        ShowFlaps = true,
                        MaxAirspeed = 25,
                        StallSpeed = 0,
                        CruiseSpeed = 18,
                        MaxAltitude = 800,
                        MaxRange = 30
                    },

                    DefaultParameters = new ParameterSet
                    {
                        Name = "QuadPlane_Defaults",
                        Parameters = new Dictionary<string, float>
                        {

                            ["TRIM_ARSPD_CM"] = 1400,
                            ["ARSPD_FBW_MIN"] = 10,
                            ["ARSPD_FBW_MAX"] = 22,

                            ["Q_ENABLE"] = 1,
                            ["Q_ANGLE_MAX"] = 3000,
                            ["Q_ASSIST_SPEED"] = 12,
                            ["Q_ASSIST_ANGLE"] = 30,
                            ["Q_TRANSITION_MS"] = 5000,
                            ["Q_RTL_MODE"] = 1,

                            ["Q_VFWD_GAIN"] = 0.05f,
                            ["Q_WVANE_GAIN"] = 0.1f,
                            ["Q_LAND_SPEED"] = 50,
                            ["Q_WP_SPEED"] = 500,
                            ["Q_WP_SPEED_UP"] = 250,
                            ["Q_WP_SPEED_DN"] = 150
                        }
                    }
                }
            };
        }

        public string[] GetFlightModesForCurrentVehicle()
        {
            return CurrentProfile.SupportedFlightModes;
        }

        public TelemetryConfiguration GetTelemetryConfig()
        {
            return CurrentProfile.TelemetryConfig;
        }

        public string GetVehicleIconPath()
        {
            return CurrentProfile.IconPath ?? GetDefaultIconPath();
        }

        private string GetDefaultIconPath()
        {
            return CurrentVehicleType switch
            {
                VehicleType.Copter => "M50,25 L75,50 M25,50 L75,50 M50,75 L50,25",
                VehicleType.Plane => "M20,50 L80,50 M50,30 L50,70 M30,55 L30,65 M70,55 L70,65",
                VehicleType.QuadPlane => "M20,50 L80,50 M40,35 L40,40 M60,35 L60,40",
                _ => "M25,50 L75,50 M50,25 L50,75"
            };
        }

    }

    public static class VehicleTypeExtensions
    {
        public static List<string> GetFlightModes(this VehicleType vehicleType)
        {
            switch (vehicleType)
            {
                case VehicleType.Copter:
                    return new List<string>
                    {

                        "STABILIZE", "ALT_HOLD", "LOITER", "RTL", "LAND", "AUTO",

                        "ACRO", "GUIDED", "CIRCLE", "POSHOLD", "BRAKE", "SPORT",
                        "AUTOTUNE", "SMART_RTL", "DRIFT", "FLIP", "THROW",

                        "FOLLOW", "ZIGZAG", "FLOWHOLD"
                    };

                case VehicleType.QuadPlane:
                    return new List<string>
                    {

                        "MANUAL", "STABILIZE", "FBWA", "FBWB", "CRUISE", "AUTO",
                        "RTL", "LOITER", "GUIDED", "CIRCLE", "AUTOTUNE",
                        "TRAINING", "ACRO", "TAKEOFF", "THERMAL",

                        "QSTABILIZE", "QHOVER", "QLOITER", "QLAND", "QRTL",
                        "QACRO", "QAUTOTUNE"
                    };

                default:
                    return new List<string> { "STABILIZE" };
            }
        }

        public static List<string> GetCalibrations(this VehicleType vehicleType)
        {
            switch (vehicleType)
            {
                case VehicleType.Copter:
                    return new List<string>
                    {
                        "Gyro",
                        "Barometer",
                        "Accelerometer",
                        "CompassMot"
                    };

                case VehicleType.QuadPlane:
                    return new List<string>
                    {
                        "Gyro",
                        "BarAS",
                        "Radio Trim",
                        "Accelerometer"
                    };

                default:
                    return new List<string> { "Gyro" };
            }
        }
    }
}