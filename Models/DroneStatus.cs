using System;

namespace SimpleDroneGCS.Models
{
    
    public class DroneStatus
    {
        public bool IsConnected { get; set; }
        public string ConnectionPort { get; set; }
        public int BaudRate { get; set; }

        public DateTime LastHeartbeat { get; set; }
        public int SystemId { get; set; }          
        public int ComponentId { get; set; }       

        public byte Autopilot { get; set; }        
        public byte Type { get; set; }             

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

        public bool IsAlive()
        {
            return IsConnected && (DateTime.Now - LastHeartbeat).TotalSeconds < 3;
        }
    }
}