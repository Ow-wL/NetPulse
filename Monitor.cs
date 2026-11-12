using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NetPulse
{
    internal sealed class Target
    {
        internal readonly string Name;
        internal readonly IPAddress Address;
        internal string Ip { get { return Address.ToString(); } }

        internal Target(string name, string ip)
        {
            Name = name;
            Address = IPAddress.Parse(ip);
        }
    }

    internal sealed class Sample
    {
        internal readonly string Status;
        internal readonly long? Rtt;
        internal readonly bool Error;
        internal bool Lost { get { return !Rtt.HasValue && !Error; } }

        internal Sample(string status, long? rtt, bool error = false)
        {
            Status = status;
            Rtt = rtt;
            Error = error;
        }
    }

    internal sealed class LossRun
    {
        internal long FirstSlot, LastSlot;
        internal DateTimeOffset FirstTime, LastTime;
        internal long Count { get { return LastSlot - FirstSlot + 1; } }
    }

    internal sealed class TargetStats
    {
        internal readonly Target Target;
        internal readonly List<LossRun> Runs = new List<LossRun>();
        internal int Received, Lost, Errors;
        internal long Sum, Max, Longest;
        internal int Valid { get { return Received + Lost; } }
        internal double? LossPercent { get { return Valid == 0 ? (double?)null : 100.0 * Lost / Valid; } }
        internal double? Average { get { return Received == 0 ? (double?)null : (double)Sum / Received; } }
        internal LossRun CurrentRun;

        internal TargetStats(Target target) { Target = target; }

        internal void Add(long slot, DateTimeOffset time, Sample sample)
        {
            if (sample.Lost)
            {
                Lost++;
                if (CurrentRun == null || slot != CurrentRun.LastSlot + 1)
                {
                    CurrentRun = new LossRun { FirstSlot = slot, FirstTime = time };
                    Runs.Add(CurrentRun);
                }
                CurrentRun.LastSlot = slot;
                CurrentRun.LastTime = time;
                Longest = Math.Max(Longest, CurrentRun.Count);
            }
            else
            {
                CurrentRun = null;
                if (sample.Error) Errors++;
                else
                {
                    Received++;
                    Sum += sample.Rtt.Value;
                    Max = Math.Max(Max, sample.Rtt.Value);
                }
            }
        }
    }

    internal sealed class Session
    {
        internal const int IntervalMs = 1000;
        internal const int TimeoutMs = 800;
        internal readonly string Mode, Folder, Id;
        internal readonly TimeSpan Duration;
        internal readonly TargetStats[] Stats;
        internal readonly Stopwatch Clock = new Stopwatch();
        internal readonly EvidenceRecorder Evidence = new EvidenceRecorder();
        internal DateTimeOffset Started, Ended;
        internal long Skipped, Rounds;
        internal string EndReason;

        internal Session(string root, string mode, TimeSpan duration, Target[] targets)
        {
            Mode = mode;
            Duration = duration;
            Id = DateTimeOffset.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + "_" + mode + "_" +
                 Guid.NewGuid().ToString("N").Substring(0, 6);
            Folder = Path.Combine(root, Id);
            Stats = targets.Select(t => new TargetStats(t)).ToArray();
        }

        internal async Task RunAsync(CancellationToken stop, Action<long, DateTimeOffset, Sample[]> onRound,
            Func<Target, Task<Sample>> probe = null)
        {
            Started = DateTimeOffset.Now;
            EndReason = "Completed";
            if (probe == null) probe = PingAsync;
            bool opened = false;
            try
            {
                Directory.CreateDirectory(Folder);
                using (var writer = Csv.Writer(Path.Combine(Folder, "samples.csv")))
                {
                    writer.WriteLine("time,session,mode,round,scheduled_ms,ip,status,rtt_ms,loss");
                    writer.Flush();
                    opened = true;
                    Evidence.Open(Folder);
                    Clock.Start();
                    long nextSlot = 0;
                    long totalSlots = (long)Math.Ceiling(Duration.TotalMilliseconds / IntervalMs);
                    while (!stop.IsCancellationRequested && Clock.Elapsed < Duration)
                    {
                        long wait = nextSlot * IntervalMs - Clock.ElapsedMilliseconds;
                        if (wait > 0)
                        {
                            try { await Task.Delay((int)wait, stop); }
                            catch (OperationCanceledException) { break; }
                            continue;
                        }

                        long slot = Clock.ElapsedMilliseconds / IntervalMs;
                        Skipped += slot - nextSlot;
                        nextSlot = slot + 1;
                        DateTimeOffset time = DateTimeOffset.Now;
                        var probes = Stats.Select(s => probe(s.Target)).ToArray();
                        Evidence.RecordRoutes(time, slot + 1, Stats);
                        Sample[] samples = await Task.WhenAll(probes);
                        for (int i = 0; i < Stats.Length; i++)
                        {
                            Sample sample = samples[i];
                            Csv.Row(writer, time, Id, Mode, slot + 1, slot * IntervalMs, Stats[i].Target.Ip,
                                sample.Status, sample.Rtt, sample.Error ? (int?)null : sample.Lost ? 1 : 0);
                        }
                        writer.Flush();
                        for (int i = 0; i < Stats.Length; i++) Stats[i].Add(slot, time, samples[i]);
                        Rounds++;
                        onRound(slot, time, samples);

                        if (nextSlot == totalSlots)
                        {
                            int remaining = (int)Math.Max(0, Math.Ceiling(Duration.TotalMilliseconds - Clock.Elapsed.TotalMilliseconds));
                            if (remaining > 0 && !stop.IsCancellationRequested)
                            {
                                try { await Task.Delay(remaining, stop); }
                                catch (OperationCanceledException) { }
                            }
                            break;
                        }
                    }

                    if (stop.IsCancellationRequested && Clock.Elapsed < Duration) EndReason = "Stopped";
                    else Skipped += Math.Max(0, totalSlots - nextSlot);
                }
            }
            catch
            {
                EndReason = "Error";
                throw;
            }
            finally
            {
                Clock.Stop();
                Ended = DateTimeOffset.Now;
                if (opened)
                {
                    try
                    {
                        if (EndReason == "Error")
                        {
                            // 원본 저장 실패가 요약 저장 실패에 가려지지 않도록 한다.
                            try { WriteReports(); }
                            catch (IOException) { }
                            catch (UnauthorizedAccessException) { }
                        }
                        else WriteReports();
                    }
                    finally { Evidence.Complete(Started, Ended); }
                }
            }
        }

        internal static async Task<Sample> PingAsync(Target target)
        {
            try
            {
                using (var ping = new Ping())
                {
                    var reply = await ping.SendPingAsync(target.Address, TimeoutMs, new byte[32]).ConfigureAwait(false);
                    return new Sample(reply.Status.ToString(), reply.Status == IPStatus.Success ? (long?)reply.RoundtripTime : null);
                }
            }
            catch (PingException ex)
            {
                var native = ex.InnerException as System.ComponentModel.Win32Exception;
                return new Sample(native == null ? "ProbeError" : "ProbeError_" + native.NativeErrorCode, null, true);
            }
            catch (System.Net.Sockets.SocketException ex)
            {
                return new Sample("ProbeError_" + ex.SocketErrorCode, null, true);
            }
        }

        private void WriteReports()
        {
            using (var writer = Csv.Writer(Path.Combine(Folder, "loss-runs.csv")))
            {
                writer.WriteLine("mode,ip,first_round,last_round,first_time,last_time,count,first_to_last_seconds");
                foreach (var stat in Stats)
                    foreach (var run in stat.Runs)
                        Csv.Row(writer, Mode, stat.Target.Ip, run.FirstSlot + 1, run.LastSlot + 1,
                            run.FirstTime, run.LastTime, run.Count,
                            Csv.Decimal((run.LastTime - run.FirstTime).TotalSeconds));
            }
            using (var writer = Csv.Writer(Path.Combine(Folder, "summary.csv")))
            {
                writer.WriteLine("session,mode,started,ended,reason,planned_seconds,elapsed_seconds,interval_ms,timeout_ms," +
                    "rounds,skipped_rounds,ip,valid,received,lost,probe_errors,loss_pct,avg_ms,max_ms,longest_loss");
                foreach (var stat in Stats)
                    Csv.Row(writer, Id, Mode, Started, Ended, EndReason, Duration.TotalSeconds,
                        Csv.Decimal(Clock.Elapsed.TotalSeconds), IntervalMs, TimeoutMs, Rounds, Skipped,
                        stat.Target.Ip, stat.Valid, stat.Received, stat.Lost, stat.Errors,
                        Csv.Decimal(stat.LossPercent), Csv.Decimal(stat.Average),
                        stat.Received == 0 ? (long?)null : stat.Max, stat.Longest);
            }
        }
    }

    internal static class Csv
    {
        internal static StreamWriter Writer(string path)
        {
            return new StreamWriter(path, false, new UTF8Encoding(true));
        }

        internal static string Decimal(double? value)
        {
            return value.HasValue ? value.Value.ToString("0.00", CultureInfo.InvariantCulture) : "";
        }

        internal static void Row(StreamWriter writer, params object[] values)
        {
            // 이 형식에 기록하는 값은 프로그램이 정한 쉼표 없는 값이다.
            writer.WriteLine(string.Join(",", values.Select(value => value is DateTimeOffset
                ? ((DateTimeOffset)value).ToString("o", CultureInfo.InvariantCulture)
                : Convert.ToString(value, CultureInfo.InvariantCulture))));
        }
    }
}
