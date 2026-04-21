using System;
using System.Collections.Generic;
using System.Text;
using SimpleDroneGCS.Simulator.Control;

namespace SimpleDroneGCS.Simulator.Mavlink
{
    // =========================================================================
    // Event argument types
    // =========================================================================

    public sealed class HeartbeatArgs : EventArgs
    {
        public byte SystemId { get; init; }
        public byte ComponentId { get; init; }
        public byte Type { get; init; }
        public byte Autopilot { get; init; }
        public byte BaseMode { get; init; }
        public uint CustomMode { get; init; }
        public byte SystemStatus { get; init; }
    }

    public sealed class ArmCommandArgs : EventArgs
    {
        public bool Arm { get; init; }
        /// <summary>true если param2 = 21196 (force arm / emergency disarm в воздухе).</summary>
        public bool Force { get; init; }
    }

    public sealed class MotorTestArgs : EventArgs
    {
        public byte MotorIndex { get; init; }
        public byte TestType { get; init; } // 0 = percent
        public float Throttle { get; init; }
        public float DurationSec { get; init; }
        public byte MotorCount { get; init; }
    }

    public sealed class ParamSetArgs : EventArgs
    {
        public string ParamId { get; init; } = "";
        public float Value { get; init; }
        public byte Type { get; init; }
    }

    public sealed class CalibrationArgs : EventArgs
    {
        public bool Gyro { get; init; }
        public bool Magnetometer { get; init; }
        public bool GroundPressure { get; init; }
        public bool Rc { get; init; }
        public bool Accelerometer { get; init; }
        public bool Airspeed { get; init; }
    }

    public sealed class GuidedTargetArgs : EventArgs
    {
        public double Lat { get; init; }
        public double Lon { get; init; }
        public double AltRelative { get; init; }
    }

    public sealed class SetHomeArgs : EventArgs
    {
        /// <summary>true = использовать текущие координаты ВС как HOME.</summary>
        public bool UseCurrent { get; init; }
        public double Lat { get; init; }
        public double Lon { get; init; }
        public double AltAmsl { get; init; }
    }

    // =========================================================================
    // Inbound parser
    // =========================================================================

    /// <summary>
    /// Парсер входящих MAVLink v2 пакетов. Собирает байты через <see cref="Feed"/>
    /// и эмитит события по распознанным сообщениям.
    /// <para>
    /// State machine: ждём <c>0xFD</c>, читаем 10 байт header, читаем payload+CRC,
    /// валидируем CRC, диспатчим. Неизвестные msg_id и битые CRC — молча пропускаем.
    /// </para>
    /// <para>
    /// <b>Thread-safety:</b> <see cref="Feed"/> должен вызываться из одного потока
    /// (UDP receive thread). События выпускаются синхронно из него же.
    /// </para>
    /// </summary>
    public sealed class MavlinkInbound
    {
        // -------------------- Events --------------------

        public event EventHandler<HeartbeatArgs> HeartbeatReceived;
        public event EventHandler<ArmCommandArgs> ArmCommand;
        public event EventHandler<uint> SetModeCommand;
        public event EventHandler RtlCommand;
        public event EventHandler LandCommand;
        public event EventHandler RebootCommand;
        public event EventHandler<MotorTestArgs> MotorTestCommand;
        public event EventHandler<CalibrationArgs> CalibrationCommand;
        public event EventHandler AutopilotCapabilitiesRequested;

        /// <summary>MAV_CMD_DO_VTOL_TRANSITION (300). param1: 3 = MC, 4 = FW.</summary>
        public event EventHandler<byte> VtolTransitionCommand;

        /// <summary>MAV_CMD_DO_CHANGE_SPEED (178). param2 = speed m/s.</summary>
        public event EventHandler<float> ChangeSpeedCommand;

        /// <summary>MAV_CMD_DO_SET_HOME (179). GCS устанавливает HOME.</summary>
        public event EventHandler<SetHomeArgs> SetHomeCommand;

        /// <summary>MAV_CMD_MISSION_START (300). GCS запускает миссию.</summary>
        public event EventHandler MissionStartCommand;

        public event EventHandler ParamRequestList;
        public event EventHandler<string> ParamRequestRead;
        public event EventHandler<ParamSetArgs> ParamSet;

        public event EventHandler MissionRequestList;
        public event EventHandler<ushort> MissionRequestInt;
        public event EventHandler<ushort> MissionCountStart;
        public event EventHandler<MissionItem> MissionItemInt;
        public event EventHandler MissionAckReceived;
        public event EventHandler MissionClearAll;
        public event EventHandler<ushort> MissionSetCurrent;

        public event EventHandler<GuidedTargetArgs> GuidedTargetInt;

        /// <summary>Нераспознанная команда (для ACK с UNSUPPORTED).</summary>
        public event EventHandler<ushort> UnhandledCommand;

        // -------------------- State --------------------

        private enum ParserStage { WaitMagic, ReadHeader, ReadPayloadAndCrc }

        private ParserStage _stage = ParserStage.WaitMagic;
        private readonly byte[] _packetBuf = new byte[300]; // max v2 packet ≈ 280
        private int _packetOffset;
        private int _expectedTotalLen;

        // CRC extras для known messages (совпадают с Outbound).
        private static readonly Dictionary<uint, byte> _crcExtras = new()
        {
            { 0, 50 },     // HEARTBEAT
            { 11, 89 },    // SET_MODE
            { 20, 214 },   // PARAM_REQUEST_READ
            { 21, 159 },   // PARAM_REQUEST_LIST
            { 23, 168 },   // PARAM_SET
            { 41, 28 },    // MISSION_SET_CURRENT
            { 43, 132 },   // MISSION_REQUEST_LIST
            { 44, 221 },   // MISSION_COUNT
            { 45, 232 },   // MISSION_CLEAR_ALL
            { 47, 153 },   // MISSION_ACK
            { 51, 196 },   // MISSION_REQUEST_INT
            { 66, 148 },   // REQUEST_DATA_STREAM
            { 69, 243 },   // MANUAL_CONTROL
            { 73, 38 },    // MISSION_ITEM_INT
            { 75, 158 },   // COMMAND_INT
            { 76, 152 },   // COMMAND_LONG
            { 86, 5 },     // SET_POSITION_TARGET_GLOBAL_INT
        };

        // =====================================================================
        // Feed (entry point)
        // =====================================================================

        /// <summary>
        /// Обработать порцию входящих байтов. Может содержать 0..N пакетов,
        /// может быть фрагментом. Парсер сам соберёт и декодирует.
        /// </summary>
        public void Feed(byte[] buffer, int offset, int length)
        {
            if (buffer == null || length <= 0) return;

            for (int i = 0; i < length; i++)
            {
                byte b = buffer[offset + i];
                switch (_stage)
                {
                    case ParserStage.WaitMagic:
                        if (b == 0xFD)
                        {
                            _packetBuf[0] = b;
                            _packetOffset = 1;
                            _stage = ParserStage.ReadHeader;
                        }
                        break;

                    case ParserStage.ReadHeader:
                        _packetBuf[_packetOffset++] = b;
                        if (_packetOffset == 10)
                        {
                            byte payloadLen = _packetBuf[1];
                            _expectedTotalLen = 10 + payloadLen + 2;
                            if (_expectedTotalLen > _packetBuf.Length)
                            {
                                Reset();
                            }
                            else
                            {
                                _stage = ParserStage.ReadPayloadAndCrc;
                            }
                        }
                        break;

                    case ParserStage.ReadPayloadAndCrc:
                        _packetBuf[_packetOffset++] = b;
                        if (_packetOffset == _expectedTotalLen)
                        {
                            ProcessPacket();
                            Reset();
                        }
                        break;
                }
            }
        }

        private void Reset()
        {
            _stage = ParserStage.WaitMagic;
            _packetOffset = 0;
            _expectedTotalLen = 0;
        }

        // =====================================================================
        // Packet validation & dispatch
        // =====================================================================

        private void ProcessPacket()
        {
            byte len = _packetBuf[1];
            byte sysId = _packetBuf[5];
            byte compId = _packetBuf[6];
            uint msgId = (uint)(_packetBuf[7]
                | (_packetBuf[8] << 8)
                | (_packetBuf[9] << 16));

            // Неизвестный MSG — молча игнорируем.
            if (!_crcExtras.TryGetValue(msgId, out byte crcExtra)) return;

            // Проверка CRC.
            ushort gotCrc = (ushort)(_packetBuf[10 + len]
                | (_packetBuf[10 + len + 1] << 8));
            ushort calcCrc = ComputeCrc(_packetBuf, 1, 9 + len, crcExtra);
            if (gotCrc != calcCrc) return; // bad packet

            // Dispatch.
            Dispatch(msgId, sysId, compId, _packetBuf, 10, len);
        }

        private void Dispatch(uint msgId, byte sysId, byte compId,
            byte[] buf, int offset, int len)
        {
            switch (msgId)
            {
                case 0: ParseHeartbeat(buf, offset, len, sysId, compId); break;
                case 11: ParseSetMode(buf, offset, len); break;
                case 20: ParseParamRequestRead(buf, offset, len); break;
                case 21: ParamRequestList?.Invoke(this, EventArgs.Empty); break;
                case 23: ParseParamSet(buf, offset, len); break;
                case 41: ParseMissionSetCurrent(buf, offset, len); break;
                case 43: MissionRequestList?.Invoke(this, EventArgs.Empty); break;
                case 44: ParseMissionCount(buf, offset, len); break;
                case 45: MissionClearAll?.Invoke(this, EventArgs.Empty); break;
                case 47: MissionAckReceived?.Invoke(this, EventArgs.Empty); break;
                case 51: ParseMissionRequestInt(buf, offset, len); break;
                case 66: /* REQUEST_DATA_STREAM — игнор */ break;
                case 69: /* MANUAL_CONTROL — игнор */ break;
                case 73: ParseMissionItemInt(buf, offset, len); break;
                case 75: ParseCommandInt(buf, offset, len); break;
                case 76: ParseCommandLong(buf, offset, len); break;
                case 86: ParseSetPositionTargetGlobalInt(buf, offset, len); break;
            }
        }

        // =====================================================================
        // Message parsers
        // =====================================================================

        private void ParseHeartbeat(byte[] buf, int offset, int len, byte sysId, byte compId)
        {
            var p = ExpandPayload(buf, offset, len, 9);
            HeartbeatReceived?.Invoke(this, new HeartbeatArgs
            {
                SystemId = sysId,
                ComponentId = compId,
                Type = p[4],
                Autopilot = p[5],
                BaseMode = p[6],
                CustomMode = GetU32(p, 0),
                SystemStatus = p[7],
            });
        }

        private void ParseSetMode(byte[] buf, int offset, int len)
        {
            var p = ExpandPayload(buf, offset, len, 6);
            uint customMode = GetU32(p, 0);
            SetModeCommand?.Invoke(this, customMode);
        }

        private void ParseCommandLong(byte[] buf, int offset, int len)
        {
            var p = ExpandPayload(buf, offset, len, 33);
            float p1 = GetF32(p, 0);
            float p2 = GetF32(p, 4);
            float p3 = GetF32(p, 8);
            float p4 = GetF32(p, 12);
            float p5 = GetF32(p, 16);
            float p6 = GetF32(p, 20);
            float p7 = GetF32(p, 24);
            ushort command = GetU16(p, 28);
            DispatchCommand(command, p1, p2, p3, p4, p5, p6, p7);
        }

        private void ParseCommandInt(byte[] buf, int offset, int len)
        {
            var p = ExpandPayload(buf, offset, len, 35);
            float p1 = GetF32(p, 0);
            float p2 = GetF32(p, 4);
            float p3 = GetF32(p, 8);
            float p4 = GetF32(p, 12);
            int x = GetI32(p, 16);
            int y = GetI32(p, 20);
            float p7 = GetF32(p, 24);
            ushort command = GetU16(p, 28);

            // Для COMMAND_INT x/y — это lat/lon × 1e7. Приводим к градусам.
            float p5 = (float)(x / 1e7);
            float p6 = (float)(y / 1e7);
            DispatchCommand(command, p1, p2, p3, p4, p5, p6, p7);
        }

        /// <summary>
        /// Общий диспатчер команд (COMMAND_LONG и COMMAND_INT).
        /// </summary>
        private void DispatchCommand(ushort cmd,
            float p1, float p2, float p3, float p4, float p5, float p6, float p7)
        {
            switch (cmd)
            {
                case 176: // DO_SET_MODE
                    SetModeCommand?.Invoke(this, (uint)p2);
                    break;

                case 178: // DO_CHANGE_SPEED
                    // p1 = speed type (0 = airspeed, 1 = groundspeed); p2 = speed m/s.
                    ChangeSpeedCommand?.Invoke(this, p2);
                    break;

                case 179: // DO_SET_HOME
                    // p1 = 1 → use current location; p1 = 0 → use p5/p6/p7 (lat/lon/alt).
                    SetHomeCommand?.Invoke(this, new SetHomeArgs
                    {
                        UseCurrent = p1 > 0.5f,
                        Lat = p5,
                        Lon = p6,
                        AltAmsl = p7,
                    });
                    break;

                case 300: // MISSION_START
                    MissionStartCommand?.Invoke(this, EventArgs.Empty);
                    break;

                case 3000: // DO_VTOL_TRANSITION
                    // p1: 3 = transition to MC, 4 = transition to FW.
                    VtolTransitionCommand?.Invoke(this, (byte)p1);
                    break;

                case 400: // COMPONENT_ARM_DISARM
                    ArmCommand?.Invoke(this, new ArmCommandArgs
                    {
                        Arm = p1 > 0.5f,
                        Force = Math.Abs(p2 - 21196f) < 1f,
                    });
                    break;

                case 20: // NAV_RETURN_TO_LAUNCH
                    RtlCommand?.Invoke(this, EventArgs.Empty);
                    break;

                case 21: // NAV_LAND
                case 85: // NAV_VTOL_LAND
                    LandCommand?.Invoke(this, EventArgs.Empty);
                    break;

                case 192: // DO_REPOSITION
                    GuidedTargetInt?.Invoke(this, new GuidedTargetArgs
                    {
                        Lat = p5,
                        Lon = p6,
                        AltRelative = p7,
                    });
                    break;

                case 209: // DO_MOTOR_TEST
                    MotorTestCommand?.Invoke(this, new MotorTestArgs
                    {
                        MotorIndex = (byte)p1,
                        TestType = (byte)p2,
                        Throttle = p3,
                        DurationSec = p4,
                        MotorCount = (byte)p5,
                    });
                    break;

                case 241: // PREFLIGHT_CALIBRATION
                    CalibrationCommand?.Invoke(this, new CalibrationArgs
                    {
                        Gyro = p1 > 0.5f,
                        Magnetometer = p2 > 0.5f,
                        GroundPressure = p3 > 0.5f,
                        Rc = p4 > 0.5f,
                        Accelerometer = p5 > 0.5f,
                        Airspeed = p6 > 0.5f,
                    });
                    break;

                case 246: // PREFLIGHT_REBOOT_SHUTDOWN
                    RebootCommand?.Invoke(this, EventArgs.Empty);
                    break;

                case 520: // REQUEST_AUTOPILOT_CAPABILITIES
                    AutopilotCapabilitiesRequested?.Invoke(this, EventArgs.Empty);
                    break;

                default:
                    UnhandledCommand?.Invoke(this, cmd);
                    break;
            }
        }

        private void ParseParamRequestRead(byte[] buf, int offset, int len)
        {
            var p = ExpandPayload(buf, offset, len, 20);
            string paramId = ReadString(p, 4, 16);
            ParamRequestRead?.Invoke(this, paramId);
        }

        private void ParseParamSet(byte[] buf, int offset, int len)
        {
            var p = ExpandPayload(buf, offset, len, 23);
            ParamSet?.Invoke(this, new ParamSetArgs
            {
                Value = GetF32(p, 0),
                ParamId = ReadString(p, 6, 16),
                Type = p[22],
            });
        }

        private void ParseMissionSetCurrent(byte[] buf, int offset, int len)
        {
            var p = ExpandPayload(buf, offset, len, 4);
            MissionSetCurrent?.Invoke(this, GetU16(p, 0));
        }

        private void ParseMissionCount(byte[] buf, int offset, int len)
        {
            var p = ExpandPayload(buf, offset, len, 5);
            MissionCountStart?.Invoke(this, GetU16(p, 0));
        }

        private void ParseMissionRequestInt(byte[] buf, int offset, int len)
        {
            var p = ExpandPayload(buf, offset, len, 5);
            MissionRequestInt?.Invoke(this, GetU16(p, 0));
        }

        private void ParseMissionItemInt(byte[] buf, int offset, int len)
        {
            var p = ExpandPayload(buf, offset, len, 38);
            var item = new MissionItem
            {
                Param1 = GetF32(p, 0),
                Param2 = GetF32(p, 4),
                Param3 = GetF32(p, 8),
                Param4 = GetF32(p, 12),
                Lat = GetI32(p, 16) / 1e7,
                Lon = GetI32(p, 20) / 1e7,
                AltRelative = GetF32(p, 24),
                Seq = GetU16(p, 28),
                Command = (MissionCommand)GetU16(p, 30),
                Frame = p[34],
                Autocontinue = p[36] != 0,
            };
            MissionItemInt?.Invoke(this, item);
        }

        private void ParseSetPositionTargetGlobalInt(byte[] buf, int offset, int len)
        {
            var p = ExpandPayload(buf, offset, len, 53);
            double lat = GetI32(p, 4) / 1e7;
            double lon = GetI32(p, 8) / 1e7;
            float alt = GetF32(p, 12);
            ushort typeMask = GetU16(p, 48);

            // type_mask: если биты 0-2 (POS_X/Y/Z) очищены — используем позицию.
            bool hasPosition = (typeMask & 0x07) == 0;
            if (!hasPosition) return;

            GuidedTargetInt?.Invoke(this, new GuidedTargetArgs
            {
                Lat = lat,
                Lon = lon,
                AltRelative = alt,
            });
        }

        // =====================================================================
        // CRC (совпадает с Outbound)
        // =====================================================================

        private static ushort ComputeCrc(byte[] buf, int start, int length, byte crcExtra)
        {
            ushort crc = 0xFFFF;
            for (int i = 0; i < length; i++)
                crc = CrcAccumulate(buf[start + i], crc);
            crc = CrcAccumulate(crcExtra, crc);
            return crc;
        }

        private static ushort CrcAccumulate(byte b, ushort crc)
        {
            byte tmp = (byte)(b ^ (byte)(crc & 0xFF));
            tmp ^= (byte)(tmp << 4);
            return (ushort)((crc >> 8) ^ (tmp << 8) ^ (tmp << 3) ^ (tmp >> 4));
        }

        // =====================================================================
        // Byte helpers
        // =====================================================================

        /// <summary>
        /// Скопировать payload в буфер фиксированной длины. Если фактический
        /// payload короче (v2 trailing-zero trim) — оставшиеся байты = 0.
        /// </summary>
        private static byte[] ExpandPayload(byte[] buf, int offset, int len, int expected)
        {
            byte[] p = new byte[expected];
            Array.Copy(buf, offset, p, 0, Math.Min(len, expected));
            return p;
        }

        private static ushort GetU16(byte[] b, int o) => (ushort)(b[o] | (b[o + 1] << 8));

        private static uint GetU32(byte[] b, int o) =>
            (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));

        private static int GetI32(byte[] b, int o) => (int)GetU32(b, o);

        private static float GetF32(byte[] b, int o) =>
            BitConverter.Int32BitsToSingle(GetI32(b, o));

        private static string ReadString(byte[] buf, int offset, int maxLen)
        {
            int end = offset;
            int limit = offset + maxLen;
            while (end < limit && buf[end] != 0) end++;
            return Encoding.ASCII.GetString(buf, offset, end - offset);
        }
    }
}