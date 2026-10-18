// Win32 helpers for Coral Launcher. Compiled at start-up by PowerShell's Add-Type (C# 5 syntax only).
using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

public static class CoralNative
{
    // ---------- shell icons (48 px, same icons Explorer shows) ----------
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [ComImport, Guid("46EB5926-582E-4017-9FDF-E8998DAA0950"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IImageList
    {
        [PreserveSig] int Add(IntPtr hbmImage, IntPtr hbmMask, ref int pi);
        [PreserveSig] int ReplaceIcon(int i, IntPtr hicon, ref int pi);
        [PreserveSig] int SetOverlayImage(int iImage, int iOverlay);
        [PreserveSig] int Replace(int i, IntPtr hbmImage, IntPtr hbmMask);
        [PreserveSig] int AddMasked(IntPtr hbmImage, int crMask, ref int pi);
        [PreserveSig] int Draw(IntPtr pimldp);
        [PreserveSig] int Remove(int i);
        [PreserveSig] int GetIcon(int i, int flags, ref IntPtr picon);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SHGetFileInfo(string pszPath, uint attr, ref SHFILEINFO psfi, uint cb, uint flags);

    [DllImport("shell32.dll", EntryPoint = "#727")]
    static extern int SHGetImageList(int iImageList, ref Guid riid, out IImageList ppv);

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr hIcon);

    const uint SHGFI_SYSICONINDEX = 0x4000;
    const int SHIL_EXTRALARGE = 2;   // 48 x 48
    const int ILD_TRANSPARENT = 1;

    public static BitmapSource GetIcon(string path)
    {
        SHFILEINFO sfi = new SHFILEINFO();
        IntPtr ok = SHGetFileInfo(path, 0, ref sfi, (uint)Marshal.SizeOf(sfi), SHGFI_SYSICONINDEX);
        if (ok == IntPtr.Zero) return null;
        Guid iid = new Guid("46EB5926-582E-4017-9FDF-E8998DAA0950");
        IImageList list;
        if (SHGetImageList(SHIL_EXTRALARGE, ref iid, out list) != 0 || list == null) return null;
        IntPtr hIcon = IntPtr.Zero;
        list.GetIcon(sfi.iIcon, ILD_TRANSPARENT, ref hIcon);
        if (hIcon == IntPtr.Zero) return null;
        try
        {
            BitmapSource bs = Imaging.CreateBitmapSourceFromHIcon(hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            bs.Freeze();
            return bs;
        }
        finally { DestroyIcon(hIcon); }
    }

    // ---------- "on desktop" layering (like Rainmeter's On Desktop) ----------
    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    const uint GW_HWNDPREV = 3;
    const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10, SWP_NOOWNERZORDER = 0x200;

    // The top-level window that hosts the desktop icons (Progman or a WorkerW).
    public static IntPtr FindDesktopHost()
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows(delegate (IntPtr h, IntPtr l)
        {
            if (FindWindowEx(h, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    // Keep 'me' directly above the desktop: behind every app window, in front of the wallpaper.
    // When the desktop itself is brought forward (Win+D, clicking the desktop) the launcher rides along with it.
    public static void KeepOnDesktop(IntPtr me)
    {
        if (me == IntPtr.Zero || GetForegroundWindow() == me) return;
        IntPtr host = FindDesktopHost();
        if (host == IntPtr.Zero) return;
        IntPtr prev = GetWindow(host, GW_HWNDPREV);
        if (prev == me) return;
        IntPtr after = prev == IntPtr.Zero ? IntPtr.Zero /* HWND_TOP */ : prev;
        SetWindowPos(me, after, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
    }

    // ---------- diagnostics: list visible top-level windows, top to bottom ----------
    [DllImport("user32.dll")] static extern IntPtr GetTopWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder sb, int n);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

    public static string DumpZOrder(IntPtr me)
    {
        StringBuilder o = new StringBuilder();
        IntPtr host = FindDesktopHost();
        IntPtr h = GetTopWindow(IntPtr.Zero);
        int n = 0;
        while (h != IntPtr.Zero && n < 600)
        {
            bool vis = IsWindowVisible(h);
            if (vis || h == me || h == host)
            {
                StringBuilder cls = new StringBuilder(256); GetClassName(h, cls, 256);
                StringBuilder title = new StringBuilder(256); GetWindowText(h, title, 256);
                uint pid; GetWindowThreadProcessId(h, out pid);
                string proc = "?";
                try { proc = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; } catch { }
                string mark = h == me ? "  <== LAUNCHER" : (h == host ? "  <== DESKTOP ICONS HOST" : "");
                o.AppendLine(string.Format("  {0,-10} {1,-28} {2,-22} {3}{4}{5}", h.ToInt64().ToString("X"), cls.ToString(), proc, title.ToString(), vis ? "" : " (hidden)", mark));
            }
            h = GetWindow(h, 2 /* GW_HWNDNEXT */);
            n++;
        }
        return o.ToString();
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder sb, int n);

    public static void SetTopmost(IntPtr h, bool on)
    {
        SetWindowPos(h, new IntPtr(on ? -1 : -2), 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);
    }

    // ---------- hide from Alt+Tab ----------
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int index);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int index, int value);
    public static void MakeToolWindow(IntPtr h)
    {
        int ex = GetWindowLong(h, -20);
        SetWindowLong(h, -20, (ex | 0x80) & ~0x40000); // + WS_EX_TOOLWINDOW, - WS_EX_APPWINDOW
    }

    // ---------- global hotkey ----------
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h, int id);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);

    public static Action OnHotkey;
    static HwndSource source;

    // One message hook for the window: hotkey presses and Everything search replies both arrive here.
    public static void InstallHook(IntPtr h)
    {
        if (source != null) return;
        source = HwndSource.FromHwnd(h);
        source.AddHook(WndProc);
    }

    public static bool HookHotkey(IntPtr h, uint mods, uint vk)
    {
        InstallHook(h);
        return RegisterHotKey(h, 0xC0DE, mods | 0x4000 /* MOD_NOREPEAT */, vk);
    }

    public static void UnhookHotkey(IntPtr h) { UnregisterHotKey(h, 0xC0DE); }

    public static void BringToFront(IntPtr h) { SetForegroundWindow(h); }

    // Windows only lets the app the user is working in hand out keyboard focus. A click on the
    // wallpaper doesn't count, so tap Alt (which unlocks SetForegroundWindow) and borrow the
    // current foreground thread's input queue for a moment.
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint a, uint b, bool attach);
    [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr h);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }

    public static void ForceForeground(IntPtr h)
    {
        uint pid;
        uint fgThread = GetWindowThreadProcessId(GetForegroundWindow(), out pid);
        uint me = GetCurrentThreadId();
        keybd_event(0x12, 0, 0, UIntPtr.Zero);          // Alt down
        keybd_event(0x12, 0, 2, UIntPtr.Zero);          // Alt up
        bool attached = fgThread != 0 && fgThread != me && AttachThreadInput(me, fgThread, true);
        BringWindowToTop(h);
        SetForegroundWindow(h);
        if (attached) AttachThreadInput(me, fgThread, false);
    }

    public static int[] CursorPos() { POINT p; GetCursorPos(out p); return new int[] { p.X, p.Y }; }

    // ---------- Everything (voidtools) search over its IPC interface ----------
    // Everything must be running. We send a WM_COPYDATA query to its hidden IPC window;
    // it answers with a WM_COPYDATA of its own, sent back to our window (see WndProc).
    [StructLayout(LayoutKind.Sequential)]
    struct COPYDATASTRUCT { public IntPtr dwData; public int cbData; public IntPtr lpData; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string title);
    [DllImport("user32.dll")] static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, ref COPYDATASTRUCT l, uint flags, uint timeout, out IntPtr result);

    const uint WM_COPYDATA = 0x004A;
    const int EVERYTHING_IPC_COPYDATAQUERYW = 2;
    const string EVERYTHING_IPC_WNDCLASS = "EVERYTHING_TASKBAR_NOTIFICATION";
    public const int REPLY_BASE = 0x0C0DE100;   // reply ids we hand to Everything: REPLY_BASE + kind

    // kind, full paths, flags (1 = folder, 2 = drive)
    public static Action<int, string[], int[]> OnEverything;

    static IntPtr FindEverything()
    {
        IntPtr h = FindWindow(EVERYTHING_IPC_WNDCLASS, null);                       // Everything 1.4
        if (h == IntPtr.Zero) h = FindWindow(EVERYTHING_IPC_WNDCLASS + "_(1.5a)", null); // Everything 1.5 alpha
        return h;
    }

    public static bool EverythingRunning() { return FindEverything() != IntPtr.Zero; }

    public static bool EverythingSearch(IntPtr replyTo, int kind, string search, int maxResults)
    {
        IntPtr ev = FindEverything();
        if (ev == IntPtr.Zero) return false;
        InstallHook(replyTo);

        // EVERYTHING_IPC_QUERYW: DWORD reply_hwnd, reply_copydata_message, search_flags, offset, max_results; WCHAR search_string[]
        byte[] text = Encoding.Unicode.GetBytes(search + "\0");
        int size = 20 + text.Length;
        IntPtr buf = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.WriteInt32(buf, 0, (int)replyTo.ToInt64());
            Marshal.WriteInt32(buf, 4, REPLY_BASE + kind);
            Marshal.WriteInt32(buf, 8, 0);            // flags: case-insensitive, match names
            Marshal.WriteInt32(buf, 12, 0);           // offset
            Marshal.WriteInt32(buf, 16, maxResults);
            Marshal.Copy(text, 0, IntPtr.Add(buf, 20), text.Length);

            COPYDATASTRUCT cds;
            cds.dwData = new IntPtr(EVERYTHING_IPC_COPYDATAQUERYW);
            cds.cbData = size;
            cds.lpData = buf;
            IntPtr result;
            IntPtr ok = SendMessageTimeout(ev, WM_COPYDATA, replyTo, ref cds, 0x0002 /* SMTO_ABORTIFHUNG */, 1500, out result);
            return ok != IntPtr.Zero && result != IntPtr.Zero;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    // EVERYTHING_IPC_LISTW: 7 DWORDs, then items of { DWORD flags, filename_offset, path_offset } (offsets from the list start)
    static void ParseEverythingReply(int kind, IntPtr list, int size)
    {
        if (OnEverything == null || size < 28) return;
        int numItems = Marshal.ReadInt32(list, 20);
        if (numItems < 0 || 28 + numItems * 12 > size) return;
        string[] paths = new string[numItems];
        int[] flags = new int[numItems];
        for (int i = 0; i < numItems; i++)
        {
            int item = 28 + i * 12;
            flags[i] = Marshal.ReadInt32(list, item);
            string name = Marshal.PtrToStringUni(IntPtr.Add(list, Marshal.ReadInt32(list, item + 4)));
            string path = Marshal.PtrToStringUni(IntPtr.Add(list, Marshal.ReadInt32(list, item + 8)));
            paths[i] = string.IsNullOrEmpty(path) ? name : path.TrimEnd('\\') + "\\" + name;
        }
        OnEverything(kind, paths, flags);
    }

    static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x0312 && wParam.ToInt32() == 0xC0DE && OnHotkey != null)
        {
            handled = true;
            OnHotkey();
        }
        else if (msg == (int)WM_COPYDATA && lParam != IntPtr.Zero)
        {
            COPYDATASTRUCT cds = (COPYDATASTRUCT)Marshal.PtrToStructure(lParam, typeof(COPYDATASTRUCT));
            long id = cds.dwData.ToInt64();
            if (id >= REPLY_BASE && id < REPLY_BASE + 16)
            {
                handled = true;
                try { ParseEverythingReply((int)(id - REPLY_BASE), cds.lpData, cds.cbData); } catch { }
                return new IntPtr(1);
            }
        }
        return IntPtr.Zero;
    }
}

// Tiny local web service for the Coral Coast wallpaper (Lively).
// The wallpaper can't keep data between restarts or read Google Calendar itself (browser rules),
// so it asks the launcher instead, on http://127.0.0.1:<port>/  (this PC only, never the network).
//   GET  /state     -> the wallpaper's saved to-do state (JSON)
//   POST /state     -> save it
//   GET  /calendar  -> the iCal feeds listed in config.json, one after another (cached 5 minutes)
public static class CoralServer
{
    static System.Net.Sockets.TcpListener listener;
    static string stateFile;
    static string[] calendarUrls = new string[0];
    static string calendarCache;
    static DateTime calendarFetched = DateTime.MinValue;
    static readonly System.Collections.Generic.Dictionary<string, string> lastGood = new System.Collections.Generic.Dictionary<string, string>();
    static readonly object gate = new object();
    public static string LogFile;

    // set by the wallpaper's edit button (POST /edit on | off | reset); read by the launcher's timer
    public static volatile bool EditMode;
    public static volatile int EditVersion;      // bumps on every change so the launcher notices
    public static volatile bool ResetLauncher;

    // "Add a task" on the wallpaper: Lively can't reliably pass typing to a wallpaper, so the
    // wallpaper asks (POST /ask), the launcher shows a small text box, and the wallpaper
    // collects the result (GET /ask: 204 = still typing, 200 = the text, 410 = cancelled).
    // LibreHardwareMonitor's "Remote Web Server" (Options menu); the wallpaper reads temperatures through us
    public static int SensorsPort = 8085;
    static string sensorsCache;
    static DateTime sensorsFetched = DateTime.MinValue, sensorsFailed = DateTime.MinValue;
    static readonly object sensorsGate = new object();
    static string GetSensors()
    {
        lock (sensorsGate)
        {
            if (sensorsCache != null && (DateTime.UtcNow - sensorsFetched).TotalMilliseconds < 1500) return sensorsCache;
            if ((DateTime.UtcNow - sensorsFailed).TotalSeconds < 10) return null;       // don't hammer a closed port
            try
            {
                System.Net.HttpWebRequest rq = (System.Net.HttpWebRequest)System.Net.WebRequest.Create("http://127.0.0.1:" + SensorsPort + "/data.json");
                rq.Timeout = 1500; rq.ReadWriteTimeout = 1500; rq.Proxy = null;
                using (System.Net.WebResponse rs = rq.GetResponse())
                using (System.IO.StreamReader rd = new System.IO.StreamReader(rs.GetResponseStream(), Encoding.UTF8))
                    sensorsCache = rd.ReadToEnd();
                sensorsFetched = DateTime.UtcNow;
                return sensorsCache;
            }
            catch { sensorsFailed = DateTime.UtcNow; sensorsCache = null; return null; }
        }
    }
    public static volatile int ShowVersion;      // bumped by /show: bring the launcher forward
    public static volatile int AskVersion;
    static string askAnswer;     // null = nothing yet
    static bool askCancelled;
    public static volatile string AskTitle = "ADD A TASK", AskHint = "";
    // tiny reader for one string field of a flat JSON object
    static string JsonField(string json, string name)
    {
        System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(json ?? "", "\"" + name + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
        if (!m.Success) return null;
        try { return System.Text.RegularExpressions.Regex.Unescape(m.Groups[1].Value); } catch { return m.Groups[1].Value; }
    }
    public static void Answer(string text, bool cancelled)
    {
        lock (gate) { askAnswer = text ?? ""; askCancelled = cancelled; }
    }

    public static bool Start(int port, string dataFolder, string[] icsUrls)
    {
        stateFile = System.IO.Path.Combine(dataFolder, "wallpaper-state.json");
        SetCalendar(icsUrls);
        try
        {
            System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12;
            listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
            listener.Start();
            System.Threading.Thread t = new System.Threading.Thread(AcceptLoop);
            t.IsBackground = true;
            t.Start();
            return true;
        }
        catch (Exception ex) { Say("server: " + ex.Message); return false; }
    }

    public static void Stop() { try { if (listener != null) listener.Stop(); } catch { } }

    // called by Refresh after config.json changes
    // webcal:// links (what "Subscribe" buttons hand out) are the same feed over https
    public static void SetCalendar(string[] icsUrls)
    {
        System.Collections.Generic.List<string> list = new System.Collections.Generic.List<string>();
        if (icsUrls != null)
            foreach (string u in icsUrls)
            {
                if (string.IsNullOrWhiteSpace(u)) continue;
                string v = u.Trim();
                if (v.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase)) v = "https://" + v.Substring(9);
                list.Add(v);
            }
        lock (gate) { calendarUrls = list.ToArray(); calendarCache = null; calendarFetched = DateTime.MinValue; }
    }

    internal static void Say(string m)
    {
        if (string.IsNullOrEmpty(LogFile)) return;
        try { lock (gate) { System.IO.File.AppendAllText(LogFile, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + m + "\r\n", Encoding.UTF8); } } catch { }
    }

    static void AcceptLoop()
    {
        while (true)
        {
            System.Net.Sockets.TcpClient c;
            try { c = listener.AcceptTcpClient(); } catch { return; }
            System.Threading.ThreadPool.QueueUserWorkItem(Handle, c);
        }
    }

    static void Handle(object o)
    {
        System.Net.Sockets.TcpClient client = (System.Net.Sockets.TcpClient)o;
        try
        {
            client.ReceiveTimeout = 5000; client.SendTimeout = 5000;
            System.Net.Sockets.NetworkStream ns = client.GetStream();

            // read the request head (up to the blank line)
            System.IO.MemoryStream head = new System.IO.MemoryStream();
            int b;
            while (head.Length < 16384 && (b = ns.ReadByte()) != -1)
            {
                head.WriteByte((byte)b);
                long L = head.Length;
                if (L >= 4)
                {
                    byte[] hb = head.GetBuffer();
                    if (hb[L - 4] == 13 && hb[L - 3] == 10 && hb[L - 2] == 13 && hb[L - 1] == 10) break;
                }
            }
            string[] lines = Encoding.ASCII.GetString(head.ToArray()).Split(new string[] { "\r\n" }, StringSplitOptions.None);
            string[] first = lines[0].Split(' ');
            if (first.Length < 2) return;
            string method = first[0].ToUpperInvariant();
            string path = first[1], query = "";
            int q = path.IndexOf('?'); if (q >= 0) { query = path.Substring(q + 1); path = path.Substring(0, q); }
            int length = 0;
            foreach (string line in lines)
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    int.TryParse(line.Substring(15).Trim(), out length);

            byte[] body = new byte[0];
            if (length > 0 && length <= 262144)
            {
                body = new byte[length];
                int got = 0, n;
                while (got < length && (n = ns.Read(body, got, length - got)) > 0) got += n;
            }

            if (method == "OPTIONS") { Reply(ns, 204, "text/plain", ""); return; }

            if (path == "/state" && method == "GET")
            {
                string json = "{}";
                lock (gate) { if (System.IO.File.Exists(stateFile)) json = System.IO.File.ReadAllText(stateFile, Encoding.UTF8); }
                Reply(ns, 200, "application/json; charset=utf-8", json);
            }
            else if (path == "/state" && method == "POST")
            {
                string json = Encoding.UTF8.GetString(body).Trim();
                if (!json.StartsWith("{") || !json.EndsWith("}")) { Reply(ns, 400, "text/plain", "expected a JSON object"); return; }
                lock (gate)
                {
                    string tmp = stateFile + ".tmp";
                    System.IO.File.WriteAllText(tmp, json, new UTF8Encoding(false));
                    if (System.IO.File.Exists(stateFile)) System.IO.File.Delete(stateFile);
                    System.IO.File.Move(tmp, stateFile);
                }
                Reply(ns, 200, "application/json", "{\"ok\":true}");
            }
            else if (path == "/calendar" && method == "GET")
            {
                if (calendarUrls.Length == 0) { Reply(ns, 404, "text/plain", "no calendar set"); return; }
                Reply(ns, 200, "text/calendar; charset=utf-8", GetCalendar());
            }
            else if (path == "/edit" && method == "POST")
            {
                string what = Encoding.UTF8.GetString(body).Trim().ToLowerInvariant();
                if (what == "on") EditMode = true;
                else if (what == "off") EditMode = false;
                else if (what == "reset") ResetLauncher = true;
                EditVersion++;
                Reply(ns, 200, "application/json", "{\"ok\":true}");
            }
            else if (path == "/ask" && method == "POST")
            {
                // body: {"title":"...","hint":"..."} (older wallpapers just send "todo")
                string askBody = Encoding.UTF8.GetString(body);
                string t = JsonField(askBody, "title"), h = JsonField(askBody, "hint");
                lock (gate) { askAnswer = null; askCancelled = false; AskTitle = string.IsNullOrEmpty(t) ? "ADD A TASK" : t; AskHint = string.IsNullOrEmpty(h) ? "Enter to add  \u00b7  Esc to cancel" : h; }
                AskVersion++;
                Reply(ns, 200, "application/json", "{\"ok\":true}");
            }
            else if (path == "/ask" && method == "GET")
            {
                string a; bool c;
                lock (gate) { a = askAnswer; c = askCancelled; if (a != null) askAnswer = null; }
                if (a == null) Reply(ns, 204, "text/plain", "");
                else if (c) Reply(ns, 410, "text/plain", "");
                else Reply(ns, 200, "text/plain; charset=utf-8", a);
            }
            else if (path == "/sensors" && method == "GET")   // temperatures from LibreHardwareMonitor
            {
                string j = GetSensors();
                if (j == null) Reply(ns, 503, "text/plain", "LibreHardwareMonitor is not reachable on port " + SensorsPort);
                else Reply(ns, 200, "application/json; charset=utf-8", j);
            }
            else if (path == "/log" && method == "POST")      // problems the wallpaper reports (weather, calendar...)
            {
                string m = Encoding.UTF8.GetString(body).Replace("\r", " ").Replace("\n", " ").Trim();
                if (m.Length > 300) m = m.Substring(0, 300);
                if (m.Length > 0) Say("wallpaper: " + m);
                Reply(ns, 200, "application/json", "{\"ok\":true}");
            }
            else if (path == "/show")      // the pinned taskbar button (CoralLauncher.vbs show)
            {
                ShowVersion++;
                Reply(ns, 200, "application/json", "{\"ok\":true}");
            }
            else if (path == "/photos" && method == "GET")    // picture list for the photo frames
                Reply(ns, 200, "application/json; charset=utf-8", ListPhotos());
            else if ((path == "/photo" || path == "/thumb") && method == "GET")
            {
                string file = PhotoPath(QueryParam(query, "f"));
                if (file == null) { Reply(ns, 404, "text/plain", "not found"); return; }
                byte[] data = path == "/thumb" ? Thumb(file) : System.IO.File.ReadAllBytes(file);
                ReplyBytes(ns, path == "/thumb" ? "image/jpeg" : ImageType(file), data);
            }
            else if (path == "/ping") Reply(ns, 200, "text/plain", "coral");
            else Reply(ns, 404, "text/plain", "not found");
        }
        catch (Exception ex) { Say("server request: " + ex.Message); }
        finally { try { client.Close(); } catch { } }
    }

    // calendarUrl may hold several addresses separated by spaces or new lines (PowerShell turns a JSON list
    // into "a b c"). Each feed is downloaded and they are sent back to back; the wallpaper colours them apart.
    // downloads every feed and returns them back to back; the wallpaper colours events by feed order
    static string GetCalendar()
    {
        string[] urls;
        lock (gate)
        {
            if (calendarCache != null && (DateTime.UtcNow - calendarFetched).TotalMinutes < 5) return calendarCache;
            urls = calendarUrls;
        }
        StringBuilder all = new StringBuilder();
        int total = 0;
        for (int k = 0; k < urls.Length; k++)
        {
            string ics = null;
            try
            {
                System.Net.WebClient wc = new System.Net.WebClient();
                wc.Encoding = Encoding.UTF8;
                ics = wc.DownloadString(urls[k]);
                if (ics.IndexOf("BEGIN:VCALENDAR", StringComparison.Ordinal) < 0) throw new Exception("not an iCal feed");
                lock (gate) { lastGood[urls[k]] = ics; }
                int n = 0, i = 0;
                while ((i = ics.IndexOf("BEGIN:VEVENT", i, StringComparison.Ordinal)) >= 0) { n++; i += 12; }
                total += n;
            }
            catch (Exception ex)
            {
                Say("calendar " + (k + 1) + " download failed: " + ex.Message);
                lock (gate) { lastGood.TryGetValue(urls[k], out ics); }
            }
            // keep the slot even when a feed fails so the colours don't shift
            all.Append(ics ?? "BEGIN:VCALENDAR\r\nEND:VCALENDAR").Append("\r\n");
        }
        string merged = all.ToString();
        lock (gate) { calendarCache = merged; calendarFetched = DateTime.UtcNow; }
        Say("calendar downloaded: " + urls.Length + " feed(s), " + total + " events");
        return merged;
    }

    // ---------- photos for the wallpaper's picture frames ----------
    // Only image files inside the folders listed in config.json ("photoFolders") are ever served.
    public static string[] PhotoFolders = new string[0];
    static readonly string[] ImageExt = { ".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp" };
    static readonly System.Collections.Generic.Dictionary<string, byte[]> thumbs = new System.Collections.Generic.Dictionary<string, byte[]>();

    static string QueryParam(string query, string name)
    {
        foreach (string part in (query ?? "").Split('&'))
        {
            int i = part.IndexOf('=');
            if (i > 0 && part.Substring(0, i) == name) return Uri.UnescapeDataString(part.Substring(i + 1).Replace('+', ' '));
        }
        return null;
    }
    static string ImageType(string file)
    {
        string e = System.IO.Path.GetExtension(file).ToLowerInvariant();
        return e == ".png" ? "image/png" : e == ".webp" ? "image/webp" : e == ".gif" ? "image/gif" : e == ".bmp" ? "image/bmp" : "image/jpeg";
    }
    // "2|Trips/beach.jpg" -> full path inside photo folder #2, or null if it isn't a picture in that folder
    static string PhotoPath(string f)
    {
        if (string.IsNullOrEmpty(f)) return null;
        int bar = f.IndexOf('|'); int idx;
        if (bar < 1 || !int.TryParse(f.Substring(0, bar), out idx) || idx < 0 || idx >= PhotoFolders.Length) return null;
        try
        {
            string root = System.IO.Path.GetFullPath(PhotoFolders[idx]).TrimEnd('\\') + "\\";
            string full = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, f.Substring(bar + 1).Replace('/', '\\')));
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;
            if (Array.IndexOf(ImageExt, System.IO.Path.GetExtension(full).ToLowerInvariant()) < 0) return null;
            return System.IO.File.Exists(full) ? full : null;
        }
        catch { return null; }
    }
    static string JsonStr(string v)
    {
        StringBuilder b = new StringBuilder("\"");
        foreach (char c in v ?? "")
        {
            if (c == '"' || c == '\\') b.Append('\\').Append(c);
            else if (c < 32) b.Append("\\u").Append(((int)c).ToString("x4"));
            else b.Append(c);
        }
        return b.Append('"').ToString();
    }
    // newest first, up to 400 pictures, three folders deep
    static string ListPhotos()
    {
        System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<DateTime, string>> found = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<DateTime, string>>();
        for (int i = 0; i < PhotoFolders.Length; i++)
        {
            string root;
            try { root = System.IO.Path.GetFullPath(PhotoFolders[i]).TrimEnd('\\') + "\\"; } catch { continue; }
            if (!System.IO.Directory.Exists(root)) continue;
            Walk(root, root, i, 0, found);
        }
        found.Sort((a, b) => b.Key.CompareTo(a.Key));
        StringBuilder o = new StringBuilder("{\"folders\":[");
        for (int i = 0; i < PhotoFolders.Length; i++) { if (i > 0) o.Append(','); o.Append(JsonStr(PhotoFolders[i])); }
        o.Append("],\"photos\":[");
        for (int i = 0; i < found.Count && i < 400; i++) { if (i > 0) o.Append(','); o.Append(found[i].Value); }
        return o.Append("]}").ToString();
    }
    static void Walk(string root, string dir, int idx, int depth, System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<DateTime, string>> found)
    {
        if (found.Count > 2000) return;
        try
        {
            foreach (string file in System.IO.Directory.GetFiles(dir))
            {
                if (Array.IndexOf(ImageExt, System.IO.Path.GetExtension(file).ToLowerInvariant()) < 0) continue;
                string rel = file.Substring(root.Length).Replace('\\', '/');
                DateTime t = System.IO.File.GetLastWriteTimeUtc(file);
                found.Add(new System.Collections.Generic.KeyValuePair<DateTime, string>(t,
                    "{\"f\":" + JsonStr(idx + "|" + rel) + ",\"name\":" + JsonStr(System.IO.Path.GetFileName(file)) + "}"));
            }
            if (depth < 3) foreach (string sub in System.IO.Directory.GetDirectories(dir)) Walk(root, sub, idx, depth + 1, found);
        }
        catch { }
    }
    // small JPEG preview for the picture picker (cached)
    static byte[] Thumb(string file)
    {
        string key = file + "|" + System.IO.File.GetLastWriteTimeUtc(file).Ticks;
        lock (thumbs) { byte[] c; if (thumbs.TryGetValue(key, out c)) return c; }
        byte[] data;
        try
        {
            using (System.Drawing.Image img = System.Drawing.Image.FromFile(file))
            {
                double k = Math.Min(1.0, 240.0 / Math.Max(img.Width, img.Height));
                int w = Math.Max(1, (int)(img.Width * k)), h = Math.Max(1, (int)(img.Height * k));
                using (System.Drawing.Bitmap bmp = new System.Drawing.Bitmap(w, h))
                using (System.Drawing.Graphics g = System.Drawing.Graphics.FromImage(bmp))
                using (System.IO.MemoryStream ms = new System.IO.MemoryStream())
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.DrawImage(img, 0, 0, w, h);
                    bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Jpeg);
                    data = ms.ToArray();
                }
            }
        }
        catch { data = System.IO.File.ReadAllBytes(file); }      // formats GDI+ can't read (webp): send the original
        lock (thumbs) { if (thumbs.Count > 600) thumbs.Clear(); thumbs[key] = data; }
        return data;
    }
    static void ReplyBytes(System.IO.Stream s, string type, byte[] data)
    {
        string headers = "HTTP/1.1 200 OK\r\nContent-Type: " + type + "\r\nContent-Length: " + data.Length +
            "\r\nAccess-Control-Allow-Origin: *\r\nAccess-Control-Allow-Private-Network: true\r\nCache-Control: max-age=3600\r\nConnection: close\r\n\r\n";
        byte[] h = Encoding.ASCII.GetBytes(headers);
        s.Write(h, 0, h.Length);
        s.Write(data, 0, data.Length);
        s.Flush();
    }

    static void Reply(System.IO.Stream s, int code, string type, string text)
    {
        byte[] data = Encoding.UTF8.GetBytes(text);
        string status = code == 200 ? "OK" : code == 204 ? "No Content" : code == 400 ? "Bad Request" : code == 410 ? "Gone" : code == 503 ? "Service Unavailable" : "Not Found";
        string headers =
            "HTTP/1.1 " + code + " " + status + "\r\n" +
            "Content-Type: " + type + "\r\n" +
            "Content-Length: " + data.Length + "\r\n" +
            "Access-Control-Allow-Origin: *\r\n" +
            "Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n" +
            "Access-Control-Allow-Headers: Content-Type\r\n" +
            "Access-Control-Allow-Private-Network: true\r\n" +
            "Cache-Control: no-store\r\n" +
            "Connection: close\r\n\r\n";
        byte[] h = Encoding.ASCII.GetBytes(headers);
        s.Write(h, 0, h.Length);
        s.Write(data, 0, data.Length);
        s.Flush();
    }
}

// ---------------------------------------------------------------------------------------------
// Coral Coast keyboard lighting through the iCUE SDK (v4, iCUESDK.x64_2019.dll next to the launcher).
// iCUE must be running with its SDK option switched on. Colours are worked out from each LED's
// physical position, so the same themes fit any Corsair keyboard, mouse or fan.
// Struct layouts follow Corsair's own definitions (cuesdk 4.0.84):
//   CorsairDeviceInfo  { uint type; char deviceId[128]; char serial[128]; char model[128]; int ledCount; int channelCount; }  396 bytes
//   CorsairLedPosition { uint id; double cx; double cy; }                                                                   24 bytes
//   CorsairLedColor    { uint id; byte r, g, b, a; }                                                                         8 bytes
// ---------------------------------------------------------------------------------------------
public static class CoralRgb
{
    const string Dll = "iCUESDK.x64_2019.dll";
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void SessionHandler(IntPtr context, IntPtr eventData);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr LoadLibrary(string path);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int CorsairConnect(SessionHandler handler, IntPtr context);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int CorsairDisconnect();
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int CorsairGetDevices(ref int filterMask, int sizeMax, IntPtr devices, out int size);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int CorsairGetLedPositions([MarshalAs(UnmanagedType.LPStr)] string deviceId, int sizeMax, IntPtr positions, out int size);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int CorsairSetLedColors([MarshalAs(UnmanagedType.LPStr)] string deviceId, int size, IntPtr colors);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int CorsairRequestControl([MarshalAs(UnmanagedType.LPStr)] string deviceId, uint accessLevel);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int CorsairReleaseControl([MarshalAs(UnmanagedType.LPStr)] string deviceId);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int CorsairGetLedLuidForKeyName([MarshalAs(UnmanagedType.LPStr)] string deviceId, sbyte keyName, out uint ledId);

    const int DEVICE_INFO_SIZE = 396, LED_POS_SIZE = 24, LED_COLOR_SIZE = 8, MAX_DEVICES = 64, MAX_LEDS = 512;
    const int CSS_Connected = 6, CDT_Keyboard = 1, CAL_ExclusiveLightingControl = 1;

    class Device
    {
        public string Id, Model; public int Type;
        public uint[] Ids; public double[] U, V;        // LED ids and positions scaled to 0..1 (left->right, top->bottom)
        public bool[] Accent;                            // keys picked out by the Lagoon theme
        public IntPtr Buffer;                            // unmanaged CorsairLedColor[]
    }

    static SessionHandler handler;                       // kept alive for the SDK's callback
    static volatile int state;
    static bool loggedTimeout;
    static string lastDeviceList;
    static volatile bool needDevices;
    static volatile string theme = "off";
    static volatile int brightness = 100;
    static System.Threading.Thread loop;
    static System.Collections.Generic.List<Device> devices = new System.Collections.Generic.List<Device>();
    static bool controlling;
    public static string Status = "not started";

    static void Log(string m) { Status = m; CoralServer.Say("lighting: " + m); }

    // the theme names the launcher menu uses
    public static readonly string[] Themes = { "sunset", "ocean", "goldenhour", "lagoon", "off" };

    public static bool Start(string dllFolder, string startTheme, int startBrightness)
    {
        SetBrightness(startBrightness);
        theme = string.IsNullOrEmpty(startTheme) ? "sunset" : startTheme;
        string path = System.IO.Path.Combine(dllFolder, Dll);
        if (!System.IO.File.Exists(path)) { Log("iCUE SDK file not found (" + Dll + ") - keyboard themes are off"); return false; }
        if (LoadLibrary(path) == IntPtr.Zero) { Log("could not load " + Dll + " (error " + Marshal.GetLastWin32Error() + ")"); return false; }
        try
        {
            handler = new SessionHandler(OnSession);
            int err = CorsairConnect(handler, IntPtr.Zero);
            if (err != 0) { Log("CorsairConnect failed (error " + err + ")"); return false; }
        }
        catch (Exception ex) { Log("iCUE SDK: " + ex.Message); return false; }
        loop = new System.Threading.Thread(Run);
        loop.IsBackground = true;
        loop.Start();
        Log("waiting for iCUE (theme " + theme + ")");
        return true;
    }

    public static void SetTheme(string name)
    {
        if (Array.IndexOf(Themes, name) < 0) return;
        theme = name;
        CoralServer.Say("lighting: theme " + name);
    }
    public static string Theme { get { return theme; } }
    public static void SetBrightness(int pct) { brightness = Math.Max(5, Math.Min(100, pct)); }

    public static void Stop()
    {
        theme = "off";
        try { if (controlling) CorsairReleaseControl(null); } catch { }
        try { CorsairDisconnect(); } catch { }
    }

    static void OnSession(IntPtr context, IntPtr data)
    {
        int s = data == IntPtr.Zero ? 0 : Marshal.ReadInt32(data, 0);
        int was = state;
        state = s;
        if (s == CSS_Connected) needDevices = true;
        if (s == was || s == 2 || (s == 3 && was == 2 && loggedTimeout)) return;   // iCUE closed: the SDK retries quietly
        if (s == 3) loggedTimeout = true; else if (s == CSS_Connected) loggedTimeout = false;
        string[] names = { "invalid", "closed", "connecting", "timed out", "refused - turn on the SDK option in iCUE's settings", "lost", "connected" };
        CoralServer.Say("lighting: iCUE " + (s >= 0 && s < names.Length ? names[s] : s.ToString()));
    }

    static void LoadDevices()
    {
        foreach (Device d in devices) Marshal.FreeHGlobal(d.Buffer);
        devices.Clear();
        IntPtr buf = Marshal.AllocHGlobal(DEVICE_INFO_SIZE * MAX_DEVICES);
        IntPtr pos = Marshal.AllocHGlobal(LED_POS_SIZE * MAX_LEDS);
        try
        {
            int mask = -1, n;                                   // CDT_All
            int err = CorsairGetDevices(ref mask, MAX_DEVICES, buf, out n);
            if (err != 0) { Log("could not list devices (error " + err + ")"); return; }
            System.Text.StringBuilder names = new System.Text.StringBuilder();
            for (int i = 0; i < n; i++)
            {
                IntPtr p = new IntPtr(buf.ToInt64() + (long)i * DEVICE_INFO_SIZE);
                Device d = new Device();
                d.Type = Marshal.ReadInt32(p, 0);
                d.Id = ReadString(p, 4, 128);
                d.Model = ReadString(p, 4 + 256, 128);
                int count;
                if (CorsairGetLedPositions(d.Id, MAX_LEDS, pos, out count) != 0 || count <= 0) continue;
                d.Ids = new uint[count]; d.U = new double[count]; d.V = new double[count]; d.Accent = new bool[count];
                double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
                double[] xs = new double[count], ys = new double[count];
                for (int k = 0; k < count; k++)
                {
                    IntPtr q = new IntPtr(pos.ToInt64() + (long)k * LED_POS_SIZE);
                    d.Ids[k] = (uint)Marshal.ReadInt32(q, 0);
                    xs[k] = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(q, 8));
                    ys[k] = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(q, 16));
                    minX = Math.Min(minX, xs[k]); maxX = Math.Max(maxX, xs[k]); minY = Math.Min(minY, ys[k]); maxY = Math.Max(maxY, ys[k]);
                }
                for (int k = 0; k < count; k++)
                {
                    d.U[k] = maxX > minX ? (xs[k] - minX) / (maxX - minX) : 0.5;
                    d.V[k] = maxY > minY ? (ys[k] - minY) / (maxY - minY) : 0.5;
                }
                if (d.Type == CDT_Keyboard)                      // WASD and their neighbours for the Lagoon theme
                    foreach (char c in "WASDQERF")
                    {
                        uint luid;
                        if (CorsairGetLedLuidForKeyName(d.Id, (sbyte)c, out luid) == 0)
                            for (int k = 0; k < count; k++) if (d.Ids[k] == luid) d.Accent[k] = true;
                    }
                d.Buffer = Marshal.AllocHGlobal(LED_COLOR_SIZE * count);
                devices.Add(d);
                if (names.Length > 0) names.Append(", ");
                names.Append(d.Model + " (" + count + " LEDs)");
            }
            string msg = devices.Count == 0 ? "connected, but iCUE reported no RGB devices" : "connected: " + names;
            if (msg != lastDeviceList) Log(msg); else Status = msg;
            lastDeviceList = msg;
        }
        finally { Marshal.FreeHGlobal(buf); Marshal.FreeHGlobal(pos); }
    }

    static string ReadString(IntPtr p, int offset, int max)
    {
        byte[] b = new byte[max];
        Marshal.Copy(new IntPtr(p.ToInt64() + offset), b, 0, max);
        int len = Array.IndexOf(b, (byte)0); if (len < 0) len = max;
        return Encoding.UTF8.GetString(b, 0, len);
    }

    static void Run()
    {
        DateTime lastList = DateTime.MinValue;
        string painted = null;
        DateTime lastPaint = DateTime.MinValue;
        while (true)
        {
            try
            {
                string t = theme;
                if (state != CSS_Connected) { painted = null; System.Threading.Thread.Sleep(500); continue; }
                if (needDevices)
                {
                    needDevices = false; lastList = DateTime.Now;
                    if (controlling) { try { CorsairReleaseControl(null); } catch { } controlling = false; }
                    LoadDevices();
                    painted = null;
                }
                if (t == "off")
                {
                    if (controlling) { CorsairReleaseControl(null); controlling = false; CoralServer.Say("lighting: handed the LEDs back to iCUE"); }
                    painted = t;
                    System.Threading.Thread.Sleep(300);
                    continue;
                }
                if (!controlling)
                {
                    foreach (Device d in devices) CorsairRequestControl(d.Id, CAL_ExclusiveLightingControl);
                    controlling = true;
                }
                bool animated = t == "ocean" || t == "goldenhour";
                // static themes are re-sent every few seconds in case iCUE repainted; animated ones run at ~30 fps
                if (animated || painted != t || (DateTime.Now - lastPaint).TotalSeconds > 3)
                {
                    double time = (DateTime.Now - DateTime.Today).TotalSeconds;
                    foreach (Device d in devices) Paint(d, t, time);
                    painted = t; lastPaint = DateTime.Now;
                }
                System.Threading.Thread.Sleep(animated ? 33 : 250);
            }
            catch (Exception ex) { Log("render: " + ex.Message); System.Threading.Thread.Sleep(2000); }
        }
    }

    // ---------- colours ----------
    struct Rgb { public double R, G, B; public Rgb(double r, double g, double b) { R = r; G = g; B = b; } }
    static Rgb Mix(Rgb a, Rgb b, double t) { t = Math.Max(0, Math.Min(1, t)); return new Rgb(a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t); }
    static Rgb Ramp(double v, double[] at, Rgb[] cols)
    {
        if (v <= at[0]) return cols[0];
        for (int i = 1; i < at.Length; i++) if (v <= at[i]) return Mix(cols[i - 1], cols[i], (v - at[i - 1]) / (at[i] - at[i - 1]));
        return cols[cols.Length - 1];
    }

    // LED-tuned versions of the wallpaper palette (LEDs wash pale colours out, so these are punchier)
    static readonly Rgb Coral = new Rgb(255, 55, 45), Amber = new Rgb(255, 120, 25), Sand = new Rgb(255, 165, 60),
        Wine = new Rgb(170, 8, 40), Teal = new Rgb(0, 200, 140), DeepTeal = new Rgb(0, 95, 90),
        Foam = new Rgb(170, 255, 225), Night = new Rgb(0, 30, 70), Dawn = new Rgb(255, 120, 110);

    static readonly double[] SunsetAt = { 0, 0.32, 0.58, 0.74, 1 };
    static readonly Rgb[] SunsetCols = { Coral, Amber, Sand, Teal, DeepTeal };

    // time-of-day keyframes for Golden Hour: hour -> top, middle, bottom colour
    static readonly double[] DayHours = { 0, 5.5, 7.5, 12, 16.5, 18.5, 20.5, 24 };
    static readonly Rgb[][] DayCols = {
        new Rgb[] { Night, new Rgb(0, 60, 90), DeepTeal },         // midnight
        new Rgb[] { Night, new Rgb(0, 60, 90), DeepTeal },         // before dawn
        new Rgb[] { Dawn, Sand, Teal },                            // sunrise
        new Rgb[] { new Rgb(40, 200, 255), Sand, Teal },           // midday sky
        new Rgb[] { Amber, Sand, Teal },                           // afternoon
        new Rgb[] { Coral, Amber, DeepTeal },                      // sunset
        new Rgb[] { Wine, new Rgb(120, 20, 90), DeepTeal },        // dusk
        new Rgb[] { Night, new Rgb(0, 60, 90), DeepTeal } };       // midnight again

    static Rgb Colour(Device d, int k, string t, double time)
    {
        double u = d.U[k], v = d.V[k];
        switch (t)
        {
            case "sunset":                                           // the wallpaper, top to bottom
                return Ramp(v, SunsetAt, SunsetCols);
            case "ocean":                                            // slow waves rolling left to right, a strip of beach
            {
                double w = 0.5 + 0.5 * Math.Sin(2 * Math.PI * (u * 1.3 - time * 0.11) + v * 2.2);
                double w2 = 0.5 + 0.5 * Math.Sin(2 * Math.PI * (u * 0.7 + time * 0.05) - v * 1.4);
                double crest = Math.Pow(w * 0.7 + w2 * 0.3, 1.5);
                Rgb sea = Mix(DeepTeal, Teal, crest);
                if (crest > 0.82) sea = Mix(sea, Foam, (crest - 0.82) / 0.18);
                return v > 0.86 ? Mix(sea, Sand, 0.75) : sea;
            }
            case "goldenhour":                                       // follows the real time of day
            {
                double h = time / 3600.0;
                int i = 1; while (i < DayHours.Length - 1 && h > DayHours[i]) i++;
                double f = (h - DayHours[i - 1]) / (DayHours[i] - DayHours[i - 1]);
                Rgb top = Mix(DayCols[i - 1][0], DayCols[i][0], f), mid = Mix(DayCols[i - 1][1], DayCols[i][1], f), bot = Mix(DayCols[i - 1][2], DayCols[i][2], f);
                Rgb c = v < 0.55 ? Mix(top, mid, v / 0.55) : Mix(mid, bot, (v - 0.55) / 0.45);
                double shimmer = 0.92 + 0.08 * Math.Sin(2 * Math.PI * (u * 0.8 - time * 0.04));
                return new Rgb(c.R * shimmer, c.G * shimmer, c.B * shimmer);
            }
            case "lagoon":                                           // calm teal with coral gaming keys
                if (d.Accent[k]) return Coral;
                return Mix(Teal, DeepTeal, v * 0.9);
        }
        return new Rgb(0, 0, 0);
    }

    static void Paint(Device d, string t, double time)
    {
        double scale = brightness / 100.0;
        for (int k = 0; k < d.Ids.Length; k++)
        {
            Rgb c = Colour(d, k, t, time);
            IntPtr q = new IntPtr(d.Buffer.ToInt64() + (long)k * LED_COLOR_SIZE);
            Marshal.WriteInt32(q, 0, (int)d.Ids[k]);
            Marshal.WriteByte(q, 4, (byte)Math.Max(0, Math.Min(255, c.R * scale)));
            Marshal.WriteByte(q, 5, (byte)Math.Max(0, Math.Min(255, c.G * scale)));
            Marshal.WriteByte(q, 6, (byte)Math.Max(0, Math.Min(255, c.B * scale)));
            Marshal.WriteByte(q, 7, 255);
        }
        CorsairSetLedColors(d.Id, d.Ids.Length, d.Buffer);
    }
}
