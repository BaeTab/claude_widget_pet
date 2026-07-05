using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Claude_Widget.Services
{
    /// <summary>Aggregate resource snapshot across a set of processes.</summary>
    public readonly struct UsageSample
    {
        public double CpuPercent { get; init; }  // summed across processes, 0..(100*cores)
        public long RamBytes { get; init; }
        public int ProcessCount { get; init; }
    }

    /// <summary>
    /// Samples aggregate CPU% and RAM for a set of PIDs. CPU is a delta measurement, so the
    /// caller must sample on a regular cadence; the first sample for a PID reports 0% (no
    /// baseline yet). Intended to run only while the HUD is open, so the cost is bounded.
    /// </summary>
    public sealed class SystemUsageSampler
    {
        private readonly Dictionary<int, (TimeSpan Cpu, DateTime At)> _last = new();

        public UsageSample Sample(IReadOnlyCollection<int> pids)
        {
            double cpuPercent = 0;
            long ram = 0;
            int count = 0;
            var seen = new HashSet<int>();
            DateTime now = DateTime.UtcNow;

            foreach (int pid in pids)
            {
                if (!seen.Add(pid))
                    continue;
                try
                {
                    using var p = Process.GetProcessById(pid);
                    ram += p.WorkingSet64;
                    count++;

                    TimeSpan cpu = p.TotalProcessorTime;
                    if (_last.TryGetValue(pid, out var prev))
                    {
                        double wallMs = (now - prev.At).TotalMilliseconds;
                        if (wallMs > 0)
                        {
                            double cpuMs = (cpu - prev.Cpu).TotalMilliseconds;
                            // Percentage of a single core; summed across processes it can exceed 100%.
                            cpuPercent += Math.Max(0, cpuMs / wallMs * 100.0);
                        }
                    }
                    _last[pid] = (cpu, now);
                }
                catch
                {
                    // Process may have exited between scan and sample; skip it.
                }
            }

            // Forget PIDs that are no longer present so the delta map does not grow.
            if (_last.Count > seen.Count)
            {
                var drop = new List<int>();
                foreach (var key in _last.Keys)
                    if (!seen.Contains(key))
                        drop.Add(key);
                foreach (var key in drop)
                    _last.Remove(key);
            }

            return new UsageSample
            {
                CpuPercent = cpuPercent,
                RamBytes = ram,
                ProcessCount = count,
            };
        }

        /// <summary>Normalizes summed CPU% to a 0..100 machine-wide figure.</summary>
        public static double ToMachinePercent(double summedCpuPercent) =>
            Environment.ProcessorCount > 0
                ? Math.Min(100.0, summedCpuPercent / Environment.ProcessorCount)
                : summedCpuPercent;

        public static string FormatRam(long bytes)
        {
            if (bytes <= 0) return "0MB";
            double mb = bytes / (1024.0 * 1024.0);
            return mb >= 1024 ? $"{mb / 1024.0:0.0}GB" : $"{mb:0}MB";
        }
    }
}
