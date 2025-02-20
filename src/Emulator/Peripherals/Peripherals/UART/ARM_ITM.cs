using System;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Utilities;

namespace Antmicro.Renode.Peripherals.UART
{
    [AllowedTranslations(AllowedTranslation.ByteToDoubleWord | AllowedTranslation.WordToDoubleWord)]
    public class ARM_ITM : IUART, IDoubleWordPeripheral, IKnownSize
    {
        public ARM_ITM()
        {
            registers = DefineRegisters();
        }

        public void Reset()
        {
            registers.Reset();
        }

        public uint ReadDoubleWord(long offset)
        {
            return registers.Read(offset);
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            if (AccessLocked && offset >= (long)Registers.TraceEnable && offset != (long)Registers.LockAccess)
                this.Log(LogLevel.Warning, "Trying to write to control register 0x{0:X} while access is locked", offset);
            else if (!itmEnable.Value && offset <= (long)Registers.TraceEnable)
                this.Log(LogLevel.Warning, "Trying to write to register 0x{0:X} while ITM is disabled", offset);
            else
                registers.Write(offset, value);
        }

        public void WriteChar(byte value)
        {
            this.Log(LogLevel.Error, "Writing to ARM ITM is not supported");
        }

        public long Size => 0x1000;
        public event Action<byte> CharReceived;
        public uint BaudRate { get; }
        public Bits StopBits { get; }
        public Parity ParityBit { get; }
        
        private DoubleWordRegisterCollection DefineRegisters()
        {
            var regs = new DoubleWordRegisterCollection(this);

            for (var offset = (long)Registers.Stimulus0; offset <= (long)Registers.Stimulus31; offset += 4)
            {
                var index = (int)((offset - (long)Registers.Stimulus0) / 4);
                regs.DefineRegister(offset)
                    .WithValueField(0, 8, name: "STIM[0:7]",
                        writeCallback: (_, val) => WriteStimulus(index, (byte)val),
                        valueProviderCallback: _ => ReadStimulus(index)
                    )
                    .WithTag("STIM[8:31]", 8, 24); // for now, we only support for the first 8 bits to be written
            }

            Registers.TraceEnable.Define(regs).WithValueField(0, 32, out traceEnable);
            Registers.TracePrivilege.Define(regs)
                .WithFlags(0, 4, out tracePrivilege)
                .WithReservedBits(4, 28);
            Registers.TraceControl.Define(regs)
                .WithFlag(0, out itmEnable, name: "ITMENA")
                .WithTaggedFlag("TSENA", 1)
                .WithTaggedFlag("SYNCENA", 2)
                .WithTaggedFlag("DWTENA", 3)
                .WithTaggedFlag("SWOENA", 4)
                .WithReservedBits(5, 3)
                .WithTag("TSPrescale", 8, 2)
                .WithReservedBits(10, 6)
                .WithTag("ATBID", 16, 7)
                .WithFlag(23, name: "BUSY", valueProviderCallback: _ => false)
                .WithReservedBits(24, 8);
            Registers.LockAccess.Define(regs).WithValueField(0, 32, out lockAccess);
            return regs;
        }

        private void WriteStimulus(int index, byte value)
        {
            if (!BitHelper.IsBitSet(traceEnable.Value, (byte)index))
            {
                this.Log(LogLevel.Warning, "Trying to write to stimulus {0} while it is disabled", index);
                return;
            }

            CharReceived?.Invoke(value);
        }

        private byte ReadStimulus(int index)
        {
            var fifoReady = itmEnable.Value && BitHelper.IsBitSet(traceEnable.Value, (byte)index);
            return (byte)(fifoReady ? 1 : 0);
        }

        private bool AccessLocked => lockAccess.Value != 0xC5ACCE55;

        private readonly DoubleWordRegisterCollection registers;
        private IValueRegisterField traceEnable;
        private IFlagRegisterField[] tracePrivilege;
        private IFlagRegisterField itmEnable;
        private IValueRegisterField lockAccess;

        private enum Registers
        {
            Stimulus0 = 0x000,

            // ...
            Stimulus31 = 0x07C,
            TraceEnable = 0xE00,
            TracePrivilege = 0xE40,
            TraceControl = 0xE80,

            // There are more registers but they are not implemented
            LockAccess = 0xFB0,
        }
    }
}