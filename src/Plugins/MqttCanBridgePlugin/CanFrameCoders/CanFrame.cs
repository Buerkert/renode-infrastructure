using System;
using Antmicro.Renode.Core.CAN;
using Antmicro.Renode.Utilities;

namespace Antmicro.Renode.Plugins.MqttCanBridgePlugin.CanFrameCoders
{
    public class CanFrame
    {
        public CanFrame(FrameType type, ushort? cobId = null, byte[] data = null)
        {
            if (type != FrameType.Error && cobId == null)
                throw new ArgumentException("CobId must be specified for non-error frames");
            if (type == FrameType.Data && data == null)
                throw new ArgumentException("Data must be specified for data frames");
            if (cobId > 0x7FF)
                throw new ArgumentException("CobId must be at most 11 bits");
            if (data?.Length > 8)
                throw new ArgumentException("Data length must be at most 8 bytes");

            Type = type;
            if (type != FrameType.Error)
                CobId = cobId;
            if (type == FrameType.Data)
                Data = data;
        }

        public CanFrame(CANMessageFrame frame) : this(frame.RemoteFrame ? FrameType.Remote : FrameType.Data, (ushort)frame.Id, frame.Data)
        {
            if (frame.ExtendedFormat)
                throw new ArgumentException("Extended format is not supported");
            if (frame.FDFormat)
                throw new ArgumentException("FD format is not supported");
            if (frame.BitRateSwitch)
                throw new ArgumentException("Bit rate switch is not supported");
        }

        public static implicit operator CANMessageFrame(CanFrame frame)
        {
            if (frame.Type == FrameType.Error)
                throw new ArgumentException("Error frames are not supported");
            return new CANMessageFrame(frame.CobId ?? 0, frame.Data, false, frame.Type == FrameType.Remote);
        }

        public override string ToString()
        {
            return $"[Message: Id={CobId}, Type={Type}, Data={Misc.PrettyPrintCollectionHex(Data)}, Length={Data.Length}]";
        }

        public FrameType Type { get; }
        public ushort? CobId { get; }
        public byte[] Data { get; }
        public uint? PubId { get; set; }
        public uint? PubCnt { get; set; }
        public ulong? TimeStamp { get; set; }

        public enum FrameType
        {
            Data,
            Remote,
            Error,
        }
    }
}