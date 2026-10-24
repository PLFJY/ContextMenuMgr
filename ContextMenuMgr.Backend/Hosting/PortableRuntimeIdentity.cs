using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ContextMenuMgr.Backend.Hosting;

// Pin each ancestor against rename and open the final object without following
// a reparse point. Keep these handles until the privileged operation completes.
internal sealed class PortableRuntimeIdentity : IDisposable
{
    private readonly List<SafeFileHandle> _handles = [];

    public void PinDirectory(string path)
    {
        var fullPath = Path.GetFullPath(path).TrimEnd('\\');
        if (fullPath.StartsWith(@"\\", StringComparison.Ordinal)) throw new IOException("Network runtime directory.");
        var directories = new Stack<string>();
        for (string? directory = fullPath; directory is not null; directory = Path.GetDirectoryName(directory))
            directories.Push(directory);
        while (directories.TryPop(out var directory)) PinObject(directory, directory: true);
    }

    public SafeFileHandle Pin(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (fullPath.StartsWith(@"\\", StringComparison.Ordinal))
            throw new IOException("Network paths cannot establish portable service ownership.");
        var directories = new Stack<string>();
        for (var directory = Path.GetDirectoryName(fullPath); directory is not null; directory = Path.GetDirectoryName(directory))
            directories.Push(directory);
        while (directories.TryPop(out var directory)) PinObject(directory, directory: true);
        return PinObject(fullPath, directory: false);
    }

    private SafeFileHandle PinObject(string path, bool directory)
    {
        var handle = CreateFileW(path, 0x80u,
            directory ? 3u : 1u, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error, "Pin runtime identity");
        }
        _handles.Add(handle);
        if (!GetFileInformationByHandle(handle, out var info))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "GetFileInformationByHandle");
        if ((info.Attributes & 0x400) != 0 || ((info.Attributes & 0x10) != 0) != directory)
            throw new IOException("Reparse point or unexpected runtime object type.");
        var finalPath = new StringBuilder(32768);
        var length = GetFinalPathNameByHandleW(handle, finalPath, finalPath.Capacity, 0);
        if (length == 0 || length >= finalPath.Capacity
            || !string.Equals(finalPath.ToString(), @"\\?\" + path, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Runtime path identity changed or is ambiguous.");
        return handle;
    }

    public void Dispose() { foreach (var handle in _handles.AsEnumerable().Reverse()) handle.Dispose(); }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation information);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder path, int length, uint flags);
}
