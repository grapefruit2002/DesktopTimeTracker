using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace DesktopTimeTracker.Services;

/// <summary>
/// 注册应用的 AUMID（AppUserModelID）并在开始菜单创建带该 AUMID 的快捷方式。
/// Windows 通知中心按 AUMID 取"应用图标"，取的是开始菜单快捷方式的图标。
/// 这样收起窗口时的通知左上角图标始终跟随 EXE 内嵌图标，不受旧缓存影响。
/// </summary>
internal static class AppIdentity
{
    public const string Id = "DesktopTimeTracker.App";

    [DllImport("shellapi.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appID);

    [DllImport("propsys.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetPropertyStoreFromParsingName(
        string pszPath, IntPtr pbc, uint mode, ref Guid riid, out IPropertyStore ppv);

    private static readonly Guid PropertyStoreIid = new("886D8EEB-8CF2-444E-8EDE-EFD62A09DC65");

    /// <summary>在进程启动时调用：注册 AUMID 并确保开始菜单快捷方式存在。</summary>
    public static void Initialize()
    {
        try { SetCurrentProcessExplicitAppUserModelID(Id); } catch { /* 非致命 */ }
        EnsureStartMenuShortcut();
    }

    private static void EnsureStartMenuShortcut()
    {
        try
        {
            string? exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath)) return;

            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Microsoft", "Windows", "Start Menu", "Programs");
            Directory.CreateDirectory(dir);
            string lnkPath = Path.Combine(dir, "桌面时长记录器.lnk");

            // 用 WScript.Shell 创建/刷新快捷方式，图标指向 EXE 本身（跟随内嵌图标）
            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return;
            dynamic? shell = Activator.CreateInstance(shellType);
            if (shell is null) return;
            dynamic lnk = shell.CreateShortcut(lnkPath);
            lnk.TargetPath = exePath;
            lnk.WorkingDirectory = Path.GetDirectoryName(exePath)!;
            lnk.IconLocation = exePath + ",0";
            lnk.Description = "桌面时长记录器";
            lnk.Save();
            Marshal.FinalReleaseComObject(shell);

            // 给 .lnk 写入 System.AppUserModel.ID 属性，与进程注册的 AUMID 对应
            Guid iid = PropertyStoreIid;
            int hr = SHGetPropertyStoreFromParsingName(lnkPath, IntPtr.Zero, 0, ref iid, out IPropertyStore store);
            if (hr != 0 || store is null) return;

            try
            {
                PropertyKey key = new(new Guid("9F4C2855-9F79-4B39-A8D0-EF034CD29A9C"), 5); // System.AppUserModel.ID
                PropVariant pv = PropVariant.FromString(Id);
                try { store.SetValue(ref key, ref pv); }
                finally { pv.Clear(); }
                store.Commit();
            }
            finally
            {
                Marshal.ReleaseComObject(store);
            }
        }
        catch
        {
            // 快捷方式创建失败不影响主功能
        }
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct PropertyKey
{
    public Guid FormatId;
    public uint PropertyId;

    public PropertyKey(Guid formatId, uint propertyId)
    {
        FormatId = formatId;
        PropertyId = propertyId;
    }
}

[ComImport]
[Guid("886D8EEB-8CF2-444E-8EDE-EFD62A09DC65")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPropertyStore
{
    void GetCount(out uint propertyCount);
    void GetAt(uint propertyIndex, out PropertyKey key);
    void GetValue(ref PropertyKey key, out PropVariant value);
    void SetValue(ref PropertyKey key, ref PropVariant value);
    void Commit();
}

[StructLayout(LayoutKind.Sequential)]
internal struct PropVariant
{
    private ushort _vt;
    private ushort _wReserved1;
    private ushort _wReserved2;
    private ushort _wReserved3;
    private IntPtr _pointer;
    private int _extra;

    public static PropVariant FromString(string value)
    {
        PropVariant pv = default;
        pv._vt = 31; // VT_LPWSTR
        pv._pointer = Marshal.StringToCoTaskMemUni(value);
        return pv;
    }

    public void Clear()
    {
        if (_pointer != IntPtr.Zero)
        {
            Marshal.FreeCoTaskMem(_pointer);
            _pointer = IntPtr.Zero;
        }
    }
}
