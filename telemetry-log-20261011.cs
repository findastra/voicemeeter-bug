using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace VoicemeeterBug {
    // Logging is observational: an unavailable disk must never prevent a repair.
    internal sealed class BoundedTelemetryLog {
        internal const int DefaultFileLimit = 2 * 1024 * 1024;
        internal const int DefaultFileCount = 4;
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
        static readonly TimeZoneInfo Denver = TimeZoneInfo.FindSystemTimeZoneById("Mountain Standard Time");
        static readonly Regex OwnedFile = new Regex(@"^watcher-(?:[0-9]{8}|archive-[a-f0-9]{32}-[0-9]{8})\.jsonl$", RegexOptions.CultureInvariant);
        readonly string directory;
        readonly Func<DateTimeOffset> now;
        readonly int fileLimit, fileCount;
        readonly object sync = new object();
        readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 };
        internal string LastError { get; private set; }
        internal long DroppedRecords { get; private set; }

        internal BoundedTelemetryLog(string directory) : this(directory, delegate { return DateTimeOffset.UtcNow; }, DefaultFileLimit, DefaultFileCount) { }
        internal BoundedTelemetryLog(string directory, Func<DateTimeOffset> now, int fileLimit, int fileCount) {
            if(fileLimit < 1024 || fileCount < 1) throw new ArgumentOutOfRangeException("Log bounds");
            this.directory = Path.GetFullPath(directory); this.now = now;
            this.fileLimit = fileLimit; this.fileCount = fileCount; LastError = "";
        }
        internal string CurrentPath { get { return PathFor(now()); } }
        string PathFor(DateTimeOffset instant) { return Path.Combine(directory, "watcher-" + TimeZoneInfo.ConvertTime(instant, Denver).ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".jsonl"); }

        internal bool Write(string message, string mode) {
            lock(sync) {
                try {
                    // The application is single-instance; also serialize diagnostic helper writes.
                    using(var mutex = new Mutex(false, "Local\\Findastra.VoicemeeterBug.Telemetry.20261011")) {
                        bool held = false;
                        try {
                            try { held = mutex.WaitOne(100); } catch(AbandonedMutexException) { held = true; }
                            if(!held) throw new IOException("Telemetry writer busy");
                            DateTimeOffset instant = now();
                            string path = PathFor(instant);
                            Directory.CreateDirectory(directory);
                            byte[] record = Encode(message ?? "", mode, instant);
                            if(File.Exists(path) && new FileInfo(path).Length + record.Length > fileLimit) {
                                string archive = Path.Combine(directory, "watcher-archive-" + Guid.NewGuid().ToString("N") + "-" + TimeZoneInfo.ConvertTime(instant, Denver).ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".jsonl");
                                File.Move(path, archive);
                            }
                            // Prune only our strictly named files, across all dates. Reserve one slot for today's file.
                            foreach(var stale in Directory.GetFiles(directory, "watcher-*.jsonl")
                                .Where(p => OwnedFile.IsMatch(Path.GetFileName(p)) && !String.Equals(p, path, StringComparison.OrdinalIgnoreCase))
                                .OrderByDescending(p => File.GetLastWriteTimeUtc(p)).ThenBy(p => p, StringComparer.OrdinalIgnoreCase).Skip(fileCount - 1)) File.Delete(stale);
                            using(var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read)) stream.Write(record, 0, record.Length);
                            LastError = "";
                            return true;
                        } finally { if(held) mutex.ReleaseMutex(); }
                    }
                } catch(Exception ex) { LastError = ex.GetType().Name + ": " + ex.Message; DroppedRecords++; return false; }
            }
        }
        byte[] Encode(string message, string mode, DateTimeOffset instant) {
            string eventName = message.Split(new char[] { ' ', '\r', '\n', ';', ':' }, 2)[0];
            if(eventName.Length > 64) eventName = "MESSAGE";
            var record = new Dictionary<string, object> {
                { "timestamp", TimeZoneInfo.ConvertTime(instant, Denver).ToString("o", CultureInfo.InvariantCulture) },
                { "utc", instant.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture) },
                { "pid", Process.GetCurrentProcess().Id }, { "mode", mode ?? "unknown" },
                { "event", eventName }, { "message", message }, { "truncated", false },
                { "dropped_records", DroppedRecords }, { "previous_write_error", LastError }
            };
            // Normal snapshots are a few KB. Bound even an exceptional oversized error message.
            if(message.Length > fileLimit) { message = message.Substring(0, fileLimit); record["truncated"] = true; }
            while(true) {
                record["message"] = message;
                byte[] bytes = Utf8.GetBytes(json.Serialize(record) + "\n");
                if(bytes.Length <= fileLimit) return bytes;
                if(message.Length == 0) throw new IOException("Telemetry metadata exceeds record limit");
                int length = message.Length / 2;
                if(length > 0 && Char.IsHighSurrogate(message[length - 1])) length--;
                message = message.Substring(0, length); record["truncated"] = true;
            }
        }
    }
}
