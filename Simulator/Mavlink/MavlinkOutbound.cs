using System;
using System.Diagnostics;
using System.Text;
using SimpleDroneGCS.Simulator.Control;
using SimpleDroneGCS.Simulator.Core;

namespace SimpleDroneGCS.Simulator.Mavlink
{
    /// <summary>
    /// Построение исходящих MAVLink v2 пакетов. Не отправляет — только собирает
    /// <c>byte[]</c>. Отправкой занимается <c>MavlinkBridge</c>.
    /// <para>
    /// Формат v2: magic=0xFD, header 10 байт + payload + CRC-16 X.25.
    /// Трункация trailing zeros. Последовательный <see cref="byte"/> counter.
    /// </para>
    /// <para>
    /// Все методы thread-safe (lock на sequence counter).
    /// </para>
    /// </summary>
    public sealed class MavlinkOutbound
    {
        private readonly byte _sysId;
        private readonly byte _compId;
        private readonly object _seqLock = new();
        private byte _sequence;

        private readonly Stopwatch _boot = Stopwatch.StartNew();

        /// <summary>
        /// Создать builder.
        /// </summary>
        /// <param name="sysId">MAVLink SYSID (1 по умолчанию).</param>
        /// <param name="compId">MAVLink COMPID (1 = AUTOPILOT1).</param>
        public MavlinkOutbound(byte sysId = 1, byte compId = 1)
        {
            _sysId = sysId;
            _compId = compId;
        }

        // =====================================================================
        // Периодическая телеметрия
        // =====================================================================

        /// <summary>HEARTBEAT (ID 0). 1 Hz. Тип ВС + режим + armed.</summary>
        public byte[] BuildHeartbeat(SimState s)
        {
            // type по VehicleType: QUADROTOR=2, VTOL_QUADROTOR=20.
            byte type = s.Vehicle == VehicleType.Vtol ? (byte)20 : (byte)2;
            byte autopilot = 3; // MAV_AUTOPILOT_ARDUPILOTMEGA
            byte baseMode = 1;  // CUSTOM_MODE_ENABLED
            if (s.Armed) baseMode |= 128; // SAFETY_ARMED
            byte sysStatus = s.Armed ? (byte)4 : (byte)3; // ACTIVE / STANDBY
            byte mavVersion = 3;

            byte[] p = new byte[9];
            PutU32(p, 0, s.CustomMode);
            p[4] = type;
            p[5] = autopilot;
            p[6] = baseMode;
            p[7] = sysStatus;
            p[8] = mavVersion;
            return BuildPacket(0, 50, p);
        }

        /// <summary>SYS_STATUS (ID 1). 2 Hz. Битмаски датчиков + батарея.</summary>
        public byte[] BuildSysStatus(SimState s)
        {
            uint sensorsPresent = 0x1FFFFFFF;
            uint sensorsEnabled = sensorsPresent;
            uint sensorsHealth = sensorsPresent;

            if (s.Failures.GpsLoss || s.Gps.FixType < 3)
                sensorsHealth &= ~(uint)0x20; // GPS
            if (!s.Ekf.Healthy || s.Failures.EkfDivergence)
                sensorsHealth &= ~(uint)0x200000; // AHRS
            if (s.Failures.CompassError)
                sensorsHealth &= ~(uint)0x04; // 3D_MAG
            if (s.Failures.BatteryCritical)
                sensorsHealth &= ~(uint)0x400000; // BATTERY

            ushort voltageMv = (ushort)Math.Clamp(Math.Round(s.Battery.VoltageV * 1000), 0, 65535);
            short currentCa = (short)Math.Clamp(Math.Round(s.Battery.CurrentA * 100), -32768, 32767);
            sbyte batteryRem = (sbyte)Math.Clamp(Math.Round(s.Battery.Percent), 0, 100);

            byte[] p = new byte[31];
            PutU32(p, 0, sensorsPresent);
            PutU32(p, 4, sensorsEnabled);
            PutU32(p, 8, sensorsHealth);
            PutU16(p, 12, 500);                   // load: 50% cpu
            PutU16(p, 14, voltageMv);
            PutI16(p, 16, currentCa);
            PutU16(p, 18, 0);                     // drop_rate_comm
            PutU16(p, 20, 0);                     // errors_comm
            PutU16(p, 22, 0); PutU16(p, 24, 0);
            PutU16(p, 26, 0); PutU16(p, 28, 0);
            p[30] = (byte)batteryRem;
            return BuildPacket(1, 124, p);
        }

        /// <summary>SYSTEM_TIME (ID 2). 1 Hz. Unix + boot.</summary>
        public byte[] BuildSystemTime()
        {
            ulong unixUs = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000UL;
            uint bootMs = GetBootMs();

            byte[] p = new byte[12];
            PutU64(p, 0, unixUs);
            PutU32(p, 8, bootMs);
            return BuildPacket(2, 137, p);
        }

        /// <summary>GPS_RAW_INT (ID 24). 5 Hz. Raw GPS.</summary>
        public byte[] BuildGpsRawInt(SimState s)
        {
            ulong timeUs = GetBootUs();
            int lat = (int)Math.Round(s.Position.Lat * 1e7);
            int lon = (int)Math.Round(s.Position.Lon * 1e7);
            int alt = (int)Math.Round(s.Position.AltAmsl * 1000); // mm
            ushort eph = (ushort)Math.Clamp(Math.Round(s.Gps.Hdop * 100), 0, 65535);
            ushort epv = (ushort)Math.Clamp(Math.Round(s.Gps.Vdop * 100), 0, 65535);
            ushort vel = (ushort)Math.Clamp(Math.Round(s.Velocity.GroundSpeed * 100), 0, 65535);
            double yawDeg = NormalizeHeadingDeg(s.Attitude.Yaw * 180.0 / Math.PI);
            ushort cog = (ushort)Math.Clamp(Math.Round(yawDeg * 100), 0, 35999);
            byte fix = s.Failures.GpsLoss ? (byte)0 : s.Gps.FixType;
            byte sats = s.Failures.GpsLoss ? (byte)0 : s.Gps.Satellites;

            byte[] p = new byte[30];
            PutU64(p, 0, timeUs);
            PutI32(p, 8, lat);
            PutI32(p, 12, lon);
            PutI32(p, 16, alt);
            PutU16(p, 20, eph);
            PutU16(p, 22, epv);
            PutU16(p, 24, vel);
            PutU16(p, 26, cog);
            p[28] = fix;
            p[29] = sats;
            return BuildPacket(24, 24, p);
        }

        /// <summary>ATTITUDE (ID 30). 10 Hz. Углы + скорости.</summary>
        public byte[] BuildAttitude(SimState s)
        {
            byte[] p = new byte[28];
            PutU32(p, 0, GetBootMs());
            PutF32(p, 4, (float)s.Attitude.Roll);
            PutF32(p, 8, (float)s.Attitude.Pitch);
            PutF32(p, 12, (float)s.Attitude.Yaw);
            PutF32(p, 16, (float)s.Attitude.RollSpeed);
            PutF32(p, 20, (float)s.Attitude.PitchSpeed);
            PutF32(p, 24, (float)s.Attitude.YawSpeed);
            return BuildPacket(30, 39, p);
        }

        /// <summary>GLOBAL_POSITION_INT (ID 33). 5 Hz. EKF output позиции.</summary>
        public byte[] BuildGlobalPositionInt(SimState s)
        {
            int lat = (int)Math.Round(s.Position.Lat * 1e7);
            int lon = (int)Math.Round(s.Position.Lon * 1e7);
            int alt = (int)Math.Round(s.Position.AltAmsl * 1000);
            int relAlt = (int)Math.Round(s.Position.AltRelative * 1000);
            short vx = (short)Math.Clamp(Math.Round(s.Velocity.Vn * 100), -32768, 32767);
            short vy = (short)Math.Clamp(Math.Round(s.Velocity.Ve * 100), -32768, 32767);
            short vz = (short)Math.Clamp(Math.Round(s.Velocity.Vd * 100), -32768, 32767);
            double hdgDeg = NormalizeHeadingDeg(s.Attitude.Yaw * 180.0 / Math.PI);
            ushort hdg = (ushort)Math.Clamp(Math.Round(hdgDeg * 100), 0, 35999);

            byte[] p = new byte[28];
            PutU32(p, 0, GetBootMs());
            PutI32(p, 4, lat);
            PutI32(p, 8, lon);
            PutI32(p, 12, alt);
            PutI32(p, 16, relAlt);
            PutI16(p, 20, vx);
            PutI16(p, 22, vy);
            PutI16(p, 24, vz);
            PutU16(p, 26, hdg);
            return BuildPacket(33, 104, p);
        }

        /// <summary>SERVO_OUTPUT_RAW (ID 36). 10 Hz. PWM 8 каналов.</summary>
        public byte[] BuildServoOutputRaw(SimState s)
        {
            byte[] p = new byte[21];
            PutU32(p, 0, (uint)(GetBootMs() * 1000)); // time_usec как u32 (wraps)
            for (int i = 0; i < 8; i++)
                PutU16(p, 4 + i * 2, s.ServoPwm[i]);
            p[20] = 1; // port
            return BuildPacket(36, 222, p);
        }

        /// <summary>VFR_HUD (ID 74). 10 Hz. Основная HUD-телеметрия.</summary>
        public byte[] BuildVfrHud(SimState s)
        {
            double hdgDeg = NormalizeHeadingDeg(s.Attitude.Yaw * 180.0 / Math.PI);
            short heading = (short)Math.Round(hdgDeg);
            ushort throttle = Math.Clamp(s.Velocity.ThrottlePercent, (ushort)0, (ushort)100);

            byte[] p = new byte[20];
            PutF32(p, 0, (float)s.Velocity.AirSpeed);
            PutF32(p, 4, (float)s.Velocity.GroundSpeed);
            PutF32(p, 8, (float)s.Position.AltAmsl);
            PutF32(p, 12, (float)s.Velocity.Climb);
            PutI16(p, 16, heading);
            PutU16(p, 18, throttle);
            return BuildPacket(74, 20, p);
        }

        /// <summary>NAV_CONTROLLER_OUTPUT (ID 62). 5 Hz. Статус навигатора.</summary>
        public byte[] BuildNavControllerOutput(SimState s)
        {
            byte[] p = new byte[26];
            PutF32(p, 0, (float)(s.Attitude.Roll * 180.0 / Math.PI));  // nav_roll
            PutF32(p, 4, (float)(s.Attitude.Pitch * 180.0 / Math.PI)); // nav_pitch
            PutF32(p, 8, (float)s.NavStatus.AltError);
            PutF32(p, 12, (float)s.NavStatus.AspdError);
            PutF32(p, 16, (float)s.NavStatus.XtrackError);
            PutI16(p, 20, (short)Math.Round(s.NavStatus.NavBearingDeg));
            PutI16(p, 22, (short)Math.Round(s.NavStatus.TargetBearingDeg));
            PutU16(p, 24, (ushort)Math.Clamp(Math.Round(s.NavStatus.WpDistance), 0, 65535));
            return BuildPacket(62, 183, p);
        }

        /// <summary>BATTERY_STATUS (ID 147). 1 Hz. Детальная батарея.</summary>
        public byte[] BuildBatteryStatus(SimState s)
        {
            int consumed = (int)Math.Round(s.Battery.ConsumedMah);
            int energy = -1; // неизвестно
            short temp = 2500; // 25°C * 100
            ushort totalMv = (ushort)Math.Clamp(Math.Round(s.Battery.VoltageV * 1000), 0, 65535);
            short current = (short)Math.Clamp(Math.Round(s.Battery.CurrentA * 100), -32768, 32767);
            sbyte remaining = (sbyte)Math.Clamp(Math.Round(s.Battery.Percent), 0, 100);

            byte[] p = new byte[36];
            PutI32(p, 0, consumed);
            PutI32(p, 4, energy);
            PutI16(p, 8, temp);
            // voltages[10]: [0] = суммарное, остальные 0xFFFF (не используется).
            PutU16(p, 10, totalMv);
            for (int i = 1; i < 10; i++) PutU16(p, 10 + i * 2, 0xFFFF);
            PutI16(p, 30, current);
            p[32] = 0; // id
            p[33] = 0; // battery_function: ALL
            p[34] = 1; // type: LIPO
            p[35] = (byte)remaining;
            return BuildPacket(147, 154, p);
        }

        /// <summary>HOME_POSITION (ID 242). 0.2 Hz. HOME точка.</summary>
        public byte[] BuildHomePosition(SimState s)
        {
            int lat = (int)Math.Round(s.Home.Lat * 1e7);
            int lon = (int)Math.Round(s.Home.Lon * 1e7);
            int alt = (int)Math.Round(s.Home.AltAmsl * 1000);

            byte[] p = new byte[52];
            PutI32(p, 0, lat);
            PutI32(p, 4, lon);
            PutI32(p, 8, alt);
            // x, y, z = 0 (local NED origin — не используется).
            // q[4]: identity (1, 0, 0, 0).
            PutF32(p, 24, 1.0f);
            // approach_x/y/z = 0.
            return BuildPacket(242, 104, p);
        }

        /// <summary>EXTENDED_SYS_STATE (ID 245). 1 Hz. VTOL state + landed.</summary>
        public byte[] BuildExtendedSysState(SimState s)
        {
            // vtol_state: UNDEFINED=0, TRANSITION_TO_FW=1, TRANSITION_TO_MC=2, MC=3, FW=4.
            byte vtolState = 0;
            if (s.Vehicle == VehicleType.Vtol)
                vtolState = s.Velocity.AirSpeed > 15 ? (byte)4 : (byte)3;

            byte[] p = new byte[2];
            p[0] = vtolState;
            p[1] = (byte)s.LandedState;
            return BuildPacket(245, 130, p);
        }

        /// <summary>VIBRATION (ID 241). 1 Hz.</summary>
        public byte[] BuildVibration(SimState s)
        {
            byte[] p = new byte[32];
            PutU64(p, 0, GetBootUs());
            PutF32(p, 8, (float)s.Vibration.X);
            PutF32(p, 12, (float)s.Vibration.Y);
            PutF32(p, 16, (float)s.Vibration.Z);
            PutU32(p, 20, s.Vibration.Clipping0);
            PutU32(p, 24, s.Vibration.Clipping1);
            PutU32(p, 28, s.Vibration.Clipping2);
            return BuildPacket(241, 90, p);
        }

        /// <summary>WIND (ID 168). 1 Hz.</summary>
        public byte[] BuildWind(SimState s)
        {
            byte[] p = new byte[12];
            PutF32(p, 0, (float)s.Wind.DirectionDeg);
            PutF32(p, 4, (float)s.Wind.SpeedMs);
            PutF32(p, 8, (float)s.Wind.SpeedZMs);
            return BuildPacket(168, 1, p);
        }

        /// <summary>EKF_STATUS_REPORT (ID 193). 1 Hz.</summary>
        public byte[] BuildEkfStatusReport(SimState s)
        {
            byte[] p = new byte[22];
            PutF32(p, 0, (float)s.Ekf.VelVariance);
            PutF32(p, 4, (float)s.Ekf.PosHorizVariance);
            PutF32(p, 8, (float)s.Ekf.PosVertVariance);
            PutF32(p, 12, (float)s.Ekf.CompassVariance);
            PutF32(p, 16, (float)s.Ekf.TerrainAltVariance);
            PutU16(p, 20, s.Ekf.Flags);
            return BuildPacket(193, 71, p);
        }

        /// <summary>ESTIMATOR_STATUS (ID 230). 1 Hz.</summary>
        public byte[] BuildEstimatorStatus(SimState s)
        {
            byte[] p = new byte[42];
            PutU64(p, 0, GetBootUs());
            PutF32(p, 8, (float)s.Ekf.VelVariance);        // vel_ratio
            PutF32(p, 12, (float)s.Ekf.PosHorizVariance);  // pos_horiz_ratio
            PutF32(p, 16, (float)s.Ekf.PosVertVariance);   // pos_vert_ratio
            PutF32(p, 20, (float)s.Ekf.CompassVariance);   // mag_ratio
            PutF32(p, 24, 0);                              // hagl_ratio
            PutF32(p, 28, 0);                              // tas_ratio
            PutF32(p, 32, 1.0f);                           // pos_horiz_accuracy (m)
            PutF32(p, 36, 1.5f);                           // pos_vert_accuracy (m)
            PutU16(p, 40, s.Ekf.Flags);
            return BuildPacket(230, 163, p);
        }

        /// <summary>RADIO_STATUS (ID 109). 1 Hz.</summary>
        public byte[] BuildRadioStatus(SimState s)
        {
            byte[] p = new byte[9];
            PutU16(p, 0, s.Radio.RxErrors);
            PutU16(p, 2, 0);                // fixed (error corrections)
            p[4] = s.Failures.RcFailsafe ? (byte)0 : s.Radio.RssiLocal;
            p[5] = s.Radio.RssiRemote;
            p[6] = s.Radio.TxBuf;
            p[7] = s.Radio.Noise;
            p[8] = s.Radio.RemoteNoise;
            return BuildPacket(109, 185, p);
        }

        // =====================================================================
        // По событию
        // =====================================================================

        /// <summary>STATUSTEXT (ID 253). Максимум 50 символов.</summary>
        public byte[] BuildStatusText(string text, byte severity = 6)
        {
            byte[] p = new byte[51];
            p[0] = severity;
            if (!string.IsNullOrEmpty(text))
            {
                byte[] utf = Encoding.ASCII.GetBytes(text);
                int copy = Math.Min(utf.Length, 50);
                Array.Copy(utf, 0, p, 1, copy);
            }
            return BuildPacket(253, 83, p);
        }

        /// <summary>MISSION_CURRENT (ID 42). Номер активного WP.</summary>
        public byte[] BuildMissionCurrent(ushort seq)
        {
            byte[] p = new byte[2];
            PutU16(p, 0, seq);
            return BuildPacket(42, 28, p);
        }

        /// <summary>MISSION_ITEM_REACHED (ID 46). Событие достижения WP.</summary>
        public byte[] BuildMissionItemReached(ushort seq)
        {
            byte[] p = new byte[2];
            PutU16(p, 0, seq);
            return BuildPacket(46, 11, p);
        }

        /// <summary>COMMAND_ACK (ID 77). Ответ на MAV_CMD.</summary>
        public byte[] BuildCommandAck(ushort command, byte result)
        {
            byte[] p = new byte[3];
            PutU16(p, 0, command);
            p[2] = result;
            return BuildPacket(77, 143, p);
        }

        // =====================================================================
        // По запросу
        // =====================================================================

        /// <summary>AUTOPILOT_VERSION (ID 148). На REQUEST_AUTOPILOT_CAPABILITIES.</summary>
        public byte[] BuildAutopilotVersion()
        {
            // capabilities: MISSION_FLOAT(1) | PARAM_FLOAT(2) | MISSION_INT(4) |
            //              COMMAND_INT(16) | MAVLINK2(65536)
            ulong capabilities = 1 | 2 | 4 | 16 | 65536;

            byte[] p = new byte[60];
            PutU64(p, 0, capabilities);
            PutU64(p, 8, _sysId);                 // uid
            PutU32(p, 16, 0x04050000);            // flight_sw: 4.5.0
            PutU32(p, 20, 0);                     // middleware_sw
            PutU32(p, 24, 0);                     // os_sw
            PutU32(p, 28, 0);                     // board_version
            PutU16(p, 32, 0x10C4);                // vendor_id (произвольный)
            PutU16(p, 34, 0x0001);                // product_id
            // custom versions (24 байта) остаются нулями.
            return BuildPacket(148, 178, p);
        }

        /// <summary>PARAM_VALUE (ID 22). Ответ на PARAM_REQUEST_READ/LIST.</summary>
        public byte[] BuildParamValue(string paramId, float value,
            byte type, ushort count, ushort index)
        {
            byte[] p = new byte[25];
            PutF32(p, 0, value);
            PutU16(p, 4, count);
            PutU16(p, 6, index);

            if (!string.IsNullOrEmpty(paramId))
            {
                byte[] idBytes = Encoding.ASCII.GetBytes(paramId);
                int copy = Math.Min(idBytes.Length, 16);
                Array.Copy(idBytes, 0, p, 8, copy);
            }
            p[24] = type;
            return BuildPacket(22, 220, p);
        }

        // =====================================================================
        // Mission download (sim → GCS)
        // =====================================================================

        /// <summary>MISSION_COUNT (ID 44). Количество WP в миссии.</summary>
        public byte[] BuildMissionCount(ushort count, byte targetSys = 255, byte targetComp = 0)
        {
            byte[] p = new byte[5];
            PutU16(p, 0, count);
            p[2] = targetSys;
            p[3] = targetComp;
            p[4] = 0; // mission_type: MISSION
            return BuildPacket(44, 221, p);
        }

        /// <summary>MISSION_ITEM_INT (ID 73). Один элемент миссии.</summary>
        public byte[] BuildMissionItemInt(MissionItem item, byte targetSys = 255, byte targetComp = 0)
        {
            int x = (int)Math.Round(item.Lat * 1e7);
            int y = (int)Math.Round(item.Lon * 1e7);
            float z = (float)item.AltRelative;

            byte[] p = new byte[38];
            PutF32(p, 0, item.Param1);
            PutF32(p, 4, item.Param2);
            PutF32(p, 8, item.Param3);
            PutF32(p, 12, item.Param4);
            PutI32(p, 16, x);
            PutI32(p, 20, y);
            PutF32(p, 24, z);
            PutU16(p, 28, item.Seq);
            PutU16(p, 30, (ushort)item.Command);
            p[32] = targetSys;
            p[33] = targetComp;
            p[34] = item.Frame;
            p[35] = 0; // current
            p[36] = item.Autocontinue ? (byte)1 : (byte)0;
            p[37] = 0; // mission_type
            return BuildPacket(73, 38, p);
        }

        /// <summary>MISSION_ACK (ID 47). Завершение миссии или ошибка.</summary>
        public byte[] BuildMissionAck(byte type, byte targetSys = 255, byte targetComp = 0)
        {
            byte[] p = new byte[4];
            p[0] = targetSys;
            p[1] = targetComp;
            p[2] = type; // MAV_MISSION_RESULT
            p[3] = 0;    // mission_type
            return BuildPacket(47, 153, p);
        }

        // =====================================================================
        // Core infrastructure: BuildPacket + CRC
        // =====================================================================

        /// <summary>
        /// Собрать v2 пакет. Трункация trailing zeros. CRC X.25 + crc_extra.
        /// </summary>
        private byte[] BuildPacket(uint msgId, byte crcExtra, byte[] payload)
        {
            // Trim trailing zeros (MAVLink v2).
            int len = payload.Length;
            while (len > 1 && payload[len - 1] == 0) len--;

            byte[] pkt = new byte[10 + len + 2];
            pkt[0] = 0xFD;
            pkt[1] = (byte)len;
            pkt[2] = 0; // incompat_flags
            pkt[3] = 0; // compat_flags

            byte seq;
            lock (_seqLock) { seq = _sequence++; }
            pkt[4] = seq;
            pkt[5] = _sysId;
            pkt[6] = _compId;
            pkt[7] = (byte)(msgId & 0xFF);
            pkt[8] = (byte)((msgId >> 8) & 0xFF);
            pkt[9] = (byte)((msgId >> 16) & 0xFF);

            Array.Copy(payload, 0, pkt, 10, len);

            ushort crc = ComputeCrc(pkt, 1, 9 + len, crcExtra);
            pkt[10 + len] = (byte)(crc & 0xFF);
            pkt[11 + len] = (byte)((crc >> 8) & 0xFF);

            return pkt;
        }

        private static ushort ComputeCrc(byte[] buf, int start, int length, byte crcExtra)
        {
            ushort crc = 0xFFFF;
            for (int i = 0; i < length; i++)
                crc = CrcAccumulate(buf[start + i], crc);
            crc = CrcAccumulate(crcExtra, crc);
            return crc;
        }

        /// <summary>X.25 CRC-16 (polynomial 0x1021, init 0xFFFF) — стандарт MAVLink.</summary>
        private static ushort CrcAccumulate(byte b, ushort crc)
        {
            byte tmp = (byte)(b ^ (byte)(crc & 0xFF));
            tmp ^= (byte)(tmp << 4);
            return (ushort)((crc >> 8) ^ (tmp << 8) ^ (tmp << 3) ^ (tmp >> 4));
        }

        // =====================================================================
        // Byte helpers
        // =====================================================================

        private static void PutU16(byte[] b, int o, ushort v)
        {
            b[o] = (byte)(v & 0xFF);
            b[o + 1] = (byte)(v >> 8);
        }

        private static void PutI16(byte[] b, int o, short v) => PutU16(b, o, (ushort)v);

        private static void PutU32(byte[] b, int o, uint v)
        {
            b[o] = (byte)(v & 0xFF);
            b[o + 1] = (byte)((v >> 8) & 0xFF);
            b[o + 2] = (byte)((v >> 16) & 0xFF);
            b[o + 3] = (byte)(v >> 24);
        }

        private static void PutI32(byte[] b, int o, int v) => PutU32(b, o, (uint)v);

        private static void PutU64(byte[] b, int o, ulong v)
        {
            for (int i = 0; i < 8; i++)
            {
                b[o + i] = (byte)(v & 0xFF);
                v >>= 8;
            }
        }

        private static void PutF32(byte[] b, int o, float v)
        {
            int bits = BitConverter.SingleToInt32Bits(v);
            PutI32(b, o, bits);
        }

        private uint GetBootMs() => (uint)_boot.ElapsedMilliseconds;
        private ulong GetBootUs() => (ulong)(_boot.ElapsedMilliseconds * 1000L);

        private static double NormalizeHeadingDeg(double d)
        {
            double r = d % 360.0;
            return r < 0 ? r + 360.0 : r;
        }
    }
}