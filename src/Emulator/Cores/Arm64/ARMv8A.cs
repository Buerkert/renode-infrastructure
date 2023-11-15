//
// Copyright (c) 2010-2023 Antmicro
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure;
using Antmicro.Renode.Exceptions;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Timers;
using Antmicro.Renode.Peripherals.IRQControllers;
using Antmicro.Renode.Utilities.Binding;

using Endianess = ELFSharp.ELF.Endianess;

namespace Antmicro.Renode.Peripherals.CPU
{
    public partial class ARMv8A : TranslationCPU, IARMTwoSecurityStatesCPU, IPeripheralRegister<ARM_GenericTimer, NullRegistrationPoint>
    {
        public ARMv8A(IMachine machine, string cpuType, ARM_GenericInterruptController genericInterruptController, uint cpuId = 0, Endianess endianness = Endianess.LittleEndian)
                : base(cpuId, cpuType, machine, endianness, CpuBitness.Bits64)
        {
            Affinity = new Affinity(cpuId);
            gic = genericInterruptController;
            try
            {
                gic.AttachCPU(this);
            }
            catch(Exception e)
            {
                throw new ConstructionException($"Failed to attach CPU to Generic Interrupt Controller: {e.Message}", e);
            }
            Reset();
            HasSingleSecurityState = TlibHasEl3() == 0;
        }

        public void GetAtomicExceptionLevelAndSecurityState(out ExceptionLevel exceptionLevel, out SecurityState securityState)
        {
            lock(elAndSecurityLock)
            {
                exceptionLevel = this.exceptionLevel;
                securityState = this.securityState;
            }
        }

        public ulong GetSystemRegisterValue(string name)
        {
            ValidateSystemRegisterAccess(name, isWrite: false);

            return TlibGetSystemRegister(name, 1u /* log_unhandled_access: true */);
        }

        public void SetAvailableExceptionLevels(bool el2Enabled, bool el3Enabled)
        {
            if(started)
            {
                throw new RecoverableException("Available Exception Levels can only be set before starting the simulation.");
            }

            var returnValue = TlibSetAvailableEls(el2Enabled ? 1u : 0u, el3Enabled ? 1u : 0u);
            switch((SetAvailableElsReturnValue)returnValue)
            {
            case SetAvailableElsReturnValue.Success:
                HasSingleSecurityState = el3Enabled;
                return;
            case SetAvailableElsReturnValue.EL2OrEL3EnablingFailed:
                throw new RecoverableException($"The '{Model}' core doesn't support all the enabled Exception Levels.");
            // It should never be returned if 'started' is false.
            case SetAvailableElsReturnValue.SimulationAlreadyStarted:
            default:
                throw new ArgumentException("Invalid TlibSetAvailableEls return value!");
            }
        }

        public void SetSystemRegisterValue(string name, ulong value)
        {
            ValidateSystemRegisterAccess(name, isWrite: true);

            TlibSetSystemRegister(name, value, 1u /* log_unhandled_access: true */);
        }

        public void Register(ARM_GenericTimer peripheral, NullRegistrationPoint registrationPoint)
        {
            if(timer != null)
            {
                throw new RegistrationException("A generic timer is already registered.");
            }
            timer = peripheral;
            machine.RegisterAsAChildOf(this, peripheral, registrationPoint);
        }

        public void Unregister(ARM_GenericTimer peripheral)
        {
            timer = null;
            machine.UnregisterAsAChildOf(this, peripheral);
        }

        public override string Architecture { get { return "arm64"; } }

        public override string GDBArchitecture { get { return "aarch64"; } }

        public override List<GDBFeatureDescriptor> GDBFeatures
        {
            get
            {
                var features = new List<GDBFeatureDescriptor>();

                var coreFeature = new GDBFeatureDescriptor("org.gnu.gdb.aarch64.core");
                for(var index = 0u; index <= 30; index++)
                {
                    coreFeature.Registers.Add(new GDBRegisterDescriptor(index, 64, $"x{index}", "uint64", "general"));
                }
                coreFeature.Registers.Add(new GDBRegisterDescriptor((uint)ARMv8ARegisters.SP, 64, "sp", "data_ptr", "general"));
                coreFeature.Registers.Add(new GDBRegisterDescriptor((uint)ARMv8ARegisters.PC, 64, "pc", "code_ptr", "general"));
                // CPSR name is in line with GDB's 'G.5.1 AArch64 Features' manual page though it should be named PSTATE.
                coreFeature.Registers.Add(new GDBRegisterDescriptor((uint)ARMv8ARegisters.PSTATE, 32, "cpsr", "uint32", "general"));
                features.Add(coreFeature);

                var systemRegistersFeature = new GDBFeatureDescriptor("org.renode.gdb.aarch64.sysregs");
                foreach(var indexNamePair in SystemRegistersDictionary)
                {
                    systemRegistersFeature.Registers.Add(new GDBRegisterDescriptor(indexNamePair.Key, SystemRegistersWidth, indexNamePair.Value, "uint64"));
                }
                features.Add(systemRegistersFeature);

                /*
                 * TODO
                 * The ‘org.gnu.gdb.aarch64.fpu’ feature is optional. If present, it should contain registers ‘v0’ through ‘v31’, ‘fpsr’, and ‘fpcr’.
                 * The ‘org.gnu.gdb.aarch64.sve’ feature is optional. If present, it should contain registers ‘z0’ through ‘z31’, ‘p0’ through ‘p15’, ‘ffr’ and ‘vg’.
                 * The ‘org.gnu.gdb.aarch64.pauth’ feature is optional. If present, it should contain registers ‘pauth_dmask’ and ‘pauth_cmask’.
                 */

                return features;
            }
        }

        public ExceptionLevel ExceptionLevel
        {
            get
            {
                lock(elAndSecurityLock)
                {
                    return exceptionLevel;
                }
            }
            set => TlibSetCurrentEl((uint)value);
        }

        public SecurityState SecurityState
        {
            get
            {
                lock(elAndSecurityLock)
                {
                    return securityState;
                }
            }
        }

        public Affinity Affinity { get; }
        public bool IsEL3UsingAArch32State => false; // ARM8vA currently supports only AArch64 execution
        public bool HasSingleSecurityState { get; private set; }

        public event Action<ExceptionLevel, SecurityState> ExecutionModeChanged;

        protected override Interrupt DecodeInterrupt(int number)
        {
            switch((InterruptSignalType)number)
            {
                case InterruptSignalType.IRQ:
                    return Interrupt.Hard;
                case InterruptSignalType.FIQ:
                    return Interrupt.TargetExternal1;
                default:
                    throw InvalidInterruptNumberException;
            }
        }

        protected IEnumerable<CPURegister> GetNonMappedRegisters()
        {
            return SystemRegistersDictionary.Keys.Select(index => new CPURegister((int)index, SystemRegistersWidth, false, false));
        }

        [Export]
        protected ulong ReadSystemRegisterInterruptCPUInterface(uint offset)
        {
            return gic.ReadSystemRegisterCPUInterface(offset);
        }

        [Export]
        protected void WriteSystemRegisterInterruptCPUInterface(uint offset, ulong value)
        {
            gic.WriteSystemRegisterCPUInterface(offset, value);
        }


        [Export]
        protected uint ReadSystemRegisterGenericTimer32(uint offset)
        {
            this.Log(LogLevel.Error, "Reading 32-bit registers of the ARM Generic Timer is not allowed in 64bit version of the CPU");
            return 0;
        }

        [Export]
        protected void WriteSystemRegisterGenericTimer32(uint offset, uint value)
        {
            this.Log(LogLevel.Error, "Writing 32-bit registers of the ARM Generic Timer is not allowed in 64bit version of the CPU");
            return;

        }

        [Export]
        protected ulong ReadSystemRegisterGenericTimer64(uint offset)
        {
            if(timer == null)
            {
                this.Log(LogLevel.Error, "Trying to read a register of the ARM Generic Timer, but the timer was not found.");
                return 0;
            }
            return timer.ReadRegisterAArch64(offset);
        }

        [Export]
        protected void WriteSystemRegisterGenericTimer64(uint offset, ulong value)
        {
            if(timer == null)
            {
                this.Log(LogLevel.Error, "Trying to write a register of the ARM Generic Timer, but the timer was not found.");
                return;
            }
            timer.WriteRegisterAArch64(offset, value);
        }

        protected bool TryGetNonMappedRegister(int index, out RegisterValue value)
        {
            // This method will be mostly used by GDB so let's prevent unhandled access logs.
            // Otherwise, 'info all-registers' generates a lot of warnings.
            var result = TryGetSystemRegisterValue((uint)index, out var ulongValue, logUnhandledAccess: false);

            value = RegisterValue.Create(ulongValue, SystemRegistersWidth);
            return result;
        }

        protected bool TrySetNonMappedRegister(int index, RegisterValue value)
        {
            if(SystemRegistersDictionary.TryGetValue((uint)index, out var name))
            {
                // ValidateSystemRegisterAccess isn't used because most of it's checks aren't needed.
                // The register must exist at this point cause it's in the dictionary built based on tlib
                // and we don't really care about the invalid access type error for unwritable registers.
                TlibSetSystemRegister(name, value, 1u /* log_unhandled_access: true */);
                return true;
            }
            return false;
        }

        [Export]
        private void OnExecutionModeChanged(uint el, uint isSecure)
        {
            lock(elAndSecurityLock)
            {
                exceptionLevel = (ExceptionLevel)el;
                securityState = isSecure != 0 ? SecurityState.Secure : SecurityState.NonSecure;
            }
            ExecutionModeChanged?.Invoke(ExceptionLevel, SecurityState);
        }

        private bool TryGetSystemRegisterValue(uint index, out ulong value, bool logUnhandledAccess)
        {
            if(SystemRegistersDictionary.TryGetValue(index, out var name))
            {
                // ValidateSystemRegisterAccess isn't used because most of it's checks aren't needed.
                // The register must exist at this point cause it's in the dictionary built based on tlib
                // and we don't really care about the invalid access type error for unreadable registers.
                value = TlibGetSystemRegister(name, logUnhandledAccess ? 1u : 0u);
                return true;
            }
            value = 0;
            return false;
        }

        private void ValidateSystemRegisterAccess(string name, bool isWrite)
        {
            switch((SystemRegisterCheckReturnValue)TlibCheckSystemRegisterAccess(name, isWrite ? 1u : 0u))
            {
            case SystemRegisterCheckReturnValue.AccessValid:
                return;
            case SystemRegisterCheckReturnValue.AccessorNotFound:
                var accessName = isWrite ? "Writing" : "Reading";
                throw new RecoverableException($"{accessName} the {name} register isn't supported.");
            case SystemRegisterCheckReturnValue.RegisterNotFound:
                throw new RecoverableException($"No such register: {name}.");
            default:
                throw new ArgumentException("Invalid TlibCheckSystemRegisterAccess return value!");
            }
        }

        private Dictionary<uint, string> SystemRegistersDictionary
        {
            get
            {
                if(systemRegisters == null)
                {
                    systemRegisters = new Dictionary<uint, string>();

                    var array = IntPtr.Zero;
                    var arrayPointer = Marshal.AllocHGlobal(IntPtr.Size);
                    try
                    {
                        var count = TlibCreateSystemRegisterNamesArray(arrayPointer);
                        if(count == 0)
                        {
                            return systemRegisters;
                        }
                        array = Marshal.ReadIntPtr(arrayPointer);

                        var namePointersArray = new IntPtr[count];
                        Marshal.Copy(array, namePointersArray, 0, (int)count);

                        var lastRegisterIndex = Enum.GetValues(typeof(ARMv8ARegisters)).Cast<uint>().Max();
                        systemRegisters = namePointersArray.Select(namePointer => Marshal.PtrToStringAnsi(namePointer))
                            .OrderBy(name => name)
                            .ToDictionary(_ => ++lastRegisterIndex);
                    }
                    finally
                    {
                        if(array != IntPtr.Zero)
                        {
                            Free(array);
                        }
                        Marshal.FreeHGlobal(arrayPointer);
                    }
                }
                return systemRegisters;
            }
        }

        private ExceptionLevel exceptionLevel;
        private SecurityState securityState;
        private Dictionary<uint, string> systemRegisters;
        private ARM_GenericTimer timer;

        private readonly object elAndSecurityLock = new object();
        private readonly ARM_GenericInterruptController gic;

        private const int SystemRegistersWidth = 64;

        // These '*ReturnValue' enums have to be in sync with their counterparts in 'tlib/arch/arm64/arch_exports.c'.
        private enum SetAvailableElsReturnValue
        {
            SimulationAlreadyStarted = 1,
            EL2OrEL3EnablingFailed = 2,
            Success = 3,
        }

        private enum SystemRegisterCheckReturnValue
        {
            RegisterNotFound = 1,
            AccessorNotFound = 2,
            AccessValid = 3,
        }

#pragma warning disable 649
        [Import]
        private FuncUInt32StringUInt32 TlibCheckSystemRegisterAccess;

        [Import]
        private FuncUInt32IntPtr TlibCreateSystemRegisterNamesArray;

        [Import]
        // The arguments are: char *name, bool log_unhandled_access.
        private FuncUInt64StringUInt32 TlibGetSystemRegister;

        [Import]
        private FuncUInt32 TlibHasEl3;

        [Import]
        private FuncUInt32UInt32UInt32 TlibSetAvailableEls;

        [Import]
        private ActionUInt32 TlibSetCurrentEl;

        [Import]
        // The arguments are: char *name, uint64_t value, bool log_unhandled_access.
        private ActionStringUInt64UInt32 TlibSetSystemRegister;
#pragma warning restore 649
    }
}
