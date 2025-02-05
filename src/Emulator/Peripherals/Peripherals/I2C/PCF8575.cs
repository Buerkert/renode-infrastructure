using System;
using System.Linq;
using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.GPIOPort;

namespace Antmicro.Renode.Peripherals.I2C
{
    public class PCF8575 : BaseGPIOPort, II2CPeripheral
    {
        public PCF8575(IMachine machine) : base(machine, 16)
        {
        }

        public override void Reset()
        {
            base.Reset();
            commState = CommState.Closed;
            port = 0;
        }

        public void Write(byte[] data)
        {
            CheckCommState(CommState.Writing);
            foreach (var b in data) WriteByte(b);
        }

        public byte[] Read(int count = 1)
        {
            CheckCommState(CommState.Reading);
            var result = new byte[count];
            for (var i = 0; i < count; i++) result[i] = ReadByte();
            return result;
        }

        public void FinishTransmission()
        {
            CheckCommState(CommState.Closed);
            this.Log(LogLevel.Debug, "FinishTransmission");
            port = 0;
        }

        private void CheckCommState(CommState newCommState)
        {
            if (newCommState == commState) return;
            if (newCommState != CommState.Closed && commState != CommState.Closed)
            {
                this.Log(LogLevel.Warning, "Unexpected state transition from {0} to {1} without finishing the previous operation", commState, newCommState);
                port = 0;
            }

            commState = newCommState;
        }

        private void WriteByte(byte value)
        {
            this.Log(LogLevel.Debug, "Setting port {0}: {1}", port, Convert.ToString(value, 2).PadLeft(8, '0'));
            var start = port * 8;
            foreach (var i in Enumerable.Range(start, 8))
            {
                Connections[i].Set((value & (1 << (i - start))) != 0);
            }

            port = (byte)((port + 1) % 2);
        }

        private byte ReadByte()
        {
            var value = 0;
            var start = port * 8;
            foreach (var i in Enumerable.Range(start, 8))
            {
                // when the pin is configured as an output aka. !Connection[i].IsSet it will always return false,
                // only when the pin is configured as an input and the corresponding input is set it will return true
                if (Connections[i].IsSet && State[i])
                    value |= 1 << (i - start);
            }

            this.Log(LogLevel.Debug, "Reading port {0}: {1}", port, Convert.ToString(value, 2).PadLeft(8, '0'));
            port = (byte)((port + 1) % 2);
            return (byte)value;
        }

        private CommState commState = CommState.Closed;
        private byte port;

        private enum CommState
        {
            Closed,
            Reading,
            Writing
        }
    }
}