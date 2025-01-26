// Derived from 1.15.0 STM32F4_I2C.cs
//
// - Fix required for interrupt state update to avoid ISR re-entrancy
//   when I2C_CR2[10:9] updated to disable events.
//
// - Updated to add DMA support.
//
// - Updated to fix multi-byte RX transfers
//
// Modifications Copyright (c) 2023-2024 eCosCentric Ltd
// Original assignment:
//
// Copyright (c) 2010-2023 Antmicro
// Copyright (c) 2011-2015 Realtime Embedded
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System;
using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;
using System.Collections.Generic;
using System.Linq;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Utilities;

namespace Antmicro.Renode.Peripherals.I2C
{
    [AllowedTranslations(AllowedTranslation.WordToDoubleWord)]
    public sealed class STM32F4_I2C : SimpleContainer<II2CPeripheral>, IDoubleWordPeripheral, IBytePeripheral,
        IKnownSize
    {
        public STM32F4_I2C(IMachine machine) : base(machine)
        {
            EventInterrupt = new GPIO();
            ErrorInterrupt = new GPIO();
            DMATransmit = new GPIO();
            DMAReceive = new GPIO();
            CreateRegisters();
            Reset();
        }

        public byte ReadByte(long offset)
        {
            if ((Registers)offset == Registers.Data)
            {
                this.Log(LogLevel.Debug,
                    "ReadByte: I2C_DR: entry: state {0} byteTransferFinished {1} acknowledgeEnable {2} willReadOnSelectedSlave {3} transmitterReceiver {4}",
                    state, byteTransferFinished.Value, acknowledgeEnable.Value, willReadOnSelectedSlave,
                    transmitterReceiver.Value);

                if (!byteTransferFinished.Value)
                {
                    if (null != selectedSlave)
                    {
                        this.Log(LogLevel.Debug, "ReadByte: ROSS: existing dataToReceive {0}", dataToReceive);
                        if (dataToReceive != null && dataToReceive.Any())
                        {
                            this.Log(LogLevel.Debug, "ReadByte:!BTF: data already pending: dataToReceive.Count {0}",
                                dataToReceive.Count);
                        }
                        else
                        {
                            dataToReceive = new Queue<byte>(selectedSlave.Read());
                            this.Log(LogLevel.Debug, "ReadByte:!BTF: ROSS dataToReceive.Count {0}",
                                dataToReceive.Count);
                        }
                    }
                }

                byteTransferFinished.Value = false;
                this.Log(LogLevel.Debug, "ReadByte: I2C_DR: byteTransferFinished set {0}", byteTransferFinished.Value);
                Update();

                byte rval = (byte)data.Read();

                this.Log(LogLevel.Debug, "ReadByte: after data.Read: dataRegisterNotEmpty {0}",
                    dataRegisterNotEmpty.Value);

                // We  rely on state being updated when the NAK/STOP processing is triggered
                if (state == State.ReceivingData)
                {
                    if (null != selectedSlave)
                    {
                        this.Log(LogLevel.Debug, "ReadByte: ROSS: existing dataToReceive {0}", dataToReceive);
                        dataToReceive = new Queue<byte>(selectedSlave.Read());
                        byteTransferFinished.Value = true;

                        DMAReceive.Unset();
                        machine.LocalTimeSource.ExecuteInNearestSyncedState(_ => DMAReceive.Set());

                        this.Log(LogLevel.Debug, "ReadByte: after ROSS: dataToReceive {0} dataRegisterNotEmpty {1}",
                            dataToReceive, dataRegisterNotEmpty.Value);

                        Update();
                    }
                }

                this.Log(LogLevel.Debug, "ReadByte: offset 0x{0:X} : rval 0x{1:X}", offset, rval);
                return rval;
            }
            else
            {
                this.LogUnhandledRead(offset);
                return 0;
            }
        }

        public void WriteByte(long offset, byte value)
        {
            this.Log(LogLevel.Debug, "WriteByte: offset 0x{0:X} value 0x{1:X} : dmaEnable {2} state {3}", offset, value,
                dmaEnable.Value, state);
            if ((Registers)offset == Registers.Data)
            {
                // dataRegisterEmpty reflects I2C_SR1:TxE state
                this.Log(LogLevel.Debug, "WriteByte:I2C_DR: dataRegisterEmpty {0} dataRegisterNotEmpty {1}",
                    dataRegisterEmpty.Value, dataRegisterNotEmpty.Value);
                data.Write(offset, value);
            }
            else
            {
                this.LogUnhandledWrite(offset, value);
            }
        }

        public uint ReadDoubleWord(long offset)
        {
            //this.Log(LogLevel.Debug, "ReadDoubleWord: offset 0x{0:X}", offset);
            //return registers.Read(offset);
            uint rval = registers.Read(offset);
            this.Log(LogLevel.Debug, "ReadDoubleWord: offset 0x{0:X} : rval 0x{1:X}", offset, rval);
            return rval;
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            this.Log(LogLevel.Debug, "WriteDoubleWord: offset 0x{0:X} value 0x{1:X}", offset, value);
            registers.Write(offset, value);
        }

        public override void Reset()
        {
            state = State.Idle;
            EventInterrupt.Unset();
            ErrorInterrupt.Unset();
            DMATransmit.Unset();
            DMAReceive.Unset();

            registers.Reset();
            data.Reset();
        }

        public GPIO EventInterrupt { get; private set; }

        public GPIO ErrorInterrupt { get; private set; }

        public GPIO DMAReceive { get; }

        public GPIO DMATransmit { get; }

        public long Size
        {
            get { return 0x400; }
        }

        private void CreateRegisters()
        {
            registers = new DoubleWordRegisterCollection(this);

            Registers.Control1.Define(registers)
                .WithFlag(0, name: "PE", writeCallback: PeripheralEnableWrite)
                .WithTaggedFlag("SMBUS", 1)
                .WithReservedBits(2, 1)
                .WithTaggedFlag("SMBTYPE", 3)
                .WithTaggedFlag("ENARP", 4)
                .WithTaggedFlag("ENPEC", 5)
                .WithTaggedFlag("ENGC", 6)
                .WithTaggedFlag("NOSTRETCH", 7)
                .WithFlag(8, FieldMode.Read, name: "START", writeCallback: StartWrite)
                .WithFlag(9, FieldMode.Read, name: "STOP", writeCallback: StopWrite)
                .WithFlag(10, out acknowledgeEnable, name: "ACK")
                // CONSIDER: support for I2C_CR1:POS (bit11) // POS only relevant for 2-byte reception
                // The I2C_CR1:POS setting indicates whether CURRENT byte being received or NEXT byte being received
                .WithTaggedFlag("POS", 11)
                .WithTaggedFlag("PEC", 12)
                .WithTaggedFlag("ALERT", 13)
                .WithReservedBits(14, 1)
                .WithTaggedFlag("SWRST", 15);

            Registers.Control2.Define(registers)
                .WithValueField(0, 6, name: "FREQ")
                .WithReservedBits(6, 2)
                .WithFlag(8, out errorInterruptEnable, name: "ITERREN")
                .WithFlag(9, out eventInterruptEnable, name: "ITEVTEN", changeCallback: InterruptEnableChange)
                .WithFlag(10, out bufferInterruptEnable, name: "ITBUFEN", changeCallback: InterruptEnableChange)
                // 0 == DMA requests disabled; 1 == DMA request enabled when TxE==1 or RxNE==1
                .WithFlag(11, out dmaEnable, name: "DMAEN", changeCallback: DMAEnableChange)
                // 0 == Next DMA EOT is NOT last transfer; 1 == Next DMA EOT is the last transfer
                .WithFlag(12, out dmaLastTransfer, name: "DMALAST")
                .WithReservedBits(13, 3);

            Registers.OwnAddress1.Define(registers)
                .WithTaggedFlag("ADD0", 0)
                .WithTag("ADD[7:1]", 1, 7)
                .WithTag("ADD[9:8]", 8, 2)
                .WithReservedBits(10, 5)
                .WithTaggedFlag("ADDMODE", 15);

            Registers.OwnAddress2.Define(registers)
                .WithTaggedFlag("ENDUAL", 0)
                .WithTag("ADD2[7:1]", 1, 7)
                .WithReservedBits(8, 8);

            data = Registers.Data.Define(registers)
                .WithValueField(0, 8, name: "DR", valueProviderCallback: (prevVal) => DataRead((uint)prevVal),
                    writeCallback: (prevVal, val) => DataWrite((uint)prevVal, (uint)val))
                .WithReservedBits(8, 8);

            Registers.Status1.Define(registers)
                .WithFlag(0, out startBit, FieldMode.Read, name: "SB")
                .WithFlag(1, out addressSentOrMatched, FieldMode.Read, name: "ADDR")
                .WithFlag(2, out byteTransferFinished, FieldMode.Read, name: "BTF")
                .WithTaggedFlag("ADD10", 3)
                .WithTaggedFlag("STOPF", 4)
                .WithReservedBits(5, 1)
                .WithFlag(6, out dataRegisterNotEmpty, FieldMode.Read, name: "RxNE",
                    valueProviderCallback: _ => dataToReceive?.Any() ?? false)
                .WithFlag(7, out dataRegisterEmpty, FieldMode.Read, name: "TxE")
                .WithTaggedFlag("BERR", 8)
                .WithTaggedFlag("ARLO", 9)
                .WithFlag(10, out acknowledgeFailed, FieldMode.ReadToClear | FieldMode.WriteZeroToClear, name: "AF",
                    changeCallback: (_, __) => Update())
                .WithTaggedFlag("OVR", 11)
                .WithTaggedFlag("PECERR", 12)
                .WithReservedBits(13, 1)
                .WithTaggedFlag("TIMEOUT", 14)
                .WithTaggedFlag("SMBALERT", 15);

            Registers.Status2.Define(registers)
                .WithFlag(0, out masterSlave, FieldMode.Read, name: "MSL", readCallback: (_, __) =>
                {
                    // CONSIDER: I2C_SR2:ADDR should possibly only be cleared if the previous I2C access was a read of the I2C_SR1 register (RM0033 Rev9 23.6.6)
                    addressSentOrMatched.Value = false;
                    Update();
                })
                .WithTaggedFlag("BUSY", 1)
                .WithFlag(2, out transmitterReceiver, FieldMode.Read, name: "TRA")
                .WithReservedBits(3, 1)
                .WithTaggedFlag("GENCALL", 4)
                .WithTaggedFlag("SMBDEFAULT", 5)
                .WithTaggedFlag("SMBHOST", 6)
                .WithTaggedFlag("DUALF", 7)
                .WithTag("PEC", 8, 8);

            Registers.ClockControl.Define(registers)
                .WithTag("CCR", 0, 12)
                .WithReservedBits(12, 2)
                .WithTaggedFlag("DUTY", 14)
                .WithTaggedFlag("F/S", 15);

            Registers.RiseTime.Define(registers, 0x2)
                .WithTag("TRISE", 0, 6)
                .WithReservedBits(6, 10);

            Registers.NoiseFilter.Define(registers)
                .WithTag("DNF", 0, 4)
                .WithTaggedFlag("ANOFF", 4)
                .WithReservedBits(5, 11);
        }

        private void InterruptEnableChange(bool oldValue, bool newValue)
        {
            this.Log(LogLevel.Debug, "InterruptEnableChange: oldValue {0} newValue {1}", oldValue, newValue);
            if (newValue)
            {
                // force synchronisation when enabling interrupt source:
                machine.LocalTimeSource.ExecuteInNearestSyncedState(_ => Update(), true);
            }
            else
            {
                // Disable immediately to to avoid interrupt
                // re-entrancy when CR2 written to disable source:
                Update();
            }
        }

        private void DMAEnableChange(bool oldValue, bool newValue)
        {
            this.Log(LogLevel.Debug, "DMAEnableChange: oldValue {0} newValue {1}", oldValue, newValue);

            // Would be ideal if we knew whether we were enabling for RX or TX
            // TODO:CONSIDER: Check if TxE==1 or RxNE==1
            // - could maybe use transmitterReceiver.Value

            // if false->true then we should allow DMA data transfers
            if (newValue)
            {
                if (!DMATransmit.IsSet)
                {
                    DMATransmit.Unset();
                    DMATransmit.Set();
                }

                if (!DMAReceive.IsSet)
                {
                    DMAReceive.Unset();
                    DMAReceive.Set();
                }
            }
            else
            {
                DMATransmit.Unset();
                DMAReceive.Unset();
            }
        }

        private void Update()
        {
            EventInterrupt.Set(eventInterruptEnable.Value &&
                               (startBit.Value || addressSentOrMatched.Value || byteTransferFinished.Value
                                || (bufferInterruptEnable.Value &&
                                    (dataRegisterEmpty.Value || dataRegisterNotEmpty.Value))));
            ErrorInterrupt.Set(errorInterruptEnable.Value && acknowledgeFailed.Value);
        }

        private uint DataRead(uint oldValue)
        {
            var result = 0u;
            this.Log(LogLevel.Debug, "DataRead: oldValue 0x{0:X} : dmaEnable {1}", oldValue, dmaEnable.Value);
            if (dataToReceive != null && dataToReceive.Any())
            {
                this.Log(LogLevel.Debug, "DataRead: dataToReceive.Count {0} : will Dequeue", dataToReceive.Count);
                result = dataToReceive.Dequeue();
            }
            else
            {
                this.Log(LogLevel.Warning, "Tried to read from an empty fifo");
            }

            byteTransferFinished.Value = (dataToReceive != null && dataToReceive.Count > 0);
            this.Log(LogLevel.Debug, "DataRead: byteTransferFinished now {0} result 0x{1:X}",
                byteTransferFinished.Value, result);

            Update();
            return result;
        }

        private void DataWrite(uint oldValue, uint newValue)
        {
            this.Log(LogLevel.Debug, "DataWrite: oldValue 0x{0:X} newValue 0x{1:X} : dmaEnable {2} state {3}", oldValue,
                newValue, dmaEnable.Value, state);
            //moved from WriteByte
            byteTransferFinished.Value = false;
            this.Log(LogLevel.Debug, "DataWrite: byteTransferFinished set {0}", byteTransferFinished.Value);
            Update();

            switch (state)
            {
                case State.AwaitingAddress:
                    startBit.Value = false;
                    willReadOnSelectedSlave = (newValue & 1) == 1; //LSB is 1 for read and 0 for write
                    var address = (int)(newValue >> 1);
                    if (ChildCollection.ContainsKey(address))
                    {
                        selectedSlave = ChildCollection[address];
                        addressSentOrMatched.Value =
                            true; //Note: ADDR is not set after a NACK reception - from documentation

                        transmitterReceiver.Value = !willReadOnSelectedSlave; //true when transmitting

                        if (willReadOnSelectedSlave)
                        {
                            this.Log(LogLevel.Debug, "DataWrite: ROSS: existing dataToReceive {0}", dataToReceive);
                            dataToReceive = new Queue<byte>(selectedSlave.Read());
                            byteTransferFinished.Value = true;
                            this.Log(LogLevel.Debug, "DataWrite: ROSS: byteTransferFinished set {0}",
                                byteTransferFinished.Value);
                            state = State.ReceivingData;
                        }
                        else
                        {
                            state = State.AwaitingData;
                            dataToTransfer = new List<byte>();

                            dataRegisterEmpty.Value = true;
                            addressSentOrMatched.Value = true;
                        }
                    }
                    else
                    {
                        this.Log(LogLevel.Debug, "DataWrite: no child for address 0x{0:X} : set state Idle", address);
                        state = State.Idle;
                        acknowledgeFailed.Value = true;
                    }

                    machine.LocalTimeSource.ExecuteInNearestSyncedState(_ => Update());
                    break;
                case State.AwaitingData:
                    dataToTransfer.Add((byte)newValue);

                    machine.LocalTimeSource.ExecuteInNearestSyncedState(_ =>
                    {
                        dataRegisterEmpty.Value = true;
                        byteTransferFinished.Value = true;
                        this.Log(LogLevel.Debug, "DataWrite: AwaitingData: byteTransferFinished set {0}",
                            byteTransferFinished.Value);
                        Update();
                    });
                    break;
                default:
                    this.Log(LogLevel.Warning, "Writing 0x{0:X} to DataRegister in unsupported state {1}.", newValue,
                        state);
                    break;
            }
        }

        private void SoftwareResetWrite(bool oldValue, bool newValue)
        {
            if (newValue)
            {
                Reset();
            }
        }

        private void StopWrite(bool oldValue, bool newValue)
        {
            this.NoisyLog("Setting STOP bit to {0} when state {1}", newValue, state);
            if (!newValue)
            {
                return;
            }

            if (selectedSlave != null && dataToTransfer != null && dataToTransfer.Count > 0)
            {
                this.Log(LogLevel.Debug, "StopWrite: selectedSlave and dataToTransfer.Count {0}", dataToTransfer.Count);
                selectedSlave.Write(dataToTransfer.ToArray());
                dataToTransfer.Clear();
                state = State.Idle;
                Update();
            }

            state = State.Idle;
            byteTransferFinished.Value = false;
            dataRegisterEmpty.Value = false;
            this.Log(LogLevel.Debug, "StopWrite: byteTransferFinished set {0} dataRegisterEmpty set {1}",
                byteTransferFinished.Value, dataRegisterEmpty.Value);
            Update();
        }

        private void StartWrite(bool oldValue, bool newValue)
        {
            this.Log(LogLevel.Debug, "StartWrite: oldValue {0} newValue {1} : state {2}", oldValue, newValue, state);
            if (!newValue)
            {
                return;
            }

            this.NoisyLog("Setting START bit to {0}", newValue);
            if (selectedSlave != null && dataToTransfer != null && dataToTransfer.Count > 0)
            {
                // repeated start condition
                selectedSlave.Write(dataToTransfer.ToArray());
                dataToTransfer.Clear();
            }

            //TODO: TRA cleared on repeated Start condition. Is this always here?
            transmitterReceiver.Value = false;
            dataRegisterEmpty.Value = false;
            byteTransferFinished.Value = false;
            this.Log(LogLevel.Debug, "StartWrite: byteTransferFinished set {0}", byteTransferFinished.Value);
            startBit.Value = true;
            if (newValue)
            {
                this.Log(LogLevel.Debug, "StartWrite: state {0}", state);
                switch (state)
                {
                    case State.Idle:
                    case State.AwaitingData: //HACK! Should not be here, forced by ExecuteIn somehow.
                    case State.ReceivingData: // "" (as comment above)
                        state = State.AwaitingAddress;
                        masterSlave.Value = true;
                        Update();
                        break;
                }
            }
        }

        private void PeripheralEnableWrite(bool oldValue, bool newValue)
        {
            this.Log(LogLevel.Debug, "PeripheralEnableWrite: oldValue {0} newValue {1} : state {2}", oldValue, newValue,
                state);
            if (!newValue)
            {
                acknowledgeEnable.Value = false;
                masterSlave.Value = false;
                acknowledgeFailed.Value = false;
                transmitterReceiver.Value = false;
                dataRegisterEmpty.Value = false;
                byteTransferFinished.Value = false;
                this.Log(LogLevel.Debug, "PeripheralEnableWrite: !newValue: byteTransferFinished set {0}",
                    byteTransferFinished.Value);
                Update();
            }
        }

        private DoubleWordRegister data;
        private IFlagRegisterField acknowledgeEnable;
        private IFlagRegisterField bufferInterruptEnable, eventInterruptEnable, errorInterruptEnable;
        private IValueRegisterField dataRegister;

        private IFlagRegisterField acknowledgeFailed,
            dataRegisterEmpty,
            dataRegisterNotEmpty,
            byteTransferFinished,
            addressSentOrMatched,
            startBit;

        private IFlagRegisterField transmitterReceiver, masterSlave;

        private IFlagRegisterField dmaLastTransfer;
        private IFlagRegisterField dmaEnable;

        private DoubleWordRegisterCollection registers;

        private State state;
        private List<byte> dataToTransfer;
        private Queue<byte> dataToReceive;
        private bool willReadOnSelectedSlave;
        private II2CPeripheral selectedSlave;

        private enum Registers
        {
            Control1 = 0x0,
            Control2 = 0x4,
            OwnAddress1 = 0x8,
            OwnAddress2 = 0xC,
            Data = 0x10,
            Status1 = 0x14,
            Status2 = 0x18,
            ClockControl = 0x1C,
            RiseTime = 0x20,
            NoiseFilter = 0x24,
        }

        private enum State
        {
            Idle,
            AwaitingAddress,
            AwaitingData,
            ReceivingData,
        }
    }
}