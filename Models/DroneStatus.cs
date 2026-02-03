using System;

namespace SimpleDroneGCS.Models
{
    /// <summary>
    /// Общий статус дрона и подключения
    /// </summary>
    public class DroneStatus
    {
        public bool IsConnected { get; set; }
        public string ConnectionPort { get; set; }
        public int BaudRate { get; set; }

        public DateTime LastHeartbeat { get; set; }
        public int SystemId { get; set; }          // MAVLink System ID (обычно 1)
        public int ComponentId { get; set; }       // MAVLink Component ID

        public byte Autopilot { get; set; }        // MAV_AUTOPILOT (3 = ArduPilot)
        public byte Type { get; set; }             // MAV_TYPE (2 = Quadcopter, 13 = Hexacopter)

        public int PacketsReceived { get; set; }
        public int PacketsSent { get; set; }
        public int PacketErrors { get; set; }

        public DroneStatus()
        {
            IsConnected = false;
            ConnectionPort = string.Empty;
            BaudRate = 57600;
            LastHeartbeat = DateTime.MinValue;
        }

        /// <summary>
        /// Проверка живости связи (heartbeat должен быть не старше 3 секунд)
        /// </summary>
        public bool IsAlive()
        {
            return IsConnected && (DateTime.Now - LastHeartbeat).TotalSeconds < 3;
        }
    }
}