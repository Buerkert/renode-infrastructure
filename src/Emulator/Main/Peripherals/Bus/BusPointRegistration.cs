//
// Copyright (c) 2010-2024 Antmicro
// Copyright (c) 2011-2015 Realtime Embedded
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System;
using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure;
using Antmicro.Renode.Peripherals.CPU;

namespace Antmicro.Renode.Peripherals.Bus
{
    public class BusPointRegistration : BusRegistration
    {
        public BusPointRegistration(ulong address, ulong offset = 0, IPeripheral cpu = null, ICluster<ICPU> cluster = null) : this(address, stateMask: null, offset, cpu, cluster)
        {
        }

        public BusPointRegistration(ulong address, string condition, ulong offset = 0) : this(address, stateMask: null, offset, condition: condition)
        {
        }

        public override string ToString()
        {
            var result = $"0x{StartingPoint:X}";
            if(Offset != 0)
            {
                result += $" with offset 0x{Offset:X}";
            }
            if(CPU != null)
            {
                result += $" for core {CPU}";
            }
            return result;
        }

        public override string PrettyString
        {
            get
            {
                return ToString();
            }
        }

        public static implicit operator BusPointRegistration(ulong address)
        {
            return new BusPointRegistration(address);
        }

        public override IConditionalRegistration WithInitiatorAndStateMask(IPeripheral initiator, StateMask mask)
        {
            return new BusPointRegistration(StartingPoint, mask, Offset, initiator);
        }

        public void RegisterForEachContext(Action<BusPointRegistration> register)
        {
            RegisterForEachContextInner(register, cpu => new BusPointRegistration(StartingPoint, Offset, cpu));
        }

        public BusRangeRegistration ToRangeRegistration(ulong size)
        {
            BusRangeRegistration result;
            if(Condition != null)
            {
                result = new BusRangeRegistration(StartingPoint, size, Condition, Offset);
            }
            else
            {
                result = new BusRangeRegistration(StartingPoint, size, Offset, CPU, Cluster);
            }
            if(StateMask.HasValue)
            {
                return (BusRangeRegistration)result.WithInitiatorAndStateMask(CPU, StateMask.Value);
            }
            return result;
        }

        private BusPointRegistration(ulong address, StateMask? stateMask, ulong offset = 0, IPeripheral cpu = null, ICluster<ICPU> cluster = null, string condition = null) : base(address, offset, cpu, cluster, stateMask, condition)
        {
        }
    }
}

