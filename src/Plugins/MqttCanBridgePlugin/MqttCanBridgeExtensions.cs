using System;
using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure;
using Antmicro.Renode.Exceptions;
using Antmicro.Renode.Plugins.MqttCanBridgePlugin.CanFrameCoders;
using Antmicro.Renode.Utilities;

namespace Antmicro.Renode.Plugins.MqttCanBridgePlugin
{
    public static class MqttCanBridgeExtensions
    {
        /// <summary>
        /// Creates a new MQTT-CAN bridge from the specified parameters and registers it as a child of the specified machine.
        /// </summary>
        /// <param name="machine">The machine to create the bridge on.</param>
        /// <param name="name">The name of the bridge.</param>
        /// <param name="format">The format of the CAN frames. Supported formats are "json" and "binary".</param>
        /// <param name="brokerUri">The URI of the MQTT broker to connect to. Must be in the format "mqtt://host:port".</param>
        /// <param name="channel">The channel number to use for the MQTT connection.</param>
        /// <param name="optionalFields">A bitmask of optional fields to include in the encoded frames. The supported fields are "pubId", "pubCnt" and "timeStamp".</param>
        public static void CreateMqttCanBridge(this IMachine machine, string name, string format = "json", string brokerUri = "mqtt://localhost:1883",
            byte channel = 0, uint optionalFields = 0)
        {
            var coder = CreateCoder(format, optionalFields);
            var bridge = new MqttCanBridge(brokerUri, channel, coder);
            machine.RegisterAsAChildOf(machine.SystemBus, bridge, NullRegistrationPoint.Instance);
            machine.SetLocalName(bridge, name);
        }

        /// <summary>
        /// Creates a can frame coder based on the specified format and optional fields.
        /// </summary>
        /// <param name="format"> The format of the CAN frames. Supported formats are "json" and "binary".</param>
        /// <param name="optionalFields">A bitmask of optional fields to include in the encoded frames. The supported fields are "pubId", "pubCnt" and "timeStamp".</param>
        /// <returns>The created can frame coder.</returns>
        /// <exception cref="ConstructionException">Thrown when the specified format or optional fields are invalid.</exception>
        private static ICanFrameCoder CreateCoder(string format, uint optionalFields)
        {
            var pubId = BitHelper.IsBitSet(optionalFields, (int)OptionalFieldPos.PubId);
            var pubCnt = BitHelper.IsBitSet(optionalFields, (int)OptionalFieldPos.PubCnt);
            var timeStamp = BitHelper.IsBitSet(optionalFields, (int)OptionalFieldPos.TimeStamp);
            var invalidFields = (optionalFields & ~(uint)(OptionalFieldPos.PubId | OptionalFieldPos.PubCnt | OptionalFieldPos.TimeStamp)) != 0;
            if (invalidFields)
                throw new ConstructionException("Invalid optional fields");

            switch (format)
            {
                case "json":
                    return new JsonCanFrameCoder(pubId, pubCnt, timeStamp);
                case "binary":
                    if (pubId || pubCnt || timeStamp)
                        throw new ConstructionException("Optional fields are not supported in binary format");
                    return new BinaryCanFrameCoder();
                default:
                    throw new ConstructionException($"Unsupported format '{format}'");
            }
        }

        [Flags]
        private enum OptionalFieldPos
        {
            PubId,
            PubCnt,
            TimeStamp
        }
    }
}