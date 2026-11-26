// VIctory Launcher - the test run: the game played to a busy scene with the current settings,
// a picture of it and a minute of its frame times, kept for comparison.
// Copyright (C) 2026 God489. GPL-3.0-or-later.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace VictoryLauncher
{
    public sealed partial class MainForm
    {
        /// A test run in progress.
        sealed class TestRun
        {
            public string Dir;
            public DateTime Started;
            public DateTime? Scene;
            public DateTime Until;
            public int Seen;
            public long Peak;
            public readonly List<double> Ms = new List<double>();
            public readonly List<int> Draws = new List<int>();
            public string Picture = "";
            public Dictionary<string, object> Settings;
        }

        TestRun test;

        /// The scene: a frame of at least this many draws, three seconds in a row (the opening
        /// cutscene has 3,000 to 6,500; menus and videos have a few dozen).
        const int TestDraws = 3000;
        const int TestSeconds = 60;
        const int TestMinutes = 15;

        string TestsDir { get { return Path.Combine(Program.Home, "tests"); } }

        object StartTest()
        {
            if (test != null) throw new Exception("A test run is already going.");
            var state = Play();
            string dir = Path.Combine(TestsDir, DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture));
            Directory.CreateDirectory(dir);
            test = new TestRun { Dir = dir, Started = DateTime.Now, Settings = TestSettings() };
            Line("VIctory Launcher: test run: waiting for a scene of " + TestDraws + " draws a frame or more", false);
            TestEvent("waiting");
            return state;
        }

        /// What a result is compared by: the settings that change how the game runs.
        Dictionary<string, object> TestSettings()
        {
            var d = new Dictionary<string, object>();
            foreach (var k in new[] { "mode", "patches", "textureCheck", "lastResort", "memory", "console", "fps60", "rayTracing", "ambient" })
                d[k] = settings.ContainsKey(k) ? settings[k] : null;
            d["consoleModel"] = ConsoleModel();
            d["limited"] = LimitedMemory();
            d["customEnv"] = Str(settings, "customEnv");
            return d;
        }

        /// Called each second with the game's caption while a test runs.
        void TestTick(IntPtr window, string caption, List<int> pids)
        {
            if (test == null) return;
            long memory = 0;
            foreach (int pid in pids)
                try { using (var p = Process.GetProcessById(pid)) memory += p.PrivateMemorySize64; } catch { }
            test.Peak = Math.Max(test.Peak, memory);
            var m = caption == null ? null : Regex.Match(caption, @"([\d.]+) ms/frame (\d+) draws/frame");
            double ms = 0;
            int draws = 0;
            bool read = m != null && m.Success
                && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out ms)
                && int.TryParse(m.Groups[2].Value, out draws);
            if (test.Scene == null)
            {
                test.Seen = read && draws >= TestDraws ? test.Seen + 1 : 0;
                if (test.Seen >= 3)
                {
                    test.Scene = DateTime.Now;
                    test.Until = DateTime.Now.AddSeconds(TestSeconds);
                    test.Picture = Snapshot(window, Path.Combine(test.Dir, "scene.png"));
                    Line("VIctory Launcher: test run: the scene, after " + (int)(DateTime.Now - test.Started).TotalSeconds + " s; measuring " + TestSeconds + " s", false);
                    TestEvent("measuring");
                }
                else if ((DateTime.Now - test.Started).TotalMinutes > TestMinutes)
                    FinishTest("no scene of " + TestDraws + " draws a frame in " + TestMinutes + " minutes");
                return;
            }
            if (read)
            {
                test.Ms.Add(ms);
                test.Draws.Add(draws);
            }
            if (DateTime.Now >= test.Until)
            {
                string late = Snapshot(window, Path.Combine(test.Dir, "scene-end.png"));
                FinishTest(null);
                if (late == "") Line("VIctory Launcher: test run: no picture at the end", false);
            }
            else TestEvent("measuring");
        }

        /// Ends the test run: its result written beside its pictures, the game stopped.
        /// `problem`: why it ended without a measurement (null: it measured).
        void FinishTest(string problem)
        {
            var t = test;
            if (t == null) return;
            test = null;
            var r = TestResult(t, problem);
            try { File.WriteAllText(Path.Combine(t.Dir, "result.json"), new JavaScriptSerializer().Serialize(r), new UTF8Encoding(false)); } catch { }
            Line("VIctory Launcher: test run: " + (problem ?? ("median " + Fmt((double)r["median"]) + " ms a frame, " + Fmt((double)r["fps"]) + " fps")), false);
            if (Running()) Stop();
            Event("testDone", r);
        }

        Dictionary<string, object> TestResult(TestRun t, string problem)
        {
            var sorted = t.Ms.OrderBy(x => x).ToList();
            Func<double, double> at = q => sorted.Count == 0 ? 0 : sorted[Math.Min(sorted.Count - 1, (int)Math.Floor(q * sorted.Count))];
            double median = at(0.5);
            var draws = t.Draws.OrderBy(x => x).ToList();
            return new Dictionary<string, object> {
                { "id", Path.GetFileName(t.Dir) },
                { "dir", t.Dir },
                { "problem", problem ?? "" },
                { "samples", sorted.Count },
                { "median", median },
                { "p90", at(0.9) },
                { "best", sorted.Count == 0 ? 0 : sorted[0] },
                { "fps", median > 0 ? 1000.0 / median : 0 },
                { "draws", draws.Count == 0 ? 0 : draws[draws.Count / 2] },
                { "sceneAfter", t.Scene.HasValue ? (t.Scene.Value - t.Started).TotalSeconds : 0 },
                { "peakGB", t.Peak / 1073741824.0 },
                { "picture", t.Picture },
                { "settings", t.Settings },
                { "build", Info().ContainsKey("victoryCommit") ? Info()["victoryCommit"] : "" },
                { "launcher", Program.Version },
            };
        }

        void TestEvent(string phase)
        {
            var t = test;
            if (t == null) return;
            Event("test", new Dictionary<string, object> {
                { "phase", phase },
                { "seconds", (DateTime.Now - t.Started).TotalSeconds },
                { "left", t.Scene.HasValue ? Math.Max(0, (t.Until - DateTime.Now).TotalSeconds) : 0 },
                { "samples", t.Ms.Count },
            });
        }

        /// The game window's own picture, or "". PrintWindow with PW_RENDERFULLCONTENT (2) asks the
        /// compositor for what the window shows, its flip-model swap chain too, so other windows over
        /// it do not end up in the picture and the focus stays where the player left it (a copy of
        /// the screen took whatever lay on top). A minimized window is restored first; the screen
        /// is copied only where PrintWindow fails.
        static string Snapshot(IntPtr window, string file)
        {
            if (window == IntPtr.Zero) return "";
            try
            {
                if (Native.IsIconic(window))
                {
                    Native.ShowWindow(window, 9);
                    System.Threading.Thread.Sleep(400);
                }
                Native.RECT r;
                if (!Native.GetWindowRect(window, out r) || r.Right <= r.Left || r.Bottom <= r.Top) return "";
                using (var bmp = new Bitmap(r.Right - r.Left, r.Bottom - r.Top))
                {
                    using (var g = Graphics.FromImage(bmp))
                    {
                        IntPtr dc = g.GetHdc();
                        bool printed;
                        try { printed = Native.PrintWindow(window, dc, 2); }
                        finally { g.ReleaseHdc(dc); }
                        if (!printed) g.CopyFromScreen(r.Left, r.Top, 0, 0, bmp.Size);
                    }
                    bmp.Save(file, ImageFormat.Png);
                }
                return file;
            }
            catch { return ""; }
        }

        /// The results of earlier test runs, newest first.
        object TestList()
        {
            var list = new List<object>();
            if (!Directory.Exists(TestsDir)) return list;
            var js = new JavaScriptSerializer();
            foreach (var dir in Directory.GetDirectories(TestsDir).OrderByDescending(d => d).Take(30))
            {
                string f = Path.Combine(dir, "result.json");
                if (!File.Exists(f)) continue;
                try { list.Add(js.DeserializeObject(File.ReadAllText(f))); } catch { }
            }
            return list;
        }

        static string Fmt(double v) { return v.ToString("0.0", CultureInfo.InvariantCulture); }
    }
}
