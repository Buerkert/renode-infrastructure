using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Antmicro.Renode.Core;
using Antmicro.Renode.Exceptions;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals;
using Antmicro.Renode.Time;

namespace Antmicro.Renode.Tools.Miscellaneous
{
    public static class LoadMeasurementExtensions
    {
        public static void CreateLoadMeasurement(this Emulation emulation, string name, string savePath = null, float sampleInterval = 0.1f)
        {
            emulation.ExternalsManager.AddExternal(new LoadMeasurement(emulation, name, savePath, sampleInterval), name);
        }
    }

    /// <summary>
    /// A tool that samples the load of emulation every <see cref="sampleInterval"/> seconds.
    /// The results can be saved as a CSV file either manually or automatically when the tool is disposed.
    /// </summary>
    public class LoadMeasurement : IExternal, IHasOwnLife, IDisposable
    {
        /// <summary>
        /// Creates a new instance of the tool.
        /// </summary>
        /// <param name="emulation">The emulation to measure.</param>
        /// <param name="name">The name of the external, used for naming the thread.</param>
        /// <param name="savePath">The path to save the logs to. If null, the logs will not be saved.</param>
        /// <param name="sampleInterval">The interval in seconds between samples.</param>
        public LoadMeasurement(Emulation emulation, string name, string savePath, float sampleInterval)
        {
            this.emulation = emulation;
            this.sampleInterval = sampleInterval;
            SavePath = savePath;
            StartThread(name);
        }

        public void Start()
        {
            Resume();
        }

        public void Pause()
        {
            lock (sync) started = false;
        }

        public void Resume()
        {
            lock (sync) started = true;
        }

        public void Dispose()
        {
            cts?.Cancel();
            thread?.Join();
            try
            {
                if (!string.IsNullOrEmpty(SavePath))
                    SaveInternal();
            }
            catch (Exception e)
            {
                this.Log(LogLevel.Error, "Could not save logs: {0}", e.Message);
            }
        }

        public void ClearLogs()
        {
            lock (sync) samples.Clear();
        }

        public void SaveLogs()
        {
            if (string.IsNullOrEmpty(SavePath))
                throw new RecoverableException("SavePath is not set");

            try
            {
                SaveInternal();
            }
            catch (Exception e)
            {
                throw new RecoverableException($"Could not save logs to {SavePath}: {e.Message}", e);
            }
        }

        public string SavePath { get; set; }

        public bool IsPaused => !started;

        private void StartThread(string name)
        {
            cts = new CancellationTokenSource();
            thread = new Thread(() => SampleThread(cts.Token))
            {
                Name = $"LoadMeasurement-{name}",
            };
            thread.Start();
        }

        private void SampleThread(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                if (ct.WaitHandle.WaitOne((int)(sampleInterval * 1000)))
                    break;

                lock (sync)
                {
                    if (!started)
                        continue;

                    samples.Add(new Sample
                    {
                        HostTime = emulation.MasterTimeSource.ElapsedHostTime,
                        VirtTime = emulation.MasterTimeSource.ElapsedVirtualTime,
                        CurrentLoad = emulation.MasterTimeSource.CurrentLoad,
                        CumulativeLoad = emulation.MasterTimeSource.CumulativeLoad
                    });
                }
            }
        }

        private void SaveInternal()
        {
            this.Log(LogLevel.Debug, "Saving logs to {0}", SavePath);
            lock (sync)
            {
                using (var writer = new StreamWriter(SavePath))
                {
                    uint idx = 0;
                    writer.WriteLine("Index,HostTime,VirtTime,CurrentLoad,CumulativeLoad");
                    foreach (var sample in samples)
                    {
                        // print the sample with '.' as decimal separator
                        var hostTime = sample.HostTime.TotalSeconds.ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture);
                        var virtTime = sample.VirtTime.TotalSeconds.ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture);
                        var currentLoad = sample.CurrentLoad.ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture);
                        var cumulativeLoad = sample.CumulativeLoad.ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture);
                        writer.WriteLine($"{idx},{hostTime},{virtTime},{currentLoad},{cumulativeLoad}");
                        idx++;
                    }

                    writer.Flush();
                }
            }
        }

        private readonly Emulation emulation;
        private readonly float sampleInterval;
        private readonly object sync = new object();
        private bool started;
        private Thread thread;
        private CancellationTokenSource cts;
        private readonly List<Sample> samples = new List<Sample>();


        private struct Sample
        {
            public TimeInterval HostTime;
            public TimeInterval VirtTime;
            public double CurrentLoad;
            public double CumulativeLoad;
        }
    }
}