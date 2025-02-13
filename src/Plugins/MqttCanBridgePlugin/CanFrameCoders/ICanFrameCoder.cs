namespace Antmicro.Renode.Plugins.MqttCanBridgePlugin.CanFrameCoders
{
    /// <summary>
    /// An interface that enables encoding and decoding of CAN frames to and from a specific format.
    /// </summary>
    public abstract class ICanFrameCoder
    {
        /// <summary>
        /// Checks if the coder supports the specified optional field.
        /// </summary>
        /// <param name="field">The optional field to check.</param>
        /// <returns>True if the field is supported, false otherwise.</returns>
        public abstract bool SupportsOptionalField(OptionalFields field);

        /// <summary>
        /// Encodes a CAN frame to a byte array.
        /// </summary>
        /// <param name="frame">The CAN frame to encode.</param>
        /// <returns>The encoded CAN frame.</returns>
        public abstract byte[] Encode(CanFrame frame);

        /// <summary>
        /// Decodes a CAN frame from a byte array.
        /// </summary>
        /// <param name="data">The byte array to decode.</param>
        /// <returns>The decoded CAN frame.</returns>
        public abstract CanFrame Decode(byte[] data);

        /// <summary>
        /// The optional fields that can be supported by the coder.
        /// </summary>
        public enum OptionalFields
        {
            PubId,
            PubCnt,
            TimeStamp,
        }
    }
}