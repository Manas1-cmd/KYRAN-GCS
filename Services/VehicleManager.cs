using System;
using System.Collections.Generic;
using SimpleDroneGCS.Models;

namespace SimpleDroneGCS.Services
{
    public class VehicleManager
    {
        private static VehicleManager _instance;
        public static VehicleManager Instance => _instance ??= new VehicleManager();

        public VehicleType CurrentVehicleType { get; private set; }
        public VehicleProfile CurrentProfile { get; private set; }

        private readonly Dictionary<VehicleType, VehicleProfile> _profiles;

        private VehicleManager()
        {
            _profiles = InitializeProfiles();
            CurrentVehicleType = VehicleType.Copter; // Default
            CurrentProfile = _profiles[CurrentVehicleType];
        }

        public void SetVehicleType(VehicleType type)
        {
            CurrentVehicleType = type;
            CurrentProfile = _profiles[type];

            // Raise event for UI updates
            VehicleTypeChanged?.Invoke(this, CurrentProfile);
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
                    MavType = 2, // MAV_TYPE_QUADROTOR
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
                        MaxAltitude = 500,  // meters
                        MaxRange = 5,       // km
                        CruiseSpeed = 15    // m/s
                    },

                    DefaultParameters = new ParameterSet
                    {
                        Name = "Copter_Defaults",
                        Parameters = new Dictionary<string, float>
                        {
                            ["WPNAV_SPEED"] = 500,      // cm/s
                            ["WPNAV_SPEED_UP"] = 250,   // cm/s
                            ["WPNAV_SPEED_DN"] = 150,   // cm/s
                            ["ANGLE_MAX"] = 3000,        // centidegrees
                            ["PILOT_SPEED_UP"] = 250,   // cm/s
                            ["RTL_ALT"] = 3000,          // cm
                            ["FENCE_TYPE"] = 3,          // ALT + CIRCLE
                            ["FENCE_ALT_MAX"] = 100,     // meters
                            ["FENCE_RADIUS"] = 300       // meters
                        }
                    }
                },

                [VehicleType.Plane] = new VehicleProfile
                {
                    Type = VehicleType.Plane,
                    DisplayName = "Fixed Wing",
                    Description = "Traditional airplane",
                    IconPath = "/Resources/Icons/plane_icon.png",
                    MavType = 1, // MAV_TYPE_FIXED_WING
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
                        "QSTABILIZE",  // For QuadPlane variants
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
                        MaxAirspeed = 30,    // m/s
                        StallSpeed = 12,     // m/s
                        CruiseSpeed = 20,    // m/s
                        MaxAltitude = 1000,  // meters
                        MaxRange = 50        // km
                    },

                    DefaultParameters = new ParameterSet
                    {
                        Name = "Plane_Defaults",
                        Parameters = new Dictionary<string, float>
                        {
                            ["TRIM_ARSPD_CM"] = 1500,   // cm/s (15 m/s)
                            ["ARSPD_FBW_MIN"] = 12,      // m/s
                            ["ARSPD_FBW_MAX"] = 25,      // m/s
                            ["WP_RADIUS"] = 90,          // meters
                            ["WP_LOITER_RAD"] = 60,      // meters
                            ["RTL_RADIUS"] = 60,         // meters
                            ["ALT_HOLD_RTL"] = 100,      // meters
                            ["LIM_ROLL_CD"] = 4500,      // centidegrees
                            ["LIM_PITCH_MAX"] = 2000,    // centidegrees
                            ["LIM_PITCH_MIN"] = -2500    // centidegrees
                        }
                    }
                },

                [VehicleType.QuadPlane] = new VehicleProfile
                {
                    Type = VehicleType.QuadPlane,
                    DisplayName = "VTOL",
                    Description = "Vertical Takeoff and Landing aircraft",
                    IconPath = "/Resources/Icons/vtol_icon.png",
                    MavType = 20, // MAV_TYPE_VTOL_QUADROTOR
                    SupportsVTOL = true,
                    RequiresAirspeed = true,
                    SupportsHover = true,

                    SupportedFlightModes = new[]
                    {
                        // Plane modes
                        "MANUAL", "STABILIZE", "FLY_BY_WIRE_A", "FLY_BY_WIRE_B",
                        "CRUISE", "AUTOTUNE", "AUTO", "RTL", "LOITER", "CIRCLE",
                        // Quad modes
                        "QSTABILIZE", "QHOVER", "QLOITER", "QLAND", "QRTL", "QACRO",
                        // Special VTOL modes
                        "QAUTOTUNE", "GUIDED", "TAKEOFF", "VTOL_TAKEOFF", "VTOL_LAND"
                    },

                    TelemetryConfig = new TelemetryConfiguration
                    {
                        ShowAirspeed = true,
                        ShowGroundSpeed = true,
                        ShowClimbRate = true,
                        ShowThrottle = true,
                        ShowRPM = false,
                        ShowVTOLStatus = true,  // Special VTOL indicator
                        ShowFlaps = true,
                        MaxAirspeed = 25,       // m/s
                        StallSpeed = 0,         // Can hover
                        CruiseSpeed = 18,       // m/s
                        MaxAltitude = 800,      // meters
                        MaxRange = 30           // km
                    },

                    DefaultParameters = new ParameterSet
                    {
                        Name = "QuadPlane_Defaults",
                        Parameters = new Dictionary<string, float>
                        {
                            // Plane parameters
                            ["TRIM_ARSPD_CM"] = 1400,    // cm/s
                            ["ARSPD_FBW_MIN"] = 10,       // m/s
                            ["ARSPD_FBW_MAX"] = 22,       // m/s

                            // Quad parameters
                            ["Q_ENABLE"] = 1,              // Enable QuadPlane
                            ["Q_ANGLE_MAX"] = 3000,        // centidegrees
                            ["Q_ASSIST_SPEED"] = 12,       // m/s (assist below this speed)
                            ["Q_ASSIST_ANGLE"] = 30,       // degrees
                            ["Q_TRANSITION_MS"] = 5000,    // ms for transition
                            ["Q_RTL_MODE"] = 1,            // VTOL approach and land

                            // VTOL specific
                            ["Q_VFWD_GAIN"] = 0.05f,      // Forward velocity gain
                            ["Q_WVANE_GAIN"] = 0.1f,      // Weathervaning gain
                            ["Q_LAND_SPEED"] = 50,        // cm/s
                            ["Q_WP_SPEED"] = 500,          // cm/s
                            ["Q_WP_SPEED_UP"] = 250,       // cm/s
                            ["Q_WP_SPEED_DN"] = 150        // cm/s
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
    // ⭐ EXTENSION МЕТОДЫ ВНУТРИ namespace, но ВНЕ класса VehicleManager
    public static class VehicleTypeExtensions
    {
        public static List<string> GetFlightModes(this VehicleType vehicleType)
        {
            switch (vehicleType)
            {
                case VehicleType.Copter:
                    return new List<string>
                    {
                        // Основные
                        "STABILIZE", "ALT_HOLD", "LOITER", "RTL", "LAND", "AUTO",
                        // Продвинутые
                        "ACRO", "GUIDED", "CIRCLE", "POSHOLD", "BRAKE", "SPORT",
                        "AUTOTUNE", "SMART_RTL", "DRIFT", "FLIP", "THROW",
                        // Специальные (если поддерживаются FC)
                        "FOLLOW", "ZIGZAG", "FLOWHOLD"
                    };

                case VehicleType.QuadPlane:
                    return new List<string>
                    {
                        // Режимы самолёта
                        "MANUAL", "STABILIZE", "FBWA", "FBWB", "CRUISE", "AUTO",
                        "RTL", "LOITER", "GUIDED", "CIRCLE", "AUTOTUNE",
                        "TRAINING", "ACRO", "TAKEOFF", "THERMAL",
                        // Режимы коптера (Q-режимы)
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
