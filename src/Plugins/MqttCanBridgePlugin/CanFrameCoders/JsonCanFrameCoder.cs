using System;
using System.Linq;
using Newtonsoft.Json;

namespace Antmicro.Renode.Plugins.MqttCanBridgePlugin.CanFrameCoders
{
    /// <summary>
    /// A CAN frame coder that encodes and decodes CAN frames to and from JSON making them human-readable.
    /// Furthermore, all optional fields are supported but can be disabled to reduce the size of the encoded frames.
    /// The JSON format is as follows:
    /// </summary>
    /// <code>
    /// {
    ///     "type": "data",                     // "data", "remote" or "error"
    ///     "cobId": 123,                       // 11-bit CAN ID
    ///     "data": [1, 2, 3, 4, 5, 6, 7, 8],   // 0-8 bytes of data
    ///     pubId: 123,                         // optional publisher ID max 32-bit
    ///     pubCnt: 123,                        // optional publisher counter max 32-bit
    ///     ts: 1234567890                      // optional 64-bit unix timestamp in microseconds 
    /// }
    /// </code>
    public class JsonCanFrameCoder : ICanFrameCoder
    {
        /// <summary>
        /// Creates a new instance of the JSON CAN frame coder.
        /// The coder will only encode and decode the optional fields that are enabled at the time of creation.
        /// If a received frame does not contain an optional but enabled field it won't be treated as malformed,
        /// instead the corresponding property in CanFrame will not be set.
        /// </summary>
        /// <param name="pubIdSupport">Whether the publisher ID field should be supported.</param>
        /// <param name="pubCntSupport">Whether the publisher counter field should be supported.</param>
        /// <param name="timeStampSupport">Whether the timestamp field should be supported.</param>
        public JsonCanFrameCoder(bool pubIdSupport, bool pubCntSupport, bool timeStampSupport)
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
            var jsonFrame = new JsonCanFrame(frame);
            if (!pubIdSupport)
                jsonFrame.PubId = null;
            if (!pubCntSupport)
                jsonFrame.PubCnt = null;
            if (!timeStampSupport)
                jsonFrame.TimeStamp = null;
            return System.Text.Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(jsonFrame));
        }

        public override CanFrame Decode(byte[] data)
        {
            var str = System.Text.Encoding.UTF8.GetString(data);
            var jsonFrame = JsonConvert.DeserializeObject<JsonCanFrame>(str);
            CanFrame.FrameType type;
            switch (jsonFrame.Type)
            {
                case "data":
                    type = CanFrame.FrameType.Data;
                    break;
                case "remote":
                    type = CanFrame.FrameType.Remote;
                    break;
                case "error":
                    type = CanFrame.FrameType.Error;
                    break;
                default:
                    throw new ArgumentException($"Invalid frame type '{jsonFrame.Type}'");
            }

            var canByteData = jsonFrame.Data?.Select(x =>
            {
                if (x > byte.MaxValue)
                    throw new ArgumentException("Data array contains values that do not fit in a byte");
                return (byte)x;
            }).ToArray();

            var canFrame = new CanFrame(type, jsonFrame.CobId, canByteData);
            if (pubIdSupport)
                canFrame.PubId = jsonFrame.PubId;
            if (pubCntSupport)
                canFrame.PubCnt = jsonFrame.PubCnt;
            if (timeStampSupport)
                canFrame.TimeStamp = jsonFrame.TimeStamp;
            return canFrame;
        }

        private class JsonCanFrame
        {
            public JsonCanFrame()
            {
            }

            public JsonCanFrame(CanFrame canFrame)
            {
                Type = canFrame.Type == CanFrame.FrameType.Data ? "data" : canFrame.Type == CanFrame.FrameType.Remote ? "remote" : "error";
                if (canFrame.Type != CanFrame.FrameType.Error)
                    CobId = canFrame.CobId;
                if (canFrame.Type == CanFrame.FrameType.Data)
                    Data = canFrame.Data.Select(x => (ushort)x).ToArray();
                PubId = canFrame.PubId;
                PubCnt = canFrame.PubCnt;
                TimeStamp = canFrame.TimeStamp;
            }

            [JsonProperty("type", Required = Required.Always)]
            public string Type { get; set; }

            [JsonProperty("cobId", NullValueHandling = NullValueHandling.Ignore)]
            public ushort? CobId { get; set; }

            [JsonProperty("data", NullValueHandling = NullValueHandling.Ignore)]
            public ushort[] Data { get; set; }

            [JsonProperty("pubId", NullValueHandling = NullValueHandling.Ignore)]
            public uint? PubId { get; set; }

            [JsonProperty("pubCnt", NullValueHandling = NullValueHandling.Ignore)]
            public uint? PubCnt { get; set; }

            [JsonProperty("ts", NullValueHandling = NullValueHandling.Ignore)]
            public ulong? TimeStamp { get; set; }
        }

        private readonly bool pubIdSupport;
        private readonly bool pubCntSupport;
        private readonly bool timeStampSupport;
    }
}