// VIctory Launcher - native Windows helpers (P/Invoke, no external packages).
// Copyright (C) 2026 God489. GPL-3.0-or-later.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace VictoryLauncher
{
    /// What the launcher reads about the machine before a run.
    public sealed class GpuInfo
    {
        public string Name = "";
        public uint VendorId;
        public ulong DedicatedMemory;
        public bool Software;
        public bool D3D12;
        public uint WaveMin;
        public uint WaveMax;
        public int ShaderModel;      // 0x66 = 6.6
        public int MeshShaderTier;   // 0 = none, 10 = tier 1
        public string Error = "";

        public string Vendor
        {
            get
            {
                switch (VendorId)
                {
                    case 0x10DE: return "NVIDIA";
                    case 0x1002: return "AMD";
                    case 0x8086: return "Intel";
                    case 0x1414: return "Microsoft";
                    default: return "Other";
                }
            }
        }
    }

    public static class Native
    {
        // ---------- CPU, memory, OS ----------

        [DllImport("kernel32.dll")]
        static extern bool IsProcessorFeaturePresent(uint feature);

        public static bool HasAvx2()
        {
            const uint PF_AVX2_INSTRUCTIONS_AVAILABLE = 40;
            try { return IsProcessorFeaturePresent(PF_AVX2_INSTRUCTIONS_AVAILABLE); }
            catch { return false; }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MEMORYSTATUSEX
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
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);

        public static MEMORYSTATUSEX Memory()
        {
            var m = new MEMORYSTATUSEX();
            m.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            GlobalMemoryStatusEx(ref m);
            return m;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool SetDllDirectory(string path);

        // ---------- GPU: the adapter VIctory itself picks ----------
        // victory-gpu creates its device on
        // IDXGIFactory6::EnumAdapterByGpuPreference(0, HIGH_PERFORMANCE); the
        // launcher asks for the same adapter, then reads its D3D12 caps.

        [DllImport("dxgi.dll")]
        static extern int CreateDXGIFactory2(uint flags, ref Guid riid, out IntPtr factory);

        [DllImport("d3d12.dll")]
        static extern int D3D12CreateDevice(IntPtr adapter, int minimumFeatureLevel, ref Guid riid, out IntPtr device);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate uint ReleaseFn(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int EnumByPreferenceFn(IntPtr self, uint index, int preference, ref Guid riid, out IntPtr adapter);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int EnumAdapters1Fn(IntPtr self, uint index, out IntPtr adapter);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int GetDesc1Fn(IntPtr self, IntPtr desc);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int CheckFeatureSupportFn(IntPtr self, int feature, IntPtr data, uint size);

        static T Slot<T>(IntPtr com, int index) where T : class
        {
            IntPtr vtable = Marshal.ReadIntPtr(com);
            IntPtr fn = Marshal.ReadIntPtr(vtable, index * IntPtr.Size);
            return Marshal.GetDelegateForFunctionPointer(fn, typeof(T)) as T;
        }

        static void Release(IntPtr com)
        {
            if (com != IntPtr.Zero) Slot<ReleaseFn>(com, 2)(com);
        }

        public static GpuInfo Gpu()
        {
            var g = new GpuInfo();
            IntPtr factory = IntPtr.Zero, adapter = IntPtr.Zero, device = IntPtr.Zero;
            try
            {
                Guid iidFactory6 = new Guid("c1b6694f-ff09-44a9-b03c-77900a0a1d17");
                Guid iidFactory1 = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
                Guid iidAdapter1 = new Guid("29038f61-3839-4626-91fd-086879011a05");
                bool six = CreateDXGIFactory2(0, ref iidFactory6, out factory) == 0;
                if (six)
                {
                    // IDXGIFactory6::EnumAdapterByGpuPreference is slot 29.
                    const int HIGH_PERFORMANCE = 2;
                    if (Slot<EnumByPreferenceFn>(factory, 29)(factory, 0, HIGH_PERFORMANCE, ref iidAdapter1, out adapter) != 0)
                        adapter = IntPtr.Zero;
                }
                else if (CreateDXGIFactory2(0, ref iidFactory1, out factory) == 0)
                {
                    // IDXGIFactory1::EnumAdapters1 is slot 12.
                    if (Slot<EnumAdapters1Fn>(factory, 12)(factory, 0, out adapter) != 0)
                        adapter = IntPtr.Zero;
                }
                if (adapter == IntPtr.Zero)
                {
                    g.Error = "No graphics adapter was found.";
                    return g;
                }
                // DXGI_ADAPTER_DESC1, 312 bytes on x64; GetDesc1 is slot 10.
                IntPtr desc = Marshal.AllocHGlobal(512);
                try
                {
                    if (Slot<GetDesc1Fn>(adapter, 10)(adapter, desc) == 0)
                    {
                        g.Name = Marshal.PtrToStringUni(desc).Trim();
                        g.VendorId = (uint)Marshal.ReadInt32(desc, 256);
                        g.DedicatedMemory = (ulong)Marshal.ReadInt64(desc, 272);
                        uint flags = (uint)Marshal.ReadInt32(desc, 304);
                        g.Software = (flags & 2) != 0;
                    }
                }
                finally { Marshal.FreeHGlobal(desc); }

                Guid iidDevice = new Guid("189819f1-1db6-4b57-be54-1821339b85f7");
                const int FEATURE_LEVEL_12_0 = 0xc000;
                if (D3D12CreateDevice(adapter, FEATURE_LEVEL_12_0, ref iidDevice, out device) != 0 || device == IntPtr.Zero)
                {
                    g.Error = "This GPU has no Direct3D 12 (feature level 12_0) device.";
                    return g;
                }
                g.D3D12 = true;
                var check = Slot<CheckFeatureSupportFn>(device, 13);
                IntPtr data = Marshal.AllocHGlobal(64);
                try
                {
                    // D3D12_FEATURE_D3D12_OPTIONS1 (8): WaveOps, WaveLaneCountMin, WaveLaneCountMax, ...
                    for (int i = 0; i < 64; i += 4) Marshal.WriteInt32(data, i, 0);
                    if (check(device, 8, data, 24) == 0)
                    {
                        g.WaveMin = (uint)Marshal.ReadInt32(data, 4);
                        g.WaveMax = (uint)Marshal.ReadInt32(data, 8);
                    }
                    // D3D12_FEATURE_SHADER_MODEL (7): ask for the highest the runtime knows.
                    foreach (int sm in new[] { 0x69, 0x68, 0x67, 0x66, 0x65, 0x64, 0x63, 0x62, 0x61, 0x60 })
                    {
                        Marshal.WriteInt32(data, 0, sm);
                        if (check(device, 7, data, 4) == 0)
                        {
                            g.ShaderModel = Marshal.ReadInt32(data, 0);
                            break;
                        }
                    }
                    // D3D12_FEATURE_D3D12_OPTIONS7 (32): MeshShaderTier, SamplerFeedbackTier.
                    for (int i = 0; i < 8; i += 4) Marshal.WriteInt32(data, i, 0);
                    if (check(device, 32, data, 8) == 0)
                        g.MeshShaderTier = Marshal.ReadInt32(data, 0);
                }
                finally { Marshal.FreeHGlobal(data); }
            }
            catch (Exception e)
            {
                g.Error = e.Message;
            }
            finally
            {
                Release(device);
                Release(adapter);
                Release(factory);
            }
            return g;
        }

        // ---------- A job object holding the game's processes ----------
        // The title can relaunch VIctory (a new process that waits for the
        // old one), so "running" means: some process of the job is alive.

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr CreateJobObject(IntPtr attributes, string name);
        [DllImport("kernel32.dll")]
        static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll")]
        static extern bool TerminateJobObject(IntPtr job, uint exitCode);
        [DllImport("kernel32.dll")]
        static extern bool QueryInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length, out uint returned);
        [DllImport("kernel32.dll")]
        public static extern bool CloseHandle(IntPtr handle);

        public static IntPtr NewJob() { return CreateJobObject(IntPtr.Zero, null); }
        public static bool Assign(IntPtr job, IntPtr process) { return AssignProcessToJobObject(job, process); }
        public static void Terminate(IntPtr job) { TerminateJobObject(job, 1); }

        /// The ids of the job's live processes.
        public static List<int> JobProcesses(IntPtr job)
        {
            var ids = new List<int>();
            const int JobObjectBasicProcessIdList = 3;
            int size = 8 + 256 * IntPtr.Size;
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.WriteInt32(buf, 0, 256);
                Marshal.WriteInt32(buf, 4, 0);
                uint got;
                if (QueryInformationJobObject(job, JobObjectBasicProcessIdList, buf, (uint)size, out got))
                {
                    int n = Marshal.ReadInt32(buf, 4);
                    for (int i = 0; i < n; i++)
                        ids.Add((int)Marshal.ReadIntPtr(buf, 8 + i * IntPtr.Size).ToInt64());
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
            return ids;
        }

        // ---------- Window helpers ----------

        delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lparam);
        [DllImport("user32.dll")]
        static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lparam);
        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll")]
        static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hwnd, int cmd);
        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
        [DllImport("user32.dll")]
        public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
        [DllImport("user32.dll")]
        public static extern bool IsIconic(IntPtr hwnd);

        /// The caption and handle of the first visible titled window of any of `pids`.
        public static string WindowOf(List<int> pids, out IntPtr found)
        {
            string caption = null;
            IntPtr hit = IntPtr.Zero;
            var set = new HashSet<int>(pids);
            EnumWindows(delegate (IntPtr h, IntPtr l)
            {
                uint pid;
                GetWindowThreadProcessId(h, out pid);
                if (!set.Contains((int)pid) || !IsWindowVisible(h)) return true;
                var sb = new StringBuilder(512);
                if (GetWindowText(h, sb, sb.Capacity) > 0)
                {
                    caption = sb.ToString();
                    hit = h;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            found = hit;
            return caption;
        }

        [DllImport("user32.dll")]
        public static extern bool ReleaseCapture();
        [DllImport("user32.dll")]
        public static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr w, IntPtr l);

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        /// Windows 11: rounded corners and a dark frame; ignored elsewhere.
        public static void StyleWindow(IntPtr hwnd)
        {
            try
            {
                int dark = 1;
                DwmSetWindowAttribute(hwnd, 20, ref dark, 4);      // DWMWA_USE_IMMERSIVE_DARK_MODE
                int round = 2;
                DwmSetWindowAttribute(hwnd, 33, ref round, 4);     // DWMWA_WINDOW_CORNER_PREFERENCE = ROUND
                int border = 0x00241C1A;                            // COLORREF 0x00BBGGRR
                DwmSetWindowAttribute(hwnd, 34, ref border, 4);    // DWMWA_BORDER_COLOR
            }
            catch { }
        }

        // ---------- The modern folder picker (IFileOpenDialog) ----------

        [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
        class FileOpenDialogCoClass { }

        [ComImport, Guid("42f85136-db7e-439c-85f1-e4075d135fc8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IFileOpenDialog
        {
            [PreserveSig] int Show(IntPtr parent);
            void SetFileTypes(uint count, IntPtr specs);
            void SetFileTypeIndex(uint index);
            void GetFileTypeIndex(out uint index);
            void Advise(IntPtr events, out uint cookie);
            void Unadvise(uint cookie);
            void SetOptions(uint options);
            void GetOptions(out uint options);
            void SetDefaultFolder(IShellItem item);
            void SetFolder(IShellItem item);
            void GetFolder(out IShellItem item);
            void GetCurrentSelection(out IShellItem item);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
            void GetResult(out IShellItem item);
            void AddPlace(IShellItem item, int place);
            void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string ext);
            void Close(int hr);
            void SetClientGuid(ref Guid guid);
            void ClearClientData();
            void SetFilter(IntPtr filter);
            void GetResults(out IntPtr items);
            void GetSelectedItems(out IntPtr items);
        }

        [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItem
        {
            void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
            void GetParent(out IShellItem item);
            void GetDisplayName(uint sigdn, [MarshalAs(UnmanagedType.LPWStr)] out string name);
            void GetAttributes(uint mask, out uint attributes);
            void Compare(IShellItem other, uint hint, out int order);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        static extern void SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid, out IShellItem item);

        /// The folder the user picks, or null.
        public static string PickFolder(IntPtr owner, string title, string start)
        {
            try
            {
                var dialog = (IFileOpenDialog)new FileOpenDialogCoClass();
                const uint FOS_PICKFOLDERS = 0x20, FOS_FORCEFILESYSTEM = 0x40, FOS_PATHMUSTEXIST = 0x800;
                dialog.SetOptions(FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM | FOS_PATHMUSTEXIST);
                dialog.SetTitle(title);
                if (!string.IsNullOrEmpty(start) && System.IO.Directory.Exists(start))
                {
                    Guid iidItem = typeof(IShellItem).GUID;
                    IShellItem folder;
                    SHCreateItemFromParsingName(start, IntPtr.Zero, ref iidItem, out folder);
                    if (folder != null) dialog.SetFolder(folder);
                }
                if (dialog.Show(owner) != 0) return null;
                IShellItem result;
                dialog.GetResult(out result);
                string path;
                const uint SIGDN_FILESYSPATH = 0x80058000;
                result.GetDisplayName(SIGDN_FILESYSPATH, out path);
                return path;
            }
            catch
            {
                return null;
            }
        }
    }
}
