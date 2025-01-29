//
// Copyright (c) 2010-2023 Antmicro
// Copyright (c) 2011-2015 Realtime Embedded
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System;
using System.Collections.Generic;
using System.Linq;
using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.DMA
{
    [AllowedTranslations(AllowedTranslation.ByteToDoubleWord | AllowedTranslation.WordToDoubleWord)]
    public sealed class STM32DMA : IDoubleWordPeripheral, IKnownSize, IGPIOReceiver, INumberedGPIOOutput
    {
        public STM32DMA(IMachine machine)
        {
            streamFinished = new bool[NumberOfStreams];
            streams = new Stream[NumberOfStreams];
            for (var i = 0; i < streams.Length; i++) streams[i] = new Stream(this, i);

            this.machine = machine;
            engine = new DmaEngine(machine.GetSystemBus(this));
            Reset();
        }

        public uint ReadDoubleWord(long offset)
        {
            //this.Log(LogLevel.Debug, "ReadDoubleWord: offset=0x{0:X}", offset);
            switch ((Registers)offset)
            {
                case Registers.LowInterruptStatus:
                case Registers.HighInterruptStatus:
                    var rval = HandleInterruptRead((int)(offset / 4));
                    this.Log(LogLevel.Debug, "ReadDoubleWord: DMA_xISR: offset=0x{0:X} val 0x{1:X}", offset, rval);
                    return rval;
                default:
                    if (offset >= StreamOffsetStart && offset <= StreamOffsetEnd)
                    {
                        offset -= StreamOffsetStart;
                        return streams[offset / StreamSize].Read(offset % StreamSize);
                    }

                    this.LogUnhandledRead(offset);
                    return 0;
            }
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            switch ((Registers)offset)
            {
                case Registers.LowInterruptClear:
                case Registers.HighInterruptClear:
                    HandleInterruptClear((int)((offset - 8) / 4), value);
                    break;
                default:
                    if (offset >= StreamOffsetStart && offset <= StreamOffsetEnd)
                    {
                        offset -= StreamOffsetStart;
                        streams[offset / StreamSize].Write(offset % StreamSize, value);
                    }
                    else
                    {
                        this.LogUnhandledWrite(offset, value);
                    }

                    break;
            }
        }

        public void Reset()
        {
            streamFinished.Initialize();
            foreach (var stream in streams) stream.Reset();
        }

        // This OnGPIO() method allows arbitrary stream# indication
        // for the controller instance. However the source peripheral
        // for each stream is actually defined by the DMA_SxCR:CHSEL
        // and the specific STM32 variant controller mapping
        // (e.g. RM0033 rev9 Table 22 and Table 23).
        //
        // CONSIDER:IMPLEMENT: We may be able to use IsConnected and
        // Endpoints and Connect methods to dynamically change the DMA
        // channel stream mapping at run-time to avoid hardwiring in
        // stm32f2.repl

        public void OnGPIO(int number, bool value)
        {
            this.Log(LogLevel.Debug, "OnGPIO: number {0} value {1}", number, value);
            if (number < 0 || number >= streams.Length)
            {
                this.Log(LogLevel.Error, "Attempted to start non-existing DMA stream number: {0}. Maximum value is {1}", number, streams.Length);
                return;
            }

            // Allow (for Set/Unset cases) the current pending DMA request state to be held:
            streams[number].DMARequest = value;
            if (!value) return;

            // direction is private to streams
            if (streams[number].Enabled)
                streams[number].SelectTransfer();
            else
                // Not really a WARNING since DMA transfer request is just ignored if DMA stream not enabled
                this.Log(LogLevel.Info, "DMA peripheral request on stream {0} ignored", number);
        }

        public long Size => 0x400;

        public IReadOnlyDictionary<int, IGPIO> Connections
        {
            get
            {
                var i = 0;
                return streams.ToDictionary(x => i++, y => (IGPIO)y.IRQ);
            }
        }

        private uint HandleInterruptRead(int offset)
        {
            lock (streamFinished)
            {
                var returnValue = 0u;
                for (var i = 4 * offset; i < 4 * (offset + 1); i++)
                {
                    if (streamFinished[i]) returnValue |= 1u << BitNumberForStream(i - 4 * offset);
                }

                return returnValue;
            }
        }

        private void HandleInterruptClear(int offset, uint value)
        {
            lock (streamFinished)
            {
                for (var i = 4 * offset; i < 4 * (offset + 1); i++)
                {
                    var bitNo = BitNumberForStream(i - 4 * offset);
                    if ((value & (1 << bitNo)) != 0)
                    {
                        this.Log(LogLevel.Debug, "HandleInterruptClear: clearing streamFinished[{0}]", i);
                        streamFinished[i] = false;
                        streams[i].IRQ.Unset();
                    }
                }
            }
        }

        private static int BitNumberForStream(int streamNo)
        {
            switch (streamNo)
            {
                case 0:
                    return 5;
                case 1:
                    return 11;
                case 2:
                    return 21;
                case 3:
                    return 27;
                default:
                    throw new InvalidOperationException("Should not reach here.");
            }
        }

        private const int NumberOfStreams = 8;
        private const int StreamOffsetStart = 0x10;
        private const int StreamOffsetEnd = 0xCC;
        private const int StreamSize = 0x18;
        private readonly DmaEngine engine;
        private readonly IMachine machine;

        private readonly bool[] streamFinished;
        private readonly Stream[] streams;

        private enum Registers
        {
            LowInterruptStatus = 0x0, // DMA_LISR
            HighInterruptStatus = 0x4, // DMA_HISR
            LowInterruptClear = 0x8, //DMA_LIFCR
            HighInterruptClear = 0xC // DMA_HIFCR
        }

        private class Stream
        {
            public Stream(STM32DMA parent, int streamNo)
            {
                this.parent = parent;
                this.streamNo = streamNo;
                IRQ = new GPIO();
                registers = CreateRegisters();
            }

            public uint Read(long offset)
            {
                parent.Log(LogLevel.Debug, "STM32DMA:Stream:Read:[{0}] offset=0x{1:X}", streamNo, offset);
                return registers.Read(offset);
            }

            public void Write(long offset, uint value)
            {
                parent.Log(LogLevel.Noisy, "STM32DMA:Stream:Write:[{0}] offset=0x{1:X} value=0x{2:X}", streamNo, offset, value);
                registers.Write(offset, value);
            }

            public void Reset()
            {
                registers.Reset();
                numberOfDataLatch = 0;
                DMARequest = false;
            }

            public GPIO IRQ { get; }

            public bool Enabled
            {
                get => _enabled.Value;
                private set
                {
                    if (_enabled.Value == value) return;
                    parent.Log(LogLevel.Debug, "Stream {0} has been {1}", streamNo, value ? "enabled" : "disabled");
                    _enabled.Value = value;
                }
            }

            public bool DMARequest { get; set; }


            private DoubleWordRegisterCollection CreateRegisters()
            {
                var regs = new DoubleWordRegisterCollection(parent);
                Registers.Configuration.Define(regs, name: $"S{streamNo}CR")
                    .WithFlag(0, out _enabled, name: "EN", changeCallback: EnableChangeCallback)
                    .WithTaggedFlag("DMEIE", 1)
                    .WithTaggedFlag("TEIE", 2)
                    .WithTaggedFlag("HTIE", 3)
                    .WithFlag(4, out transferCompleteIsrEnable, name: "TCIE")
                    .WithTaggedFlag("PFCTRL", 5)
                    .WithEnumField(6, 2, out dataTransferDirection, name: "DIR")
                    .WithFlag(8, out circularMode, name: "CIRC")
                    .WithFlag(9, out peripheralIncrementAddressMode, name: "PINC")
                    .WithFlag(10, out memoryIncrementAddressMode, name: "MINC")
                    .WithEnumField(11, 2, out peripheralDataSize, name: "PSIZE")
                    .WithEnumField(13, 2, out memoryDataSize, name: "MSIZE")
                    .WithTaggedFlag("PINCOS", 15)
                    .WithTag("PL", 16, 2)
                    .WithTaggedFlag("DBM", 18)
                    .WithTaggedFlag("CT", 19)
                    .WithTag("PBURST", 21, 2)
                    .WithTag("MBURST", 23, 2)
                    .WithTag("CHSEL", 25, 3)
                    .WithReservedBits(28, 4);

                Registers.NumberOfData.Define(regs, name: $"S{streamNo}NDTR")
                    .WithValueField(0, 16, out numberOfData, name: "NDT")
                    .WithReservedBits(16, 16);

                Registers.PeripheralAddress.Define(regs, name: $"S{streamNo}PAR")
                    .WithValueField(0, 32, out peripheralAddress, name: "PAR");

                Registers.Memory0Address.Define(regs, name: $"S{streamNo}M0AR")
                    .WithValueField(0, 32, out memory0Address, name: "M0A");

                Registers.Memory1Address.Define(regs, name: $"S{streamNo}M1AR")
                    .WithTag("M1A", 0, 32);

                Registers.FIFOControl.Define(regs, name: $"S{streamNo}FCR", resetValue: 0x21)
                    .WithTag("FTH", 0, 2)
                    .WithTaggedFlag("DMDIS", 2)
                    .WithTag("FS", 3, 3)
                    .WithReservedBits(6, 1)
                    .WithTaggedFlag("FEIE", 7)
                    .WithReservedBits(8, 24);
                return regs;
            }

            private Request CreateRequest(uint size)
            {
                if (size == 0)
                    throw new InvalidOperationException("Transfer size is 0.");
                if (numberOfData.Value == 0)
                    throw new InvalidOperationException("Number of data is 0.");
                if (size > numberOfData.Value)
                    throw new InvalidOperationException("Requested size is greater than the number of data.");

                var sourceAddress = 0u;
                var destinationAddress = 0u;
                switch (dataTransferDirection.Value)
                {
                    case Direction.PeripheralToMemory:
                    case Direction.MemoryToMemory:
                        sourceAddress = (uint)peripheralAddress.Value;
                        destinationAddress = (uint)memory0Address.Value;
                        break;
                    case Direction.MemoryToPeripheral:
                        sourceAddress = (uint)memory0Address.Value;
                        destinationAddress = (uint)peripheralAddress.Value;
                        break;
                }

                // for now only direct mode is supported so memoryTransferType is a don't care
                if (memoryDataSize.Value != peripheralDataSize.Value)
                    parent.Log(LogLevel.Warning, "CreateRequest[{0}]: memoryTransferType is {1} while peripheralTransferType is {2}.", streamNo,
                        memoryDataSize.Value, peripheralDataSize.Value);
                var sourceTransferTypeSize = (uint)peripheralDataSize.Value + 1;
                var destinationTransferSize = (uint)peripheralDataSize.Value + 1;
                var incrementSourceAddress =
                    dataTransferDirection.Value == Direction.PeripheralToMemory ? peripheralIncrementAddressMode.Value : memoryIncrementAddressMode.Value;
                var incrementDestinationAddress =
                    dataTransferDirection.Value == Direction.MemoryToPeripheral ? peripheralIncrementAddressMode.Value : memoryIncrementAddressMode.Value;
                var alreadyTransferred = numberOfDataLatch - (uint)numberOfData.Value;
                if (incrementSourceAddress)
                    sourceAddress += alreadyTransferred * sourceTransferTypeSize;
                if (incrementDestinationAddress)
                    destinationAddress += alreadyTransferred * destinationTransferSize;
                return new Request(sourceAddress, destinationAddress, (int)(size * sourceTransferTypeSize), (DMA.TransferType)sourceTransferTypeSize,
                    (DMA.TransferType)destinationTransferSize, incrementSourceAddress, incrementDestinationAddress);
            }

            private void DoMemoryTransfer()
            {
                Request request;
                try
                {
                    request = CreateRequest((uint)numberOfData.Value);
                }
                catch (Exception e)
                {
                    parent.Log(LogLevel.Error, "DoMemoryTransfer[{0}]: {1}", streamNo, e.Message);
                    Enabled = false;
                    return;
                }

                lock (parent.streamFinished)
                {
                    LogTransferRequest(request);
                    parent.engine.IssueCopy(request);
                    if (circularMode.Value)
                    {
                        numberOfData.Value = numberOfDataLatch;
                        parent.Log(LogLevel.Debug, "DoMemoryTransfer: circular: reset numberOfData to {0}", numberOfData.Value);
                    }
                    else
                    {
                        numberOfData.Value = 0;
                        Enabled = false;
                    }

                    parent.streamFinished[streamNo] = true;
                    parent.Log(LogLevel.Debug, "DoMemoryTransfer: setting streamFinished[{0}] true : interruptOnComplete {1} numberOfData {2}", streamNo,
                        transferCompleteIsrEnable.Value, numberOfData.Value);
                    if (transferCompleteIsrEnable.Value) parent.machine.LocalTimeSource.ExecuteInNearestSyncedState(_ => IRQ.Set());
                }
            }

            private void DoPeripheralTransfer()
            {
                Request request;
                try
                {
                    request = CreateRequest(1);
                }
                catch (Exception e)
                {
                    parent.Log(LogLevel.Error, "DoPeripheralTransfer[{0}]: {1}", streamNo, e.Message);
                    Enabled = false;
                    return;
                }

                lock (parent.streamFinished)
                {
                    LogTransferRequest(request);
                    parent.engine.IssueCopy(request);
                    numberOfData.Value--;
                    if (numberOfData.Value > 0)
                    {
                        parent.Log(LogLevel.Debug, "DoPeripheralTransfer[{0}]: Transfer not finished yet {1}/{2}", streamNo,
                            numberOfDataLatch - (uint)numberOfData.Value, numberOfDataLatch);
                        return;
                    }

                    parent.Log(LogLevel.Debug, "DoPeripheralTransfer[{0}]: Transfer finished -> {1} setting IRQ", streamNo,
                        transferCompleteIsrEnable.Value ? "" : "not");
                    if (circularMode.Value)
                    {
                        numberOfData.Value = numberOfDataLatch;
                        parent.Log(LogLevel.Debug, "DoPeripheralTransfer[{0}]: circular mode enabled -> reset numberOfData to {1}", streamNo,
                            numberOfData.Value);
                    }
                    else
                        Enabled = false;

                    parent.streamFinished[streamNo] = true;
                    if (transferCompleteIsrEnable.Value) parent.machine.LocalTimeSource.ExecuteInNearestSyncedState(_ => IRQ.Set());
                }
            }

            public void SelectTransfer()
            {
                if (dataTransferDirection.Value == Direction.MemoryToMemory)
                    DoMemoryTransfer();

                if (dataTransferDirection.Value == Direction.PeripheralToMemory || dataTransferDirection.Value == Direction.MemoryToPeripheral)
                    DoPeripheralTransfer();
            }

            private void EnableChangeCallback(bool oldValue, bool newValue)
            {
                if (!newValue) return;
                numberOfDataLatch = (uint)numberOfData.Value; // as per RM0033 rev9 9.5.6
                parent.Log(LogLevel.Debug, "EnableChangeCallback[{0}]: stream has been enabled", streamNo);
                if (dataTransferDirection.Value == Direction.MemoryToMemory && DMARequest)
                    DoMemoryTransfer();
            }

            private void LogTransferRequest(Request request)
            {
                parent.Log(LogLevel.Noisy,
                    $"CopyRequest[{streamNo}]: Direction: {dataTransferDirection.Value} Source: 0x{request.Source.Address:X}, Destination: 0x{request.Destination.Address:X}, " +
                    $"Size: {request.Size}, ReadTransferType: {request.ReadTransferType}, WriteTransferType: {request.WriteTransferType}, " +
                    $"IncrementReadAddress: {request.IncrementReadAddress}, IncrementWriteAddress: {request.IncrementWriteAddress}"
                );
            }

            private readonly STM32DMA parent;
            private readonly int streamNo;
            private readonly DoubleWordRegisterCollection registers;
            private uint numberOfDataLatch;

            private IFlagRegisterField _enabled;
            private IFlagRegisterField transferCompleteIsrEnable;
            private IEnumRegisterField<Direction> dataTransferDirection;
            private IFlagRegisterField circularMode;
            private IFlagRegisterField peripheralIncrementAddressMode;
            private IFlagRegisterField memoryIncrementAddressMode;
            private IEnumRegisterField<TransferType> peripheralDataSize;
            private IEnumRegisterField<TransferType> memoryDataSize;
            private IValueRegisterField numberOfData;
            private IValueRegisterField peripheralAddress;
            private IValueRegisterField memory0Address;

            private enum Registers
            {
                Configuration = 0x0,
                NumberOfData = 0x4,
                PeripheralAddress = 0x8,
                Memory0Address = 0xC,
                Memory1Address = 0x10,
                FIFOControl = 0x14
            }

            private enum Direction : byte
            {
                PeripheralToMemory = 0,
                MemoryToPeripheral = 1,
                MemoryToMemory = 2
            }

            private enum TransferType : byte
            {
                Byte = 0,
                HalfWord = 1,
                Word = 2
            }
        }
    }
}