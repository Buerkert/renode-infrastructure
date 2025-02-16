using System;
using System.Collections.Generic;
using System.Linq;
using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Time;
using Antmicro.Renode.Utilities;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    /// <summary>
    /// A peripheral that can be used to measure the frequency and duty cycle of a PWM signal.
    /// The measurements are performed at regular intervals defined by the <see cref="SampleInterval"/> property.
    /// Only full periods within a measurement window are counted, which means that the lowest measurable frequency is 1 / <see cref="SampleInterval"/> Hz.
    /// On the other hand, the measured frequency won't asymptotically approach the real frequency, since the measurement window is fixed.
    /// A log message is generated when the frequency or duty cycle changes by more than the thresholds defined by the <see cref="FrequencyThreshold"/> and <see cref="DutyCycleThreshold"/> properties.
    /// </summary>
    public class PwmInput : IGPIOReceiver
    {
        /// <summary>
        /// Creates a new instance of the PWM input peripheral.
        /// </summary>
        /// <param name="machine">The machine this peripheral belongs to.</param>
        /// <param name="sampleInterval">The interval between samples in seconds.</param>
        /// <param name="frequencyThreshold">The threshold for frequency change detection in Hz.</param>
        /// <param name="dutyCycleThreshold">The threshold for duty cycle change detection.</param>
        public PwmInput(IMachine machine, float sampleInterval = 1.0f, float frequencyThreshold = 1.0f, float dutyCycleThreshold = 0.01f)
        {
            this.machine = machine;
            SampleInterval = sampleInterval;
            FrequencyThreshold = frequencyThreshold;
            DutyCycleThreshold = dutyCycleThreshold;
            Reset();
        }

        public void Reset()
        {
            timeoutEvent?.Cancel();
            timeoutEvent = machine.LocalTimeSource.EnqueueTimeoutEvent((ulong)Math.Round(SampleInterval * 1000), CalcSignal);
            PinState = false;
            PwmSignal = new Signal();
            samples.Clear();
            Sample();
        }

        public void OnGPIO(int number, bool value)
        {
            if (number != 0)
                throw new ArgumentOutOfRangeException(nameof(number), "PwmInput supports only one input.");

            PinState = value;
            Sample();
        }

        public class Signal : IEmulationElement
        {
            public float Frequency { get; set; }
            public float DutyCycle { get; set; }

            public override string ToString() => $"Frequency: {Frequency} Hz, DutyCycle: {DutyCycle * 100.0f} %";
        }

        public float SampleInterval { get; set; }
        public float FrequencyThreshold { get; set; }
        public float DutyCycleThreshold { get; set; }
        public bool PinState { get; private set; }
        public Signal PwmSignal { get; private set; }

        private void Sample() => samples.Add((machine.LocalTimeSource.ElapsedVirtualTime, PinState));

        private void CalcSignal()
        {
            timeoutEvent = machine.LocalTimeSource.EnqueueTimeoutEvent((ulong)Math.Round(SampleInterval * 1000), CalcSignal);
            Sample();

            var highTime = TimeInterval.Empty;
            var lowTime = TimeInterval.Empty;
            (TimeInterval, bool)? lastEdge = null;
            var periods = new List<TimeInterval>();
            var prev = samples.First();

            foreach (var (ts, state) in samples.Skip(1))
            {
                var tempPrev = prev;
                prev = (ts, state);
                var diff = ts - tempPrev.Item1;
                if (tempPrev.Item2)
                    highTime += diff;
                else
                    lowTime += diff;

                if (state == tempPrev.Item2)
                    continue;
                if (lastEdge == null)
                {
                    lastEdge = (ts, state);
                    continue;
                }

                if (lastEdge.Value.Item2 != state)
                    continue;

                periods.Add(ts - lastEdge.Value.Item1);
                lastEdge = (ts, state);
            }

            var sig = new Signal
            {
                Frequency = periods.Any() ? (float)(1.0 / periods.Average(x => x.TotalSeconds)) : 0.0f,
                DutyCycle = (float)(highTime.TotalSeconds / (highTime.TotalSeconds + lowTime.TotalSeconds))
            };
            if (Math.Abs(sig.Frequency - PwmSignal.Frequency) > FrequencyThreshold || Math.Abs(sig.DutyCycle - PwmSignal.DutyCycle) > DutyCycleThreshold)
                this.Log(LogLevel.Noisy, "PWM signal changed: {0}", sig);

            PwmSignal = sig;
            samples.Clear();
            Sample();
        }

        private readonly IMachine machine;
        private TimeoutEvent timeoutEvent;
        private readonly List<(TimeInterval, bool)> samples = new List<(TimeInterval, bool)>();
    }
}