// VIctory Launcher - a Windows launcher for the VIctory runtime (AMD fork,
// with the wave64-on-32-lane-GPU work of PRs #68 and #128 built in).
// Copyright (C) 2026 God489. GPL-3.0-or-later; see LICENSE.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace VictoryLauncher
{
    static class Program
    {
        public const string Version = "1.4.0";
        public static string Home;      // %LOCALAPPDATA%\VIctory Launcher
        public static string Runtime;   // the unpacked payload of this build

        [STAThread]
        static int Main(string[] args)
        {
            bool first;
            using (var single = new Mutex(true, "VIctoryLauncher.SingleInstance", out first))
            {
                if (!first)
                {
                    Activate();
                    return 0;
                }
                try
                {
                    Home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VIctory Launcher");
                    Directory.CreateDirectory(Home);
                    Runtime = Payload.Prepare(Home);
                    Native.SetDllDirectory(Runtime);
                    AppDomain.CurrentDomain.AssemblyResolve += Resolve;
                }
                catch (Exception e)
                {
                    MessageBox.Show("VIctory Launcher could not unpack its files:\n\n" + e.Message,
                        "VIctory Launcher", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return 1;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                return Start();
            }
        }

        // Kept apart from Main: the JIT resolves WebView2's types only when this runs,
        // after the resolver is in place.
        [MethodImpl(MethodImplOptions.NoInlining)]
        static int Start()
        {
            Application.Run(new MainForm());
            return 0;
        }

        static Assembly Resolve(object sender, ResolveEventArgs e)
        {
            string name = new AssemblyName(e.Name).Name + ".dll";
            string path = Path.Combine(Runtime, name);
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        }

        static void Activate()
        {
            foreach (var p in Process.GetProcessesByName(Process.GetCurrentProcess().ProcessName))
            {
                if (p.Id != Process.GetCurrentProcess().Id && p.MainWindowHandle != IntPtr.Zero)
                {
                    Native.ShowWindow(p.MainWindowHandle, 9); // SW_RESTORE
                    Native.SetForegroundWindow(p.MainWindowHandle);
                }
            }
        }
    }

    /// The files this exe carries (the UI, WebView2's loader, victory.exe):
    /// unpacked once per build into Home\runtime\<id>.
    static class Payload
    {
        public static string Prepare(string home)
        {
            var asm = Assembly.GetExecutingAssembly();
            string id;
            using (var s = asm.GetManifestResourceStream("payload.id"))
            using (var r = new StreamReader(s))
                id = r.ReadToEnd().Trim();
            string root = Path.Combine(home, "runtime");
            string dir = Path.Combine(root, id);
            if (File.Exists(Path.Combine(dir, ".complete")))
                return dir;
            string tmp = dir + ".tmp" + Process.GetCurrentProcess().Id;
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
            Directory.CreateDirectory(tmp);
            using (var s = asm.GetManifestResourceStream("payload.zip"))
            using (var zip = new ZipArchive(s, ZipArchiveMode.Read))
            {
                foreach (var entry in zip.Entries)
                {
                    string target = Path.GetFullPath(Path.Combine(tmp, entry.FullName));
                    if (!target.StartsWith(Path.GetFullPath(tmp), StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (entry.FullName.EndsWith("/")) { Directory.CreateDirectory(target); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    entry.ExtractToFile(target, true);
                }
            }
            File.WriteAllText(Path.Combine(tmp, ".complete"), id);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.Move(tmp, dir);
            // Older builds' payloads, unless one of their processes still runs.
            foreach (var old in Directory.GetDirectories(root))
            {
                if (string.Equals(old, dir, StringComparison.OrdinalIgnoreCase)) continue;
                try { Directory.Delete(old, true); } catch { }
            }
            return dir;
        }
    }

    public sealed partial class MainForm : Form
    {
        readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        Microsoft.Web.WebView2.WinForms.WebView2 web;
        Dictionary<string, object> settings;
        // What this start went by when settings.json was there but could not be read (another
        // program held it, or it was cut short): the file is then merged with, never written over.
        Dictionary<string, object> settingsBase;
        string settingsPath;
        bool uiReady;

        public MainForm()
        {
            Text = "VIctory Launcher";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(10, 11, 16);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            settingsPath = Path.Combine(Program.Home, "settings.json");
            settings = LoadSettings();
            web = new Microsoft.Web.WebView2.WinForms.WebView2();
            web.Dock = DockStyle.Fill;
            web.DefaultBackgroundColor = Color.FromArgb(10, 11, 16);
            Controls.Add(web);
            Load += OnLoad;
            FormClosing += OnClosing;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ClassStyle |= 0x20000;            // CS_DROPSHADOW
                cp.Style |= 0x00020000 | 0x00080000;  // WS_MINIMIZEBOX | WS_SYSMENU: taskbar minimize
                return cp;
            }
        }

        async void OnLoad(object sender, EventArgs e)
        {
            float scale = DeviceDpi / 96f;
            Size = new Size((int)(1200 * scale), (int)(760 * scale));
            CenterToScreen();
            Native.StyleWindow(Handle);
            try
            {
                // VLAUNCHER_DEBUG_PORT=<n> exposes the page to DevTools (testing only).
                Microsoft.Web.WebView2.Core.CoreWebView2EnvironmentOptions options = null;
                string port = Environment.GetEnvironmentVariable("VLAUNCHER_DEBUG_PORT");
                int portNumber;
                if (int.TryParse(port, out portNumber) && portNumber > 1024)
                    options = new Microsoft.Web.WebView2.Core.CoreWebView2EnvironmentOptions(
                        "--remote-debugging-port=" + portNumber);
                var env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(
                    null, Path.Combine(Program.Home, "webview"), options);
                await web.EnsureCoreWebView2Async(env);
            }
            catch (Exception ex)
            {
                var r = MessageBox.Show(this,
                    "VIctory Launcher needs the Microsoft Edge WebView2 Runtime, which is part of Windows 11 " +
                    "and most Windows 10 installations.\n\n" + ex.Message + "\n\nOpen the download page?",
                    "VIctory Launcher", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (r == DialogResult.Yes)
                    OpenUrl("https://developer.microsoft.com/microsoft-edge/webview2/");
                Close();
                return;
            }
            var core = web.CoreWebView2;
            bool debug = Environment.GetEnvironmentVariable("VLAUNCHER_DEBUG") == "1";
            core.Settings.AreDevToolsEnabled = debug;
            core.Settings.AreDefaultContextMenusEnabled = debug;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = debug;
            core.Settings.IsPinchZoomEnabled = false;
            core.Settings.IsSwipeNavigationEnabled = false;
            core.SetVirtualHostNameToFolderMapping("launcher.victory", Path.Combine(Program.Runtime, "web"),
                Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.Deny);
            core.NavigationStarting += (s, a) =>
            {
                if (!a.Uri.StartsWith("https://launcher.victory/", StringComparison.OrdinalIgnoreCase))
                {
                    a.Cancel = true;
                    if (a.Uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) OpenUrl(a.Uri);
                }
            };
            core.NewWindowRequested += (s, a) =>
            {
                a.Handled = true;
                if (a.Uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) OpenUrl(a.Uri);
            };
            core.WebMessageReceived += OnMessage;
            core.Navigate("https://launcher.victory/index.html");
            game.Tick += GameTick;
            game.Interval = 1000;
            flush.Tick += FlushLog;
            flush.Interval = 120;
            flush.Start();
        }

        void OnClosing(object sender, FormClosingEventArgs e)
        {
            SaveSettings();
            if (scan != null) { try { KillTree(scan); } catch { } }
            if (tool != null) { try { KillTree(tool); } catch { } }
        }

        // ---------- messages from the page ----------

        void OnMessage(object sender, Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
        {
            Dictionary<string, object> m;
            try { m = json.Deserialize<Dictionary<string, object>>(e.WebMessageAsJson); }
            catch { return; }
            string cmd = Str(m, "cmd"), id = Str(m, "id");
            try
            {
                object result = Run(cmd, m, e);
                if (id != "") Reply(id, true, result, null);
            }
            catch (Exception ex)
            {
                if (id != "") Reply(id, false, null, ex.Message);
            }
        }

        object Run(string cmd, Dictionary<string, object> m, Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
        {
            switch (cmd)
            {
                case "init":
                    uiReady = true;
                    if (settingsBase != null)
                    {
                        // the read at start failed: the file again, now that a moment has passed
                        var disk = ReadSettings(settingsPath);
                        if (disk != null) { settings = disk; settingsBase = null; }
                    }
                    StartSystemProbe();
                    return new Dictionary<string, object> {
                        { "settings", settings },
                        { "info", Info() },
                        { "game", GameStatus(Str(settings, "gamePath")) },
                        { "running", Running() },
                    };
                case "saveSettings":
                    var incoming = m["settings"] as Dictionary<string, object>;
                    if (incoming != null)
                    {
                        string keep = Str(settings, "gamePath");
                        settings = incoming;
                        if (!settings.ContainsKey("gamePath")) settings["gamePath"] = keep;
                        SaveSettings();
                    }
                    return true;
                case "browseGame":
                    {
                        string start = Str(settings, "gamePath");
                        if (start != "" && File.Exists(start)) start = Path.GetDirectoryName(start);
                        string picked = Native.PickFolder(Handle, "Choose the folder of your game", start);
                        if (picked == null) return null;
                        return SetGame(picked);
                    }
                case "pickFolder":
                    return Native.PickFolder(Handle, Str(m, "title"), Str(m, "start"));
                case "browseExe":
                    using (var d = new OpenFileDialog())
                    {
                        d.Title = "Choose the game's executable";
                        d.Filter = "Executables (*.exe)|*.exe";
                        if (d.ShowDialog(this) != DialogResult.OK) return null;
                        return SetGame(d.FileName);
                    }
                case "drop":
                    {
                        string path = null;
                        if (e.AdditionalObjects != null)
                        {
                            foreach (var o in e.AdditionalObjects)
                            {
                                var f = o as Microsoft.Web.WebView2.Core.CoreWebView2File;
                                if (f != null) { path = f.Path; break; }
                            }
                        }
                        if (path == null) throw new Exception("Nothing usable was dropped.");
                        return SetGame(path);
                    }
                case "setGame":
                    return SetGame(Str(m, "path"));
                case "clearGame":
                    settings["gamePath"] = "";
                    SaveSettings();
                    return GameStatus("");
                case "verify":
                    Verify();
                    return true;
                case "play":
                    return Play();
                case "test":
                    return StartTest();
                case "tests":
                    return TestList();
                case "liveSet":
                    return LiveSet(Str(m, "text"));
                case "openFile":
                    {
                        string f = Str(m, "path");
                        if (f.StartsWith(Path.Combine(Program.Home, "tests"), StringComparison.OrdinalIgnoreCase) && File.Exists(f))
                            Process.Start(new ProcessStartInfo(f) { UseShellExecute = true });
                        return true;
                    }
                case "stop":
                    Stop();
                    return true;
                case "focusGame":
                    FocusGame();
                    return true;
                case "scan":
                    Scan(Str(m, "lanes"), Bool(m, "validate"));
                    return true;
                case "cancelScan":
                    if (scan != null) KillTree(scan);
                    return true;
                case "tool":
                    Tool(Str(m, "kind"));
                    return true;
                case "cancelTool":
                    if (tool != null) KillTree(tool);
                    return true;
                case "open":
                    OpenPlace(Str(m, "what"));
                    return true;
                case "openUrl":
                    {
                        string url = Str(m, "url");
                        if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) OpenUrl(url);
                        return true;
                    }
                case "clearCache":
                    return ClearCache();
                case "copy":
                    Clipboard.SetText(Str(m, "text").Length == 0 ? " " : Str(m, "text"));
                    return true;
                case "saveLog":
                    using (var d = new SaveFileDialog())
                    {
                        d.Title = "Save the log";
                        d.Filter = "Text files (*.txt;*.log)|*.txt;*.log|All files (*.*)|*.*";
                        d.FileName = "victory-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) + ".log";
                        if (d.ShowDialog(this) != DialogResult.OK) return false;
                        File.WriteAllText(d.FileName, Str(m, "text"), new UTF8Encoding(false));
                        return true;
                    }
                case "minimize":
                    WindowState = FormWindowState.Minimized;
                    return true;
                case "close":
                    Close();
                    return true;
                case "drag":
                    Native.ReleaseCapture();
                    Native.SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero); // WM_NCLBUTTONDOWN, HTCAPTION
                    return true;
            }
            throw new Exception("Unknown command: " + cmd);
        }

        void Reply(string id, bool ok, object data, string error)
        {
            Post(new Dictionary<string, object> { { "type", "reply" }, { "id", id }, { "ok", ok }, { "data", data }, { "error", error } });
        }

        public void Event(string name, object data)
        {
            if (InvokeRequired) { BeginInvoke(new Action(() => Event(name, data))); return; }
            if (!uiReady || web.CoreWebView2 == null) return;
            Post(new Dictionary<string, object> { { "type", "event" }, { "name", name }, { "data", data } });
        }

        void Post(object o)
        {
            try { web.CoreWebView2.PostWebMessageAsJson(json.Serialize(o)); } catch { }
        }

        // ---------- settings ----------

        /// settings.json; when it cannot be read, the copy the last save kept (.bak), or a save cut
        /// short before its rename (.tmp, from builds that deleted the file first). A start that
        /// found the file but could not read it used to show "Add your game" and, on closing,
        /// wrote its empty settings over the game's path.
        Dictionary<string, object> LoadSettings()
        {
            settingsBase = null;
            var d = ReadSettings(settingsPath);
            if (d != null) return d;
            bool there = File.Exists(settingsPath);
            d = ReadSettings(settingsPath + ".bak") ?? ReadSettings(settingsPath + ".tmp") ?? new Dictionary<string, object>();
            if (there) settingsBase = new Dictionary<string, object>(d);
            return d;
        }

        /// The settings in `file`, or null when it is missing, empty or not JSON. A file another
        /// program holds for a moment (a scanner, a backup, a sync) is read again for two seconds.
        Dictionary<string, object> ReadSettings(string file)
        {
            bool held;
            return ReadSettings(file, out held);
        }

        /// `held`: still unreadable after the two seconds (as opposed to missing, empty or broken).
        Dictionary<string, object> ReadSettings(string file, out bool held)
        {
            held = false;
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    if (!File.Exists(file)) return null;
                    return json.Deserialize<Dictionary<string, object>>(File.ReadAllText(file));
                }
                catch (IOException) { if (attempt >= 10) { held = true; return null; } }
                catch (UnauthorizedAccessException) { if (attempt >= 10) { held = true; return null; } }
                catch { return null; }
                System.Threading.Thread.Sleep(200);
            }
        }

        void SaveSettings()
        {
            try
            {
                if (settingsBase != null)
                {
                    // this start could not read the file: what it holds stays, but for what was
                    // changed here; still held by another program, it is left as it is; broken (cut
                    // short, not JSON), it is written anew from what this start went by
                    bool held;
                    var disk = ReadSettings(settingsPath, out held);
                    if (held) return;
                    if (disk != null)
                    {
                        foreach (var kv in settings)
                        {
                            object was;
                            if (!disk.ContainsKey(kv.Key) || !settingsBase.TryGetValue(kv.Key, out was)
                                || json.Serialize(was) != json.Serialize(kv.Value))
                                disk[kv.Key] = kv.Value;
                        }
                        settings = disk;
                    }
                    settingsBase = null;
                }
                if (!settings.ContainsKey("gamePath"))
                {
                    // this start never learned where the game is (none of the files could be read
                    // then): the path a copy on disk has stays; "Remove game" leaves "" here instead
                    var any = ReadSettings(settingsPath) ?? ReadSettings(settingsPath + ".bak") ?? ReadSettings(settingsPath + ".tmp");
                    object path;
                    if (any != null && any.TryGetValue("gamePath", out path)) settings["gamePath"] = path;
                }
                // written whole and flushed first, then swapped in at once: no moment without the file
                string tmp = settingsPath + ".tmp";
                byte[] bytes = new UTF8Encoding(false).GetBytes(json.Serialize(settings));
                using (var f = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    f.Write(bytes, 0, bytes.Length);
                    f.Flush(true);
                }
                if (File.Exists(settingsPath)) File.Replace(tmp, settingsPath, settingsPath + ".bak", true);
                else File.Move(tmp, settingsPath);
            }
            catch { }
        }

        static string Str(Dictionary<string, object> m, string key)
        {
            object v;
            return m != null && m.TryGetValue(key, out v) && v != null ? Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) : "";
        }

        static bool Bool(Dictionary<string, object> m, string key)
        {
            object v;
            return m != null && m.TryGetValue(key, out v) && v is bool && (bool)v;
        }

        // ---------- paths ----------

        public string VictoryExe { get { return Path.Combine(Program.Runtime, "victory.exe"); } }

        string LocalVictory(string leaf)
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "victory", leaf);
        }

        string DataDir { get { string s = Str(settings, "dataDir"); return s != "" ? s : LocalVictory("data"); } }
        string CacheDir { get { string s = Str(settings, "cacheDir"); return s != "" ? s : LocalVictory("cache"); } }
        string LogsDir { get { return Path.Combine(Program.Home, "logs"); } }

        void OpenPlace(string what)
        {
            string path = null;
            switch (what)
            {
                case "data": path = DataDir; break;
                case "cache": path = CacheDir; break;
                case "logs": path = LogsDir; break;
                case "runtime": path = Program.Runtime; break;
                case "game":
                    string g = Str(settings, "gamePath");
                    path = g != "" ? Path.GetDirectoryName(g) : null;
                    break;
                case "crashes":
                    path = FindCrashLog();
                    if (path == null) throw new Exception("No crashes.log yet: nothing has crashed.");
                    Process.Start(new ProcessStartInfo("notepad.exe", Quote(path)) { UseShellExecute = true });
                    return;
            }
            if (path == null) throw new Exception("Nothing to open.");
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", Quote(path)) { UseShellExecute = true });
        }

        string FindCrashLog()
        {
            if (!Directory.Exists(DataDir)) return null;
            try
            {
                return Directory.GetFiles(DataDir, "crashes.log", SearchOption.AllDirectories)
                    .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            }
            catch { return null; }
        }

        static void OpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        }

        public static string Quote(string s)
        {
            if (s.Length > 0 && s.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return s;
            var b = new StringBuilder("\"");
            int slashes = 0;
            foreach (char c in s)
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') b.Append('\\', slashes * 2 + 1);
                else b.Append('\\', slashes);
                slashes = 0;
                b.Append(c);
            }
            b.Append('\\', slashes * 2);
            return b.Append('"').ToString();
        }

        // ---------- what the launcher carries ----------

        Dictionary<string, object> Info()
        {
            var d = new Dictionary<string, object>();
            string file = Path.Combine(Program.Runtime, "build-info.json");
            if (File.Exists(file))
            {
                try { d = json.Deserialize<Dictionary<string, object>>(File.ReadAllText(file)); } catch { }
            }
            d["launcher"] = Program.Version;
            d["runtime"] = Program.Runtime;
            d["home"] = Program.Home;
            d["dataDir"] = DataDir;
            d["cacheDir"] = CacheDir;
            d["validator"] = FindValidator();
            return d;
        }

        /// The newest Windows SDK's dxil.dll, as `victory translate --validate` finds it.
        static string FindValidator()
        {
            string env = Environment.GetEnvironmentVariable("VICTORY_DXIL");
            if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
            try
            {
                string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Windows Kits\10\bin");
                if (!Directory.Exists(root)) return "";
                return Directory.GetDirectories(root, "10.*")
                    .Select(d => Path.Combine(d, @"x64\dxil.dll"))
                    .Where(File.Exists)
                    .OrderByDescending(p => p, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault() ?? "";
            }
            catch { return ""; }
        }

        // ---------- the machine ----------

        void StartSystemProbe()
        {
            var t = new Thread(() =>
            {
                var sys = new Dictionary<string, object>();
                try
                {
                    var mem = Native.Memory();
                    sys["ramTotal"] = (double)mem.ullTotalPhys;
                    sys["ramFree"] = (double)mem.ullAvailPhys;
                    sys["commitLimit"] = (double)mem.ullTotalPageFile;
                    sys["commitFree"] = (double)mem.ullAvailPageFile;
                    sys["avx2"] = Native.HasAvx2();
                    sys["cores"] = Environment.ProcessorCount;
                    sys["cpu"] = Reg(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString").Trim();
                    string product = Reg(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "ProductName");
                    string build = Reg(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "CurrentBuild");
                    int b;
                    if (int.TryParse(build, out b) && b >= 22000) product = product.Replace("Windows 10", "Windows 11");
                    sys["os"] = product + " " + Reg(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "DisplayVersion");
                    sys["osBuild"] = build;
                    sys["x64"] = Environment.Is64BitOperatingSystem;
                    var g = Native.Gpu();
                    sys["gpu"] = new Dictionary<string, object> {
                        { "name", g.Name }, { "vendor", g.Vendor }, { "vram", (double)g.DedicatedMemory },
                        { "d3d12", g.D3D12 }, { "waveMin", (int)g.WaveMin }, { "waveMax", (int)g.WaveMax },
                        { "shaderModel", g.ShaderModel }, { "meshTier", g.MeshShaderTier },
                        { "software", g.Software }, { "error", g.Error },
                    };
                }
                catch (Exception ex)
                {
                    sys["error"] = ex.Message;
                }
                Event("system", sys);
            });
            t.IsBackground = true;
            t.Start();
        }

        static string Reg(string key, string value)
        {
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(key))
                {
                    object v = k == null ? null : k.GetValue(value);
                    return v == null ? "" : v.ToString();
                }
            }
            catch { return ""; }
        }
    }
}
