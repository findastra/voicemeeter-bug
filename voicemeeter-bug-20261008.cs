using System;
using System.IO;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace VoicemeeterBug {
    internal static class Program {
        internal const string AppName = "Voicemeeter Bug";
        internal static bool TestUi;
        internal static bool TestGestures;
        internal static bool InitialHosted, InitialRepair, InitialStatus;
        internal static readonly string Root = AppDomain.CurrentDomain.BaseDirectory;
        internal static readonly string Data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "findastra", "voicemeeter-bug");
        internal static readonly string SettingsPath = Path.Combine(Data, "preferences-20261008.txt");
        internal static string Day { get { return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Mountain Standard Time")).ToString("yyyyMMdd"); } }
        internal static readonly BoundedTelemetryLog Telemetry = new BoundedTelemetryLog(Data);
        internal static string LogMode = "starting";
        internal static string LogPath { get { return Telemetry.CurrentPath; } }
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
        internal static void Log(string message) {
            Telemetry.Write(message, LogMode);
        }
        [STAThread] static void Main(string[] args) {
            // Self-test must exit before creating the live engine, UI, or single-instance signals.
            if(Array.IndexOf(args,"--self-test")>=0) { SelfTest(); return; }
            Directory.CreateDirectory(Data);
            TestUi=Array.IndexOf(args,"--test-ui")>=0;
            TestGestures=Array.IndexOf(args,"--test-gestures")>=0;
            InitialHosted=Array.IndexOf(args,"--hosted")>=0;
            InitialRepair=Array.IndexOf(args,"--repair")>=0;
            InitialStatus=Array.IndexOf(args,"--status")>=0;
            SetProcessDPIAware();
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            if (Array.IndexOf(args, "--diagnose") >= 0) {
                LogMode="diagnostic-probe";
                using(var engine = new RecoveryEngine(Log)) {
                    engine.Automatic = false;
                    for(int i=0;i<5;i++) { engine.Tick(); Thread.Sleep(1000); }
                    File.WriteAllText(Path.Combine(Data, "diagnostics-" + Day + ".txt"), engine.Diagnostics());
                }
                return;
            }
            if (Array.IndexOf(args, "--preview") >= 0) {
                using(var bmp=new Bitmap(610,230)) using(var g=Graphics.FromImage(bmp)) {
                    g.Clear(Color.FromArgb(27,24,38));
                    g.TranslateTransform(28,45); g.ScaleTransform(2.5f,2.5f); BugArt.Draw(g,0,true,false); g.ResetTransform();
                    g.TranslateTransform(197,76); BugArt.Star(g,40); g.ResetTransform();
                    using(var font=new Font("Segoe UI",13,FontStyle.Bold)) g.DrawString("Voicemeeter Bug",font,Brushes.White,195,28);
                    using(var font=new Font("Segoe UI",10)) g.DrawString("Click the Bug to restart audio.\nDrag to move. Star to hide/show.\nRight-click for controls.",font,Brushes.LightGray,195,137);
                    bmp.Save(Path.Combine(Root,"voicemeeter-bug-preview-20261008.png"),System.Drawing.Imaging.ImageFormat.Png);
                }
                using(var icon=BugArt.MakeIcon())using(var file=File.Create(Path.Combine(Root,"voicemeeter-bug-icon-20261008.ico")))icon.Save(file);
                return;
            }
            bool owns; using(var mutex=new Mutex(true,"Local\\Findastra.VoicemeeterBug.20261008",out owns)) {
                if(!owns || Array.IndexOf(args,"--quit")>=0) {
                    string action=Array.IndexOf(args,"--quit")>=0?"Quit":Array.IndexOf(args,"--check")>=0?"Check":Array.IndexOf(args,"--status")>=0?"Status":InitialHosted?"Hosted":InitialRepair?"Repair":"Show";
                    try { using(var ev=EventWaitHandle.OpenExisting("Local\\Findastra.VoicemeeterBug."+action)) ev.Set(); } catch { }
                    return;
                }
                try { using(var app=new BugContext()) Application.Run(app); }
                catch(Exception ex) { Log("FATAL " + ex); MessageBox.Show("The Bug could not start.\n"+ex.Message,AppName); }
            }
        }
        static void SelfTest() {
            var output = new StringWriter(CultureInfo.InvariantCulture);
            TextWriter original = Console.Out;
            bool passed = false;
            try {
                Console.SetOut(output);
                RecoveryTests.Run();
                TelemetryLogTests.Run();
                passed = true;
            } catch(Exception ex) { output.WriteLine("SELF-TEST FAILED: " + ex); }
            finally { Console.SetOut(original); }
            string reportDirectory=Path.Combine(Root,"bin");
            string report=Path.Combine(reportDirectory,"self-test-results-"+Day+".json");
            try {
                Directory.CreateDirectory(reportDirectory);
                File.WriteAllText(report, new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(new {
                    passed=passed, timestamp=DateTimeOffset.Now.ToString("o"), live_audio_used=false, results=output.ToString()
                }));
                Console.WriteLine(output.ToString()); Console.WriteLine("Report: "+report);
            } catch(Exception ex) { passed=false; Console.Error.WriteLine("Could not write self-test report: "+ex.Message); }
            Environment.ExitCode=passed?0:1;
        }
    }

    internal sealed class BugContext : ApplicationContext {
        readonly RecoveryEngine engine;
        readonly PetWindow bug, star;
        readonly NotifyIcon tray;
        readonly ContextMenuStrip menu;
        readonly ToolStripMenuItem automaticItem, startupItem, primaryItem;
        readonly System.Windows.Forms.Timer tick = new System.Windows.Forms.Timer();
        readonly System.Windows.Forms.Timer animation = new System.Windows.Forms.Timer();
        readonly EventWaitHandle showEvent = new EventWaitHandle(false,EventResetMode.AutoReset,"Local\\Findastra.VoicemeeterBug.Show");
        readonly EventWaitHandle quitEvent = new EventWaitHandle(false,EventResetMode.AutoReset,"Local\\Findastra.VoicemeeterBug.Quit");
        readonly EventWaitHandle checkEvent = new EventWaitHandle(false,EventResetMode.AutoReset,"Local\\Findastra.VoicemeeterBug.Check");
        readonly EventWaitHandle statusEvent = new EventWaitHandle(false,EventResetMode.AutoReset,"Local\\Findastra.VoicemeeterBug.Status");
        readonly EventWaitHandle hostedEvent = new EventWaitHandle(false,EventResetMode.AutoReset,"Local\\Findastra.VoicemeeterBug.Hosted");
        readonly EventWaitHandle repairEvent = new EventWaitHandle(false,EventResetMode.AutoReset,"Local\\Findastra.VoicemeeterBug.Repair");
        readonly Stopwatch clock = Stopwatch.StartNew();
        readonly Icon trayIcon;
        Point origin;
        bool hidden, hosted, exiting, hotkeyRegistered, resourcesDisposed;
        IntPtr hotkeyHandle;
        long repairAnimationUntil;
        string lastFeedback="";
        long feedbackUntil;
        string lastTip = "";
        Form detailsWindow;
        const string RunKey="Software\\Microsoft\\Windows\\CurrentVersion\\Run";
        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hwnd,int id,uint modifiers,uint key);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hwnd,int id);
        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr handle);
        internal BugContext() {
            engine=new RecoveryEngine(Program.Log);
            var working=Screen.PrimaryScreen.WorkingArea;
            origin=new Point(working.Left+300,working.Bottom-222);
            Load();
            if(Program.InitialHosted)hosted=true;
            Program.LogMode=hosted?"hosted":"standalone";
            bug=new PetWindow(false); star=new PetWindow(true);
            bug.PetClick+=delegate { Repair("pet click"); };
            star.PetClick+=delegate { ToggleBug(); };
            bug.DragDelta+=MoveGroup; star.DragDelta+=MoveGroup;
            bug.DragEnded+=FinishDrag; star.DragEnded+=FinishDrag;
            bug.Hotkey+=delegate { Repair("Ctrl+Alt+F10"); };
            menu=new ContextMenuStrip();
            menu.Items.Add("Fix audio now   Ctrl+Alt+F10",null,delegate { Repair("menu"); });
            menu.Items.Add("Show Bug here",null,delegate { Reveal(true); });
            menu.Items.Add("Hide / show Bug",null,delegate { ToggleBug(); });
            menu.Items.Add(new ToolStripSeparator());
            automaticItem=new ToolStripMenuItem("Automatic reconnect repair") { Checked=engine.Automatic, CheckOnClick=true };
            automaticItem.Click+=delegate { engine.Automatic=automaticItem.Checked; Program.Log("Automatic="+engine.Automatic); Save(); };
            menu.Items.Add(automaticItem);
            primaryItem=new ToolStripMenuItem("Also watch A1 (disable native A1 auto first)") { Checked=!engine.NativeA1AutoRestart, CheckOnClick=true };
            primaryItem.Click+=delegate { engine.NativeA1AutoRestart=!primaryItem.Checked; Save(); };
            menu.Items.Add(primaryItem);
            startupItem=new ToolStripMenuItem("Start with Windows") { Checked=StartsWithWindows(), CheckOnClick=true };
            startupItem.Click+=delegate { SetStartup(startupItem.Checked); };
            menu.Items.Add(startupItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Status and help",null,delegate { ShowDetails(); });
            menu.Items.Add("Open local recovery log",null,delegate { Program.Log("Log opened"); Process.Start(new ProcessStartInfo(Program.LogPath){UseShellExecute=true}); });
            menu.Items.Add("Quit Bug",null,delegate { Shutdown(); });
            menu.Opening+=delegate { automaticItem.Checked=engine.Automatic; startupItem.Checked=StartsWithWindows(); primaryItem.Checked=!engine.NativeA1AutoRestart; };
            bug.ContextMenuStrip=menu; star.ContextMenuStrip=menu;
            trayIcon=MakeIcon();
            tray=new NotifyIcon { Icon=trayIcon,Text=Program.AppName,Visible=true,ContextMenuStrip=menu };
            tray.MouseClick+=delegate(object sender,MouseEventArgs e) { if(e.Button==MouseButtons.Left) Reveal(true); };
            engine.Notification+=OnNotification;
            Clamp(); Position(); if(!hosted){star.Show(); if(!hidden)bug.Show();}
            hotkeyHandle=bug.Handle;
            hotkeyRegistered=RegisterHotKey(hotkeyHandle,1,0x4003,0x79);
            Program.Log("START UI; hotkey="+hotkeyRegistered+"; automatic="+engine.Automatic);
            if(!hotkeyRegistered) OnNotification("Ctrl+Alt+F10 is already in use. Click the Bug to repair audio.");
            tick.Interval=1000; tick.Tick+=delegate { Update(); }; tick.Start();
            animation.Interval=200; animation.Tick+=delegate { bug.Phase=star.Phase=clock.ElapsedMilliseconds; bug.Repairing=clock.ElapsedMilliseconds<repairAnimationUntil; if(bug.Visible)bug.Invalidate(); if(star.Visible)star.Invalidate(); }; animation.Start();
            SystemEvents.DisplaySettingsChanged+=DisplayChanged;
            Update();
            if(Program.InitialRepair)Repair("desktop pets click");
            if(Program.InitialStatus)ShowDetails();
        }
        void Update() {
            if(quitEvent.WaitOne(0)) { Shutdown(); return; }
            if(showEvent.WaitOne(0)) Reveal(true);
            if(hostedEvent.WaitOne(0)){hosted=true;Program.LogMode="hosted";bug.Hide();star.Hide();Save();}
            engine.Tick(); bug.Connected=engine.Connected;
            if(repairEvent.WaitOne(0)){Repair("desktop pets click");return;}
            if(statusEvent.WaitOne(0))ShowDetails();
            if(checkEvent.WaitOne(0))File.WriteAllText(Path.Combine(Program.Data,"ui-check-"+Program.Day+".txt"),"Bug visible: "+bug.Visible+"; topmost: "+bug.TopMost+"; fully on screen: "+Screen.FromControl(bug).WorkingArea.Contains(bug.Bounds)+"\r\nStar visible: "+star.Visible+"; topmost: "+star.TopMost+"; fully on screen: "+Screen.FromControl(star).WorkingArea.Contains(star.Bounds)+"\r\nHotkey registered: "+hotkeyRegistered+"; start with Windows: "+StartsWithWindows()+"\r\n"+engine.Diagnostics());
            automaticItem.Checked=engine.Automatic;
            string caption=clock.ElapsedMilliseconds<feedbackUntil ? lastFeedback : clock.ElapsedMilliseconds<repairAnimationUntil ? "Restart requested" : engine.Connected ? "Click to fix audio" : "Voicemeeter offline";
            bug.Caption=caption;
            string tip="Voicemeeter Bug\n"+engine.Status+"\nClick: restart audio | Drag: move | Right-click: controls";
            if(lastTip!=tip) { bug.SetTip(tip); lastTip=tip; }
            star.SetTip(hidden?"Show Voicemeeter Bug. Drag to move. Right-click for controls.":"Hide Voicemeeter Bug. Drag to move. Right-click for controls.");
            tray.Text="Voicemeeter Bug - "+(engine.Connected?(engine.Automatic?"Watching device reconnects":"manual mode"):"Voicemeeter offline");
        }
        void OnNotification(string message) {
            Program.Log("NOTICE "+message);
            if(message.IndexOf("restart requested",StringComparison.OrdinalIgnoreCase)>=0)repairAnimationUntil=clock.ElapsedMilliseconds+4000;
            tray.BalloonTipTitle=Program.AppName;tray.BalloonTipText=message;tray.ShowBalloonTip(3500);
        }
        void Repair(string reason) {
            if(Program.TestGestures){Program.Log("TEST repair gesture: "+reason+"; no repair requested");return;}
            if(engine.Repair(reason,false)) {feedbackUntil=0;repairAnimationUntil=clock.ElapsedMilliseconds+4000;}
            else {lastFeedback=engine.Status=="Repair cooldown"?"Wait a moment":"See status / help";feedbackUntil=clock.ElapsedMilliseconds+3500;}
            Update();
        }
        void MoveGroup(Point delta) { origin.Offset(delta); Position(); }
        void FinishDrag() { Clamp(); Position(); Save(); Program.Log("Pet drag finished; no repair requested"); }
        void Position() { bug.Location=origin; star.Location=new Point(origin.X+76,origin.Y+10); }
        void Clamp() {
            Rectangle work=Screen.FromPoint(new Point(origin.X+52,origin.Y+26)).WorkingArea;
            origin.X=Math.Max(work.Left,Math.Min(origin.X,work.Right-128));
            origin.Y=Math.Max(work.Top,Math.Min(origin.Y,work.Bottom-72));
        }
        void DisplayChanged(object sender,EventArgs e) { if(!exiting && star.IsHandleCreated) try { star.BeginInvoke((Action)delegate { Clamp();Position();Save(); }); } catch(InvalidOperationException) { } }
        void Reveal(bool resetPosition) {
            if(resetPosition) { var w=Screen.FromPoint(Cursor.Position).WorkingArea;origin=new Point(w.Left+300,w.Bottom-222); }
            hosted=false;Program.LogMode="standalone";hidden=false;Clamp();Position();star.Show();bug.Show();star.TopMost=true;bug.TopMost=true;Save();
        }
        void ToggleBug() { hosted=false;Program.LogMode="standalone";hidden=!hidden; if(hidden)bug.Hide();else bug.Show();star.Show();Save(); }
        void Load() {
            try { if(!File.Exists(Program.SettingsPath))return; foreach(var line in File.ReadAllLines(Program.SettingsPath)) { var bits=line.Split('=');if(bits.Length!=2)continue; int number; bool flag;
                if(bits[0]=="x"&&int.TryParse(bits[1],out number))origin.X=number;
                if(bits[0]=="y"&&int.TryParse(bits[1],out number))origin.Y=number;
                if(bits[0]=="automatic"&&bool.TryParse(bits[1],out flag))engine.Automatic=flag;
                if(bits[0]=="native-a1"&&bool.TryParse(bits[1],out flag))engine.NativeA1AutoRestart=flag;
                if(bits[0]=="hidden"&&bool.TryParse(bits[1],out flag))hidden=flag;
                if(bits[0]=="hosted"&&bool.TryParse(bits[1],out flag))hosted=flag;
            } } catch(Exception e) { Program.Log("Preferences read: "+e.Message); }
        }
        void Save() { try { File.WriteAllLines(Program.SettingsPath,new[]{"x="+origin.X,"y="+origin.Y,"automatic="+engine.Automatic,"native-a1="+engine.NativeA1AutoRestart,"hidden="+hidden,"hosted="+hosted}); }catch(Exception e){Program.Log("Preferences save: "+e.Message);} }
        bool StartsWithWindows() { using(var key=Registry.CurrentUser.OpenSubKey(RunKey)){string value=key==null?null:key.GetValue("findastra-voicemeeter-bug") as string;string exe="\""+Application.ExecutablePath+"\"";return String.Equals(value,exe,StringComparison.OrdinalIgnoreCase)||String.Equals(value,exe+" --hosted",StringComparison.OrdinalIgnoreCase);} }
        void SetStartup(bool enabled) {
            try { using(var key=Registry.CurrentUser.CreateSubKey(RunKey)) { if(enabled)key.SetValue("findastra-voicemeeter-bug","\""+Application.ExecutablePath+"\""+(hosted?" --hosted":""));else key.DeleteValue("findastra-voicemeeter-bug",false); }Program.Log("Startup="+enabled); }
            catch(Exception e){OnNotification("Could not change sign-in startup: "+e.Message);} startupItem.Checked=StartsWithWindows();
        }
        void ShowDetails() {
            if(detailsWindow!=null&&!detailsWindow.IsDisposed){detailsWindow.Activate();return;}
            detailsWindow=new Form {Text="Voicemeeter Bug - status and help",ClientSize=new Size(650,520),StartPosition=FormStartPosition.CenterScreen,TopMost=true,BackColor=Color.FromArgb(28,33,39)};
            var info=new TextBox {Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical,Dock=DockStyle.Fill,BorderStyle=BorderStyle.None,BackColor=detailsWindow.BackColor,ForeColor=Color.FromArgb(236,234,243),Font=new Font("Segoe UI",10),Margin=new Padding(18)};
            var refresh=new Button {Text="Refresh status",Dock=DockStyle.Bottom,Height=36};
            Action refreshText=delegate {info.Text="VOICEMEETER BUG  /  1.0\r\nBy Astra\r\n\r\nClick Bug: restart the audio engine.\r\nCtrl+Alt+F10: same action.\r\nDrag Bug or star to move. Click star to hide/show Bug.\r\nRight-click either, or use the tray icon, for controls.\r\n\r\nAutomatic repair watches selected output device reconnects.\r\n"+(engine.NativeA1AutoRestart?"A1 is handled by Voicemeeter's native auto-restart.":"The Bug is also watching A1.")+"\r\nNo microphone recording. Silence does not cause a reset.\r\nIf the cord glitches without a Windows device event, click Bug.\r\nA successful command does not confirm what you hear.\r\n\r\n"+engine.Diagnostics()+"\r\n\r\nHotkey: "+(hotkeyRegistered?"registered":"unavailable; use Bug click")+"\r\nMode: "+Program.LogMode+"\r\nLocal log: "+Program.LogPath+"\r\nLog write status: "+(Program.Telemetry.LastError.Length==0?"writing enabled":Program.Telemetry.LastError)+"; dropped records: "+Program.Telemetry.DroppedRecords;};
            refresh.Click+=delegate {refreshText();};refreshText();detailsWindow.Controls.Add(info);detailsWindow.Controls.Add(refresh);detailsWindow.Show();
        }
        static Icon MakeIcon() { return BugArt.MakeIcon(); }
        void Shutdown() { if(exiting)return;exiting=true;Save();Program.Log("STOP UI");ExitThread(); }
        protected override void Dispose(bool disposing) {if(disposing&&!resourcesDisposed){resourcesDisposed=true;SystemEvents.DisplaySettingsChanged-=DisplayChanged;tick.Dispose();animation.Dispose();if(hotkeyRegistered)UnregisterHotKey(hotkeyHandle,1);engine.Dispose();tray.Visible=false;tray.Dispose();trayIcon.Dispose();if(detailsWindow!=null)detailsWindow.Dispose();bug.Dispose();star.Dispose();menu.Dispose();showEvent.Dispose();quitEvent.Dispose();checkEvent.Dispose();statusEvent.Dispose();hostedEvent.Dispose();repairEvent.Dispose();}base.Dispose(disposing);}
    }

    internal sealed class PetWindow : Form {
        readonly bool isStar;
        readonly ToolTip tip=new ToolTip {InitialDelay=300,ReshowDelay=100,AutoPopDelay=8000};
        bool dragging,moved;
        Point lastMouse,downMouse;
        internal event Action PetClick;
        internal event Action<Point> DragDelta;
        internal event Action DragEnded;
        internal event Action Hotkey;
        internal bool Connected=true,Repairing;
        internal long Phase;
        internal string Caption="Click to fix audio";
        internal PetWindow(bool star) {
            isStar=star;Text=star?"Voicemeeter Bug - Star":"Voicemeeter Bug - Click to fix audio";
            FormBorderStyle=FormBorderStyle.None;ShowInTaskbar=Program.TestUi;StartPosition=FormStartPosition.Manual;AutoScaleMode=AutoScaleMode.None;
            TopMost=true;DoubleBuffered=true;BackColor=Color.FromArgb(1,2,3);TransparencyKey=BackColor;ClientSize=star?new Size(52,52):new Size(BugArt.PetSize,BugArt.PetSize);Cursor=Cursors.Hand;
            AccessibleName=Text;AccessibleRole=AccessibleRole.PushButton;
        }
        protected override bool ShowWithoutActivation {get{return true;}}
        protected override CreateParams CreateParams {get{var p=base.CreateParams;if(!Program.TestUi)p.ExStyle|=0x08000000|0x80;return p;}}
        internal void SetTip(string value) {if(tip.GetToolTip(this)!=value)tip.SetToolTip(this,value);}
        protected override void WndProc(ref Message m) {if(m.Msg==0x312){if(Hotkey!=null)Hotkey();return;}base.WndProc(ref m);}
        protected override void OnMouseDown(MouseEventArgs e) {base.OnMouseDown(e);if(e.Button!=MouseButtons.Left)return;dragging=true;moved=false;lastMouse=downMouse=PointToScreen(e.Location);Capture=true;}
        void MovePointer(Point now) {if(!moved && Math.Abs(now.X-downMouse.X)+Math.Abs(now.Y-downMouse.Y)<6)return;moved=true;var delta=new Point(now.X-lastMouse.X,now.Y-lastMouse.Y);lastMouse=now;if(DragDelta!=null)DragDelta(delta);}
        protected override void OnMouseMove(MouseEventArgs e) {base.OnMouseMove(e);if(dragging)MovePointer(PointToScreen(e.Location));}
        protected override void OnMouseUp(MouseEventArgs e) {base.OnMouseUp(e);if(e.Button!=MouseButtons.Left||!dragging)return;MovePointer(PointToScreen(e.Location));dragging=false;Capture=false;if(moved){if(DragEnded!=null)DragEnded();}else if(PetClick!=null)PetClick();}
        protected override void OnMouseCaptureChanged(EventArgs e) {base.OnMouseCaptureChanged(e);if(dragging&&!Capture){dragging=false;if(moved&&DragEnded!=null)DragEnded();}}
        protected override void OnPaint(PaintEventArgs e) {base.OnPaint(e);if(isStar){e.Graphics.TranslateTransform(6,6);BugArt.Star(e.Graphics,40);}else BugArt.Draw(e.Graphics,Phase,Connected,Repairing);}
        protected override void Dispose(bool disposing){if(disposing)tip.Dispose();base.Dispose(disposing);}
    }

}
