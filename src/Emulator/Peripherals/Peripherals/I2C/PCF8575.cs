using System;
using System.Linq;
using System.Collections.Generic;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Core;

namespace Antmicro.Renode.Peripherals.I2C
{
    public class PCF8575 : II2CPeripheral, INumberedGPIOOutput, IGPIOReceiver
    {
        public PCF8575()
        {
            outputs = Enumerable.Range(0, 16).Select(x => new GPIO()).ToList();
            inputs = Enumerable.Range(0, 16).Select(x => false).ToList();
        }

        public void Reset()
        {
            state = State.Closed;
            port = 0;
            outputs.ForEach(x => x.Unset());
            inputs = Enumerable.Range(0, 16).Select(x => false).ToList();
        }

        public void OnGPIO(int number, bool value)
        {
            if (number >= inputs.Count)
            {
                this.Log(LogLevel.Error, "OnGPIO: pin {0} does not exist", number);
                return;
            }

            this.Log(LogLevel.Debug, "OnGPIO: pin {0} set to {1}", number, value);
            inputs[number] = value;
        }

        public void Write(byte[] data)
        {
            CheckState(State.Writing);
            foreach (var b in data) WriteByte(b);
        }

        public byte[] Read(int count = 1)
        {
            CheckState(State.Reading);
            var result = new byte[count];
            for (var i = 0; i < count; i++) result[i] = ReadByte();
            return result;
        }

        public void FinishTransmission()
        {
            CheckState(State.Closed);
            this.Log(LogLevel.Debug, "FinishTransmission");
            port = 0;
        }

        public IReadOnlyDictionary<int, IGPIO> Connections
        {
            get
            {
                var i = 0;
                return outputs.ToDictionary(_ => i++, x => (IGPIO)x);
            }
        }

        public List<bool> Inputs => new List<bool>(inputs);
        public List<bool> Outputs => outputs.Select(x => x.IsSet).ToList();

        private void CheckState(State newState)
        {
            if (newState == state) return;
            if (newState != State.Closed)
            {
                this.Log(LogLevel.Warning, "Unexpected state transition from {0} to {1} without finishing the previous operation", state, newState);
                port = 0;
            }

            state = newState;
        }

        private void WriteByte(byte value)
        {
            this.Log(LogLevel.Debug, "Setting port {0}: {1}", port, Convert.ToString(value, 2).PadLeft(8, '0'));
            var start = port * 8;
            foreach (var i in Enumerable.Range(start, 8))
            {
                outputs[i].Set((value & (1 << (i - start))) != 0);
            }

            port = (byte)((port + 1) % 2);
        }

        private byte ReadByte()
        {
            var value = 0;
            var start = port * 8;
            foreach (var i in Enumerable.Range(start, 8))
            {
                // TODO: should the current output state be taken into account,
                //       meaning when the corresponding output is 0 aka pulling to ground should the input read 0 regardless of the actual input state?
                if (inputs[i]) value |= 1 << (i - start);
            }

            this.Log(LogLevel.Debug, "Reading port {0}: {1}", port, Convert.ToString(value, 2).PadLeft(8, '0'));
            port = (byte)((port + 1) % 2);
            return (byte)value;
        }

        private State state = State.Closed;
        private byte port;
        private readonly List<GPIO> outputs;
        private List<bool> inputs;

        private enum State
        {
            Closed,
            Reading,
            Writing
        }
    }
}