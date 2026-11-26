// VIctory Launcher - finding, checking and running the title.
// Copyright (C) 2026 God489. GPL-3.0-or-later.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml;

namespace VictoryLauncher
{
    public sealed partial class MainForm
    {
        readonly System.Windows.Forms.Timer game = new System.Windows.Forms.Timer();
        readonly System.Windows.Forms.Timer flush = new System.Windows.Forms.Timer();
        readonly object logLock = new object();
        List<Dictionary<string, object>> pending = new List<Dictionary<string, object>>();
        Process child;
        IntPtr job = IntPtr.Zero;
        DateTime started;
        StreamWriter sessionLog;
        string lastError = "";
        string lastCaption = "";
        bool stoppedByUser;
        Process scan;

        // ---------- the game's files ----------

        object SetGame(string path)
        {
            var status = GameStatus(path);
            if (!(bool)status["found"])
                throw new Exception((string)status["problem"]);
            settings["gamePath"] = (string)status["exe"];
            SaveSettings();
            return status;
        }

        /// What is known of the title at `path` (its folder, or its executable).
        Dictionary<string, object> GameStatus(string path)
        {
            var d = new Dictionary<string, object>();
            d["found"] = false;
            d["path"] = path ?? "";
            if (string.IsNullOrEmpty(path))
            {
                d["problem"] = "";
                return d;
            }
            string exe = null, dir;
            if (File.Exists(path))
            {
                if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) exe = path;
                dir = Path.GetDirectoryName(path);
            }
            else if (Directory.Exists(path))
            {
                dir = path;
            }
            else
            {
                d["problem"] = "The game's files are no longer at " + path + ".";
                return d;
            }
            string config = FindConfig(dir);
            if (config != null) dir = Path.GetDirectoryName(config);
            var cfg = ReadConfig(config);
            if (exe == null) exe = FindExe(dir, cfg);
            if (exe == null)
            {
                d["problem"] = config == null
                    ? "No game was found there: an Xbox Series title's folder holds MicrosoftGame.config and the game's .exe."
                    : "MicrosoftGame.config is there, but not the executable it names.";
                return d;
            }
            d["found"] = true;
            d["exe"] = exe;
            d["dir"] = Path.GetDirectoryName(exe);
            d["exeName"] = Path.GetFileName(exe);
            d["config"] = config != null;
            d["title"] = cfg.ContainsKey("title") ? cfg["title"] : "";
            d["identity"] = cfg.ContainsKey("identity") ? cfg["identity"] : "";
            d["version"] = cfg.ContainsKey("version") ? cfg["version"] : "";
            string folder = Path.GetDirectoryName(exe);
            d["appdata0"] = File.Exists(Path.Combine(folder, "appdata0.rpf"));
            try
            {
                d["exeSize"] = (double)new FileInfo(exe).Length;
                d["rpfCount"] = Directory.GetFiles(folder, "*.rpf", SearchOption.TopDirectoryOnly).Length;
            }
            catch { d["rpfCount"] = 0; }
            d["problem"] = "";
            return d;
        }

        static string FindConfig(string dir)
        {
            string here = Path.Combine(dir, "MicrosoftGame.config");
            if (File.Exists(here)) return here;
            try
            {
                foreach (var sub in Directory.GetDirectories(dir))
                {
                    string c = Path.Combine(sub, "MicrosoftGame.config");
                    if (File.Exists(c)) return c;
                }
            }
            catch { }
            return null;
        }

        static Dictionary<string, string> ReadConfig(string file)
        {
            var d = new Dictionary<string, string>();
            if (file == null) return d;
            try
            {
                var x = new XmlDocument();
                x.Load(file);
                foreach (XmlElement e in x.GetElementsByTagName("Identity"))
                {
                    d["identity"] = e.GetAttribute("Name");
                    d["version"] = e.GetAttribute("Version");
                }
                foreach (XmlElement e in x.GetElementsByTagName("ShellVisuals"))
                    d["title"] = e.GetAttribute("DefaultDisplayName");
                var exes = new List<string>();
                foreach (XmlElement e in x.GetElementsByTagName("Executable"))
                    if (e.GetAttribute("Name") != "") exes.Add(e.GetAttribute("Name"));
                d["executables"] = string.Join("|", exes);
            }
            catch { }
            return d;
        }

        static string FindExe(string dir, Dictionary<string, string> cfg)
        {
            string named;
            if (cfg.TryGetValue("executables", out named))
            {
                foreach (var n in named.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string p = Path.Combine(dir, n);
                    if (File.Exists(p)) return p;
                }
            }
            string usual = Path.Combine(dir, "game_scarlett_master.exe");
            if (File.Exists(usual)) return usual;
            try
            {
                var exes = Directory.GetFiles(dir, "*.exe", SearchOption.TopDirectoryOnly);
                return exes.FirstOrDefault(p => Path.GetFileName(p).IndexOf("scarlett", StringComparison.OrdinalIgnoreCase) >= 0)
                    ?? (exes.Length == 1 ? exes[0] : null);
            }
            catch { return null; }
        }

        // ---------- what VIctory is told ----------

        /// The settings avatarmanz/VIctory-7900XTX is played and measured with
        /// (its scripts\play.ps1), set for every run.
        static readonly string[,] Played = {
            { "VICTORY_TEXTURE_NO_WAIT", "1" }, { "VICTORY_RELAY", "2" }, { "VICTORY_WIDE_BACK", "1" },
            { "VICTORY_CARD_REHASH_RUNS", "1" }, { "VICTORY_JOIN_COPIES", "1" }, { "VICTORY_INDIRECT_WINDOW", "1" },
            { "VICTORY_RELAY_COPY", "1" }, { "VICTORY_CARD_STEPS", "2" }, { "VICTORY_INDIRECT_BATCH", "2" },
            { "VICTORY_MAP_THREADS", "1" }, { "VICTORY_TITLE_PRIORITIES", "0" }, { "VICTORY_CARD_AWAKE", "1" },
            { "VICTORY_SHOW_AFTER", "1" }, { "VICTORY_D3D12_DRED", "light" }, { "VICTORY_SKIP_PIPELINES", "none" },
        };

        void ApplyEnvironment(ProcessStartInfo p, string gameDir)
        {
            // As play.ps1: settings left over from elsewhere go, the paths stay.
            var stale = new List<string>();
            foreach (string k in p.EnvironmentVariables.Keys)
                if (k.StartsWith("VICTORY_", StringComparison.OrdinalIgnoreCase)
                    && !Regex.IsMatch(k, "^VICTORY_(CONFIG|GAME_DIR|CACHE_DIR|DATA_DIR)$", RegexOptions.IgnoreCase))
                    stale.Add(k);
            foreach (var k in stale) p.EnvironmentVariables.Remove(k);

            p.EnvironmentVariables["VICTORY_GAME_DIR"] = gameDir;
            Env(p, "VICTORY_DATA_DIR", Str(settings, "dataDir"));
            Env(p, "VICTORY_CACHE_DIR", Str(settings, "cacheDir"));
            for (int i = 0; i < Played.GetLength(0); i++) Env(p, Played[i, 0], Played[i, 1]);
            // Quality (the default): the list calls carried out on the title's threads and no
            // joined submissions. codec_d/cool_fox on an RTX 4060 (8 GB): with VICTORY_THIN=1 the
            // heads of people in play do not draw (eyes and teeth only), and with
            // VICTORY_JOIN_PARTS=8 the opening video repeats frames. Fast is the 7900XTX fork's
            // way, faster with those faults; Safe leaves out its newest paths as well.
            string mode = Str(settings, "mode");
            // Fast unless chosen otherwise: the runs that held on an RTX 3050 (tests A and B,
            // 2026-11-26) were Fast.
            if (mode == "") mode = "fast";
            if (mode == "safe")
            {
                Env(p, "VICTORY_UNDER_WIDE", "0");
                Env(p, "VICTORY_LAND_ECHO", "0");
            }
            else if (mode == "fast")
            {
                Env(p, "VICTORY_THIN", "1");
                Env(p, "VICTORY_JOIN_PARTS", "8");
                Env(p, "VICTORY_UPLOAD_AHEAD", "1");
            }
            else
            {
                Env(p, "VICTORY_THIN", "0");
                Env(p, "VICTORY_JOIN_PARTS", "0");
                Env(p, "VICTORY_UPLOAD_AHEAD", "1");
            }
            if (Bool(settings, "rayTracing")) Env(p, "VICTORY_BVH_BUILDER", "1");
            // What codec_d/cool_fox runs with by default, which the 7900XTX fork took behind
            // switches: one-slot heaps where a list sets none, XMA voices refilled, the heap
            // buffer pass per heap, a DirectStorage staging floor of 256 MiB.
            Env(p, "VICTORY_FALLBACK_HEAPS", "1");
            Env(p, "VICTORY_XMA_REFILL", "1");
            Env(p, "VICTORY_SWEEP_PER_HEAP", "1");
            Env(p, "VICTORY_DSTORAGE_STAGING_MB", "256");
            // Renderer patches (the 7900XTX fork's ports): 1 and 5 by default, as cool_fox runs
            // them on RTX cards; "all" adds 2, 6 and 7 (avatarmanz: 57-75 ms to 44.9 ms).
            string patches = Str(settings, "patches");
            if (patches != "off")
            {
                Env(p, "VICTORY_PORT_CONSTANTS", "1");
                Env(p, "VICTORY_PORT_CBV", "1");
                if (patches == "all")
                {
                    Env(p, "VICTORY_PORT_DESCRIPTORS", "1");
                    Env(p, "VICTORY_PORT_GUARD", "1");
                    Env(p, "VICTORY_PORT_JOURNAL", "1");
                }
            }
            // Exact texture checks: every check hashes the texture (cool_fox: the write guard
            // misses some of the title's writes; skin and a phone's frame read colour noise).
            if (Str(settings, "textureCheck") != "guard") Env(p, "VICTORY_TEXTURE_GUARD", "0");
            // 60 fps: the title's own 30 fps cap and our clock follow 16.667 ms (menus, videos).
            if (Bool(settings, "fps60")) Env(p, "VICTORY_FRAME_INTERVAL_MS", "16.667");
            // Output size (Series X; the 7900XTX fork's VICTORY_OUTPUT): the title renders to
            // that size itself, at 2/3 of it with its own upscaler. Auto: 1920x1080 on a card
            // under 12 GB (4K output asks for more than such a card has), the title's own else.
            if (ConsoleModel() == "series-x")
            {
                string output = OutputSize();
                if (output != "") Env(p, "VICTORY_OUTPUT", output);
            }
            // The live link: commands to the running game, its numbers each second (Live).
            Env(p, "VICTORY_LIVE", LiveDir);
            // Wave64 programs no other form translates (four in the opening cutscene on a
            // 32-lane card): their per-lane branches taken by the whole console wave at once,
            // instead of the title going without them.
            if (Str(settings, "lastResort") != "off") Env(p, "VICTORY_WAVE_LAST_RESORT", "1");
            // Limited memory: what codec_d/cool_fox runs with on an RTX 5090 with 32 GB of RAM
            // (its Play-VIctory.cmd pareto-residency): textures' levels made on demand, every
            // title buffer copied into the card's memory, and the air's light raised (indoor
            // haze otherwise). With Series S (ConsoleModel).
            if (LimitedMemory())
            {
                Env(p, "VICTORY_LEVELS_ON_DEMAND", "1");
                Env(p, "VICTORY_CARD_BUFFERS_MIN_KB", "64");
                Env(p, "VICTORY_AMBIENT_AIR", "1.5");
                // cool_fox's residency budgets (the 7900XTX fork's VICTORY_RESIDENCY), and no
                // driver recompiles in the background (cool_fox's 4 GB profile).
                Env(p, "VICTORY_RESIDENCY", "1");
                Env(p, "VICTORY_D3D12_BACKGROUND", "0");
            }
            // The light stand-in: an even level by default (cool_fox: the estimate follows the
            // frame's radiance, and a bright doorway washed the whole room out); "estimate" as
            // the 7900XTX fork plays.
            string ambient = Str(settings, "ambient");
            if (ambient == "") ambient = "0.8";
            if (ambient != "off")
                Env(p, "VICTORY_AMBIENT", Str(settings, "ambientMode") != "constant" || ambient.StartsWith("constant:") ? ambient : "constant:" + ambient);
            if (Str(settings, "textureBack") == "off") Env(p, "VICTORY_TEXTURE_BACK", "0");
            if (Bool(settings, "panel")) Env(p, "VICTORY_PANEL", "1");
            if (Str(settings, "pipelineBook") != "off") Env(p, "VICTORY_PIPELINE_BOOK", "1");
            string log = Str(settings, "logLevel");
            if (log != "" && log != "info") Env(p, "VICTORY_LOG", log);
            // Advanced: VICTORY_* lines the user adds, last.
            foreach (var raw in Str(settings, "customEnv").Split('\n'))
            {
                var m = Regex.Match(raw.Trim(), @"^(VICTORY_[A-Z0-9_]+)\s*=\s*(.*)$");
                if (m.Success) p.EnvironmentVariables[m.Groups[1].Value] = m.Groups[2].Value.Trim();
            }
        }

        /// The output size asked for: as chosen, or with "auto" 1920x1080 on a card under 12 GB
        /// of video memory; "" for the title's own (4K on Series X).
        string OutputSize()
        {
            string o = Str(settings, "output");
            if (o == "native") return "";
            if (System.Text.RegularExpressions.Regex.IsMatch(o, @"^\d+x\d+$")) return o;
            var g = Native.Gpu();
            return g.DedicatedMemory > 0 && g.DedicatedMemory < 12UL << 30 ? "1920x1080" : "";
        }

        /// The memory profile: "limited" or "standard", or by this PC's RAM when "auto" (under
        /// 48 GB: the 7900XTX build takes 40-46 GB of private memory in the world).
        bool LimitedMemory()
        {
            string m = Str(settings, "memory");
            if (m == "limited") return true;
            if (m == "standard") return false;
            return Native.Memory().ullTotalPhys < 48UL << 30;
        }

        static void Env(ProcessStartInfo p, string name, string value)
        {
            if (!string.IsNullOrEmpty(value)) p.EnvironmentVariables[name] = value;
        }

        ProcessStartInfo Victory(string arguments, string gameDir)
        {
            var p = new ProcessStartInfo(VictoryExe, arguments);
            p.UseShellExecute = false;
            p.CreateNoWindow = true;
            p.RedirectStandardOutput = true;
            p.RedirectStandardError = true;
            p.StandardOutputEncoding = Encoding.UTF8;
            p.StandardErrorEncoding = Encoding.UTF8;
            p.WorkingDirectory = Program.Runtime;
            ApplyEnvironment(p, gameDir);
            return p;
        }

        // ---------- checking the title without starting it ----------

        void Verify()
        {
            var status = GameStatus(Str(settings, "gamePath"));
            if (!(bool)status["found"]) throw new Exception("Add your game first.");
            string exe = (string)status["exe"];
            string args = "run " + Quote(exe) + " --console " + ConsoleModel();
            var t = new Thread(() =>
            {
                var r = new Dictionary<string, object>();
                var lines = new List<string>();
                try
                {
                    var p = Process.Start(Victory(args, Path.GetDirectoryName(exe)));
                    p.OutputDataReceived += (s, a) => { if (a.Data != null) lock (lines) lines.Add(a.Data); };
                    p.ErrorDataReceived += (s, a) => { if (a.Data != null) lock (lines) lines.Add(a.Data); };
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    if (!p.WaitForExit(180000)) { KillTree(p); r["error"] = "VIctory did not finish checking within 3 minutes."; }
                    p.WaitForExit();
                    r["code"] = p.ExitCode;
                }
                catch (Exception ex) { r["error"] = ex.Message; }
                List<string> all;
                lock (lines) all = new List<string>(lines);
                var missing = new List<string>();
                int dlls = 0;
                foreach (var l in all)
                {
                    var b = Regex.Match(l, @"^build\s+(.+?)\s+\(([0-9a-fA-Fx]+)\)\s*$");
                    if (b.Success) { r["build"] = b.Groups[1].Value; r["hash"] = b.Groups[2].Value; }
                    var c = Regex.Match(l, @"^console\s+(.+)$");
                    if (c.Success) r["console"] = c.Groups[1].Value.Trim();
                    if (l.StartsWith("title dll")) dlls++;
                    var mi = Regex.Match(l, @"^\s+missing\s+(.+)$");
                    if (mi.Success) missing.Add(mi.Groups[1].Value.Trim());
                    if (l.StartsWith("Error:") && !r.ContainsKey("error")) r["error"] = l.Substring(6).Trim();
                }
                r["dlls"] = dlls;
                r["missing"] = missing;
                r["lines"] = all.Count > 400 ? all.Skip(all.Count - 400).ToList() : all;
                Event("verified", r);
            });
            t.IsBackground = true;
            t.Start();
        }

        /// The console model: as chosen, or with "auto" (or nothing chosen) Series S where Series X
        /// does not fit, and Series X else. Series X draws the faces that Series S leaves out
        /// (codec_d/cool_fox on an RTX 4060: eyes and teeth only), but on an RTX 3050 (8 GB) with
        /// 16 GB of RAM a Series X run filled the card ("the card has no room for new ones") and
        /// ended two minutes in with E_OUTOFMEMORY, where Series S runs on: so Series S on a card
        /// under 12 GB or with limited memory.
        string ConsoleModel()
        {
            string c = Str(settings, "console");
            if (c == "series-s" || c == "series-x") return c;
            var g = Native.Gpu();
            bool smallCard = g.DedicatedMemory > 0 && g.DedicatedMemory < 12UL << 30;
            return smallCard || LimitedMemory() ? "series-s" : "series-x";
        }

        // ---------- running the title ----------

        bool Running()
        {
            if (job == IntPtr.Zero) return false;
            return Native.JobProcesses(job).Count > 0 || (child != null && !child.HasExited);
        }

        object Play()
        {
            if (Running()) throw new Exception("The game is already running.");
            var status = GameStatus(Str(settings, "gamePath"));
            if (!(bool)status["found"]) throw new Exception("Add your game first.");
            string exe = (string)status["exe"];
            var args = new StringBuilder("run " + Quote(exe) + " --execute --console " + ConsoleModel());
            if (Str(settings, "unimplemented") == "zero") args.Append(" --unimplemented zero");
            if (Bool(settings, "strict")) args.Append(" --strict");
            string extra = Str(settings, "titleArgs").Trim();
            if (extra != "") args.Append(" -- ").Append(extra);
            var info = Victory(args.ToString(), Path.GetDirectoryName(exe));

            Directory.CreateDirectory(LogsDir);
            foreach (var old in Directory.GetFiles(LogsDir, "session-*.log").OrderByDescending(f => f).Skip(9))
                try { File.Delete(old); } catch { }
            string logFile = Path.Combine(LogsDir, "session-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) + ".log");
            sessionLog = new StreamWriter(logFile, false, new UTF8Encoding(false));
            sessionLog.AutoFlush = true;
            sessionLog.WriteLine("# " + info.FileName + " " + info.Arguments);
            foreach (string k in info.EnvironmentVariables.Keys)
                if (k.StartsWith("victory_", StringComparison.OrdinalIgnoreCase))
                    sessionLog.WriteLine("# " + k.ToUpperInvariant() + "=" + info.EnvironmentVariables[k]);

            lastError = "";
            lastCaption = "";
            stoppedByUser = false;
            LiveClear();
            if (job != IntPtr.Zero) Native.CloseHandle(job);
            job = Native.NewJob();
            child = new Process { StartInfo = info, EnableRaisingEvents = true };
            child.OutputDataReceived += (s, a) => Line(a.Data, false);
            child.ErrorDataReceived += (s, a) => Line(a.Data, true);
            child.Start();
            Native.Assign(job, child.Handle);
            child.BeginOutputReadLine();
            child.BeginErrorReadLine();
            started = DateTime.Now;
            game.Start();
            Line("VIctory Launcher: started " + Path.GetFileName(exe) + " (" + ConsoleModel() + "), pid " + child.Id, false);
            if (Bool(settings, "minimizeOnPlay")) WindowState = System.Windows.Forms.FormWindowState.Minimized;
            return State();
        }

        void Stop()
        {
            stoppedByUser = true;
            if (job != IntPtr.Zero) Native.Terminate(job);
            Line("VIctory Launcher: stopped by the user", false);
        }

        void FocusGame()
        {
            if (job == IntPtr.Zero) return;
            IntPtr h;
            Native.WindowOf(Native.JobProcesses(job), out h);
            if (h != IntPtr.Zero) { Native.ShowWindow(h, 9); Native.SetForegroundWindow(h); }
        }

        Dictionary<string, object> State()
        {
            var d = new Dictionary<string, object>();
            var pids = job == IntPtr.Zero ? new List<int>() : Native.JobProcesses(job);
            d["running"] = pids.Count > 0;
            d["pids"] = pids;
            d["started"] = started.ToUniversalTime().Subtract(new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
            d["caption"] = lastCaption;
            return d;
        }

        void GameTick(object sender, EventArgs e)
        {
            var pids = job == IntPtr.Zero ? new List<int>() : Native.JobProcesses(job);
            if (pids.Count == 0 && (child == null || child.HasExited))
            {
                if (test != null) FinishTest(stoppedByUser ? "stopped by the user" : "the game ended before the measurement was done");
                game.Stop();
                int code = 0;
                try { code = child != null ? child.ExitCode : 0; } catch { }
                // Give the reader a moment to deliver the last lines.
                Thread.Sleep(150);
                FlushLog(null, null);
                Line("VIctory Launcher: the game ended (exit code " + code + ")", false);
                FlushLog(null, null);
                if (sessionLog != null) { sessionLog.Dispose(); sessionLog = null; }
                Event("exited", new Dictionary<string, object> {
                    { "code", code }, { "error", lastError }, { "stopped", stoppedByUser },
                    { "seconds", (DateTime.Now - started).TotalSeconds },
                    { "crashLog", FindCrashLog() ?? "" },
                });
                if (WindowState == System.Windows.Forms.FormWindowState.Minimized)
                    WindowState = System.Windows.Forms.FormWindowState.Normal;
                return;
            }
            IntPtr h;
            string caption = Native.WindowOf(pids, out h);
            if (caption != null) lastCaption = caption;
            TestTick(h, caption, pids);
            LiveTick();
            Event("state", State());
        }

        void Line(string text, bool err)
        {
            if (text == null) return;
            lock (logLock)
            {
                if (sessionLog != null) { try { sessionLog.WriteLine(text); } catch { } }
                if (text.StartsWith("Error") || text.Contains("panicked") || text.StartsWith("VIctory: ")
                    || text.Contains(" ERROR ")) lastError = text;
                pending.Add(new Dictionary<string, object> { { "t", text }, { "e", err } });
                if (pending.Count > 4000) pending.RemoveRange(0, pending.Count - 4000);
            }
        }

        void FlushLog(object sender, EventArgs e)
        {
            List<Dictionary<string, object>> batch;
            lock (logLock)
            {
                if (pending.Count == 0) return;
                batch = pending;
                pending = new List<Dictionary<string, object>>();
            }
            Event("log", batch);
        }

        static void KillTree(Process p)
        {
            try
            {
                if (p.HasExited) return;
                var k = Process.Start(new ProcessStartInfo("taskkill.exe", "/T /F /PID " + p.Id) { CreateNoWindow = true, UseShellExecute = false });
                k.WaitForExit(5000);
            }
            catch { }
        }

        // ---------- the shader scan ----------

        void Scan(string lanes, bool validate)
        {
            if (scan != null && !scan.HasExited) throw new Exception("A scan is already running.");
            var status = GameStatus(Str(settings, "gamePath"));
            if (!(bool)status["found"]) throw new Exception("Add your game first.");
            string dir = (string)status["dir"];
            var args = new StringBuilder("translate " + Quote(dir));
            if (validate && FindValidator() != "") args.Append(" --validate");
            var info = Victory(args.ToString(), dir);
            // `VICTORY_HOST_WAVE=<most lanes>`: translate as for a GPU whose waves are at most that.
            var lanesMatch = Regex.Match(lanes ?? "", @"^\d+-(\d+)$");
            if (lanesMatch.Success) info.EnvironmentVariables["VICTORY_HOST_WAVE"] = lanesMatch.Groups[1].Value;
            string v = FindValidator();
            if (v != "") info.EnvironmentVariables["VICTORY_DXIL"] = v;
            scan = new Process { StartInfo = info, EnableRaisingEvents = true };
            var p = scan;
            DataReceived out1 = (text, err) => { if (text != null) Event("scanLine", new Dictionary<string, object> { { "t", text }, { "e", err } }); };
            p.OutputDataReceived += (s, a) => out1(a.Data, false);
            p.ErrorDataReceived += (s, a) => out1(a.Data, true);
            p.Exited += (s, a) =>
            {
                Thread.Sleep(200);
                int code = -1;
                try { code = p.ExitCode; } catch { }
                Event("scanDone", new Dictionary<string, object> { { "code", code } });
            };
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            Event("scanLine", new Dictionary<string, object> { { "t", "> victory " + args }, { "e", false } });
        }

        delegate void DataReceived(string text, bool err);

        // ---------- before the first run: shaders and pipelines made ahead ----------

        Process tool;

        /// `victory shaders <game>` (every shader translated into the cache) or
        /// `victory warm --used` (the host pipelines earlier runs drew with, for the
        /// driver's cache). Both can be stopped and go on later.
        void Tool(string kind)
        {
            if (tool != null && !tool.HasExited) throw new Exception("A tool is already running.");
            if (Running()) throw new Exception("Stop the game first.");
            var status = GameStatus(Str(settings, "gamePath"));
            if (!(bool)status["found"]) throw new Exception("Add your game first.");
            string dir = (string)status["dir"];
            string args;
            if (kind == "shaders") args = "shaders " + Quote(dir);
            else if (kind == "warm") args = "warm --used";
            else throw new Exception("Unknown tool: " + kind);
            var info = Victory(args, dir);
            tool = new Process { StartInfo = info, EnableRaisingEvents = true };
            var p = tool;
            DataReceived line = (text, err) =>
            {
                if (text == null) return;
                Line(text, err);
                Event("toolLine", new Dictionary<string, object> { { "kind", kind }, { "t", text }, { "e", err } });
            };
            p.OutputDataReceived += (s, a) => line(a.Data, false);
            p.ErrorDataReceived += (s, a) => line(a.Data, true);
            p.Exited += (s, a) =>
            {
                Thread.Sleep(200);
                int code = -1;
                try { code = p.ExitCode; } catch { }
                Line("VIctory Launcher: " + kind + " ended (exit code " + code + ")", false);
                Event("toolDone", new Dictionary<string, object> { { "kind", kind }, { "code", code } });
            };
            Line("VIctory Launcher: victory " + args, false);
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
        }

        object ClearCache()
        {
            if (Running()) throw new Exception("Stop the game first.");
            string dir = CacheDir;
            if (!Directory.Exists(dir)) return 0;
            int removed = 0;
            foreach (var f in Directory.GetFiles(dir))
                try { File.Delete(f); removed++; } catch { }
            foreach (var d in Directory.GetDirectories(dir))
                try { Directory.Delete(d, true); removed++; } catch { }
            return removed;
        }
    }
}
