// VIctory Launcher - the live link: commands to the running game and its numbers each second,
// through two files VIctory watches and writes (`VICTORY_LIVE`, victory-gpu's live.rs).
// Copyright (C) 2026 God489. GPL-3.0-or-later.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace VictoryLauncher
{
    public sealed partial class MainForm
    {
        string LiveDir { get { return Path.Combine(Program.Home, "live"); } }

        DateTime liveRead = DateTime.MinValue;

        /// The file of the running game's process: `<pid>.cmd` or `<pid>.json`.
        string LiveFile(string extension)
        {
            if (child == null) return null;
            try { if (child.HasExited) return null; } catch { return null; }
            return Path.Combine(LiveDir, child.Id + extension);
        }

        /// Replaces the values the running game takes from the launcher: `text` is every
        /// `set NAME=VALUE` wanted (the game reads the whole file again when it changes).
        object LiveSet(string text)
        {
            string file = LiveFile(".cmd");
            if (file == null) throw new Exception("The game is not running.");
            Directory.CreateDirectory(LiveDir);
            string temp = file + ".tmp";
            File.WriteAllText(temp, text ?? "", new UTF8Encoding(false));
            if (File.Exists(file)) File.Delete(file);
            File.Move(temp, file);
            return true;
        }

        /// Each second while the game runs: its numbers, when it wrote new ones.
        void LiveTick()
        {
            string file = LiveFile(".json");
            if (file == null || !File.Exists(file)) return;
            try
            {
                var written = File.GetLastWriteTimeUtc(file);
                if (written <= liveRead) return;
                liveRead = written;
                var data = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(file));
                Event("live", data);
            }
            catch { }
        }

        /// What an earlier run left in the folder: its files go before a run starts.
        void LiveClear()
        {
            liveRead = DateTime.MinValue;
            try
            {
                if (!Directory.Exists(LiveDir)) return;
                foreach (var f in Directory.GetFiles(LiveDir))
                    try { File.Delete(f); } catch { }
            }
            catch { }
        }
    }
}
