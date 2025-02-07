//
// Copyright (c) 2025 Burkert
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System;
using Antmicro.Renode.Plugins;
using Antmicro.Renode.UserInterface;

namespace Antmicro.Renode.Plugins.MqttCanBridgePlugin
{
    [Plugin(Name = "MQTT Can Bridge Plugin", Version = "0.1", Description = "Enables bridging of CAN interfaces over a MQTT broker.", Vendor = "Burkert")]
    public sealed class MqttCanBridgePlugin : IDisposable
    {
        public MqttCanBridgePlugin(Monitor monitor)
        {
        }

        public void Dispose()
        {
        }
    }
}