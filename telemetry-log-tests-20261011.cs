using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace VoicemeeterBug {
    internal static class TelemetryLogTests {
        static int assertions;
        static void Assert(bool condition, string name) { assertions++; if(!condition) throw new Exception("Telemetry: " + name); }
        internal static int Run() {
            assertions = 0;
            string directory = Path.Combine(Path.GetTempPath(), "voicemeeter-bug-tests-" + Guid.NewGuid().ToString("N") + "-20261011");
            Directory.CreateDirectory(directory);
            try {
                DateTimeOffset instant = new DateTimeOffset(2026, 10, 11, 8, 20, 0, TimeSpan.Zero);
                var log = new BoundedTelemetryLog(directory, delegate { return instant; }, 2048, 4);
                string message = "MANUAL_BEFORE quoted \"device\"\nnew line \u2764";
                Assert(log.Write(message, "self-test"), "first write succeeds");
                string[] lines = File.ReadAllLines(log.CurrentPath);
                var json = new JavaScriptSerializer();
                var record = json.DeserializeObject(lines[0]) as System.Collections.Generic.Dictionary<string, object>;
                Assert(lines.Length == 1 && (string)record["message"] == message, "JSON round trips multiline Unicode snapshot");
                Assert(((string)record["timestamp"]).StartsWith("2026-10-11T02:20:00") && ((string)record["timestamp"]).EndsWith("-06:00"), "Denver timestamp");
                Assert((string)record["event"] == "MANUAL_BEFORE" && (string)record["mode"] == "self-test", "event and mode");
                File.WriteAllText(Path.Combine(directory, "keep-20261011.txt"), "preserve");
                File.WriteAllText(Path.Combine(directory, "watcher-unrelated-20261011.jsonl"), "preserve");
                File.WriteAllText(Path.Combine(directory, "recovery-20261011.log"), "preserve");
                for(int i=0;i<30;i++) Assert(log.Write("HEARTBEAT " + new string('x', 900), "self-test"), "rotation write " + i);
                string[] owned = Directory.GetFiles(directory, "watcher-*.jsonl").Where(p => !p.Contains("unrelated")).ToArray();
                Assert(owned.Length == 4 && owned.All(p => new FileInfo(p).Length <= 2048), "rotation file and size cap");
                for(int day=1;day<=6;day++) { instant = instant.AddDays(1); Assert(log.Write("HEARTBEAT new day", "self-test"), "date rollover"); }
                owned = Directory.GetFiles(directory, "watcher-*.jsonl").Where(p => !p.Contains("unrelated")).ToArray();
                Assert(owned.Length == 4 && owned.Sum(p => new FileInfo(p).Length) <= 8192, "retention bounded across dates");
                foreach(string path in owned) foreach(string line in File.ReadAllLines(path)) Assert(json.DeserializeObject(line) != null, "every retained line valid JSON");
                Assert(File.ReadAllText(Path.Combine(directory, "keep-20261011.txt")) == "preserve" && File.ReadAllText(Path.Combine(directory, "watcher-unrelated-20261011.jsonl")) == "preserve" && File.ReadAllText(Path.Combine(directory, "recovery-20261011.log")) == "preserve", "unrelated and legacy files untouched");
                Assert(log.Write("ERROR " + String.Concat(Enumerable.Repeat("\ud83d\udc1e\n\"", 4000)), "self-test"), "oversized Unicode record contained");
                record = json.DeserializeObject(File.ReadAllLines(log.CurrentPath).Last()) as System.Collections.Generic.Dictionary<string, object>;
                Assert((bool)record["truncated"] && new FileInfo(log.CurrentPath).Length <= 2048, "oversized record explicitly marked and bounded");
                string blocked = Path.Combine(directory, "blocked-20261011.txt"); File.WriteAllText(blocked, "file instead of directory");
                var failing = new BoundedTelemetryLog(blocked, delegate { return instant; }, 2048, 4);
                Assert(!failing.Write("HEARTBEAT", "self-test") && failing.DroppedRecords == 1 && failing.LastError.Length > 0, "disk failure reported without throwing");
                File.Delete(blocked);
                Assert(failing.Write("HEARTBEAT recovered", "self-test") && failing.LastError == "", "logging recovers after disk failure");
                record = json.DeserializeObject(File.ReadAllText(failing.CurrentPath)) as System.Collections.Generic.Dictionary<string, object>;
                Assert(Convert.ToInt64(record["dropped_records"]) == 1 && ((string)record["previous_write_error"]).Length > 0, "recovery reports lost telemetry");
                Console.WriteLine("Telemetry self-test passed: " + assertions + " assertions. No audio APIs called.");
                return 0;
            } finally {
                string full = Path.GetFullPath(directory);
                string temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if(full.StartsWith(temp, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(full).StartsWith("voicemeeter-bug-tests-", StringComparison.Ordinal)) Directory.Delete(full, true);
            }
        }
    }
}
