using System;
using System.Threading.Channels;
using System.Threading.Tasks;
using Antmicro.Renode.Core;
using Antmicro.Renode.Core.CAN;
using Antmicro.Renode.Core.Structure;
using Antmicro.Renode.Exceptions;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.CAN;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Formatter;
using MQTTnet.Protocol;

namespace Antmicro.Renode.Plugins.MqttCanBridgePlugin
{
    public static class MqttCanBridgeExtensions
    {
        public static void CreateMqttCanBridge(this IMachine machine, string name, string brokerUri = "mqtt://localhost:1883", byte channel = 0,
            string format = "json")
        {
            ICanFrameCoder coder;
            switch (format)
            {
                case "json":
                    coder = new JsonCanFrameCoder(true, true, true);
                    break;
                default:
                    throw new ConstructionException($"Unsupported format '{format}'");
            }

            var bridge = new MqttCanBridge(brokerUri, channel, coder);
            machine.RegisterAsAChildOf(machine.SystemBus, bridge, NullRegistrationPoint.Instance);
            machine.SetLocalName(bridge, name);
        }
    }

    public class MqttCanBridge : ICAN
    {
        public MqttCanBridge(string brokerUri, byte channel, ICanFrameCoder coder)
        {
            this.brokerUri = new Uri(brokerUri);
            this.channel = channel;
            this.coder = coder;
            txQueue = Channel.CreateUnbounded<CANMessageFrame>();
            mqttClient = mqttFactory.CreateMqttClient();
            mqttClient.DisconnectedAsync += async e =>
            {
                this.Log(LogLevel.Error, "Disconnected from broker: {0}", e.Exception?.Message);
                await Task.Delay(TimeSpan.FromSeconds(5));
                await ConnectToBrokerAsync();
            };
            mqttClient.ConnectedAsync += async e =>
            {
                this.Log(LogLevel.Info, "Connected to broker {0}", brokerUri);
                await SubToChannelAsync();
            };
            mqttClient.ApplicationMessageReceivedAsync += HandleRxAsync;
            Task.Run(async () =>
            {
                await ConnectToBrokerAsync();
                await HandleTxAsync();
            });
        }

        public void Reset()
        {
            // nothing to do
        }

        public event Action<CANMessageFrame> FrameSent;

        public void OnFrameReceived(CANMessageFrame message)
        {
            if (!txQueue.Writer.TryWrite(message))
                this.Log(LogLevel.Warning, "CAN frame dropped");
        }

        private async Task ConnectToBrokerAsync()
        {
            this.Log(LogLevel.Debug, "Connecting to broker {0}", brokerUri);
            var connectOptions = mqttFactory.CreateClientOptionsBuilder()
                .WithConnectionUri(brokerUri)
                .WithProtocolVersion(MqttProtocolVersion.V500)
                .WithCleanSession()
                .WithCleanStart()
                .Build();
            try
            {
                await mqttClient.ConnectAsync(connectOptions);
                this.Log(LogLevel.Debug, "Connected to broker {0}", brokerUri);
            }
            catch (Exception e)
            {
                this.Log(LogLevel.Error, "Failed to connect to broker: {0}", e.Message);
            }
        }

        private async Task SubToChannelAsync()
        {
            this.Log(LogLevel.Debug, "Subscribing to channel {0}", channel);
            var topicFilter = mqttFactory.CreateTopicFilterBuilder()
                .WithTopic(Topic + "#")
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtMostOnce)
                .WithNoLocal()
                .WithRetainHandling(MqttRetainHandling.DoNotSendOnSubscribe)
                .Build();
            var subOptions = mqttFactory.CreateSubscribeOptionsBuilder().WithTopicFilter(topicFilter).Build();
            try
            {
                await mqttClient.SubscribeAsync(subOptions);
                this.Log(LogLevel.Debug, "Subscribed to topic {0}", topicFilter.Topic);
            }
            catch (Exception e)
            {
                this.Log(LogLevel.Error, "Failed to subscribe to topic {0}: {1}", topicFilter.Topic, e.Message);
            }
        }

        private async Task HandleTxAsync()
        {
            while (true)
            {
                var frame = await txQueue.Reader.ReadAsync();
                try
                {
                    var canFrame = new CanFrame(frame);
                    var payload = coder.Encode(canFrame);
                    var message = new MqttApplicationMessageBuilder()
                        .WithTopic(Topic + canFrame.CobId)
                        .WithPayload(payload)
                        .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtMostOnce)
                        .Build();

                    await mqttClient.PublishAsync(message);
                }
                catch (Exception e)
                {
                    this.Log(LogLevel.Error, "Failed to publish message: {0}", e.Message);
                }
            }
        }

        private Task HandleRxAsync(MqttApplicationMessageReceivedEventArgs e)
        {
            try
            {
                var topic = e.ApplicationMessage.Topic;
                var msg = e.ApplicationMessage.PayloadSegment.ToArray();
                var canFrame = coder.Decode(msg);
                var expectedTopic = Topic + canFrame.CobId;
                if (topic == expectedTopic)
                    FrameSent?.Invoke(canFrame);
                else
                    this.Log(LogLevel.Warning, "Received from unexpected topic '{0}', expected '{1}'", topic, expectedTopic);
            }
            catch (Exception exception)
            {
                this.Log(LogLevel.Error, "Failed to decode msg can frame: {0}", exception.Message);
            }

            return Task.CompletedTask;
        }


        private string Topic => $"bus/can/{channel}/";

        private readonly Uri brokerUri;
        private readonly byte channel;
        private readonly ICanFrameCoder coder;
        private readonly MqttFactory mqttFactory = new MqttFactory();
        private readonly IMqttClient mqttClient;
        private readonly Channel<CANMessageFrame> txQueue;
    }
}