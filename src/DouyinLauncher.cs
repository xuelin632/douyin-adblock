using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;

class DouyinLauncher
{
    [DllImport("wininet.dll", SetLastError = true)]
    static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);
    [DllImport("kernel32.dll")]
    static extern IntPtr GetConsoleWindow();
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

    const string REG = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    const string PROXY = "127.0.0.1:8080";
    const string ORIGIN_PROXY = "127.0.0.1:7897";

    static string RootDir;
    static string PythonDir;
    static string MitmExe;
    static string MitmArgsPrefix = "";
    static string PidFile;
    static string AddonPath;
    static string DouyinExe;
    static string ConfigPath;

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

    static void ApplyProxy()
    {
        using (RegistryKey k = Registry.CurrentUser.OpenSubKey(REG, true))
        {
            if (k == null) return;
            k.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
            k.SetValue("ProxyServer", PROXY, RegistryValueKind.String);
        }
        InternetSetOption(IntPtr.Zero, 39, IntPtr.Zero, 0);
        InternetSetOption(IntPtr.Zero, 37, IntPtr.Zero, 0);
    }

    static void DisableProxy()
    {
        try
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(REG, true))
            {
                if (k == null) return;
                k.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
                k.SetValue("ProxyServer", ORIGIN_PROXY, RegistryValueKind.String);
            }
            InternetSetOption(IntPtr.Zero, 39, IntPtr.Zero, 0);
            InternetSetOption(IntPtr.Zero, 37, IntPtr.Zero, 0);
        }
        catch { }
    }

    static bool MitmAlive()
    {
        try
        {
            if (!File.Exists(PidFile)) return false;
            int pid;
            if (!int.TryParse(File.ReadAllText(PidFile).Trim(), out pid)) return false;
            Process.GetProcessById(pid);
            return true;
        }
        catch { return false; }
    }

    static void KillMitm()
    {
        // 先杀我们自己启动的那个（可能是 python.exe）
        try
        {
            if (File.Exists(PidFile))
            {
                int pid;
                if (int.TryParse(File.ReadAllText(PidFile).Trim(), out pid))
                {
                    try { Process.GetProcessById(pid).Kill(); } catch { }
                }
                try { File.Delete(PidFile); } catch { }
            }
        }
        catch { }
        foreach (Process p in Process.GetProcessesByName("mitmdump"))
        { try { p.Kill(); } catch { } }
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

        var cands = new System.Collections.Generic.List<string>();

        // 1) 同目录下的便携版
        cands.Add(Path.Combine(RootDir, "python", "Scripts", "mitmdump.exe"));

        // 2) 旁边的便携包
        cands.Add(Path.Combine(new DirectoryInfo(RootDir).Parent.FullName,
            "抖音去广告便携版", "python", "Scripts", "mitmdump.exe"));

        // 3) DSH 运行时的各版本（自动枚举，抗更新）
        string rt = Path.Combine(home, ".dsh", "dsh-runtimes");
        try
        {
            if (Directory.Exists(rt))
            {
                foreach (string d in Directory.GetDirectories(rt))
                {
                    string c = Path.Combine(d, "dependencies", "python", "Scripts", "mitmdump.exe");
                    if (File.Exists(c)) cands.Insert(0, c);   // 活的最好，放最前
                }
            }
        }
        catch { }

        // 4) 常见安装位置兜底
        cands.Add(Path.Combine(home, "AppData", "Local", "Programs", "Python", "Scripts", "mitmdump.exe"));
        cands.Add(@"C:\Python312\Scripts\mitmdump.exe");
        cands.Add(@"C:\Python311\Scripts\mitmdump.exe");

        foreach (string c in cands)
        {
            try { if (File.Exists(c)) return c; } catch { }
        }

        // 5) PATH 里找
        try
        {
            foreach (string p in Environment.GetEnvironmentVariable("PATH").Split(';'))
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
        try
        {
            RootDir = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName);
            PythonDir = Path.Combine(RootDir, "python");
            MitmExe = FindMitm();
            AddonPath = Path.Combine(RootDir, "ad_filter.py");
            PidFile = Path.Combine(RootDir, "mitm.pid");
            ConfigPath = Path.Combine(RootDir, "config.txt");

            Console.WriteLine("========================================");
            Console.WriteLine("   抖音去广告");
            Console.WriteLine("========================================");
            Console.WriteLine();

            if (!File.Exists(MitmExe))
            {
                Console.WriteLine("错误：找不到 python\\Scripts\\mitmdump.exe");
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

            Console.Write("[1/3] 启动代理组件 ... ");
            if (Running("mitmdump") || MitmAlive()) Console.WriteLine("已在运行");
            else
            {
                ProcessStartInfo psi = new ProcessStartInfo(MitmExe,
                    MitmArgsPrefix + "--listen-port 8080 -s \"" + AddonPath + "\"");
                psi.WorkingDirectory = RootDir;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                Process mp = Process.Start(psi);
                try { File.WriteAllText(PidFile, mp.Id.ToString()); } catch { }
                Thread.Sleep(2000);
                Console.WriteLine("完成");
            }

            Console.Write("[2/3] 设置系统代理 ... ");
            ApplyProxy();
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
            for (int i = 0; i < 30; i++)
            {
                if (Running("douyin")) { started = true; break; }
                Thread.Sleep(500);
            }

            if (!started)
            {
                Console.WriteLine();
                Console.WriteLine("抖音没有启动，代理保持开启。");
                Console.WriteLine("按任意键关闭...");
                Console.ReadKey();
                return;
            }

            Console.WriteLine("抖音已启动。本窗口将隐藏，抖音关闭后自动关闭代理。");
            Thread.Sleep(1500);

            IntPtr hw = GetConsoleWindow();
            if (hw != IntPtr.Zero) ShowWindow(hw, 0);

            while (Running("douyin")) Thread.Sleep(2000);

            KillMitm();
            DisableProxy();
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Console.WriteLine("出错了: " + ex.Message);
            Console.WriteLine("按任意键关闭...");
            Console.ReadKey();
        }
    }
}
