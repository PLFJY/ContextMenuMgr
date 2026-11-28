using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using ContextMenuMgr.Contracts;

namespace ContextMenuMgr.Backend.Services;

internal static class WinXShortcutFile
{
    private const int RunAsAdministratorFlagOffset = 0x15;
    private const byte RunAsAdministratorFlag = 0x20;
    private const uint ClsctxInprocServer = 0x1;
    private const uint CoinitApartmentThreaded = 0x2;
    private const int RpcEChangedMode = unchecked((int)0x80010106);
    private const int StgmReadWrite = 0x2;
    private const int MaxPathBuffer = 32768;
    private static readonly Guid ShellLinkClsid = new("00021401-0000-0000-C000-000000000046");
    private static readonly Guid ShellLinkIid = new("000214F9-0000-0000-C000-000000000046");

    public static ShortcutInfo Read(string path) =>
        RunInSta(() =>
        {
            var (shellLink, persistFile) = CreateShellLink();
            try
            {
                persistFile.Load(path, StgmReadWrite);
                var targetPath = new StringBuilder(MaxPathBuffer);
                shellLink.GetPath(targetPath, targetPath.Capacity, out _, 0);
                var arguments = GetString(shellLink.GetArguments);
                var workingDirectory = GetString(shellLink.GetWorkingDirectory);
                var description = GetString(shellLink.GetDescription);
                var iconPath = new StringBuilder(MaxPathBuffer);
                shellLink.GetIconLocation(iconPath, iconPath.Capacity, out var iconIndex);
                var iconLocation = iconPath.Length == 0 ? string.Empty : $"{iconPath},{iconIndex}";

                return new ShortcutInfo(
                    targetPath.ToString(),
                    arguments,
                    workingDirectory,
                    description,
                    iconLocation,
                    IsRunAsAdministrator(path));
            }
            finally
            {
                Marshal.FinalReleaseComObject(shellLink);
            }
        });

    public static void Write(
        string path,
        string targetPath,
        string? arguments,
        string? workingDirectory,
        string? description,
        string? iconLocation,
        bool? runAsAdministrator)
    {
        RunInSta(() =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var (shellLink, persistFile) = CreateShellLink();
            try
            {
                shellLink.SetPath(targetPath);
                shellLink.SetArguments(arguments ?? string.Empty);
                shellLink.SetWorkingDirectory(string.IsNullOrWhiteSpace(workingDirectory)
                    ? Path.GetDirectoryName(targetPath) ?? string.Empty
                    : workingDirectory);
                shellLink.SetDescription(description ?? string.Empty);
                SetIconLocation(shellLink, iconLocation);
                persistFile.Save(path, true);
            }
            finally
            {
                Marshal.FinalReleaseComObject(shellLink);
            }

            if (runAsAdministrator is not null)
            {
                SetRunAsAdministrator(path, runAsAdministrator.Value);
            }
        });
    }

    public static void Update(
        string path,
        string? targetPath = null,
        string? arguments = null,
        string? workingDirectory = null,
        string? description = null,
        string? iconLocation = null,
        bool? runAsAdministrator = null)
    {
        RunInSta(() =>
        {
            var (shellLink, persistFile) = CreateShellLink();
            try
            {
                persistFile.Load(path, StgmReadWrite);
                if (targetPath is not null) shellLink.SetPath(targetPath);
                if (arguments is not null) shellLink.SetArguments(arguments);
                if (workingDirectory is not null) shellLink.SetWorkingDirectory(workingDirectory);
                if (description is not null) shellLink.SetDescription(description);
                if (iconLocation is not null) SetIconLocation(shellLink, iconLocation);
                persistFile.Save(path, true);
            }
            finally
            {
                Marshal.FinalReleaseComObject(shellLink);
            }

            if (runAsAdministrator is not null)
            {
                SetRunAsAdministrator(path, runAsAdministrator.Value);
            }
        });
    }

    private static (IShellLinkW ShellLink, IPersistFile PersistFile) CreateShellLink()
    {
        var clsid = ShellLinkClsid;
        var iid = ShellLinkIid;
        var hr = CoCreateInstance(ref clsid, IntPtr.Zero, ClsctxInprocServer, ref iid, out var shellLinkPointer);
        Marshal.ThrowExceptionForHR(hr);
        try
        {
            var shellLink = (IShellLinkW)Marshal.GetObjectForIUnknown(shellLinkPointer);
            return (shellLink, (IPersistFile)shellLink);
        }
        finally
        {
            Marshal.Release(shellLinkPointer);
        }
    }

    private static string GetString(ShellLinkStringGetter getter)
    {
        var value = new StringBuilder(MaxPathBuffer);
        getter(value, value.Capacity);
        return value.ToString();
    }

    private static void SetIconLocation(IShellLinkW shellLink, string? iconLocation)
    {
        if (string.IsNullOrWhiteSpace(iconLocation))
        {
            shellLink.SetIconLocation(string.Empty, 0);
            return;
        }

        var separator = iconLocation.LastIndexOf(',');
        if (separator > 0 && int.TryParse(iconLocation[(separator + 1)..].Trim(), out var iconIndex))
        {
            shellLink.SetIconLocation(iconLocation[..separator].Trim(), iconIndex);
            return;
        }

        shellLink.SetIconLocation(iconLocation, 0);
    }

    private static bool IsRunAsAdministrator(string path)
    {
        var bytes = File.ReadAllBytes(path);
        return bytes.Length > RunAsAdministratorFlagOffset
               && (bytes[RunAsAdministratorFlagOffset] & RunAsAdministratorFlag) == RunAsAdministratorFlag;
    }

    private static void SetRunAsAdministrator(string path, bool enabled)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length <= RunAsAdministratorFlagOffset)
        {
            return;
        }

        if (enabled)
        {
            bytes[RunAsAdministratorFlagOffset] |= RunAsAdministratorFlag;
        }
        else
        {
            bytes[RunAsAdministratorFlagOffset] &= unchecked((byte)~RunAsAdministratorFlag);
        }

        File.WriteAllBytes(path, bytes);
    }

    private static void RunInSta(Action action) => RunInSta<object?>(() =>
    {
        action();
        return null;
    });

    private static T RunInSta<T>(Func<T> action)
    {
        T? result = default;
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            var shouldUninitialize = false;
            try
            {
                var hr = CoInitializeEx(IntPtr.Zero, CoinitApartmentThreaded);
                shouldUninitialize = hr >= 0;
                if (hr == RpcEChangedMode)
                {
                    WinXHasher.FileLoggerHost.Log(
                        RuntimeLogLevel.Warning,
                        $"WinXShortcutComInitializeChangedMode: HResult=0x{hr:X8}, Action=Continue.");
                }
                else
                {
                    Marshal.ThrowExceptionForHR(hr);
                }

                result = action();
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                if (shouldUninitialize)
                {
                    CoUninitialize();
                }
            }
        })
        {
            IsBackground = true,
            Name = "ContextMenuMgr WinX ShellLink"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
        return result!;
    }

    private delegate void ShellLinkStringGetter(StringBuilder value, int capacity);

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(
        ref Guid rclsid,
        IntPtr pUnkOuter,
        uint dwClsContext,
        ref Guid riid,
        out IntPtr ppv);

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, out Win32FindDataW pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Win32FindDataW
    {
        public uint FileAttributes;
        public FILETIME CreationTime;
        public FILETIME LastAccessTime;
        public FILETIME LastWriteTime;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint Reserved0;
        public uint Reserved1;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string FileName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
        public string AlternateFileName;
    }
}
