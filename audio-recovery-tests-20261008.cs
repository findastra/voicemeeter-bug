// Deterministic detector tests: all repair calls go to a fake Remote API.
using System;
using System.Collections.Generic;
using System.Threading;
namespace VoicemeeterBug
{
    internal static class RecoveryTests
    {
        private static int assertions;
        private static void Assert(bool condition, string message)
        { assertions++; if (!condition) throw new Exception("FAIL: " + message); }
        [STAThread] private static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--live-read-only")
            {
                using (RecoveryEngine engine = new RecoveryEngine(Console.WriteLine))
                {
                    engine.Automatic = false;
                    for (int i = 0; i < 5; i++) { engine.Tick(); Thread.Sleep(1000); }
                    Console.WriteLine(engine.Diagnostics());
                    return engine.Connected ? 0 : 2;
                }
            }
            return Run();
        }
        internal static int Run()
        {
            assertions = 0;
            Mapping(); StateMachine(); NativeA1(); AutomaticPolicyChanges(); NativeA1PolicyChanges(); RateOnly(); Disconnect(); Circuit(); IndependentOutputs(); Reenumeration(); CallbackQueue();
            HeartbeatTelemetry(); RawEventTelemetry(); ManualSnapshots(); SnapshotFailures();
            Console.WriteLine("PASS: " + assertions + " assertions; all restart requests used a fake API.");
            return 0;
        }
        private static void Mapping()
        {
            List<Endpoint> eps = new List<Endpoint> { new Endpoint("one", "Speakers (Razer Kraken Ultimate)", 1) };
            Assert(RecoveryEngine.MatchOutput("Speakers (Razer Kraken Ultimate)", eps) != null, "exact name match");
            Assert(RecoveryEngine.MatchOutput("Speakers", eps) == null, "short ambiguous prefix rejected");
            Assert(RecoveryEngine.MatchOutput("Speakers (Razer Kraken Ultimate", eps) != null, "truncated name match");
            eps.Add(new Endpoint("two", "Speakers (Razer Kraken Ultimate)", 1));
            Assert(RecoveryEngine.MatchOutput("Speakers (Razer Kraken Ultimate)", eps) == null, "duplicate exact names rejected");
            Assert(RecoveryEngine.MatchOutput("Speakers (Razer Kraken Ultimate", eps) == null, "duplicate prefixes rejected");
        }
        private static void StateMachine()
        {
            using (Fixture f = new Fixture())
            {
                f.Step(1); f.Step(4); Assert(f.Voice.Restarts == 0, "startup no restart");
                f.Device.Change("unrelated", 4); f.Step(1); f.Device.Change("unrelated", 1); f.Step(4);
                Assert(f.Voice.Restarts == 0, "unrelated endpoint ignored");
                f.Device.Change("headset", 4); f.Step(1); f.Step(4);
                Assert(f.Voice.Restarts == 0 && f.Engine.Status == "Waiting for device", "unplug waits");
                f.Device.Change("headset", 1); f.Step(1);
                Assert(f.Voice.Restarts == 0, "reconnect debounce");
                f.Step(3); Assert(f.Voice.Restarts == 1, "stable reconnect restarts");
                f.Step(6); f.Device.Change("headset", 4); f.Step(1); f.Device.Change("headset", 1); f.Step(1); f.Step(3);
                Assert(f.Voice.Restarts == 1, "automatic cooldown");
                f.Step(25); Assert(f.Voice.Restarts == 2, "cooldown retains reconnect");
                Assert(!f.Engine.Repair("double click", false), "manual immediate double click blocked");
                f.Step(6); Assert(f.Engine.Repair("manual test", false), "manual retry after five seconds");
                Assert(f.Voice.Restarts == 3, "manual forwarded once");
            }
        }
        private static void NativeA1()
        {
            using (Fixture f = new Fixture())
            {
                f.Step(1); f.Step(4); f.Device.Change("speaker", 4); f.Step(1); f.Device.Change("speaker", 1); f.Step(1); f.Step(3);
                Assert(f.Voice.Restarts == 0, "native A1 avoids duplicate restart");
                f.Engine.NativeA1AutoRestart = false;
                f.Step(4);
                f.Device.Change("speaker", 4); f.Step(1); f.Device.Change("speaker", 1); f.Step(1); f.Step(3);
                Assert(f.Voice.Restarts == 1, "explicit A1 override works");
            }
        }
        private static void AutomaticPolicyChanges()
        {
            using (Fixture f = new Fixture())
            {
                f.Step(1); f.Step(4);
                f.Device.Change("headset", 4); f.Step(1); f.Device.Change("headset", 1); f.Step(1);
                Assert(f.Engine.Diagnostics().Contains("pending outputs: 1"), "reconnected output pending before automatic disabled");
                f.Engine.Automatic = false;
                Assert(f.Engine.Diagnostics().Contains("pending outputs: 0"), "disabling automatic immediately clears pending repair");
                f.Step(4); f.Step(40);
                Assert(f.Voice.Restarts == 0, "disabled automatic cannot replay an already pending repair");
                f.Device.Change("headset", 4); f.Step(1); f.Device.Change("headset", 1); f.Step(1); f.Step(3);
                Assert(f.Engine.Diagnostics().Contains("pending outputs: 0"), "callbacks observed while disabled do not queue repair work");
                // Also leave callbacks undrained across the setting change.
                f.Device.Change("headset", 4); f.Device.Change("headset", 1);
                f.Engine.Automatic = true;
                f.Step(1); f.Step(4); f.Step(31);
                Assert(f.Voice.Restarts == 0, "enabling automatic rebaselines without replaying old or queued callbacks");
                f.Device.Change("headset", 4); f.Step(1); f.Device.Change("headset", 1); f.Step(1);
                f.Engine.Automatic = true;
                Assert(f.Engine.Diagnostics().Contains("pending outputs: 1"), "assigning unchanged automatic mode retains legitimate pending recovery");
                f.Step(3);
                Assert(f.Voice.Restarts == 1, "future reconnect still repairs after automatic mode reenabled");
                f.Engine.Automatic = false; f.Engine.Automatic = true; f.Step(6);
                Assert(!f.Engine.Repair("automatic guard survives mode toggles", true), "automatic mode changes preserve existing restart cooldown");
            }
        }
        private static void NativeA1PolicyChanges()
        {
            using (Fixture f = new Fixture())
            {
                f.Step(1); f.Step(4);
                f.Device.Change("speaker", 4); f.Device.Change("speaker", 1);
                f.Engine.NativeA1AutoRestart = false;
                f.Step(1); f.Step(4);
                Assert(f.Voice.Restarts == 0, "taking ownership of A1 ignores previously queued callbacks");
                f.Device.Change("speaker", 4); f.Step(1); f.Device.Change("speaker", 1); f.Step(1);
                Assert(f.Engine.Diagnostics().Contains("pending outputs: 1"), "A1 recovery is pending before ownership released");
                f.Engine.NativeA1AutoRestart = true;
                Assert(f.Engine.Diagnostics().Contains("pending outputs: 0"), "releasing A1 ownership immediately clears pending repair");
                f.Step(4);
                Assert(f.Voice.Restarts == 0, "releasing A1 ownership cannot replay a pending A1 restart");
                Assert(f.Engine.Details.Contains("A1 is excluded") && f.Engine.Details.Contains("not verified"), "A1 status describes exclusion without claiming verified native setting");
                f.Engine.NativeA1AutoRestart = false; f.Step(4);
                f.Device.Change("speaker", 4); f.Step(1); f.Device.Change("speaker", 1); f.Step(1);
                f.Engine.NativeA1AutoRestart = false;
                Assert(f.Engine.Diagnostics().Contains("pending outputs: 1"), "assigning unchanged A1 policy retains legitimate pending recovery");
                f.Step(3);
                Assert(f.Voice.Restarts == 1, "future A1 reconnect repairs after ownership rebaselines");
            }
        }
        private static void RateOnly()
        {
            using (Fixture f = new Fixture())
            {
                f.Step(1); f.Step(4); f.Voice.ZeroRate = true; f.Step(20);
                Assert(f.Voice.Restarts == 0, "sample rate zero alone never repairs");
                Assert(f.Engine.Details.IndexOf("sample rate", StringComparison.OrdinalIgnoreCase) >= 0, "sample rate diagnostic visible");
                f.Engine.Automatic = false; f.Device.Change("headset", 4); f.Step(1); f.Device.Change("headset", 1); f.Step(1); f.Step(3);
                Assert(f.Voice.Restarts == 0, "automatic off honors preference");
            }
        }
        private static void Disconnect()
        {
            using (Fixture f = new Fixture())
            {
                f.Step(1); f.Step(4); f.Voice.Dirty = -2; f.Step(1);
                Assert(!f.Engine.Connected, "VM stopped detected");
                Assert(!f.Engine.Repair("not open", false), "no restart without VM");
                f.Device.Change("headset", 4); f.Step(1); f.Device.Change("headset", 1); f.Step(1);
                f.Voice.Dirty = 0; f.Step(1); f.Step(5);
                Assert(f.Engine.Connected && f.Voice.Restarts == 0, "VM reopening baseline no restart");
                f.Device.FailScan = true; f.Step(6);
                Assert(f.Voice.Restarts == 0 && f.Engine.Details.IndexOf("unavailable", StringComparison.OrdinalIgnoreCase) >= 0, "scan failure safe");
                f.Device.FailScan = false; f.Step(6); f.Step(4);
                Assert(f.Voice.Restarts == 0, "scan recovery rebaselines");
            }
        }
        private static void Circuit()
        {
            using (Fixture f = new Fixture())
            {
                f.Step(1); f.Step(4);
                for (int i = 0; i < 4; i++)
                {
                    f.Step(31); f.Device.Change("headset", 4); f.Step(1); f.Device.Change("headset", 1); f.Step(1); f.Step(3);
                }
                Assert(f.Voice.Restarts == 3, "circuit caps automatic attempts");
                Assert(f.Engine.Status == "Automatic repair paused", "circuit is visible");
                f.Step(6); Assert(f.Engine.Repair("manual circuit override", false), "manual available during circuit");
                f.Step(31); f.Device.Change("headset", 4); f.Step(1); f.Device.Change("headset", 1); f.Step(1); f.Step(3);
                Assert(f.Voice.Restarts == 4, "manual does not reset automatic circuit");
                f.Step(601); Assert(f.Voice.Restarts == 5, "circuit expires and retained event runs");
            }
        }
        private static void IndependentOutputs()
        {
            using (Fixture f = new Fixture())
            {
                f.Step(1); f.Step(4);
                f.Device.Change("headset", 4); f.Step(1);
                f.Device.Change("secondary", 4); f.Step(1); f.Device.Change("secondary", 1); f.Step(1); f.Step(3);
                Assert(f.Voice.Restarts == 1, "unplugged A2 cannot block recovered A3");
                Assert(f.Engine.Diagnostics().Contains("pending outputs: 1"), "unplugged pending output retained");
                f.Step(31); Assert(f.Voice.Restarts == 1, "retained unavailable output does not loop");
                f.Device.Change("headset", 1); f.Step(1); f.Step(3);
                Assert(f.Voice.Restarts == 2, "retained output repairs after later reconnect");
            }
        }
        private static void Reenumeration()
        {
            using (Fixture f = new Fixture())
            {
                f.Step(1); f.Step(4); f.Device.Reenumerate("headset", "headset-new"); f.Step(1); f.Step(3);
                Assert(f.Voice.Restarts == 1, "new endpoint ID with same selected friendly name recovers");
            }
        }
        private static void CallbackQueue()
        {
            EndpointNotifications callback = new EndpointNotifications();
            callback.OnDefaultDeviceChanged(0, 0, "anything"); callback.OnPropertyValueChanged("anything", new PropertyKey());
            Assert(callback.Drain().Count == 0, "default and property changes never enter repair queue");
            Assert(callback.LastEvent.Kind == EndpointEventKind.PropertyChanged && callback.LastEvent.TimestampUtc != DateTime.MinValue, "ignored property event remains timestamped in raw telemetry");
            for (int i = 0; i < 500; i++) callback.OnDeviceStateChanged("test", 1);
            Assert(callback.Drain().Count == 128 && callback.Dropped == 372, "callback queue remains bounded");
            callback.OnDeviceStateChanged("test", 4);
            Assert(callback.Drain().Count == 1, "queue recovers after overflow");
        }
        private static int CountMessages(Fixture fixture, string prefix)
        { int count = 0; foreach (string message in fixture.Messages) if (message.StartsWith(prefix, StringComparison.Ordinal)) count++; return count; }
        private static string LastMessage(Fixture fixture, string prefix)
        { for (int i = fixture.Messages.Count - 1; i >= 0; i--) if (fixture.Messages[i].StartsWith(prefix, StringComparison.Ordinal)) return fixture.Messages[i]; return ""; }
        private static void HeartbeatTelemetry()
        {
            using (Fixture f = new Fixture())
            {
                f.Step(1); Assert(CountMessages(f, "HEARTBEAT ") == 1, "first completed tick emits heartbeat");
                for (int i = 0; i < 4; i++) f.Step(1);
                Assert(CountMessages(f, "HEARTBEAT ") == 1, "heartbeat no faster than five seconds");
                f.Step(1); string healthy = LastMessage(f, "HEARTBEAT ");
                Assert(CountMessages(f, "HEARTBEAT ") == 2, "heartbeat repeats at five seconds");
                Assert(healthy.Contains("alive=true") && healthy.Contains("last_tick_completed_utc=2026-10-09T00:00:06.0000000Z"), "heartbeat shows completed-tick UTC timestamp");
                Assert(healthy.Contains("last_successful_remote_poll_utc=2026-10-09T00:00:06.0000000Z") && healthy.Contains("endpoint_id=headset") && healthy.Contains("sample rate 48000"), "heartbeat includes successful poll and mapped output data");
                Assert(healthy.Contains("no selected-output transition pending") && healthy.Contains("automatic_cooldown_remaining_seconds=0.000"), "heartbeat includes decision and guard state");
                Assert(f.Engine.Status == "Watching device reconnects", "status accurately identifies reconnect watcher");
                f.Voice.Dirty = -2; f.Step(5); string offline = LastMessage(f, "HEARTBEAT ");
                Assert(offline.Contains("alive=true") && offline.Contains("last_tick_outcome=remote synchronization returned -2"), "offline early return still emits heartbeat");
                Assert(offline.Contains("last_successful_remote_poll_utc=2026-10-09T00:00:06.0000000Z"), "offline tick does not falsely advance successful poll timestamp");
                f.Voice.Dirty = 0; f.Voice.FailReads = true; f.Step(5); string error = LastMessage(f, "HEARTBEAT ");
                Assert(error.Contains("alive=true") && error.Contains("last_tick_outcome=error: Injected output read failure"), "exception path still emits heartbeat");
                Assert(f.Voice.Restarts == 0, "telemetry never initiates a repair");
            }
        }
        private static void RawEventTelemetry()
        {
            using (Fixture f = new Fixture())
            {
                f.Step(1); f.Step(4);
                f.Device.RecordRaw("capture-or-unmatched", EndpointEventKind.State, 4, true); f.Step(1);
                string raw = LastMessage(f, "HEARTBEAT ");
                Assert(raw.Contains("last_raw_device_event_utc=2026-10-09T00:00:05.0000000Z") && raw.Contains("endpoint_id=capture-or-unmatched"), "unmatched raw event remains visible after queue drain");
                f.Device.RecordRaw("default-only", EndpointEventKind.DefaultChanged, 0, false); f.Step(5);
                Assert(LastMessage(f, "HEARTBEAT ").Contains("kind=DefaultChanged"), "non-triggering raw default event visible");
                Assert(f.Voice.Restarts == 0, "raw-event telemetry never broadens repair triggers");
            }
        }
        private static void ManualSnapshots()
        {
            using (Fixture f = new Fixture())
            {
                f.Step(1); f.Step(4); f.Timeline.Clear();
                int drains = f.Device.Drains;
                f.Voice.ZeroRateAfterRestart = true;
                Assert(f.Engine.Repair("ordered test", false), "manual repair still accepted");
                int before = f.Timeline.IndexOf("MANUAL_BEFORE"); int restart = f.Timeline.IndexOf("RESTART"); int after = f.Timeline.IndexOf("MANUAL_AFTER");
                Assert(before >= 0 && before < restart && restart < after, "before snapshot is logged before command and after snapshot in finally");
                Assert(LastMessage(f, "MANUAL_BEFORE").Contains("sample rate 48000") && LastMessage(f, "MANUAL_AFTER").Contains("sample rate 0"), "before and after snapshots independently read fresh output state");
                Assert(f.Device.Drains == drains && f.Voice.Restarts == 1, "manual snapshots neither drain notifications nor request extra repair");
                f.Device.Change("headset", 4); f.Timeline.Clear(); int pendingEvents = f.Device.Queued;
                Assert(!f.Engine.Repair("cooldown rejection", false), "manual cooldown remains unchanged");
                Assert(f.Timeline.IndexOf("MANUAL_BEFORE") >= 0 && f.Timeline.IndexOf("MANUAL_AFTER") >= 0 && f.Timeline.IndexOf("RESTART") < 0, "blocked manual repair still has paired snapshots without command");
                Assert(LastMessage(f, "MANUAL_BEFORE").Contains("(state 4)") && LastMessage(f, "MANUAL_AFTER").Contains("outcome=not accepted"), "blocked snapshots include fresh disconnected state and outcome");
                Assert(f.Device.Queued == pendingEvents && f.Device.Drains == drains, "blocked snapshots preserve queued events");
                f.Voice.HeadsetName = "Changed outside detector";
                Assert(!f.Engine.Repair("fresh selection test", false), "additional blocked repair still rejected");
                Assert(LastMessage(f, "MANUAL_BEFORE").Contains("Changed outside detector") && f.Engine.SelectedOutputs[1].Contains("Headset output"), "fresh snapshot never overwrites detector selected-output cache");
                Assert(f.Engine.Diagnostics().Contains("pending outputs: 0") && f.Voice.Restarts == 1, "snapshot read has no pending-work or extra-repair side effect");
            }
        }
        private static void SnapshotFailures()
        {
            using (Fixture f = new Fixture())
            {
                f.Step(1); f.Step(4); f.Voice.FailReads = true; f.Device.FailScan = true;
                Assert(f.Engine.Repair("snapshot errors", false), "snapshot failures cannot block actual manual command");
                Assert(LastMessage(f, "MANUAL_BEFORE").Contains("fresh_remote_error=") && LastMessage(f, "MANUAL_AFTER").Contains("fresh_endpoint_error="), "both snapshot errors reported");
                Assert(f.Voice.Restarts == 1, "snapshot error path sends one command only");
            }
            using (Fixture f = new Fixture())
            {
                f.Step(1); f.Step(4); f.Voice.Dirty = -2;
                Assert(!f.Engine.Repair("offline snapshots", false), "offline manual repair remains rejected");
                Assert(CountMessages(f, "MANUAL_BEFORE") == 1 && CountMessages(f, "MANUAL_AFTER") == 1 && f.Voice.Restarts == 0, "offline manual return has paired snapshots without restart");
            }
            using (Fixture f = new Fixture())
            {
                f.Step(1); f.Step(4); f.Voice.FailRestart = true;
                Assert(!f.Engine.Repair("restart throws", false), "command exception remains handled");
                Assert(LastMessage(f, "MANUAL_AFTER").Contains("outcome=not accepted") && LastMessage(f, "MANUAL_AFTER").Contains("Injected restart failure"), "command exception still gets after snapshot");
            }
        }
        private sealed class Fixture : IDisposable
        {
            internal DateTime Time = new DateTime(2026, 10, 9, 0, 0, 0, DateTimeKind.Utc);
            internal readonly FakeVoice Voice = new FakeVoice(); internal readonly FakeDevices Device = new FakeDevices();
            internal readonly List<string> Messages = new List<string>();
            internal readonly List<string> Timeline = new List<string>();
            internal readonly RecoveryEngine Engine;
            internal Fixture()
            {
                Device.Clock = delegate { return Time; }; Voice.Trace = delegate(string value) { Timeline.Add(value); };
                Engine = new RecoveryEngine(Voice, Device, delegate { return Time; }, delegate(string value) { Messages.Add(value); if (value.StartsWith("MANUAL_BEFORE")) Timeline.Add("MANUAL_BEFORE"); if (value.StartsWith("MANUAL_AFTER")) Timeline.Add("MANUAL_AFTER"); });
            }
            internal void Step(int seconds) { Time = Time.AddSeconds(seconds); Engine.Tick(); }
            public void Dispose() { Engine.Dispose(); }
        }
        private sealed class FakeVoice : IVoiceRemote
        {
            internal int Restarts, Dirty; internal bool ZeroRate, ZeroRateAfterRestart, FailReads, FailRestart;
            internal string HeadsetName = "Headset output";
            internal Action<string> Trace = delegate { };
            public string Description { get { return "fake"; } }
            public int Synchronize() { return Dirty; }
            public OutputReading[] ReadOutputs() { if (FailReads) throw new Exception("Injected output read failure"); return new OutputReading[] { new OutputReading(0, "Primary speakers", 48000), new OutputReading(1, HeadsetName, ZeroRate ? 0 : 48000), new OutputReading(2, "Secondary headset", 48000) }; }
            public int Restart() { Trace("RESTART"); if (FailRestart) throw new Exception("Injected restart failure"); Restarts++; if (ZeroRateAfterRestart) ZeroRate = true; return 0; }
            public void Dispose() { }
        }
        private sealed class FakeDevices : IEndpointMonitor
        {
            private readonly List<Endpoint> data = new List<Endpoint> { new Endpoint("speaker", "Primary speakers", 1), new Endpoint("headset", "Headset output", 1), new Endpoint("unrelated", "Other speaker", 1), new Endpoint("secondary", "Secondary headset", 1) };
            private readonly List<EndpointEvent> events = new List<EndpointEvent>();
            private EndpointEvent lastEvent;
            internal bool FailScan;
            internal int Drains;
            internal Func<DateTime> Clock = delegate { return DateTime.UtcNow; };
            internal int Queued { get { return events.Count; } }
            internal void RecordRaw(string id, EndpointEventKind kind, uint state, bool enqueue)
            { lastEvent = new EndpointEvent { Id = id, State = state, Kind = kind, TimestampUtc = Clock() }; if (enqueue) events.Add(lastEvent); }
            internal void Change(string id, uint state)
            { for (int i = 0; i < data.Count; i++) if (data[i].Id == id) data[i] = new Endpoint(id, data[i].Name, state); RecordRaw(id, EndpointEventKind.State, state, true); }
            internal void Reenumerate(string oldId, string newId)
            { for (int i = 0; i < data.Count; i++) if (data[i].Id == oldId) data[i] = new Endpoint(newId, data[i].Name, 1); RecordRaw(oldId, EndpointEventKind.Removed, 4, true); RecordRaw(newId, EndpointEventKind.Added, 1, true); }
            public int DroppedEvents { get { return 0; } }
            public EndpointEvent LastEvent { get { return lastEvent; } }
            public List<Endpoint> Snapshot() { if (FailScan) throw new Exception("Injected device scan failure"); return new List<Endpoint>(data); }
            public List<EndpointEvent> Drain() { Drains++; List<EndpointEvent> copy = new List<EndpointEvent>(events); events.Clear(); return copy; }
            public void Dispose() { }
        }
    }
}
