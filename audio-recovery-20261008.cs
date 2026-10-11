// Voicemeeter Bug: supported Remote API restart and conservative endpoint monitoring.
// Compile for x64; all public methods are called on the same UI thread.
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace VoicemeeterBug
{
    public sealed class RecoveryEngine : IDisposable
    {
        private readonly Action<string> log;
        private readonly Func<DateTime> clock;
        private readonly RestartGuard guard = new RestartGuard();
        private IVoiceRemote remote;
        private IEndpointMonitor monitor;
        private readonly Dictionary<string, Endpoint> known = new Dictionary<string, Endpoint>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<int, PendingRecovery> pending = new Dictionary<int, PendingRecovery>();
        private OutputReading[] outputs = new OutputReading[0];
        private List<Endpoint> endpoints = new List<Endpoint>();
        private DateTime retryRemote, retryMonitor, nextScan, baselineUntil, ignoreUntil, recentActionUntil;
        private DateTime nextHeartbeat, lastTickCompleted, lastRemotePoll, lastEndpointScan;
        private string lastTickOutcome = "not started";
        private EndpointEvent lastRawDeviceEvent;
        private string lastAction = "No repair requested", detectorHealth = "Starting", remoteError = "", monitorError = "";
        private bool baseline, disposed, circuitNotified, injected;
        private int droppedEvents;
        public bool Automatic { get; set; }
        public bool NativeA1AutoRestart { get; set; }
        public string Status { get; private set; }
        public string Details { get; private set; }
        public bool Connected { get; private set; }
        public event Action<string> Notification;

        public RecoveryEngine(Action<string> logger)
        {
            log = logger ?? delegate { }; clock = delegate { return DateTime.UtcNow; };
            Automatic = true; NativeA1AutoRestart = true;
            Status = "Starting"; Details = "Connecting to Voicemeeter";
        }

        internal RecoveryEngine(IVoiceRemote voice, IEndpointMonitor devices, Func<DateTime> time, Action<string> logger)
            : this(logger)
        { remote = voice; monitor = devices; clock = time; injected = true; }

        public string[] SelectedOutputs
        {
            get
            {
                List<string> result = new List<string>();
                foreach (OutputReading output in outputs)
                    if (output.Name.Length > 0) result.Add("A" + (output.Bus + 1) + ": " + output.Name);
                return result.ToArray();
            }
        }

        public void Tick()
        {
            if (disposed) return;
            DateTime now = clock();
            lastTickOutcome = "running";
            try
            {
                EnsureServices(now);
                if (remote == null) { Connected = false; Status = "Voicemeeter unavailable"; Details = remoteError; lastTickOutcome = "remote unavailable"; return; }
                int dirty = remote.Synchronize();
                if (dirty < 0)
                {
                    if (Connected) Write("Voicemeeter disconnected (" + dirty + ")");
                    Connected = false; baseline = false; pending.Clear();
                    Status = "Waiting for Voicemeeter";
                    Details = dirty == -2 ? "Open Voicemeeter to enable click-to-fix." : "Remote API synchronization error " + dirty;
                    lastTickOutcome = "remote synchronization returned " + dirty;
                    DrainWithoutAction();
                    return;
                }
                bool newlyConnected = !Connected;
                Connected = true;
                OutputReading[] latest = remote.ReadOutputs();
                lastRemotePoll = clock();
                bool changed = !SameSelections(outputs, latest);
                outputs = latest;
                if (newlyConnected || changed)
                {
                    pending.Clear(); baseline = false; baselineUntil = now.AddSeconds(3);
                    Write(newlyConnected ? "Connected to Voicemeeter; collecting a baseline without restarting." : "Selected outputs changed; resetting the detector baseline.");
                }
                ObserveEndpoints(now, newlyConnected || changed);
                if (!baseline && now >= baselineUntil && monitor != null && monitorError.Length == 0)
                { baseline = true; Write("Endpoint baseline ready."); }
                UpdateStatus(now);
                EvaluatePending(now);
                lastTickOutcome = monitor == null || monitorError.Length > 0 ? "remote poll succeeded; endpoint watcher unavailable" : "poll completed";
            }
            catch (Exception ex)
            {
                detectorHealth = "Detector error: " + ex.Message;
                Status = "Click to retry"; Details = detectorHealth;
                lastTickOutcome = "error: " + ex.Message;
                WriteOnceError(detectorHealth);
            }
            finally
            {
                // A completed tick proves this loop ran, not that audio sounded healthy.
                lastTickCompleted = clock();
                RememberRawDeviceEvent();
                if (lastTickCompleted >= nextHeartbeat)
                {
                    nextHeartbeat = lastTickCompleted.AddSeconds(5);
                    try { Write("HEARTBEAT alive=true; meaning=completed_tick_not_audio_health\n" + Diagnostics()); }
                    catch (Exception ex) { Write("HEARTBEAT alive=true; snapshot_utc=" + Utc(lastTickCompleted) + "; telemetry_error=" + ex.Message); }
                }
            }
        }

        private string lastErrorLogged;
        private void WriteOnceError(string value)
        { if (!String.Equals(lastErrorLogged, value, StringComparison.Ordinal)) { lastErrorLogged = value; Write(value); } }

        private void EnsureServices(DateTime now)
        {
            if (remote == null && !injected && now >= retryRemote)
            {
                retryRemote = now.AddSeconds(30);
                try { remote = new VoicemeeterRemote(); remoteError = ""; Write("Loaded " + remote.Description); }
                catch (Exception ex) { remoteError = ex.Message; WriteOnceError("Remote API: " + ex.Message); }
            }
            if (monitor == null && !injected && now >= retryMonitor)
            {
                retryMonitor = now.AddSeconds(30);
                try { monitor = new EndpointMonitor(); monitorError = ""; baseline = false; baselineUntil = now.AddSeconds(3); }
                catch (Exception ex) { monitorError = ex.Message; WriteOnceError("Endpoint watcher: " + ex.Message); }
            }
        }

        private void DrainWithoutAction()
        { if (monitor != null) { RememberRawDeviceEvent(); monitor.Drain(); } }

        private void RememberRawDeviceEvent()
        {
            if (monitor == null) return;
            try { EndpointEvent latest = monitor.LastEvent; if (latest != null) lastRawDeviceEvent = latest; }
            catch { } // Telemetry must not interrupt the repair loop.
        }

        private void ObserveEndpoints(DateTime now, bool force)
        {
            if (monitor == null) { detectorHealth = "Windows endpoint watcher unavailable: " + monitorError; return; }
            RememberRawDeviceEvent();
            List<EndpointEvent> events = monitor.Drain();
            bool scan = force || events.Count > 0 || now >= nextScan;
            if (!scan) return;
            nextScan = now.AddSeconds(5);
            List<Endpoint> current;
            try { current = monitor.Snapshot(); monitorError = ""; lastEndpointScan = clock(); }
            catch (Exception ex)
            {
                monitorError = ex.Message; detectorHealth = "Windows endpoint scan failed: " + ex.Message;
                baseline = false; baselineUntil = now.AddSeconds(3); pending.Clear();
                WriteOnceError(detectorHealth); return;
            }
            endpoints = current;
            // Keep a bounded history so a removal can still be mapped by its old friendly name.
            Dictionary<string, Endpoint> combined = new Dictionary<string, Endpoint>(known, StringComparer.OrdinalIgnoreCase);
            foreach (Endpoint ep in current) combined[ep.Id] = ep;
            List<Endpoint> previousMapping = new List<Endpoint>(known.Values);
            Dictionary<string, uint> transitions = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
            foreach (Endpoint ep in known.Values) transitions[ep.Id] = ep.State;
            bool allow = baseline && now >= ignoreUntil && !force;
            foreach (EndpointEvent ev in events)
            {
                Endpoint ep;
                if (!combined.TryGetValue(ev.Id, out ep)) continue; // Capture devices/unrelated IDs never enter render snapshots.
                uint previous;
                bool hadPrevious = transitions.TryGetValue(ev.Id, out previous);
                uint state = ev.Kind == EndpointEventKind.Removed ? 4u : ev.Kind == EndpointEventKind.Added ? ep.State : ev.State;
                if (allow && ((hadPrevious && previous != state) || ev.Kind == EndpointEventKind.Removed || (!hadPrevious && ev.Kind == EndpointEventKind.Added)))
                    RecordTransition(ep.Id, state, state == 1 ? current : previousMapping, now);
                transitions[ev.Id] = state;
            }
            // The snapshot also catches a missed callback; no default-device changes are considered.
            foreach (Endpoint ep in current)
            {
                uint previous;
                if (allow && transitions.TryGetValue(ep.Id, out previous) && previous != ep.State)
                    RecordTransition(ep.Id, ep.State, current, now);
            }
            foreach (Endpoint ep in known.Values)
                if (allow && ep.State == 1 && !ContainsId(current, ep.Id) && !HasRemoval(events, ep.Id))
                    RecordTransition(ep.Id, 4u, previousMapping, now);
            Dictionary<string, Endpoint> next = new Dictionary<string, Endpoint>(StringComparer.OrdinalIgnoreCase);
            foreach (Endpoint ep in current) next[ep.Id] = ep;
            foreach (Endpoint ep in known.Values)
                if (!next.ContainsKey(ep.Id) && next.Count < 256) next[ep.Id] = new Endpoint(ep.Id, ep.Name, 4);
            known.Clear(); foreach (Endpoint ep in next.Values) known[ep.Id] = ep;
            if (monitor.DroppedEvents != droppedEvents)
            {
                droppedEvents = monitor.DroppedEvents;
                Write("Endpoint event queue overflowed; snapshot reconciled. Dropped events: " + droppedEvents);
            }
            detectorHealth = "Watching Windows output-device state changes; audio content is not recorded.";
        }

        private void RecordTransition(string id, uint state, List<Endpoint> mapping, DateTime now)
        {
            Endpoint primary = outputs.Length > 0 && outputs[0].Bus == 0 ? MatchOutput(outputs[0].Name, mapping) : null;
            if (NativeA1AutoRestart && primary != null && String.Equals(primary.Id, id, StringComparison.OrdinalIgnoreCase))
            { Write("A1 device transition observed; Voicemeeter's native A1 auto-restart owns recovery."); return; }
            foreach (OutputReading output in outputs)
            {
                Endpoint match = MatchOutput(output.Name, mapping);
                if (match == null || !String.Equals(match.Id, id, StringComparison.OrdinalIgnoreCase)) continue;
                pending[output.Bus] = new PendingRecovery { Bus = output.Bus, Id = id, Name = output.Name, LastChange = now };
                Write("A" + (output.Bus + 1) + " endpoint " + (state == 1 ? "active" : "unavailable") + ": " + output.Name);
            }
        }

        private void EvaluatePending(DateTime now)
        {
            if (!Automatic || !Connected || !baseline || pending.Count == 0 || monitorError.Length > 0 || now < ignoreUntil) return;
            List<int> obsolete = new List<int>();
            foreach (KeyValuePair<int, PendingRecovery> item in pending)
            {
                OutputReading selected = FindOutput(item.Key);
                if (selected == null || !String.Equals(selected.Name, item.Value.Name, StringComparison.OrdinalIgnoreCase)) obsolete.Add(item.Key);
            }
            foreach (int bus in obsolete) pending.Remove(bus);
            if (pending.Count == 0) return;
            List<PendingRecovery> ready = new List<PendingRecovery>();
            List<PendingRecovery> waiting = new List<PendingRecovery>();
            string waitingName = null;
            foreach (PendingRecovery item in pending.Values)
            {
                Endpoint current = MatchOutput(item.Name, endpoints);
                if (current == null || current.State != 1)
                { waiting.Add(item); waitingName = item.Name; }
                else if (now < item.LastChange.AddSeconds(2)) waiting.Add(item);
                else ready.Add(item);
            }
            if (ready.Count == 0)
            {
                Status = waitingName == null ? "Device settling" : "Waiting for device";
                Details = waitingName == null ? "Waiting for the output connection to stabilize." : "Reconnect " + waitingName + ". I will wait until Windows reports it active.";
                return;
            }
            string blocked = guard.AutomaticBlock(now);
            if (blocked != null)
            {
                Status = guard.CircuitOpen(now) ? "Automatic repair paused" : "Repair cooldown"; Details = blocked;
                if (guard.CircuitOpen(now) && !circuitNotified)
                { circuitNotified = true; Notify("Automatic repair paused after 3 attempts in 10 minutes. Click the Bug to repair manually; check the cable if it keeps recurring."); }
                return;
            }
            circuitNotified = false;
            List<string> names = new List<string>();
            foreach (PendingRecovery item in ready) names.Add("A" + (item.Bus + 1));
            if (Repair("Output reconnected: " + String.Join(", ", names.ToArray()), true))
                foreach (PendingRecovery item in waiting) pending[item.Bus] = item;
        }

        public bool Repair(string reason, bool automatic)
        {
            if (disposed) return false;
            DateTime now = clock();
            if (automatic) return RepairCore(reason, true, now);
            bool? accepted = null;
            try
            {
                WriteManualSnapshot("MANUAL_BEFORE", reason, "not attempted");
                bool result = RepairCore(reason, false, now);
                accepted = result;
                return result;
            }
            finally
            {
                WriteManualSnapshot("MANUAL_AFTER", reason, accepted.HasValue ? (accepted.Value ? "accepted" : "not accepted") : "exception before return");
            }
        }

        private bool RepairCore(string reason, bool automatic, DateTime now)
        {
            EnsureServices(now);
            if (remote == null) { Status = "Voicemeeter unavailable"; Details = remoteError; Notify(Details); return false; }
            string blocked = automatic ? guard.AutomaticBlock(now) : guard.ManualBlock(now);
            if (blocked != null) { Status = "Repair cooldown"; Details = blocked; return false; }
            try
            {
                int dirty = remote.Synchronize();
                if (dirty < 0)
                { Connected = false; Status = "Waiting for Voicemeeter"; Details = "Open Voicemeeter, then click me again."; Notify(Details); return false; }
                Connected = true;
                guard.RecordAttempt(now, automatic);
                int result = remote.Restart();
                lastAction = (automatic ? "Automatic" : "Manual") + " at " + now.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + ": " + reason + "; API result " + result;
                Write(lastAction);
                if (result != 0)
                { Status = "Repair not accepted"; Details = "Voicemeeter returned " + result + ". Click again after a few seconds."; Notify(Details); return false; }
                pending.Clear(); ignoreUntil = now.AddSeconds(5); recentActionUntil = now.AddSeconds(8);
                Status = "Restart requested"; Details = "Voicemeeter accepted the audio-engine restart. Sound recovery needs your ears.";
                Notify(automatic ? "A selected output reconnected. Audio-engine restart requested." : "Audio-engine restart requested. Give it a moment.");
                return true;
            }
            catch (Exception ex)
            { Status = "Repair unavailable"; Details = ex.Message; Write("Repair error: " + ex.Message); Notify("Could not request a restart: " + ex.Message); return false; }
        }

        private void UpdateStatus(DateTime now)
        {
            if (now < recentActionUntil) return;
            if (monitor == null || monitorError.Length > 0)
            { Status = "Click-to-fix ready"; Details = "Automatic detector unavailable: " + monitorError; return; }
            if (!baseline) { Status = "Getting ready"; Details = "Learning the selected outputs without restarting audio."; return; }
            Status = Automatic ? "Watching device reconnects" : "Click-to-fix ready";
            int mapped = 0, uncertain = 0; List<string> zero = new List<string>();
            foreach (OutputReading output in outputs)
            {
                if (output.Name.Length == 0) continue;
                if (MatchOutput(output.Name, endpoints) != null) mapped++; else uncertain++;
                if (output.SampleRate <= 0) zero.Add("A" + (output.Bus + 1));
            }
            Details = Automatic ? "Watching " + mapped + " selected output(s)." : "Automatic repair is off. Click me if sound breaks.";
            if (NativeA1AutoRestart) Details += " Voicemeeter handles A1 automatically.";
            if (uncertain > 0) Details += " " + uncertain + " output(s) cannot be matched uniquely.";
            if (zero.Count > 0) Details += " No reported sample rate on " + String.Join(", ", zero.ToArray()) + "; this alone does not trigger a restart.";
        }

        public string Diagnostics()
        {
            DateTime now = clock();
            RememberRawDeviceEvent();
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Voicemeeter Bug diagnostics");
            sb.AppendLine("snapshot_utc=" + Utc(now) + "; source=cached_detector_state");
            sb.AppendLine("last_tick_completed_utc=" + Utc(lastTickCompleted) + "; tick_age_seconds=" + Age(now, lastTickCompleted) + "; last_tick_outcome=" + lastTickOutcome);
            sb.AppendLine("last_successful_remote_poll_utc=" + Utc(lastRemotePoll) + "; last_successful_endpoint_scan_utc=" + Utc(lastEndpointScan));
            sb.AppendLine("Connected: " + Connected + "; automatic: " + Automatic + "; native A1 auto-restart: " + NativeA1AutoRestart);
            sb.AppendLine("Status: " + Status); sb.AppendLine(Details);
            sb.AppendLine("Detector: " + detectorHealth);
            sb.AppendLine("Baseline ready: " + baseline + "; pending outputs: " + pending.Count + "; dropped endpoint events: " + droppedEvents);
            sb.AppendLine("Remote: " + (remote == null ? remoteError : remote.Description));
            AppendOutputs(sb, outputs, endpoints);
            AppendDecisionState(sb, now);
            sb.AppendLine("Last action: " + lastAction);
            sb.AppendLine("Detection limits: Windows may not report analog cable noise or a driver that stays active but sounds robotic. Click the Bug in that case. Silence and sample rate zero never trigger repairs on their own.");
            sb.AppendLine("Protection: 2-second debounce, wait for active endpoint, 30-second automatic cooldown, 3 automatic attempts per 10 minutes; manual clicks have a 5-second cooldown.");
            return sb.ToString();
        }

        // This only reads into local variables. It never ticks/drains the detector or
        // changes its output cache, endpoint cache, pending work, baseline, or guard.
        private void WriteManualSnapshot(string phase, string reason, string outcome)
        {
            try
            {
                DateTime started = clock();
                StringBuilder sb = new StringBuilder();
                sb.AppendLine(phase + " snapshot_utc=" + Utc(started) + "; reason=" + reason + "; outcome=" + outcome + "; source=fresh_read_only");
                OutputReading[] freshOutputs = null;
                List<Endpoint> freshEndpoints = null;
                try
                {
                    if (remote == null) sb.AppendLine("fresh_remote=unavailable; " + remoteError);
                    else
                    {
                        int sync = remote.Synchronize();
                        sb.AppendLine("fresh_remote_synchronize=" + sync);
                        if (sync >= 0) freshOutputs = remote.ReadOutputs();
                    }
                }
                catch (Exception ex) { sb.AppendLine("fresh_remote_error=" + ex.Message); }
                try
                {
                    if (monitor == null) sb.AppendLine("fresh_endpoints=unavailable; " + monitorError);
                    else { freshEndpoints = monitor.Snapshot(); sb.AppendLine("fresh_endpoint_count=" + freshEndpoints.Count); }
                }
                catch (Exception ex) { sb.AppendLine("fresh_endpoint_error=" + ex.Message); }
                if (freshOutputs != null) AppendOutputs(sb, freshOutputs, freshEndpoints);
                else sb.AppendLine("fresh_selected_outputs=unavailable; cached values below are not a fresh reading");
                sb.AppendLine("fresh_snapshot_completed_utc=" + Utc(clock()));
                sb.AppendLine(Diagnostics());
                Write(sb.ToString());
            }
            catch (Exception ex) { Write(phase + " snapshot_utc=" + Utc(clock()) + "; snapshot_error=" + ex.Message); }
        }

        private static void AppendOutputs(StringBuilder sb, OutputReading[] selected, IList<Endpoint> devices)
        {
            foreach (OutputReading output in selected)
            {
                if (output.Name.Length == 0) continue;
                Endpoint match = devices == null ? null : MatchOutput(output.Name, devices);
                sb.AppendLine("A" + (output.Bus + 1) + ": " + output.Name + "; sample rate " + output.SampleRate.ToString(CultureInfo.InvariantCulture)
                    + "; endpoint " + (devices == null ? "snapshot unavailable" : match == null ? "not uniquely matched" : match.Name + " (state " + match.State + ")")
                    + "; endpoint_id=" + (match == null ? "unavailable" : match.Id));
            }
        }

        private void AppendDecisionState(StringBuilder sb, DateTime now)
        {
            EndpointEvent raw = lastRawDeviceEvent;
            sb.AppendLine(raw == null ? "last_raw_device_event=none observed" : "last_raw_device_event_utc=" + Utc(raw.TimestampUtc) + "; kind=" + raw.Kind + "; endpoint_id=" + raw.Id + "; state=" + raw.State + "; detail=" + (raw.Detail ?? ""));
            List<string> reasons = new List<string>();
            if (!Automatic) reasons.Add("automatic disabled");
            if (!Connected) reasons.Add("Voicemeeter not connected");
            if (!baseline) reasons.Add("baseline not ready; earliest=" + Utc(baselineUntil));
            if (monitor == null) reasons.Add("endpoint watcher unavailable");
            if (monitorError.Length > 0) reasons.Add("endpoint scan error: " + monitorError);
            if (now < ignoreUntil) reasons.Add("post-restart suppression until=" + Utc(ignoreUntil));
            if (pending.Count == 0) reasons.Add("no selected-output transition pending");
            sb.AppendLine("automatic_gate_reasons=" + (reasons.Count == 0 ? "none" : String.Join(" | ", reasons.ToArray())));
            sb.AppendLine(guard.DescribeReadOnly(now));
            foreach (PendingRecovery item in pending.Values)
            {
                Endpoint current = MatchOutput(item.Name, endpoints);
                string disposition = current == null ? "endpoint not uniquely matched" : current.State != 1 ? "waiting for active endpoint" : now < item.LastChange.AddSeconds(2) ? "two-second debounce" : "device ready; subject to automatic gates and guard";
                sb.AppendLine("pending=A" + (item.Bus + 1) + "; name=" + item.Name + "; event_endpoint_id=" + item.Id + "; last_change_utc=" + Utc(item.LastChange) + "; reason=" + disposition);
            }
            sb.AppendLine("native_a1_ownership=" + (NativeA1AutoRestart ? "A1 excluded; relies on configured native auto-restart, not read back from Voicemeeter" : "Bug watches A1"));
            sb.AppendLine("alive semantics: a completed tick is not proof of healthy audio; gaps can mean a stopped or stalled loop or missing logging.");
        }

        private static string Utc(DateTime value)
        { return value == DateTime.MinValue ? "never" : value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture); }
        private static string Age(DateTime now, DateTime previous)
        { return previous == DateTime.MinValue ? "unavailable" : Math.Max(0, (now - previous).TotalSeconds).ToString("0.000", CultureInfo.InvariantCulture); }

        internal static Endpoint MatchOutput(string selected, IList<Endpoint> candidates)
        {
            if (String.IsNullOrWhiteSpace(selected)) return null;
            Endpoint found = null; int count = 0;
            foreach (Endpoint endpoint in candidates)
                if (String.Equals(selected.Trim(), endpoint.Name.Trim(), StringComparison.OrdinalIgnoreCase)) { found = endpoint; count++; }
            if (count != 0) return count == 1 ? found : null;
            // Voicemeeter may truncate device names to 31 characters. Short generic names are not prefixes.
            if (selected.Trim().Length < 30) return null;
            foreach (Endpoint endpoint in candidates)
                if (endpoint.Name.StartsWith(selected.Trim(), StringComparison.OrdinalIgnoreCase)) { found = endpoint; count++; }
            return count == 1 ? found : null;
        }
        private OutputReading FindOutput(int bus)
        { foreach (OutputReading output in outputs) if (output.Bus == bus) return output; return null; }
        private static bool SameSelections(OutputReading[] a, OutputReading[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i].Bus != b[i].Bus || !String.Equals(a[i].Name, b[i].Name, StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }
        private static bool ContainsId(List<Endpoint> list, string id)
        { foreach (Endpoint ep in list) if (String.Equals(ep.Id, id, StringComparison.OrdinalIgnoreCase)) return true; return false; }
        private static bool HasRemoval(List<EndpointEvent> list, string id)
        { foreach (EndpointEvent ev in list) if (ev.Kind == EndpointEventKind.Removed && String.Equals(ev.Id, id, StringComparison.OrdinalIgnoreCase)) return true; return false; }
        private void Notify(string value) { Action<string> handler = Notification; if (handler != null) { try { handler(value); } catch { } } }
        private void Write(string value) { try { log(value); } catch { } }
        public void Dispose()
        { if (disposed) return; disposed = true; if (monitor != null) monitor.Dispose(); if (remote != null) remote.Dispose(); }
        private sealed class PendingRecovery { internal int Bus; internal string Id, Name; internal DateTime LastChange; }
    }

    internal sealed class RestartGuard
    {
        private readonly Queue<DateTime> attempts = new Queue<DateTime>();
        private DateTime lastAttempt = DateTime.MinValue;
        private void Prune(DateTime now) { while (attempts.Count > 0 && now - attempts.Peek() >= TimeSpan.FromMinutes(10)) attempts.Dequeue(); }
        internal bool CircuitOpen(DateTime now) { Prune(now); return attempts.Count >= 3; }
        internal string ManualBlock(DateTime now) { return now < lastAttempt.AddSeconds(5) ? "Wait five seconds between repair attempts." : null; }
        internal string AutomaticBlock(DateTime now)
        {
            if (CircuitOpen(now)) return "Three automatic attempts in ten minutes. Click for manual repair, or wait for the automatic limit to clear.";
            if (now < lastAttempt.AddSeconds(30)) return "Waiting for the 30-second cooldown; the device event is retained.";
            return null;
        }
        internal void RecordAttempt(DateTime now, bool automatic) { lastAttempt = now; if (automatic) attempts.Enqueue(now); }
        internal string DescribeReadOnly(DateTime now)
        {
            int recent = 0;
            foreach (DateTime attempt in attempts) if (now - attempt < TimeSpan.FromMinutes(10)) recent++;
            double manualRemaining = lastAttempt == DateTime.MinValue ? 0 : Math.Max(0, (lastAttempt.AddSeconds(5) - now).TotalSeconds);
            double automaticRemaining = lastAttempt == DateTime.MinValue ? 0 : Math.Max(0, (lastAttempt.AddSeconds(30) - now).TotalSeconds);
            return "guard: manual_cooldown_remaining_seconds=" + manualRemaining.ToString("0.000", CultureInfo.InvariantCulture)
                + "; automatic_cooldown_remaining_seconds=" + automaticRemaining.ToString("0.000", CultureInfo.InvariantCulture)
                + "; automatic_attempts_in_ten_minutes=" + recent + "; circuit_open=" + (recent >= 3);
        }
    }
    internal sealed class OutputReading
    {
        internal readonly int Bus; internal readonly string Name; internal readonly float SampleRate;
        internal OutputReading(int bus, string name, float rate) { Bus = bus; Name = (name ?? "").Trim(); SampleRate = rate; }
    }
    internal sealed class Endpoint
    {
        internal readonly string Id, Name; internal readonly uint State;
        internal Endpoint(string id, string name, uint state) { Id = id; Name = name; State = state; }
    }
    internal enum EndpointEventKind { State, Added, Removed, DefaultChanged, PropertyChanged }
    internal sealed class EndpointEvent
    { internal string Id, Detail; internal uint State; internal EndpointEventKind Kind; internal DateTime TimestampUtc; }
    internal interface IVoiceRemote : IDisposable
    { string Description { get; } int Synchronize(); OutputReading[] ReadOutputs(); int Restart(); }
    internal interface IEndpointMonitor : IDisposable
    { List<Endpoint> Snapshot(); List<EndpointEvent> Drain(); int DroppedEvents { get; } EndpointEvent LastEvent { get; } }

    internal sealed class VoicemeeterRemote : IVoiceRemote
    {
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int NoArgs();
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetTypeDelegate(out int value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetFloat([MarshalAs(UnmanagedType.LPStr)] string parameter, out float value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetFloat([MarshalAs(UnmanagedType.LPStr)] string parameter, float value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)] private delegate int GetString([MarshalAs(UnmanagedType.LPStr)] string parameter, [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder value);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)] private static extern IntPtr GetProcAddress(IntPtr module, string name);
        [DllImport("kernel32.dll")] private static extern bool FreeLibrary(IntPtr module);
        private IntPtr library;
        private NoArgs logout, dirty;
        private GetTypeDelegate type;
        private GetFloat getFloat;
        private GetString getString;
        private SetFloat setFloat;
        private bool loggedIn;
        public string Description { get; private set; }
        internal VoicemeeterRemote()
        {
            string path = FindLibrary();
            library = LoadLibraryEx(path, IntPtr.Zero, 0x100 | 0x800); // DLL directory + System32 only.
            if (library == IntPtr.Zero) throw new InvalidOperationException("Could not load the installed Voicemeeter Remote API (Windows error " + Marshal.GetLastWin32Error() + ").");
            try
            {
                NoArgs login = Bind<NoArgs>("VBVMR_Login"); logout = Bind<NoArgs>("VBVMR_Logout"); dirty = Bind<NoArgs>("VBVMR_IsParametersDirty");
                type = Bind<GetTypeDelegate>("VBVMR_GetVoicemeeterType"); getFloat = Bind<GetFloat>("VBVMR_GetParameterFloat");
                getString = Bind<GetString>("VBVMR_GetParameterStringW"); setFloat = Bind<SetFloat>("VBVMR_SetParameterFloat");
                int result = login();
                if (result != 0 && result != 1) throw new InvalidOperationException("Voicemeeter Remote API login failed: " + result);
                loggedIn = true; Description = path + " (login " + result + ")";
            }
            catch { Dispose(); throw; }
        }
        private T Bind<T>(string name) where T : class
        {
            IntPtr function = GetProcAddress(library, name);
            if (function == IntPtr.Zero) throw new InvalidOperationException("Voicemeeter API export missing: " + name);
            return Marshal.GetDelegateForFunctionPointer(function, typeof(T)) as T;
        }
        private static string FindLibrary()
        {
            string name = IntPtr.Size == 8 ? "VoicemeeterRemote64.dll" : "VoicemeeterRemote.dll";
            List<string> folders = new List<string>();
            folders.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "VB", "Voicemeeter"));
            foreach (RegistryView view in new RegistryView[] { RegistryView.Registry32, RegistryView.Registry64 })
            {
                try
                {
                    using (RegistryKey machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                    using (RegistryKey key = machine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\VB:Voicemeeter {17359A74-1236-5467}"))
                    {
                        if (key == null) continue;
                        string location = key.GetValue("InstallLocation") as string;
                        if (!String.IsNullOrWhiteSpace(location)) folders.Add(location.Trim('"'));
                        string uninstall = key.GetValue("UninstallString") as string;
                        if (!String.IsNullOrWhiteSpace(uninstall))
                        {
                            string executable = uninstall.Trim();
                            if (executable.StartsWith("\"")) executable = executable.Substring(1).Split('"')[0];
                            else { int end = executable.IndexOf(".exe", StringComparison.OrdinalIgnoreCase); if (end >= 0) executable = executable.Substring(0, end + 4); }
                            string directory = Path.GetDirectoryName(executable); if (!String.IsNullOrEmpty(directory)) folders.Add(directory);
                        }
                    }
                }
                catch (Exception) { }
            }
            foreach (string folder in folders)
            { string candidate = Path.GetFullPath(Path.Combine(folder, name)); if (File.Exists(candidate)) return candidate; }
            throw new FileNotFoundException("Install Voicemeeter to enable repair. The installed Remote API DLL was not found.");
        }
        public int Synchronize() { return dirty(); }
        public OutputReading[] ReadOutputs()
        {
            int edition; int result = type(out edition);
            if (result < 0) throw new InvalidOperationException("Unable to read Voicemeeter edition: " + result);
            int count = edition == 1 ? 1 : edition == 2 ? 3 : edition == 3 ? 5 : 0;
            if (count == 0) throw new InvalidOperationException("Unknown Voicemeeter edition " + edition);
            OutputReading[] values = new OutputReading[count];
            for (int bus = 0; bus < count; bus++)
            {
                StringBuilder name = new StringBuilder(1024); float rate;
                int nr = getString("Bus[" + bus + "].device.name", name);
                int sr = getFloat("Bus[" + bus + "].device.sr", out rate);
                if (nr != 0) throw new InvalidOperationException("Unable to read A" + (bus + 1) + " device: " + nr);
                values[bus] = new OutputReading(bus, name.ToString(), sr == 0 ? rate : -1);
            }
            return values;
        }
        public int Restart() { return setFloat("Command.Restart", 1f); }
        public void Dispose()
        {
            if (loggedIn) { loggedIn = false; try { logout(); } catch { } }
            if (library != IntPtr.Zero) { FreeLibrary(library); library = IntPtr.Zero; }
        }
    }

    internal sealed class EndpointMonitor : IEndpointMonitor
    {
        private IMMDeviceEnumerator enumerator;
        private EndpointNotifications callbacks;
        internal EndpointMonitor()
        {
            try
            {
                enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
                callbacks = new EndpointNotifications();
                Marshal.ThrowExceptionForHR(enumerator.RegisterEndpointNotificationCallback(callbacks));
            }
            catch { Dispose(); throw; }
        }
        public int DroppedEvents { get { return callbacks == null ? 0 : callbacks.Dropped; } }
        public EndpointEvent LastEvent { get { return callbacks == null ? null : callbacks.LastEvent; } }
        public List<EndpointEvent> Drain() { return callbacks.Drain(); }
        public List<Endpoint> Snapshot()
        {
            List<Endpoint> result = new List<Endpoint>(); IMMDeviceCollection collection = null;
            try
            {
                Marshal.ThrowExceptionForHR(enumerator.EnumAudioEndpoints(0, 15, out collection));
                uint count; Marshal.ThrowExceptionForHR(collection.GetCount(out count));
                for (uint i = 0; i < count; i++)
                {
                    IMMDevice device = null; IPropertyStore properties = null;
                    try
                    {
                        Marshal.ThrowExceptionForHR(collection.Item(i, out device));
                        string id; uint state; Marshal.ThrowExceptionForHR(device.GetId(out id)); Marshal.ThrowExceptionForHR(device.GetState(out state));
                        Marshal.ThrowExceptionForHR(device.OpenPropertyStore(0, out properties));
                        PropertyKey key = new PropertyKey(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);
                        PropVariant value; Marshal.ThrowExceptionForHR(properties.GetValue(ref key, out value));
                        string name;
                        try { name = value.Type == 31 ? Marshal.PtrToStringUni(value.Pointer) : null; }
                        finally { PropVariantClear(ref value); }
                        if (!String.IsNullOrEmpty(name)) result.Add(new Endpoint(id, name, state));
                    }
                    finally { Release(properties); Release(device); }
                }
            }
            finally { Release(collection); }
            return result;
        }
        public void Dispose()
        {
            if (enumerator != null && callbacks != null) { try { enumerator.UnregisterEndpointNotificationCallback(callbacks); } catch { } }
            Release(enumerator); enumerator = null; callbacks = null;
        }
        private static void Release(object obj) { if (obj != null && Marshal.IsComObject(obj)) Marshal.ReleaseComObject(obj); }
        [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropVariant value);
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    internal sealed class EndpointNotifications : IMMNotificationClient
    {
        private readonly ConcurrentQueue<EndpointEvent> events = new ConcurrentQueue<EndpointEvent>();
        private int count, dropped;
        private EndpointEvent lastEvent;
        internal int Dropped { get { return Interlocked.CompareExchange(ref dropped, 0, 0); } }
        internal EndpointEvent LastEvent { get { return Interlocked.CompareExchange(ref lastEvent, null, null); } }
        private EndpointEvent Remember(string id, uint state, EndpointEventKind kind, string detail)
        {
            EndpointEvent item = new EndpointEvent { Id = id ?? "(none)", State = state, Kind = kind, Detail = detail, TimestampUtc = DateTime.UtcNow };
            Interlocked.Exchange(ref lastEvent, item);
            return item;
        }
        private void Enqueue(string id, uint state, EndpointEventKind kind)
        {
            EndpointEvent item = Remember(id, state, kind, "");
            if (String.IsNullOrEmpty(id)) return;
            if (Interlocked.Increment(ref count) > 128) { Interlocked.Decrement(ref count); Interlocked.Increment(ref dropped); return; }
            events.Enqueue(item);
        }
        internal List<EndpointEvent> Drain()
        {
            List<EndpointEvent> result = new List<EndpointEvent>(); EndpointEvent item;
            while (events.TryDequeue(out item)) { Interlocked.Decrement(ref count); result.Add(item); }
            return result;
        }
        public int OnDeviceStateChanged(string id, uint state) { Enqueue(id, state, EndpointEventKind.State); return 0; }
        public int OnDeviceAdded(string id) { Enqueue(id, 0, EndpointEventKind.Added); return 0; }
        public int OnDeviceRemoved(string id) { Enqueue(id, 4, EndpointEventKind.Removed); return 0; }
        public int OnDefaultDeviceChanged(int flow, int role, string id) { Remember(id, 0, EndpointEventKind.DefaultChanged, "flow=" + flow + "; role=" + role + "; diagnostic only"); return 0; }
        public int OnPropertyValueChanged(string id, PropertyKey key) { Remember(id, 0, EndpointEventKind.PropertyChanged, "property=" + key.Format + "/" + key.Id + "; diagnostic only"); return 0; }
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct PropertyKey
    { internal Guid Format; internal uint Id; internal PropertyKey(Guid format, uint id) { Format = format; Id = id; } }
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    internal struct PropVariant
    { [FieldOffset(0)] internal ushort Type; [FieldOffset(8)] internal IntPtr Pointer; }
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] internal class MMDeviceEnumerator { }
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, uint mask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IMMNotificationClient client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IMMNotificationClient client);
    }
    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceCollection
    { [PreserveSig] int GetCount(out uint count); [PreserveSig] int Item(uint index, out IMMDevice device); }
    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid id, uint context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object result);
        [PreserveSig] int OpenPropertyStore(uint access, out IPropertyStore properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out uint state);
    }
    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }
    [ComVisible(true), Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMNotificationClient
    {
        [PreserveSig] int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string id, uint state);
        [PreserveSig] int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int OnDefaultDeviceChanged(int flow, int role, [MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string id, PropertyKey key);
    }
}
