namespace Antmicro.Renode.Plugins.MqttCanBridgePlugin.CanFrameCoders
{
    public abstract class ICanFrameCoder
    {
        public abstract bool SupportsOptionalField(OptionalFields field);

        public abstract byte[] Encode(CanFrame frame);

        public abstract CanFrame Decode(byte[] data);

        public enum OptionalFields
        {
            PubId,
            PubCnt,
            TimeStamp,
        }
    }
}