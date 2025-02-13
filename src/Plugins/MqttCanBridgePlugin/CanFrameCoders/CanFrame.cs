using System;
using Antmicro.Renode.Core.CAN;
using Antmicro.Renode.Utilities;

namespace Antmicro.Renode.Plugins.MqttCanBridgePlugin.CanFrameCoders
{
    /// <summary>
    /// A class representing a CAN frame that is compatible with the MQTT CAN bridge protocol.
    /// </summary>
    public class CanFrame
    {
        /// <summary>
        /// Creates a new CAN frame with the specified type, cobId and data.
        /// </summary>
        /// <param name="type">The type of the frame.</param>
        /// <param name="cobId">The 11-bit CAN ID of the frame.</param>
        /// <param name="data">The data of the frame.</param>
        /// <exception cref="ArgumentException">Thrown when the created frame is invalid. E.g. when cobId is not specified for non-error frames.</exception>
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

        /// <summary>
        /// Creates a new CAN frame from the specified CAN message frame.
        /// </summary>
        /// <param name="frame">The CAN message frame to create the CAN frame from.</param>
        /// <exception cref="ArgumentException">Thrown when the created frame is invalid. Or the specified frame makes use of unsupported features.</exception>
        public CanFrame(CANMessageFrame frame) : this(frame.RemoteFrame ? FrameType.Remote : FrameType.Data, (ushort)frame.Id, frame.Data)
        {
            if (frame.ExtendedFormat)
                throw new ArgumentException("Extended format is not supported");
            if (frame.FDFormat)
                throw new ArgumentException("FD format is not supported");
            if (frame.BitRateSwitch)
                throw new ArgumentException("Bit rate switch is not supported");
        }

        /// <summary>
        /// Converts the specified CAN frame to a CAN message frame.
        /// </summary>
        /// <param name="frame">The CAN frame to convert.</param>
        /// <returns>The converted CAN message frame.</returns>
        /// <exception cref="ArgumentException">Thrown when the conversion is not possible. E.g. when the specified frame is an error frame.</exception>
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
            Error
        }
    }
}