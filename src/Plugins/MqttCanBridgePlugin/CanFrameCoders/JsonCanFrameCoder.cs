using System;
using System.Linq;
using Newtonsoft.Json;

namespace Antmicro.Renode.Plugins.MqttCanBridgePlugin.CanFrameCoders
{
    public class JsonCanFrameCoder : ICanFrameCoder
    {
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