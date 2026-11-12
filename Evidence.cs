using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security;

namespace NetPulse
{
    internal sealed class Evidence
    {
        internal const string Header = "time,kind,round,ip,interface_index,interface_type,provider,event_id,record_id,status";
        internal const string IntelProvider = "e2fexpress";
        internal const double MatchSeconds = 3;
        internal readonly DateTimeOffset Time;
        internal readonly string Kind, Ip, InterfaceType, Provider, Status;
        internal readonly long? Round, RecordId;
        internal readonly uint? InterfaceIndex;
        internal readonly int? EventId;

        internal Evidence(DateTimeOffset time, string kind, string status, long? round = null, string ip = "",
            uint? index = null, string interfaceType = "", string provider = "", int? eventId = null, long? recordId = null)
        {
            Time = time; Kind = kind; Status = status; Round = round; Ip = ip;
            InterfaceIndex = index; InterfaceType = interfaceType; Provider = provider; EventId = eventId; RecordId = recordId;
        }

        internal void Write(StreamWriter writer)
        {
            Csv.Row(writer, Time, Kind, Round, Ip, InterfaceIndex, InterfaceType, Provider, EventId, RecordId, Status);
        }
    }

    internal sealed class EvidenceRecorder
    {
        private readonly object gate = new object();
        private readonly Dictionary<string, Evidence> routes = new Dictionary<string, Evidence>();
        private StreamWriter writer;
        private bool accepting;
        internal bool SaveFailed { get; private set; }

        internal void Open(string folder)
        {
            lock (gate)
            {
                try
                {
                    writer = Csv.Writer(Path.Combine(folder, "evidence.csv"));
                    writer.WriteLine(Evidence.Header);
                    writer.Flush();
                    accepting = true;
                }
                catch (Exception ex)
                {
                    if (!(ex is IOException || ex is UnauthorizedAccessException)) throw;
                    DisableWriter();
                }
            }
        }

        internal bool Mark(DateTimeOffset time)
        {
            lock (gate)
            {
                return accepting && Append(new Evidence(time, "Mark", "UserReported"));
            }
        }

        internal void RecordRoutes(DateTimeOffset time, long round, TargetStats[] targets)
        {
            foreach (TargetStats stat in targets)
            {
                Evidence previous;
                bool initial = !routes.TryGetValue(stat.Target.Ip, out previous);
                var route = BestRoute.Read(time, round, stat.Target, previous);
                if (!initial && previous.InterfaceIndex == route.InterfaceIndex &&
                    previous.InterfaceType == route.InterfaceType && previous.Status == route.Status) continue;

                routes[route.Ip] = route;
                lock (gate)
                {
                    if (!accepting) return;
                    Append(new Evidence(time, initial ? "RouteInitial" : "RouteChange", route.Status,
                        round, route.Ip, route.InterfaceIndex, route.InterfaceType));
                }
            }
        }

        internal Evidence LatestRoute(string ip)
        {
            Evidence route;
            return routes.TryGetValue(ip, out route) ? route : null;
        }

        internal void Complete(DateTimeOffset start, DateTimeOffset end)
        {
            lock (gate)
            {
                accepting = false;
                if (writer == null) return;
            }

            foreach (Evidence item in ReadLinkEvents(start, end))
            {
                if (!Append(item)) break;
            }
            if (writer != null)
            {
                try { writer.Dispose(); }
                catch (IOException) { SaveFailed = true; }
                finally { writer = null; }
            }
        }

        private bool Append(Evidence item)
        {
            if (writer == null) return false;
            try
            {
                item.Write(writer);
                writer.Flush();
                return true;
            }
            catch (IOException)
            {
                DisableWriter();
                return false;
            }
        }

        private void DisableWriter()
        {
            SaveFailed = true;
            accepting = false;
            if (writer == null) return;
            try { writer.Dispose(); }
            catch (IOException) { }
            finally { writer = null; }
        }

        internal static List<Evidence> ReadLinkEvents(DateTimeOffset start, DateTimeOffset end)
        {
            var records = new List<Evidence>();
            string since = UtcStamp(start.AddSeconds(-Evidence.MatchSeconds));
            string until = UtcStamp(end);
            string filter = "*[System[Provider[@Name='" + Evidence.IntelProvider + "'] and " +
                "(EventID=27 or EventID=32) and TimeCreated[@SystemTime >= '" + since +
                "' and @SystemTime <= '" + until + "']]]";
            string status = "Read";
            try
            {
                var query = new EventLogQuery("System", PathType.LogName, filter);
                using (var reader = new EventLogReader(query))
                {
                    EventRecord record;
                    while ((record = reader.ReadEvent()) != null)
                    {
                        using (record)
                        {
                            if (!record.TimeCreated.HasValue) continue;
                            records.Add(new Evidence(new DateTimeOffset(record.TimeCreated.Value), "Link",
                                record.Id == 27 ? "LinkDown" : "LinkUp", provider: Evidence.IntelProvider,
                                eventId: record.Id, recordId: record.RecordId));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (!(ex is EventLogException || ex is UnauthorizedAccessException || ex is SecurityException ||
                    ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception)) throw;
                status = "Unavailable_" + ex.GetType().Name;
            }
            records.Add(new Evidence(end, "LogQuery", status, provider: Evidence.IntelProvider));
            return records;
        }

        private static string UtcStamp(DateTimeOffset value)
        {
            return value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        }
    }

    internal static class BestRoute
    {
        [DllImport("iphlpapi.dll", ExactSpelling = true)]
        private static extern uint GetBestInterface(uint destination, out uint index);

        internal static Evidence Read(DateTimeOffset time, long round, Target target, Evidence previous)
        {
            uint index;
            uint status = GetBestInterface(BitConverter.ToUInt32(target.Address.GetAddressBytes(), 0), out index);
            if (status != 0)
                return new Evidence(time, "Route", "RouteError_" + status.ToString(CultureInfo.InvariantCulture),
                    round, target.Ip);
            bool reuse = previous != null && previous.InterfaceIndex == index && previous.InterfaceType != "Unknown" &&
                (round - 1) % 30 != 0;
            string type = reuse ? previous.InterfaceType : LookupType(index);
            return new Evidence(time, "Route", "Read", round, target.Ip, index, type);
        }

        private static string LookupType(uint index)
        {
            try
            {
                foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (!adapter.Supports(NetworkInterfaceComponent.IPv4)) continue;
                    var properties = adapter.GetIPProperties().GetIPv4Properties();
                    if (properties == null || (uint)properties.Index != index) continue;
                    switch (adapter.NetworkInterfaceType)
                    {
                        case NetworkInterfaceType.Wireless80211: return "WiFi";
                        case NetworkInterfaceType.Ethernet:
                        case NetworkInterfaceType.Ethernet3Megabit:
                        case NetworkInterfaceType.FastEthernetFx:
                        case NetworkInterfaceType.FastEthernetT:
                        case NetworkInterfaceType.GigabitEthernet: return "Ethernet";
                        default: return "Other";
                    }
                }
            }
            catch (NetworkInformationException) { }
            catch (SecurityException) { }
            catch (UnauthorizedAccessException) { }
            return "Unknown";
        }
    }
}
