using System;
using System.Text;

namespace SimpleDroneGCS.Simulator
{
    public enum SimCommandType
    {
        GcsHeartbeat,
        Arm,
        Disarm,
        SetMode,
        MissionStart,
        MissionCount,
        MissionItemInt,
        MissionWritePartialList,
        MissionClearAll,
        SetCurrentWaypoint
    }

    public class SimCommand
    {
        public SimCommandType Type { get; init; }
        public string? ModeName { get; init; }
        public int MissionCount { get; init; }
        public SimWaypoint? Waypoint { get; init; }
        public int WpSeq { get; init; }
    }

    public class SimWaypoint
    {
        public int Seq { get; set; }
        public ushort Command { get; set; }
        public double Lat { get; set; }
        public double Lon { get; set; }
        public float Alt { get; set; }
        public float Param1 { get; set; }
        public float Param2 { get; set; }
        public float Param3 { get; set; }
        public float Param4 { get; set; }
        public byte Frame { get; set; }
    }

    /// <summary>
    /// MAVLink v1 packet builder + MAVLink v1/v2 parser.
    /// </summary>
    public static class SimMAVLink
    {
        private static byte _seq;
        private const byte SysId = 1;
        private const byte CompId = 1;

        // ─── CRC-16/MCRF4XX ───────────────────────────────────────────────────────

        private static ushort Crc16(byte[] data, int len, byte extra)
        {
            ushort crc = 0xFFFF;
            for (int i = 0; i < len; i++)
            {
                byte b = (byte)(data[i] ^ (crc & 0xFF));
                b ^= (byte)(b << 4);
                crc = (ushort)((crc >> 8) ^ ((ushort)b << 8) ^ ((ushort)b << 3) ^ (b >> 4));
            }
            byte eb = (byte)(extra ^ (crc & 0xFF));
            eb ^= (byte)(eb << 4);
            crc = (ushort)((crc >> 8) ^ ((ushort)eb << 8) ^ ((ushort)eb << 3) ^ (eb >> 4));
            return crc;
        }

        // ─── v1 packet builder ────────────────────────────────────────────────────
        // [0xFE][LEN][SEQ][SYS][COMP][MSG_ID][PAYLOAD][CRC_L][CRC_H]

        private static byte[] BuildPacket(byte msgId, byte crcExtra, byte[] payload)
        {
            byte len = (byte)payload.Length;
            byte seq = _seq++;
            byte[] crcData = new byte[5 + len];
            crcData[0] = len; crcData[1] = seq;
            crcData[2] = SysId; crcData[3] = CompId; crcData[4] = msgId;
            Array.Copy(payload, 0, crcData, 5, len);
            ushort crc = Crc16(crcData, crcData.Length, crcExtra);
            byte[] pkt = new byte[6 + len + 2];
            pkt[0] = 0xFE; pkt[1] = len; pkt[2] = seq;
            pkt[3] = SysId; pkt[4] = CompId; pkt[5] = msgId;
            Array.Copy(payload, 0, pkt, 6, len);
            pkt[6 + len] = (byte)(crc & 0xFF);
            pkt[6 + len + 1] = (byte)(crc >> 8);
            return pkt;
        }

        // ─── Outgoing packets ─────────────────────────────────────────────────────

        // msg_id=0, len=9, crc_extra=50
        public static byte[] Heartbeat(bool armed, string mode)
        {
            uint customMode = ModeToCustomMode(mode);
            byte baseMode = armed ? (byte)(0x80 | 0x04) : (byte)0x04;
            byte[] p = new byte[9];
            BitConverter.GetBytes(customMode).CopyTo(p, 0);
            p[4] = 2;   // MAV_TYPE_QUADROTOR
            p[5] = 3;   // MAV_AUTOPILOT_ARDUPILOTMEGA
            p[6] = baseMode;
            p[7] = 4;   // MAV_STATE_ACTIVE
            p[8] = 3;
            return BuildPacket(0, 50, p);
        }

        // msg_id=1, len=31, crc_extra=124
        public static byte[] SysStatus(double battPct, double voltage)
        {
            byte[] p = new byte[31];
            // 0x8001F = GPS | gyro | accel | mag | barometer + AHRS bit
            uint sensors = 0x8001F;
            BitConverter.GetBytes(sensors).CopyTo(p, 0);  // sensors present
            BitConverter.GetBytes(sensors).CopyTo(p, 4);  // sensors enabled
            BitConverter.GetBytes(sensors).CopyTo(p, 8);  // sensors health
            BitConverter.GetBytes((ushort)500).CopyTo(p, 12);
            BitConverter.GetBytes((ushort)(voltage * 1000)).CopyTo(p, 14);
            BitConverter.GetBytes((short)-1).CopyTo(p, 16);
            // battery_remaining as sbyte — set only in BATTERY_STATUS now, here -1 = unknown
            p[18] = 0xFF; // -1 as byte = unknown → GCS uses BATTERY_STATUS instead
            return BuildPacket(1, 124, p);
        }

        // msg_id=147, len=36, crc_extra=154  — BATTERY_STATUS
        public static byte[] BatteryStatus(double battPct, double voltage, double currentAmps)
        {
            byte[] p = new byte[36];
            // current_consumed int32 offset 0 = -1 (unknown)
            BitConverter.GetBytes(-1).CopyTo(p, 0);
            // energy_consumed int32 offset 4 = -1 (unknown)
            BitConverter.GetBytes(-1).CopyTo(p, 4);
            // temperature int16 offset 8 = INT16_MAX (unknown)
            BitConverter.GetBytes((short)32767).CopyTo(p, 8);
            // voltages uint16[10] offset 10 — first cell only
            BitConverter.GetBytes((ushort)(voltage * 1000)).CopyTo(p, 10);
            for (int i = 1; i < 10; i++)
                BitConverter.GetBytes((ushort)65535).CopyTo(p, 10 + i * 2); // UINT16_MAX = unknown
            // current_battery int16 offset 30 (mA*100)
            BitConverter.GetBytes((short)(currentAmps * 100)).CopyTo(p, 30);
            // id uint8 offset 32
            p[32] = 0;
            // battery_function uint8 offset 33
            p[33] = 0;
            // type uint8 offset 34
            p[34] = 0;
            // battery_remaining int8 offset 35
            p[35] = (byte)(sbyte)Math.Clamp(battPct, 0, 100);
            return BuildPacket(147, 154, p);
        }

        // msg_id=24, len=30, crc_extra=24
        public static byte[] GpsRawInt(double lat, double lon, double altMsl,
                                        double speed, double heading, byte fixType, byte sats)
        {
            byte[] p = new byte[30];
            BitConverter.GetBytes((ulong)(Environment.TickCount64 * 1000)).CopyTo(p, 0);
            BitConverter.GetBytes((int)(lat * 1e7)).CopyTo(p, 8);
            BitConverter.GetBytes((int)(lon * 1e7)).CopyTo(p, 12);
            BitConverter.GetBytes((int)(altMsl * 1000)).CopyTo(p, 16);
            BitConverter.GetBytes((ushort)100).CopyTo(p, 20);
            BitConverter.GetBytes((ushort)100).CopyTo(p, 22);
            BitConverter.GetBytes((ushort)(speed * 100)).CopyTo(p, 24);
            BitConverter.GetBytes((ushort)((heading * 100) % 36000)).CopyTo(p, 26);
            p[28] = fixType;
            p[29] = sats;
            return BuildPacket(24, 24, p);
        }

        // msg_id=30, len=28, crc_extra=39
        public static byte[] Attitude(float rollRad, float pitchRad, float yawRad)
        {
            byte[] p = new byte[28];
            BitConverter.GetBytes((uint)Environment.TickCount64).CopyTo(p, 0);
            BitConverter.GetBytes(rollRad).CopyTo(p, 4);
            BitConverter.GetBytes(pitchRad).CopyTo(p, 8);
            BitConverter.GetBytes(yawRad).CopyTo(p, 12);
            return BuildPacket(30, 39, p);
        }

        // msg_id=33, len=28, crc_extra=104
        public static byte[] GlobalPositionInt(double lat, double lon, double altMsl,
                                                double altRel, double speed, double heading)
        {
            byte[] p = new byte[28];
            BitConverter.GetBytes((uint)Environment.TickCount64).CopyTo(p, 0);
            BitConverter.GetBytes((int)(lat * 1e7)).CopyTo(p, 4);
            BitConverter.GetBytes((int)(lon * 1e7)).CopyTo(p, 8);
            BitConverter.GetBytes((int)(altMsl * 1000)).CopyTo(p, 12);
            BitConverter.GetBytes((int)(altRel * 1000)).CopyTo(p, 16);
            double brg = heading * Math.PI / 180.0;
            BitConverter.GetBytes((short)(Math.Cos(brg) * speed * 100)).CopyTo(p, 20);
            BitConverter.GetBytes((short)(Math.Sin(brg) * speed * 100)).CopyTo(p, 22);
            BitConverter.GetBytes((short)0).CopyTo(p, 24);
            BitConverter.GetBytes((ushort)((heading * 100) % 36000)).CopyTo(p, 26);
            return BuildPacket(33, 104, p);
        }

        // msg_id=74, len=20, crc_extra=20
        public static byte[] VfrHud(double groundspeed, double altRel,
                                     double climbRate, double heading, int throttle)
        {
            byte[] p = new byte[20];
            BitConverter.GetBytes((float)groundspeed).CopyTo(p, 0);
            BitConverter.GetBytes((float)groundspeed).CopyTo(p, 4);
            BitConverter.GetBytes((float)altRel).CopyTo(p, 8);
            BitConverter.GetBytes((float)climbRate).CopyTo(p, 12);
            BitConverter.GetBytes((short)Math.Round(heading)).CopyTo(p, 16);
            BitConverter.GetBytes((ushort)Math.Clamp(throttle, 0, 100)).CopyTo(p, 18);
            return BuildPacket(74, 20, p);
        }

        // msg_id=62, len=26, crc_extra=183
        // NAV_CONTROLLER_OUTPUT: nav_bearing = целевой курс FC
        public static byte[] NavControllerOutput(short navBearing, short targetBearing, ushort wpDist)
        {
            byte[] p = new byte[26];
            // float nav_roll=0, nav_pitch=0, alt_error=0, aspd_error=0, xtrack_error=0
            BitConverter.GetBytes(0f).CopyTo(p, 0);   // nav_roll
            BitConverter.GetBytes(0f).CopyTo(p, 4);   // nav_pitch
            BitConverter.GetBytes(0f).CopyTo(p, 8);   // alt_error
            BitConverter.GetBytes(0f).CopyTo(p, 12);  // aspd_error
            BitConverter.GetBytes(0f).CopyTo(p, 16);  // xtrack_error
            BitConverter.GetBytes(navBearing).CopyTo(p, 20);
            BitConverter.GetBytes(targetBearing).CopyTo(p, 22);
            BitConverter.GetBytes(wpDist).CopyTo(p, 24);
            return BuildPacket(62, 183, p);
        }

        // msg_id=42, len=2, crc_extra=28
        public static byte[] MissionCurrent(ushort seq)
        {
            byte[] p = new byte[2];
            BitConverter.GetBytes(seq).CopyTo(p, 0);
            return BuildPacket(42, 28, p);
        }

        // msg_id=253, len=51, crc_extra=83
        public static byte[] StatusText(byte severity, string text)
        {
            byte[] p = new byte[51];
            p[0] = severity;
            byte[] tb = Encoding.ASCII.GetBytes(text);
            Array.Copy(tb, 0, p, 1, Math.Min(tb.Length, 50));
            return BuildPacket(253, 83, p);
        }

        // msg_id=77, len=3, crc_extra=143
        public static byte[] CommandAck(ushort command, byte result)
        {
            byte[] p = new byte[3];
            BitConverter.GetBytes(command).CopyTo(p, 0);
            p[2] = result;
            return BuildPacket(77, 143, p);
        }

        // msg_id=51, len=4, crc_extra=196
        public static byte[] MissionRequestInt(ushort seq)
        {
            byte[] p = new byte[4];
            p[0] = 1; p[1] = 1;
            BitConverter.GetBytes(seq).CopyTo(p, 2);
            return BuildPacket(51, 196, p);
        }

        // msg_id=47, len=3, crc_extra=153
        public static byte[] MissionAck(byte type = 0)
        {
            byte[] p = new byte[3];
            p[0] = 1; p[1] = 1; p[2] = type;
            return BuildPacket(47, 153, p);
        }

        // ─── Incoming parser — supports both MAVLink v1 (0xFE) and v2 (0xFD) ─────

        public static SimCommand? ParsePacket(byte[] data, int len)
        {
            if (len < 8 || (data[0] != 0xFE && data[0] != 0xFD)) return null;

            byte msgId;
            byte[] payload;

            if (data[0] == 0xFE) // MAVLink v1
            {
                byte payloadLen = data[1];
                if (len < 6 + payloadLen + 2) return null;
                msgId = data[5];
                payload = new byte[payloadLen];
                Array.Copy(data, 6, payload, 0, payloadLen);
            }
            else // MAVLink v2 (0xFD)
            {
                if (len < 12) return null;
                byte payloadLen = data[1];
                if (len < 10 + payloadLen + 2) return null;
                uint msgId32 = data[7] | ((uint)data[8] << 8) | ((uint)data[9] << 16);
                if (msgId32 > 255) return null; // extended IDs not used here
                msgId = (byte)msgId32;
                payload = new byte[payloadLen];
                Array.Copy(data, 10, payload, 0, payloadLen);
            }

            return msgId switch
            {
                0 => new SimCommand { Type = SimCommandType.GcsHeartbeat },
                76 => ParseCommandLong(payload),
                11 => ParseSetMode(payload),
                44 => ParseMissionCount(payload),
                73 => ParseMissionItemInt(payload),
                45 => new SimCommand { Type = SimCommandType.MissionClearAll },
                38 => ParseMissionWritePartialList(payload),
                41 => ParseMissionSetCurrent(payload),
                _ => null
            };
        }

        // COMMAND_LONG: param1-7 float (0-27), command uint16 (28)
        // MAVLink v2 may truncate trailing zeros → accept ≥ 30 bytes
        private static SimCommand? ParseCommandLong(byte[] p)
        {
            if (p.Length < 30) return null;
            float param1 = BitConverter.ToSingle(p, 0);
            ushort cmd = BitConverter.ToUInt16(p, 28);
            return cmd switch
            {
                400 => new SimCommand { Type = param1 >= 1f ? SimCommandType.Arm : SimCommandType.Disarm },
                300 => new SimCommand { Type = SimCommandType.MissionStart },
                _ => null
            };
        }

        // SET_MODE: custom_mode uint32 (0), target_system uint8 (4), base_mode uint8 (5)
        private static SimCommand? ParseSetMode(byte[] p)
        {
            if (p.Length < 5) return null;
            uint customMode = BitConverter.ToUInt32(p, 0);
            return new SimCommand { Type = SimCommandType.SetMode, ModeName = CustomModeToName(customMode) };
        }

        // MISSION_COUNT: count uint16 (0)
        private static SimCommand? ParseMissionCount(byte[] p)
        {
            if (p.Length < 2) return null;
            ushort count = BitConverter.ToUInt16(p, 0);
            return new SimCommand { Type = SimCommandType.MissionCount, MissionCount = count };
        }

        private static SimCommand? ParseMissionWritePartialList(byte[] p)
        {
            if (p.Length < 6) return null;
            short startIdx = BitConverter.ToInt16(p, 0);
            short endIdx = BitConverter.ToInt16(p, 2);
            return new SimCommand
            {
                Type = SimCommandType.MissionWritePartialList,
                WpSeq = startIdx,
                MissionCount = endIdx
            };
        }

        // MISSION_ITEM_INT: param1-4 float (0-15), x int32 (16), y int32 (20), z float (24),
        //                   seq uint16 (28), command uint16 (30)
        private static SimCommand? ParseMissionItemInt(byte[] p)
        {
            if (p.Length < 32) return null;
            var wp = new SimWaypoint
            {
                Param1 = BitConverter.ToSingle(p, 0),
                Param2 = BitConverter.ToSingle(p, 4),
                Param3 = BitConverter.ToSingle(p, 8),
                Param4 = BitConverter.ToSingle(p, 12),
                Lat = BitConverter.ToInt32(p, 16) / 1e7,
                Lon = BitConverter.ToInt32(p, 20) / 1e7,
                Alt = BitConverter.ToSingle(p, 24),
                Seq = BitConverter.ToUInt16(p, 28),
                Command = BitConverter.ToUInt16(p, 30),
                Frame = p.Length > 34 ? p[34] : (byte)0
            };
            return new SimCommand { Type = SimCommandType.MissionItemInt, Waypoint = wp };
        }

        // MISSION_SET_CURRENT: seq uint16 (0)
        private static SimCommand? ParseMissionSetCurrent(byte[] p)
        {
            if (p.Length < 2) return null;
            ushort seq = BitConverter.ToUInt16(p, 0);
            return new SimCommand { Type = SimCommandType.SetCurrentWaypoint, WpSeq = seq };
        }

        // ─── Helpers ──────────────────────────────────────────────────────────────

        private static uint ModeToCustomMode(string mode) => mode switch
        {
            "STABILIZE" => 0,
            "ACRO" => 1,
            "ALT_HOLD" => 2,
            "AUTO" => 3,
            "GUIDED" => 4,
            "LOITER" => 5,
            "RTL" => 6,
            "CIRCLE" => 7,
            "LAND" => 9,
            "BRAKE" => 17,
            _ => 0
        };

        private static string CustomModeToName(uint cm) => cm switch
        {
            0 => "STABILIZE",
            1 => "ACRO",
            2 => "ALT_HOLD",
            3 => "AUTO",
            4 => "GUIDED",
            5 => "LOITER",
            6 => "RTL",
            7 => "CIRCLE",
            9 => "LAND",
            17 => "BRAKE",
            _ => "STABILIZE"
        };
    }
}