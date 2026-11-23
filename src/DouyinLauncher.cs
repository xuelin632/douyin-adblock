using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32;

class AdBlockStatistics
{
    public const long RefreshMilliseconds = 5 * 60 * 1000;
    readonly string logPath;
    long position;
    string pending = "";
    public long TotalBlocked { get; private set; }

    public AdBlockStatistics(string path)
    {
        logPath = path;
        // 从启动时的文件末尾开始，只统计这次运行新过滤的广告。
        if (File.Exists(logPath)) position = new FileInfo(logPath).Length;
    }

    public bool Update()
    {
        try
        {
            if (!File.Exists(logPath)) return true;
            using (FileStream stream = new FileStream(logPath, FileMode.Open,
                FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                if (stream.Length < position) { position = 0; pending = ""; }
                stream.Seek(position, SeekOrigin.Begin);
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                {
                    pending += reader.ReadToEnd();
                    position = stream.Position;
                }
            }
            int end;
            while ((end = pending.IndexOf('\n')) >= 0)
            {
                string line = pending.Substring(0, end);
                pending = pending.Substring(end + 1);
                int marker = line.IndexOf(" REMOVED ", StringComparison.Ordinal);
                if (marker < 0) continue;
                int start = marker + " REMOVED ".Length;
                int slash = line.IndexOf('/', start);
                long count;
                if (slash > start && long.TryParse(line.Substring(start, slash - start), out count)
                    && count > 0) TotalBlocked += count;
            }
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}

/// <summary>
/// Windows Job Object：绑定子进程，确保启动器在任何情况下退出时由内核自动终止 mitmproxy，避免僵尸进程。
/// </summary>
class ChildProcessJob : IDisposable
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetInformationJobObject(IntPtr hJob, int infoType, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr hObject);

    const int JobObjectExtendedLimitInformation = 9;
    const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    IntPtr handle;

    public ChildProcessJob()
    {
        handle = CreateJobObject(IntPtr.Zero, null);
        if (handle == IntPtr.Zero) return;

        JOBOBJECT_EXTENDED_LIMIT_INFORMATION info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;

        int length = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
        IntPtr ptr = Marshal.AllocHGlobal(length);
        try
        {
            Marshal.StructureToPtr(info, ptr, false);
            if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformation, ptr, (uint)length))
            {
                CloseHandle(handle);
                handle = IntPtr.Zero;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    public void AddProcess(Process process)
    {
        if (handle == IntPtr.Zero || process == null) return;
        try { AssignProcessToJobObject(handle, process.Handle); } catch { }
    }

    public void Dispose()
    {
        if (handle != IntPtr.Zero)
        {
            CloseHandle(handle);
            handle = IntPtr.Zero;
        }
    }
}

class DouyinLauncher
{
    [DllImport("wininet.dll", SetLastError = true)]
    static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);

    [DllImport("user32.dll")]
    static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    delegate bool ConsoleCtrlDelegate(int ctrlType);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetConsoleCtrlHandler(ConsoleCtrlDelegate handler, bool add);

    const int CTRL_C_EVENT = 0;
    const int CTRL_BREAK_EVENT = 1;
    const int CTRL_CLOSE_EVENT = 2;
    const int CTRL_LOGOFF_EVENT = 5;
    const int CTRL_SHUTDOWN_EVENT = 6;

    const string REG = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    const int PREFERRED_PORT = 18080;
    const int MAX_PORT = 18099;
    const string MUTEX_NAME = @"Local\DouyinAdBlockLauncher_SingleInstance";

    static string RootDir;
    static string MitmExe;
    static string MitmArgsPrefix = "";
    static string PidFile;
    static string AddonPath;
    static string DouyinExe;
    static string ConfigPath;

    static int ListenPort = PREFERRED_PORT;
    static bool HasOrigProxySnapshot = false;
    static int OrigProxyEnable = 0;
    static string OrigProxyServer = null;
    static bool ProxyApplied = false;

    static Process MitmProcess = null;
    static ChildProcessJob ProcessJob = null;
    static int CleanupDone = 0;
    static volatile bool StopRequested = false;

    // 必须持有静态引用，避免委托被 GC 回收导致非托管回调崩溃。
    static ConsoleCtrlDelegate CtrlHandlerRef;

    static void ShowStatistics(AdBlockStatistics statistics, ref long lastTotal, bool final)
    {
        if (!statistics.Update())
        {
            Console.WriteLine("[{0:HH:mm:ss}] 暂时无法读取过滤日志，下次刷新时重试。", DateTime.Now);
            return;
        }
        Console.WriteLine("[{0:HH:mm:ss}] {1}本次累计屏蔽 {2} 条广告（自上次统计新增 {3} 条）。",
            DateTime.Now, final ? "最终统计：" : "", statistics.TotalBlocked,
            statistics.TotalBlocked - lastTotal);
        lastTotal = statistics.TotalBlocked;
    }

    static bool Running(string name)
    {
        try { return Process.GetProcessesByName(name).Length > 0; }
        catch { return false; }
    }

    // ---------- 找抖音 ----------
    static string FindDouyin()
    {
        // 1. 配置文件
        try
        {
            if (File.Exists(ConfigPath))
            {
                foreach (string line in File.ReadAllLines(ConfigPath))
                {
                    if (line.StartsWith("douyin="))
                    {
                        string p = line.Substring(7).Trim();
                        if (File.Exists(p)) return p;
                    }
                }
            }
        }
        catch { }

        // 2. 注册表卸载项
        string[] roots = {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
        };
        foreach (string root in roots)
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(root))
                {
                    if (k == null) continue;
                    foreach (string sub in k.GetSubKeyNames())
                    {
                        try
                        {
                            using (RegistryKey sk = k.OpenSubKey(sub))
                            {
                                if (sk == null) continue;
                                string dn = sk.GetValue("DisplayName") as string;
                                if (dn == null || (!dn.Contains("抖音") && !dn.ToLower().Contains("douyin"))) continue;
                                string loc = sk.GetValue("InstallLocation") as string;
                                if (!string.IsNullOrEmpty(loc))
                                {
                                    string c = Path.Combine(loc, "douyin.exe");
                                    if (File.Exists(c)) return c;
                                    c = Path.Combine(loc, "app", "douyin.exe");
                                    if (File.Exists(c)) return c;
                                }
                                string icon = sk.GetValue("DisplayIcon") as string;
                                if (!string.IsNullOrEmpty(icon))
                                {
                                    icon = icon.Replace("\"", "").Trim();
                                    int comma = icon.LastIndexOf(',');
                                    if (comma > 0) icon = icon.Substring(0, comma);
                                    if (File.Exists(icon)) return icon;
                                }
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        // 3. 常见路径
        foreach (string d in Directory.GetLogicalDrives())
        {
            string[] cand = {
                Path.Combine(d, @"Program Files (x86)\douyin\douyin.exe"),
                Path.Combine(d, @"Program Files\douyin\douyin.exe"),
                Path.Combine(d, @"douyin\douyin.exe"),
                Path.Combine(d, @"douyin\app\douyin.exe")
            };
            foreach (string c in cand)
            {
                try { if (File.Exists(c)) return c; } catch { }
            }
        }
        return null;
    }

    // ---------- 端口探测 ----------
    static int PickAvailablePort(int startPort, int endPort)
    {
        for (int port = startPort; port <= endPort; port++)
        {
            TcpListener listener = null;
            try
            {
                listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
                return port;
            }
            catch (SocketException) { }
            finally
            {
                if (listener != null) { try { listener.Stop(); } catch { } }
            }
        }
        return startPort;
    }

    static bool IsTcpPortOpen(string host, int port, int timeoutMs)
    {
        try
        {
            using (TcpClient client = new TcpClient())
            {
                IAsyncResult ar = client.BeginConnect(host, port, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(timeoutMs)) return false;
                client.EndConnect(ar);
                return true;
            }
        }
        catch { return false; }
    }

    static bool WaitForMitmReady(Process proc, int port, int timeoutMs)
    {
        Stopwatch sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (proc != null && proc.HasExited) return false;
            if (IsTcpPortOpen("127.0.0.1", port, 150)) return true;
            Thread.Sleep(150);
        }
        return false;
    }

    // ---------- 系统代理现场保存、上游检测与还原 ----------
    static void SnapshotOriginalProxy()
    {
        try
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(REG, false))
            {
                if (k == null) return;
                object en = k.GetValue("ProxyEnable");
                if (en is int) OrigProxyEnable = (int)en;
                else if (en != null) int.TryParse(en.ToString(), out OrigProxyEnable);

                OrigProxyServer = k.GetValue("ProxyServer") as string;

                // 如果上一次异常退出残留了旧版 127.0.0.1:8080 且未启用，退出时清理为空避免误导。
                if (OrigProxyEnable == 0 && string.Equals(OrigProxyServer, "127.0.0.1:8080", StringComparison.OrdinalIgnoreCase))
                {
                    OrigProxyServer = "";
                }

                HasOrigProxySnapshot = true;
            }
        }
        catch { }
    }

    static bool TryParseHostPort(string raw, out string host, out int port)
    {
        host = null;
        port = 0;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        string target = raw.Trim();
        // 处理 Windows 分协议格式：http=127.0.0.1:7897;https=127.0.0.1:7897
        if (target.Contains("="))
        {
            string picked = null;
            foreach (string part in target.Split(';'))
            {
                string item = part.Trim();
                if (item.StartsWith("https=", StringComparison.OrdinalIgnoreCase))
                {
                    picked = item.Substring(6).Trim();
                    break;
                }
                if (picked == null && item.StartsWith("http=", StringComparison.OrdinalIgnoreCase))
                {
                    picked = item.Substring(5).Trim();
                }
            }
            if (string.IsNullOrWhiteSpace(picked)) return false;
            target = picked;
        }

        if (target.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            target = target.Substring(7);
        else if (target.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            target = target.Substring(8);

        int slash = target.IndexOf('/');
        if (slash >= 0) target = target.Substring(0, slash);

        int colon = target.LastIndexOf(':');
        if (colon <= 0 || colon >= target.Length - 1) return false;

        string h = target.Substring(0, colon).Trim();
        int p;
        if (!int.TryParse(target.Substring(colon + 1).Trim(), out p) || p <= 0 || p > 65535)
            return false;

        host = h;
        port = p;
        return true;
    }

    static string DetectUpstreamProxy(int selfPort)
    {
        if (!HasOrigProxySnapshot || OrigProxyEnable == 0 || string.IsNullOrWhiteSpace(OrigProxyServer))
            return null;

        string host;
        int port;
        if (!TryParseHostPort(OrigProxyServer, out host, out port))
            return null;

        bool isLocal = string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase);

        // 绝不把自己当上游，避免循环代理。
        if (isLocal && (port == selfPort || (port >= PREFERRED_PORT && port <= MAX_PORT)))
            return null;

        if (!IsTcpPortOpen(host, port, 300))
            return null;

        return "http://" + host + ":" + port;
    }

    static void RefreshSystemProxy()
    {
        InternetSetOption(IntPtr.Zero, 39, IntPtr.Zero, 0);
        InternetSetOption(IntPtr.Zero, 37, IntPtr.Zero, 0);
    }

    static void ApplyProxy(int port)
    {
        using (RegistryKey k = Registry.CurrentUser.OpenSubKey(REG, true))
        {
            if (k == null) return;
            k.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
            k.SetValue("ProxyServer", "127.0.0.1:" + port, RegistryValueKind.String);
        }
        ProxyApplied = true;
        RefreshSystemProxy();
    }

    static void RestoreProxy()
    {
        if (!ProxyApplied) return;
        try
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(REG, true))
            {
                if (k == null) return;
                if (HasOrigProxySnapshot)
                {
                    k.SetValue("ProxyEnable", OrigProxyEnable, RegistryValueKind.DWord);
                    if (OrigProxyServer != null)
                        k.SetValue("ProxyServer", OrigProxyServer, RegistryValueKind.String);
                }
                else
                {
                    k.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
                }
            }
            RefreshSystemProxy();
            ProxyApplied = false;
        }
        catch { }
    }

    // ---------- 安全的 mitmproxy 进程校验与清理 ----------
    static bool IsOurMitmProcess(Process p)
    {
        if (p == null) return false;
        try
        {
            if (p.HasExited) return false;
            string name = (p.ProcessName ?? "").ToLowerInvariant();
            if (name != "python" && name != "pythonw" && name != "mitmdump")
                return false;

            string exePath = p.MainModule.FileName;
            if (string.IsNullOrEmpty(exePath)) return false;

            if (!string.IsNullOrEmpty(RootDir) &&
                exePath.StartsWith(RootDir, StringComparison.OrdinalIgnoreCase))
                return true;

            if (!string.IsNullOrEmpty(MitmExe) &&
                string.Equals(exePath, MitmExe, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        catch { }
        return false;
    }

    static void CleanupStalePidFile()
    {
        try
        {
            if (!File.Exists(PidFile)) return;
            int pid;
            if (int.TryParse(File.ReadAllText(PidFile).Trim(), out pid))
            {
                try
                {
                    Process p = Process.GetProcessById(pid);
                    if (IsOurMitmProcess(p))
                    {
                        p.Kill();
                        try { p.WaitForExit(2000); } catch { }
                    }
                }
                catch { }
            }
            try { File.Delete(PidFile); } catch { }
        }
        catch { }
    }

    static void KillMitm()
    {
        try
        {
            if (MitmProcess != null)
            {
                try
                {
                    if (!MitmProcess.HasExited)
                    {
                        MitmProcess.Kill();
                        try { MitmProcess.WaitForExit(2000); } catch { }
                    }
                }
                catch { }
                MitmProcess = null;
            }
        }
        catch { }

        CleanupStalePidFile();

        try
        {
            if (ProcessJob != null)
            {
                ProcessJob.Dispose();
                ProcessJob = null;
            }
        }
        catch { }
    }

    static void Cleanup()
    {
        if (Interlocked.Exchange(ref CleanupDone, 1) != 0) return;
        RestoreProxy();
        KillMitm();
    }

    static bool OnConsoleCtrl(int ctrlType)
    {
        if (ctrlType == CTRL_C_EVENT || ctrlType == CTRL_BREAK_EVENT)
        {
            StopRequested = true;
            return true;
        }
        if (ctrlType == CTRL_CLOSE_EVENT || ctrlType == CTRL_LOGOFF_EVENT || ctrlType == CTRL_SHUTDOWN_EVENT)
        {
            StopRequested = true;
            Cleanup();
            return false;
        }
        return false;
    }

    static bool ActivateDouyin()
    {
        HashSet<uint> pids = new HashSet<uint>();
        foreach (Process p in Process.GetProcessesByName("douyin")) pids.Add((uint)p.Id);
        if (pids.Count == 0) return false;
        IntPtr best = IntPtr.Zero; long bestArea = 0;
        EnumWindows(delegate(IntPtr h, IntPtr l)
        {
            uint pid;
            GetWindowThreadProcessId(h, out pid);
            if (!pids.Contains(pid)) return true;
            RECT r;
            if (!GetWindowRect(h, out r)) return true;
            long area = (long)(r.Right - r.Left) * (r.Bottom - r.Top);
            if ((r.Right - r.Left) > 400 && (r.Bottom - r.Top) > 300 && area > bestArea)
            { bestArea = area; best = h; }
            return true;
        }, IntPtr.Zero);
        if (best == IntPtr.Zero) return false;
        ShowWindow(best, 9);
        ShowWindow(best, 5);
        return true;
    }

    // 按优先级找 mitmdump.exe
    static string FindMitm()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // 优先：本目录 python + run_mitmdump.py（不依赖 mitmdump.exe 的 shebang）
        string wrapper = Path.Combine(RootDir, "run_mitmdump.py");
        if (File.Exists(wrapper))
        {
            string py = Path.Combine(RootDir, "python", "python.exe");
            if (File.Exists(py))
            {
                MitmArgsPrefix = "\"" + wrapper + "\" ";
                return py;
            }
        }

        List<string> cands = new List<string>();
        cands.Add(Path.Combine(RootDir, "python", "Scripts", "mitmdump.exe"));
        cands.Add(Path.Combine(new DirectoryInfo(RootDir).Parent.FullName,
            "抖音去广告便携版", "python", "Scripts", "mitmdump.exe"));

        string rt = Path.Combine(home, ".dsh", "dsh-runtimes");
        try
        {
            if (Directory.Exists(rt))
            {
                foreach (string d in Directory.GetDirectories(rt))
                {
                    string c = Path.Combine(d, "dependencies", "python", "Scripts", "mitmdump.exe");
                    if (File.Exists(c)) cands.Insert(0, c);
                }
            }
        }
        catch { }

        cands.Add(Path.Combine(home, "AppData", "Local", "Programs", "Python", "Scripts", "mitmdump.exe"));
        cands.Add(@"C:\Python312\Scripts\mitmdump.exe");
        cands.Add(@"C:\Python311\Scripts\mitmdump.exe");

        foreach (string c in cands)
        {
            try { if (File.Exists(c)) return c; } catch { }
        }

        try
        {
            string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (string p in pathEnv.Split(';'))
            {
                if (string.IsNullOrWhiteSpace(p)) continue;
                string c = Path.Combine(p.Trim(), "mitmdump.exe");
                if (File.Exists(c)) return c;
            }
        }
        catch { }

        return cands.Count > 0 ? cands[0] : "";
    }

    static void Main()
    {
        Console.Title = "抖音去广告";

        bool createdNew;
        using (Mutex singleInstance = new Mutex(true, MUTEX_NAME, out createdNew))
        {
            if (!createdNew)
            {
                Console.WriteLine("抖音去广告启动器已在运行中。");
                if (ActivateDouyin()) Console.WriteLine("已将抖音切换到前台。");
                Thread.Sleep(1500);
                return;
            }

            CtrlHandlerRef = new ConsoleCtrlDelegate(OnConsoleCtrl);
            SetConsoleCtrlHandler(CtrlHandlerRef, true);
            AppDomain.CurrentDomain.ProcessExit += delegate { Cleanup(); };

            try
            {
                RootDir = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName);
                MitmExe = FindMitm();
                AddonPath = Path.Combine(RootDir, "ad_filter.py");
                PidFile = Path.Combine(RootDir, "mitm.pid");
                ConfigPath = Path.Combine(RootDir, "config.txt");

                Console.WriteLine("========================================");
                Console.WriteLine("   抖音去广告（白名单精准版）");
                Console.WriteLine("========================================");
                Console.WriteLine();

                if (!File.Exists(MitmExe))
                {
                    Console.WriteLine("错误：找不到内置 python 或 mitmdump.exe");
                    Console.WriteLine("请确认整个文件夹完整解压（不要只复制 exe）。");
                    Console.WriteLine("按任意键退出...");
                    Console.ReadKey();
                    return;
                }

                DouyinExe = FindDouyin();
                if (DouyinExe == null)
                {
                    Console.WriteLine("错误：没找到抖音安装位置。");
                    Console.WriteLine("请在 config.txt 里写一行（等号后面填完整路径）：");
                    Console.WriteLine("    douyin=D:\\你的路径\\douyin.exe");
                    Console.WriteLine();
                    File.WriteAllText(ConfigPath,
                        "# 抖音安装路径（如果自动找不到，取消下面一行的注释并改成你的路径）\r\n" +
                        "# douyin=C:\\Program Files (x86)\\douyin\\douyin.exe\r\n");
                    Console.WriteLine("已生成 config.txt，改好后重新运行本程序。");
                    Console.WriteLine("按任意键退出...");
                    Console.ReadKey();
                    return;
                }
                Console.WriteLine("抖音位置: " + DouyinExe);

                // 先安全清理上次可能残留的旧 mitm.pid（会校验进程归属，绝不误杀）。
                CleanupStalePidFile();

                // 保存启动前的系统代理现场，并检测是否需要串联上游代理（如 Clash 7897）。
                SnapshotOriginalProxy();
                ListenPort = PickAvailablePort(PREFERRED_PORT, MAX_PORT);
                string upstream = DetectUpstreamProxy(ListenPort);

                Console.WriteLine("本地监听: 127.0.0.1:" + ListenPort + "（仅解密 www.douyin.com）");
                if (!string.IsNullOrEmpty(upstream))
                    Console.WriteLine("上游代理: " + upstream + "（已自动串联，保持原代理不断）");

                AdBlockStatistics statistics = new AdBlockStatistics(Path.Combine(RootDir, "filter.log"));
                long lastTotal = 0;

                Console.Write("[1/3] 启动代理组件 ... ");
                StringBuilder args = new StringBuilder();
                args.Append(MitmArgsPrefix);
                args.Append("--listen-host 127.0.0.1 --listen-port ");
                args.Append(ListenPort);
                if (!string.IsNullOrEmpty(upstream))
                {
                    args.Append(" --mode upstream:");
                    args.Append(upstream);
                }
                args.Append(" -q -s \"");
                args.Append(AddonPath);
                args.Append("\"");

                ProcessStartInfo psi = new ProcessStartInfo(MitmExe, args.ToString());
                psi.WorkingDirectory = RootDir;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                psi.RedirectStandardError = true;
                psi.RedirectStandardOutput = true;

                StringBuilder errBuf = new StringBuilder();
                ProcessJob = new ChildProcessJob();
                MitmProcess = new Process();
                MitmProcess.StartInfo = psi;
                MitmProcess.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e)
                {
                    if (!string.IsNullOrEmpty(e.Data) && errBuf.Length < 2000)
                        errBuf.AppendLine(e.Data);
                };
                MitmProcess.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e) { };

                MitmProcess.Start();
                ProcessJob.AddProcess(MitmProcess);
                MitmProcess.BeginErrorReadLine();
                MitmProcess.BeginOutputReadLine();
                try { File.WriteAllText(PidFile, MitmProcess.Id.ToString()); } catch { }

                if (!WaitForMitmReady(MitmProcess, ListenPort, 15000))
                {
                    Console.WriteLine("失败");
                    if (errBuf.Length > 0)
                    {
                        Console.WriteLine("代理组件报错信息：");
                        Console.WriteLine(errBuf.ToString().Trim());
                    }
                    else
                    {
                        Console.WriteLine("代理端口 127.0.0.1:" + ListenPort + " 启动超时。");
                    }
                    Cleanup();
                    Console.WriteLine("按任意键退出...");
                    Console.ReadKey();
                    return;
                }
                Console.WriteLine("完成");

                Console.Write("[2/3] 设置系统代理 ... ");
                ApplyProxy(ListenPort);
                Console.WriteLine("完成");

                Console.Write("[3/3] 启动抖音 ... ");
                if (Running("douyin"))
                {
                    if (ActivateDouyin()) Console.WriteLine("已在运行，已切到前台");
                    else
                    {
                        Console.Write("残留在托盘，重启中 ... ");
                        foreach (Process p in Process.GetProcessesByName("douyin"))
                        { try { p.Kill(); } catch { } }
                        Thread.Sleep(2500);
                        Process.Start("explorer.exe", "\"" + DouyinExe + "\"");
                        Console.WriteLine("完成");
                    }
                }
                else
                {
                    // 关键：必须用 explorer 启动（抖音检测父进程）
                    Process.Start("explorer.exe", "\"" + DouyinExe + "\"");
                    Console.WriteLine("完成");
                }

                Console.WriteLine();
                Console.WriteLine("等待抖音启动...");
                bool started = false;
                for (int i = 0; i < 30 && !StopRequested; i++)
                {
                    if (Running("douyin")) { started = true; break; }
                    Thread.Sleep(500);
                }

                if (!started)
                {
                    Console.WriteLine();
                    Console.WriteLine("抖音没有启动，正在自动恢复代理并清理...");
                    Cleanup();
                    Console.WriteLine("代理已恢复。按任意键关闭...");
                    Console.ReadKey();
                    return;
                }

                Console.WriteLine("抖音已启动。本窗口保持显示，广告统计每 5 分钟刷新一次。");
                Console.WriteLine("统计范围：本次启动后实际过滤的广告条目。可最小化本窗口继续使用。");
                Console.WriteLine("退出抖音或直接关闭本窗口，均会自动还原系统代理。");
                ShowStatistics(statistics, ref lastTotal, false);

                Stopwatch refreshClock = Stopwatch.StartNew();
                long nextRefresh = AdBlockStatistics.RefreshMilliseconds;
                int missCount = 0;
                while (!StopRequested)
                {
                    Thread.Sleep(1500);
                    if (Running("douyin"))
                    {
                        missCount = 0;
                    }
                    else
                    {
                        missCount++;
                        // 连续 2 次未检测到进程再判定退出，防止抖音自更新重启瞬间误关代理。
                        if (missCount >= 2) break;
                    }

                    if (refreshClock.ElapsedMilliseconds >= nextRefresh)
                    {
                        ShowStatistics(statistics, ref lastTotal, false);
                        nextRefresh = refreshClock.ElapsedMilliseconds + AdBlockStatistics.RefreshMilliseconds;
                    }
                }

                Cleanup();
                ShowStatistics(statistics, ref lastTotal, true);
                Console.WriteLine("代理已还原，统计窗口即将自动关闭。");
                Thread.Sleep(1500);
            }
            catch (Exception ex)
            {
                Cleanup();
                Console.WriteLine();
                Console.WriteLine("出错了: " + ex.Message);
                Console.WriteLine("系统代理已自动恢复。按任意键关闭...");
                Console.ReadKey();
            }
            finally
            {
                Cleanup();
            }
        }
    }
}
