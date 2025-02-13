using System;
using System.Collections.Generic;
using Antmicro.Renode.Utilities;

namespace Antmicro.Renode.Plugins.MqttCanBridgePlugin.CanFrameCoders
{
    /// <summary>
    /// Encodes CAN frames efficiently in a manually implemented binary format.
    /// Currently none of the optional fields are supported.
    /// To make the format endianness agnostic, data the cobId is stored in big endian format.
    /// The binary format is as follows:
    /// </summary>
    /// <code>
    ///  0        1        2        3        4        5        6        7        8        9        10       11       12
    ///  +--------+--------+--------+--------+--------+--------+--------+--------+--------+--------+--------+--------+
    ///  | MAGIC  |TYPE/LEN| COBIDH | COBIDL | DATA   | DATA   | DATA   | DATA   | DATA   | DATA   | DATA   | DATA   |
    ///  +--------+--------+--------+--------+--------+--------+--------+--------+--------+--------+--------+--------+
    /// </code>
    public class BinaryCanFrameCoder : ICanFrameCoder
    {
        public override bool SupportsOptionalField(OptionalFields field)
        {
            return false;
        }

        public override byte[] Encode(CanFrame frame)
        {
            var data = new List<byte>((int)BytePos.DataEnd + 1) { MsgMagic };
            byte typeLen = 0;
            typeLen = typeLen.ReplaceBits((byte)FrameType2MsgType(frame.Type), MsgTypeWidth, destinationPosition: MsgTypePos);
            var dataLength = frame.Data?.Length ?? 0;
            typeLen = typeLen.ReplaceBits((byte)dataLength, MsgLenWidth, destinationPosition: MsgLenPos);
            data.Add(typeLen);
            if (frame.Type != CanFrame.FrameType.Error)
            {
                var cobId = frame.CobId ?? throw new ArgumentException("CobId must be specified for non-error frames");
                data.Add((byte)(cobId >> 8));
                data.Add((byte)(cobId & 0xFF));
            }

            if (frame.Type == CanFrame.FrameType.Data)
            {
                if (dataLength > MaxDataLength)
                    throw new ArgumentException("Data length must be at most 8 bytes");
                if (frame.Data == null)
                    throw new ArgumentException("Data must be specified for data frames");
                data.AddRange(frame.Data);
            }
            else if (dataLength != 0)
            {
                throw new ArgumentException("Data must not be specified for non-data frames");
            }

            return data.ToArray();
        }

        public override CanFrame Decode(byte[] data)
        {
            if (data == null)
                throw new ArgumentNullException(nameof(data));
            if (data.Length < (int)BytePos.TypeLength + 1)
                throw new ArgumentException($"Frame must be at least {(int)BytePos.TypeLength + 1} bytes long");
            if (data.Length > (int)BytePos.DataEnd + 1)
                throw new ArgumentException($"Frame must be at most {(int)BytePos.DataEnd + 1} bytes long");
            if (data[(int)BytePos.Magic] != MsgMagic)
                throw new ArgumentException("Magic byte does not match");

            var msgType = (MsgType)BitHelper.GetValue(data[(int)BytePos.TypeLength], MsgTypePos, MsgTypeWidth);
            var msgLength = BitHelper.GetValue(data[(int)BytePos.TypeLength], MsgLenPos, MsgLenWidth);
            if (msgType == MsgType.Error)
            {
                if (data.Length != (int)BytePos.TypeLength + 1)
                    throw new ArgumentException($"Error frames must be exactly {BytePos.TypeLength + 1} bytes long");
                if (msgLength != 0)
                    throw new ArgumentException("Error frames length field must be 0");
                return new CanFrame(CanFrame.FrameType.Error);
            }

            if (data.Length != (int)BytePos.DataStart + msgLength)
                throw new ArgumentException("Frames' length field does not match the actual data length");

            var cobId = (ushort)((data[(int)BytePos.CobIdHigh] << 8) | data[(int)BytePos.CobIdLow]);
            if (cobId > MaxCobId)
                throw new ArgumentException($"CobId must be at most 0x{MaxCobId:X}");

            byte[] frameData = null;
            CanFrame.FrameType frameType;
            switch (msgType)
            {
                case MsgType.Data:
                    frameType = CanFrame.FrameType.Data;
                    frameData = new byte[msgLength];
                    Array.Copy(data, (int)BytePos.DataStart, frameData, 0, msgLength);
                    break;
                case MsgType.Remote:
                    frameType = CanFrame.FrameType.Remote;
                    if (msgLength != 0)
                        throw new ArgumentException("Remote frames length field must be 0");
                    break;
                default:
                    throw new ArgumentException($"Invalid frame type '{(byte)msgType}'");
            }

            return new CanFrame(frameType, cobId, frameData);
        }

        private static MsgType FrameType2MsgType(CanFrame.FrameType type)
        {
            switch (type)
            {
                case CanFrame.FrameType.Data:
                    return MsgType.Data;
                case CanFrame.FrameType.Remote:
                    return MsgType.Remote;
                case CanFrame.FrameType.Error:
                    return MsgType.Error;
                default:
                    throw new ArgumentOutOfRangeException(nameof(type), type, null);
            }
        }

        private enum MsgType : byte
        {
            Data = 0,
            Remote = 1,
            Error = 2,
        }

        private enum BytePos : uint
        {
            Magic = 0,
            TypeLength = 1,
            CobIdHigh = 2,
            CobIdLow = 3,
            DataStart = 4,
            DataEnd = 11,
        }

        private const byte MsgMagic = 0x42;
        private const int MsgTypePos = 0;
        private const int MsgTypeWidth = 2;
        private const int MsgLenPos = 2;
        private const int MsgLenWidth = 6;
        private const int MaxCobId = 0x7FF;
        private const int MaxDataLength = 8;
    }
}