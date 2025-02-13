using System;
using Google.Protobuf;
using static Antmicro.Renode.Plugins.MqttCanBridgePlugin.CanFrameCoders.Proto.CanFrame;

namespace Antmicro.Renode.Plugins.MqttCanBridgePlugin.CanFrameCoders
{
    /// <summary>
    /// A CAN frame coder that encodes and decodes CAN frames to and from the ProtoBuf format.
    /// Furthermore, all optional fields are supported but can be disabled to reduce the size of the encoded frames.
    /// See ProtoCanFrame.proto for the definition of the format.
    /// </summary>
    public class ProtoCanFrameCoder : ICanFrameCoder
    {
        /// <summary>
        /// Creates a new instance of the ProtoBuf CAN frame coder.
        /// The coder will only encode and decode the optional fields that are enabled at the time of creation.
        /// If a received frame does not contain an optional but enabled field it won't be treated as malformed,
        /// instead the corresponding property in CanFrame will not be set.
        /// </summary>
        /// <param name="pubIdSupport">Whether the publisher ID field should be supported.</param>
        /// <param name="pubCntSupport">Whether the publisher counter field should be supported.</param>
        /// <param name="timeStampSupport">Whether the timestamp field should be supported.</param>
        public ProtoCanFrameCoder(bool pubIdSupport, bool pubCntSupport, bool timeStampSupport)
        {
            this.pubIdSupport = pubIdSupport;
            this.pubCntSupport = pubCntSupport;
            this.timeStampSupport = timeStampSupport;
        }

        public override bool SupportsOptionalField(OptionalFields field)
        {
            switch (field)
            {
                case OptionalFields.PubId:
                    return pubIdSupport;
                case OptionalFields.PubCnt:
                    return pubCntSupport;
                case OptionalFields.TimeStamp:
                    return timeStampSupport;
                default:
                    throw new ArgumentOutOfRangeException(nameof(field), field, null);
            }
        }

        public override byte[] Encode(CanFrame frame)
        {
            var protoFrame = new Proto.CanFrame();
            switch (frame.Type)
            {
                case CanFrame.FrameType.Data:
                    protoFrame.DataFrame = new Types.DataFrame
                    {
                        CobId = frame.CobId ?? throw new ArgumentException("CobId is required for data frame"),
                        Data = ByteString.CopyFrom(frame.Data ?? throw new ArgumentException("Data is required for data frame"))
                    };
                    break;
                case CanFrame.FrameType.Remote:
                    protoFrame.RemoteFrame = new Types.RemoteFrame
                    {
                        CobId = frame.CobId ?? throw new ArgumentException("CobId is required for remote frame")
                    };
                    break;
                case CanFrame.FrameType.Error:
                    protoFrame.ErrorFrame = new Types.ErrorFrame();
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(frame.Type), frame.Type, "Invalid frame type");
            }

            if (pubIdSupport && frame.PubId.HasValue)
                protoFrame.PubId = frame.PubId.Value;
            if (pubCntSupport && frame.PubCnt.HasValue)
                protoFrame.PubCnt = frame.PubCnt.Value;
            if (timeStampSupport && frame.TimeStamp.HasValue)
                protoFrame.Timestamp = frame.TimeStamp.Value;

            return protoFrame.ToByteArray();
        }

        public override CanFrame Decode(byte[] data)
        {
            var protoFrame = Parser.ParseFrom(data);
            CanFrame canFrame = null;
            if (protoFrame.DataFrame != null)
            {
                var frameData = protoFrame.DataFrame.Data.ToByteArray();
                var cobId = protoFrame.DataFrame.CobId;
                if (frameData.Length > 8)
                    throw new ArgumentException("Data length must be at most 8 bytes");
                if (cobId > 0x7FF)
                    throw new ArgumentException("CobId must be at most 0x7FF");
                canFrame = new CanFrame(CanFrame.FrameType.Data, (ushort)cobId, frameData);
            }
            else if (protoFrame.RemoteFrame != null)
            {
            }
            else if (protoFrame.ErrorFrame != null)
            {
            }
            else
            {
                throw new ArgumentException("Invalid frame type");
            }

            return canFrame;
        }

        private readonly bool pubIdSupport;
        private readonly bool pubCntSupport;
        private readonly bool timeStampSupport;
    }
}