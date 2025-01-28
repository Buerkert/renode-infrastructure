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
            dataToReceive = new Queue<byte>();
            dataToTransfer = new List<byte>();
            EventInterrupt = new GPIO();
            ErrorInterrupt = new GPIO();
            DMATransmit = new GPIO();
            DMAReceive = new GPIO();
            EventInterrupt.AddStateChangedHook(b => this.NoisyLog("EventInterrupt set to {0}", b));
            ErrorInterrupt.AddStateChangedHook(b => this.NoisyLog("ErrorInterrupt set to {0}", b));
            DMATransmit.AddStateChangedHook(b => this.NoisyLog("DMATransmit set to {0}", b));
            DMAReceive.AddStateChangedHook(b => this.NoisyLog("DMAReceive set to {0}", b));
            CreateRegisters();
            Reset();
        }

        // We can't use AllowedTranslations because then WriteByte/WriteWord will trigger
        // an additional read (see ReadWriteExtensions:WriteByteUsingDoubleWord).
        // We can't have this happen for the data register.
        public byte ReadByte(long offset)
        {
            if (offset % 4 == 0)
            {
                return (byte)ReadDoubleWord(offset);
            }

            this.LogUnhandledRead(offset);
            return 0;
        }

        public void WriteByte(long offset, byte value)
        {
            if (offset % 4 == 0)
            {
                WriteDoubleWord(offset, value);
            }
            else
            {
                this.LogUnhandledWrite(offset, value);
            }
        }

        public uint ReadDoubleWord(long offset)
        {
            var rval = registers.Read(offset);
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
            dataToReceive.Clear();
            dataToTransfer.Clear();
            EventInterrupt.Unset();
            ErrorInterrupt.Unset();
            DMATransmit.Unset();
            DMAReceive.Unset();
            registers.Reset();
        }

        public GPIO EventInterrupt { get; private set; }

        public GPIO ErrorInterrupt { get; private set; }

        public GPIO DMAReceive { get; }

        public GPIO DMATransmit { get; }

        public long Size => 0x400;

        private void CreateRegisters()
        {
            registers = new DoubleWordRegisterCollection(this);

            Registers.Control1.Define(registers, name: "CR1")
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

            Registers.Control2.Define(registers, name: "CR2")
                .WithValueField(0, 6, name: "FREQ")
                .WithReservedBits(6, 2)
                .WithFlag(8, out errorInterruptEnable, name: "ITERREN")
                .WithFlag(9, out eventInterruptEnable, name: "ITEVTEN", changeCallback: InterruptEnableChange)
                .WithFlag(10, out bufferInterruptEnable, name: "ITBUFEN", changeCallback: InterruptEnableChange)
                // 0 == DMA requests disabled; 1 == DMA request enabled when TxE==1 or RxNE==1
                .WithFlag(11, out dmaEnable, name: "DMAEN", changeCallback: (_, __) => Update())
                // 0 == Next DMA EOT is NOT last transfer; 1 == Next DMA EOT is the last transfer
                .WithFlag(12, out dmaLastTransfer, name: "DMALAST")
                .WithReservedBits(13, 3);

            Registers.OwnAddress1.Define(registers, name: "OAR1")
                .WithTaggedFlag("ADD0", 0)
                .WithTag("ADD[7:1]", 1, 7)
                .WithTag("ADD[9:8]", 8, 2)
                .WithReservedBits(10, 5)
                .WithTaggedFlag("ADDMODE", 15);

            Registers.OwnAddress2.Define(registers, name: "OAR2")
                .WithTaggedFlag("ENDUAL", 0)
                .WithTag("ADD2[7:1]", 1, 7)
                .WithReservedBits(8, 8);

            Registers.Data.Define(registers, name: "DR")
                .WithValueField(0, 8, name: "DR", valueProviderCallback: prevVal => DataRead((uint)prevVal),
                    writeCallback: (prevVal, val) => DataWrite((uint)prevVal, (uint)val))
                .WithReservedBits(8, 8);

            Registers.Status1.Define(registers, name: "SR1")
                .WithFlag(0, out startBit, FieldMode.Read, name: "SB")
                .WithFlag(1, FieldMode.Read, name: "ADDR", valueProviderCallback: _ => AddressSendOrMatched)
                .WithFlag(2, FieldMode.Read, name: "BTF", valueProviderCallback: _ => ByteTransferFinished)
                .WithTaggedFlag("ADD10", 3)
                .WithTaggedFlag("STOPF", 4)
                .WithReservedBits(5, 1)
                .WithFlag(6, FieldMode.Read, name: "RxNE", valueProviderCallback: _ => RxDataRegisterNotEmpty)
                .WithFlag(7, FieldMode.Read, name: "TxE", valueProviderCallback: _ => TxDataRegisterEmpty)
                .WithTaggedFlag("BERR", 8)
                .WithTaggedFlag("ARLO", 9)
                .WithFlag(10, out acknowledgeFailed, FieldMode.ReadToClear | FieldMode.WriteZeroToClear, name: "AF",
                    changeCallback: (_, __) => Update())
                .WithTaggedFlag("OVR", 11)
                .WithTaggedFlag("PECERR", 12)
                .WithReservedBits(13, 1)
                .WithTaggedFlag("TIMEOUT", 14)
                .WithTaggedFlag("SMBALERT", 15)
                .WithReadCallback((_, __) =>
                {
                    if (state != State.AwaitingSr1Read) return;
                    state = State.AwaitingSr2Read;
                    this.Log(LogLevel.Debug, "SR1 read: new state={0}", state);
                });

            Registers.Status2.Define(registers, name: "SR2")
                .WithFlag(0, FieldMode.Read, name: "MSL", valueProviderCallback: _ => state != State.Idle)
                .WithFlag(1, FieldMode.Read, name: "BUSY", valueProviderCallback: _ => state != State.Idle)
                .WithFlag(2, out transmitterReceiver, FieldMode.Read, name: "TRA")
                .WithReservedBits(3, 1)
                .WithTaggedFlag("GENCALL", 4)
                .WithTaggedFlag("SMBDEFAULT", 5)
                .WithTaggedFlag("SMBHOST", 6)
                .WithTaggedFlag("DUALF", 7)
                .WithTag("PEC", 8, 8)
                .WithReadCallback((_, __) =>
                {
                    if (state != State.AwaitingSr2Read) return;
                    if (willReadOnSelectedSlave)
                    {
                        state = State.ReceivingData;
                        machine.LocalTimeSource.ExecuteInNearestSyncedState(___ => ReceiveDataFromSlave());
                    }
                    else
                    {
                        state = State.AwaitingData;
                        machine.LocalTimeSource.ExecuteInNearestSyncedState(___ => Update());
                    }

                    this.Log(LogLevel.Debug, "SR2 read: new state={0}", state);
                });

            Registers.ClockControl.Define(registers, name: "CCR")
                .WithTag("CCR", 0, 12)
                .WithReservedBits(12, 2)
                .WithTaggedFlag("DUTY", 14)
                .WithTaggedFlag("F/S", 15);

            Registers.RiseTime.Define(registers, resetValue: 0x2, name: "TRISE")
                .WithTag("TRISE", 0, 6)
                .WithReservedBits(6, 10);

            Registers.NoiseFilter.Define(registers, name: "FLTR")
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

        private void Update()
        {
            // Handle event interrupts
            EventInterrupt.Set(eventInterruptEnable.Value &&
                               (startBit.Value || AddressSendOrMatched || ByteTransferFinished ||
                                (bufferInterruptEnable.Value && (TxDataRegisterEmpty || RxDataRegisterNotEmpty))));
            ErrorInterrupt.Set(errorInterruptEnable.Value && acknowledgeFailed.Value);
            // Handle dma requests
            DMAReceive.Set(dmaEnable.Value && RxDataRegisterNotEmpty && state == State.ReceivingData);
            DMATransmit.Set(dmaEnable.Value && TxDataRegisterEmpty && state == State.AwaitingData);
        }

        private uint DataRead(uint oldValue)
        {
            uint ret = 0;
            switch (state)
            {
                case State.ReceivingData:
                    if (dataToReceive.Any())
                    {
                        ret = dataToReceive.Dequeue();
                        Update();
                        machine.LocalTimeSource.ExecuteInNearestSyncedState(_ => ReceiveDataFromSlave());
                    }
                    else
                        this.Log(LogLevel.Warning, "DataRead: no data to receive");

                    break;
                default:
                    this.Log(LogLevel.Warning, "DataRead: reading in unsupported state {0}.", state);
                    break;
            }

            this.NoisyLog("DataRead: returning 0x{0:X}", ret);
            return ret;
        }

        private void DataWrite(uint oldValue, uint newValue)
        {
            switch (state)
            {
                case State.AwaitingAddress:
                    startBit.Value = false;
                    willReadOnSelectedSlave = (newValue & 1) == 1; //LSB is 1 for read and 0 for write
                    var address = (int)(newValue >> 1);
                    if (ChildCollection.TryGetValue(address, out selectedSlave))
                    {
                        transmitterReceiver.Value = !willReadOnSelectedSlave; //true when transmitting
                        state = State.AwaitingSr1Read;
                        if (willReadOnSelectedSlave)
                            dataToReceive.Clear();
                        else
                            dataToTransfer.Clear();
                        this.Log(LogLevel.Debug, "DataWrite: child for address 0x{0:X} for {1} found", address,
                            willReadOnSelectedSlave ? "reads" : "writes");
                    }
                    else
                    {
                        this.Log(LogLevel.Warning, "DataWrite: no child for address 0x{0:X} : set state Idle", address);
                        state = State.Idle;
                        acknowledgeFailed.Value = true;
                    }

                    machine.LocalTimeSource.ExecuteInNearestSyncedState(_ => Update());
                    break;
                case State.AwaitingData:
                    this.Log(LogLevel.Debug, "DataWrite: queueing 0x{0:X} for transmission", newValue);
                    dataToTransfer.Add((byte)newValue);
                    Update();
                    machine.LocalTimeSource.ExecuteInNearestSyncedState(_ => SendDataToSlave());
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
                return;
            if (state != State.Idle && state != State.AwaitingAddress)
            {
                this.Log(LogLevel.Debug, "StopWrite: sending finsh transmission to slave");
                selectedSlave.FinishTransmission();
            }

            state = State.Idle;
            dataToReceive.Clear();
            dataToTransfer.Clear();
            this.Log(LogLevel.Debug, "StopWrite: byteTransferFinished set {0} dataRegisterEmpty set {1}",
                ByteTransferFinished, TxDataRegisterEmpty);
            Update();
        }

        private void StartWrite(bool oldValue, bool newValue)
        {
            this.Log(LogLevel.Debug, "StartWrite: oldValue {0} newValue {1} : state {2}", oldValue, newValue, state);
            if (!newValue)
                return;

            if (state != State.Idle && state != State.AwaitingAddress)
            {
                this.Log(LogLevel.Debug, "StartWrite: repeated start condition");
                selectedSlave.FinishTransmission();
            }

            //TODO: TRA cleared on repeated Start condition. Is this always here?
            transmitterReceiver.Value = false;
            startBit.Value = true;
            dataToReceive.Clear();
            dataToTransfer.Clear();
            this.Log(LogLevel.Debug, "StartWrite: state {0}", state);
            state = State.AwaitingAddress;
            Update();
        }

        private void PeripheralEnableWrite(bool oldValue, bool newValue)
        {
            this.Log(LogLevel.Debug, "PeripheralEnableWrite: oldValue {0} newValue {1} : state {2}", oldValue, newValue,
                state);
            if (!newValue)
            {
                acknowledgeEnable.Value = false;
                acknowledgeFailed.Value = false;
                transmitterReceiver.Value = false;
                Update();
            }
        }

        private void SendDataToSlave()
        {
            this.Log(LogLevel.Debug, "SendDataToSlave: state={0}", state);
            if (state != State.AwaitingData)
            {
                this.Log(LogLevel.Warning, "SendDataToSlave: state is not AwaitingData");
                return;
            }

            if (selectedSlave == null)
            {
                this.Log(LogLevel.Warning, "SendDataToSlave: selectedSlave is null");
                return;
            }

            if (dataToTransfer.Count == 0)
            {
                this.Log(LogLevel.Warning, "SendDataToSlave: dataToTransfer.Count is 0");
                return;
            }

            var data = dataToTransfer.ToArray();
            this.NoisyLog("SendDataToSlave: sending to slave {0}", data);
            selectedSlave.Write(data);
            dataToTransfer.Clear();
            Update();
        }

        private void ReceiveDataFromSlave()
        {
            this.Log(LogLevel.Debug, "ReceiveDataFromSlave: state={0}", state);
            if (state != State.ReceivingData)
            {
                this.Log(LogLevel.Warning, "ReceiveDataFromSlave: state is not ReceivingData");
                return;
            }

            if (selectedSlave == null)
            {
                this.Log(LogLevel.Warning, "ReceiveDataFromSlave: selectedSlave is null");
                return;
            }

            var data = selectedSlave.Read();
            this.NoisyLog("ReceiveDataFromSlave: slave returned {0}", data);
            dataToReceive.EnqueueRange(data);
            Update();
        }

        private bool AddressSendOrMatched => state == State.AwaitingSr1Read || state == State.AwaitingSr2Read;

        private bool TxDataRegisterEmpty => state == State.AwaitingData && dataToTransfer.Count == 0 ||
                                            (!willReadOnSelectedSlave && (state == State.AwaitingSr1Read ||
                                                                          state == State.AwaitingSr2Read));

        private bool RxDataRegisterNotEmpty => state == State.ReceivingData && dataToReceive.Count > 0;

        private bool ByteTransferFinished => (state == State.AwaitingData || state == State.ReceivingData) &&
                                             (willReadOnSelectedSlave ? RxDataRegisterNotEmpty : TxDataRegisterEmpty);

        private IFlagRegisterField acknowledgeEnable;
        private IFlagRegisterField bufferInterruptEnable;
        private IFlagRegisterField eventInterruptEnable;
        private IFlagRegisterField errorInterruptEnable;
        private IFlagRegisterField acknowledgeFailed;
        private IFlagRegisterField startBit;
        private IFlagRegisterField transmitterReceiver;
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
            AwaitingSr1Read,
            AwaitingSr2Read,
            AwaitingData,
            ReceivingData,
        }
    }
}