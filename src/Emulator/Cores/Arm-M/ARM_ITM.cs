using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Exceptions;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.CPU;
using Antmicro.Renode.Utilities;
using ELFSharp.ELF;

namespace Antmicro.Renode.Peripherals.UART
{
    /// <summary>
    /// Implementation of ARM ITM (Instrumentation Trace Macrocell), which is used by devices for SWO logging.
    /// Other features like integration with DWT (Data Watchpoint and Trace) and TPIU (Trace Port Interface Unit) are not implemented.
    /// To see the output, register a <see cref="VirtualConsole"/> at the desired index (0-31) and attach an analyzer to it.
    /// </summary>
    /// <code>
    /// -- repl file
    /// itm: UART.ARM_ITM @ sysbus 0xE0000000
    /// swo0: UART.VirtualConsole @ itm 0
    ///
    /// -- resc file
    /// showAnalyzer itm.swo0
    /// </code>
    [AllowedTranslations(AllowedTranslation.ByteToDoubleWord | AllowedTranslation.WordToDoubleWord)]
    public class ARM_ITM : SimpleContainer<VirtualConsole>, IDoubleWordPeripheral, IKnownSize
    {
        /// <summary>
        /// Creates a new instance of ARM ITM.
        /// When also providing the CPU, the 'ITM Trace Privilege Register' will be checked when writing to stimulus registers.
        /// </summary>
        /// <param name="machine">The machine this peripheral is part of.</param>
        /// <param name="cpu">The CPU that will be used to check the 'ITM Trace Privilege Register' when writing to stimulus registers.</param>
        public ARM_ITM(IMachine machine, Arm cpu = null) : base(machine)
        {
            this.cpu = cpu;
            registers = DefineRegisters();
        }

        public override void Register(VirtualConsole peripheral, NumberRegistrationPoint<int> registrationPoint)
        {
            if (registrationPoint.Address > 31)
                throw new RegistrationException($"Trying to register console for port {registrationPoint.Address} which is larger than 31");

            base.Register(peripheral, registrationPoint);
        }

        public override void Reset()
        {
            registers.Reset();
        }

        public uint ReadDoubleWord(long offset)
        {
            return registers.Read(offset);
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            if (!CpuPrivileged && offset > (long)Registers.TraceEnable)
                this.Log(LogLevel.Warning, "Trying to write to control register 0x{0:X} while not in privileged mode", offset);
            else if (AccessLocked && offset >= (long)Registers.TraceEnable && offset != (long)Registers.LockAccess)
                this.Log(LogLevel.Warning, "Trying to write to control register 0x{0:X} while access is locked", offset);
            else if (!itmEnable.Value && offset <= (long)Registers.TraceEnable)
                this.Log(LogLevel.Warning, "Trying to write to register 0x{0:X} while ITM is disabled", offset);
            else
                registers.Write(offset, value);
        }

        public long Size => 0x1000;

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

            Registers.TraceEnable.Define(regs).WithValueField(0, 32,
                valueProviderCallback: _ => BitHelper.GetValueFromBitsArray(traceEnable),
                writeCallback: (_, val) =>
                {
                    var newEnable = BitHelper.GetBits(val);
                    var privileged = CpuPrivileged;
                    // loop over all bit and check if change is privileged
                    for (var i = 0; traceEnable.Length > i; i++)
                    {
                        if (newEnable[i] != traceEnable[i] && !privileged && tracePrivilege[i / 8].Value)
                            this.Log(LogLevel.Warning, "Trying to change trace enable for stimulus {0} while not in privileged mode", i);
                        else
                            traceEnable[i] = newEnable[i];
                    }
                }
            );
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
            if (!CpuPrivileged && tracePrivilege[index / 8].Value)
            {
                this.Log(LogLevel.Warning, "Trying to write to privileged stimulus {0} while not in privileged mode", index);
                return;
            }

            if (!traceEnable[index])
            {
                this.Log(LogLevel.Warning, "Trying to write to stimulus {0} while it is disabled", index);
                return;
            }

            if (!TryGetByAddress(index, out var console))
            {
                this.Log(LogLevel.Warning, "Device wrote 0x{0:X} to stimulus {1} but there is no console registered for this index", value, index);
                return;
            }

            console.DisplayChar(value);
        }

        private byte ReadStimulus(int index)
        {
            var fifoReady = itmEnable.Value && traceEnable[index];
            return (byte)(fifoReady ? 1 : 0);
        }

        private bool AccessLocked => lockAccess.Value != 0xC5ACCE55;
        private bool CpuPrivileged => (cpu?.CPSR.GetBytes(Endianess.LittleEndian)[0] & 1) != 1;

        private readonly Arm cpu;
        private readonly DoubleWordRegisterCollection registers;
        private readonly bool[] traceEnable = new bool[32];
        private IFlagRegisterField[] tracePrivilege;
        private IFlagRegisterField itmEnable;
        private IValueRegisterField lockAccess;

        // There are way more registers, but they aren't implemented by all cores.
        // And there doesn't seem to be a doc of all possible registers independent of the core.
        private enum Registers
        {
            Stimulus0 = 0x000,
            Stimulus31 = 0x07C,
            TraceEnable = 0xE00,
            TracePrivilege = 0xE40,
            TraceControl = 0xE80,
            LockAccess = 0xFB0,
        }
    }
}