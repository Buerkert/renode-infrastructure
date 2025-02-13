using System;
using System.Threading.Channels;
using System.Threading.Tasks;
using Antmicro.Renode.Core.CAN;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.CAN;
using Antmicro.Renode.Plugins.MqttCanBridgePlugin.CanFrameCoders;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Formatter;
using MQTTnet.Protocol;

namespace Antmicro.Renode.Plugins.MqttCanBridgePlugin
{
    public static class DateTimeOffsetExtensions
    {
        private static readonly DateTimeOffset UnixEpoch = new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero);
        private const long TicksPerMicrosecond = TimeSpan.TicksPerMillisecond / 1000;

        public static long ToUnixTimeMicroseconds(this DateTimeOffset timestamp)
        {
            var duration = timestamp - UnixEpoch;
            return duration.Ticks / TicksPerMicrosecond;
        }
    }

    /// <summary>
    /// Connects to a MQTT broker to send and receive CAN frames which are encoded and decoded using a specified coder.
    /// The topic of the published can frame depends on the channel and the cobId of the frame: bus/can/{channel}/{cobId}.
    /// To only receive the published frames of others and not one's own the non-local option of MQTTv5 is used.
    /// Therefore, a broker that supports MQTTv5 is required.
    /// </summary>
    public class MqttCanBridge : ICAN
    {
        /// <summary>
        /// Creates a new instance of the MQTT CAN bridge.
        /// </summary>
        /// <param name="brokerUri">The URI of the MQTT broker to connect to. E.g.: "mqtt://localhost:1883".</param>
        /// <param name="channel">The channel of the CAN bus to use.</param>
        /// <param name="coder">The coder to use for encoding and decoding CAN frames.</param>
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
                .WithProtocolVersion(MqttProtocolVersion.V500) // use MQTTv5 since we need the non-local option
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
                .WithTopic(Topic + "#") // subscribe to all messages on given channel since we want to bridge the whole network
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtMostOnce) // use QoS 0 to keep messages as fast as possible
                .WithNoLocal() // do not receive own messages
                .WithRetainHandling(MqttRetainHandling.DoNotSendOnSubscribe) // there shouldn't be any retained messages but just in case
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
                    if (coder.SupportsOptionalField(ICanFrameCoder.OptionalFields.PubId))
                        canFrame.PubId = pubId;
                    if (coder.SupportsOptionalField(ICanFrameCoder.OptionalFields.PubCnt))
                        canFrame.PubCnt = pubCnt;
                    if (coder.SupportsOptionalField(ICanFrameCoder.OptionalFields.TimeStamp))
                        canFrame.TimeStamp = (ulong)DateTimeOffset.Now.ToUnixTimeMicroseconds();

                    var payload = coder.Encode(canFrame);
                    var message = new MqttApplicationMessageBuilder()
                        .WithTopic(Topic + canFrame.CobId) // publish frame to topic <channel-topic>/{cobId} so other consumers can filter by cobId
                        .WithPayload(payload)
                        .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtMostOnce) // use QoS 0 to keep messages as fast as possible
                        .Build();
                    await mqttClient.PublishAsync(message);
                    pubCnt++;
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
                if (topic != expectedTopic)
                    // this might happen if a member does not use the correct topic encoding
                    this.Log(LogLevel.Warning, "Received from unexpected topic '{0}', expected '{1}'", topic, expectedTopic);
                else if (canFrame.PubId == pubId)
                    // this should not happen since we use the non-local option, but just in case
                    this.Log(LogLevel.Warning, "Received own message, ignoring");
                else
                    FrameSent?.Invoke(canFrame);
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
        private readonly uint pubId = (uint)Random.Shared.Next();
        private uint pubCnt;
        private readonly ICanFrameCoder coder;
        private readonly MqttFactory mqttFactory = new MqttFactory();
        private readonly IMqttClient mqttClient;
        private readonly Channel<CANMessageFrame> txQueue;
    }
}