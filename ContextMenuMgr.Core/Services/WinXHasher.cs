using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security;
using System.Text;
using System.Threading;
using ComTypes = System.Runtime.InteropServices.ComTypes;
using ContextMenuMgr.Contracts;

namespace ContextMenuMgr.Backend.Services;

internal sealed record WinXHashResult(
    bool Success,
    uint? Hash,
    string? TargetPath,
    string? Arguments,
    string? GeneralizedTargetPath,
    string? Error,
    bool? VerificationSucceeded = null,
    uint? ReadBackHash = null,
    string? VerificationWarning = null);

internal static class WinXHasher
{
    private static readonly Dictionary<string, string> GeneralizePathMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["%ProgramFiles%"] = "{905e63b6-c1bf-494e-b29c-65b732d3d21a}",
        [@"C:\Windows\System32"] = "{1ac14e77-02e7-4e5d-b744-2eb1ae5198b7}",
        [@"C:\Windows"] = "{f38bf404-1d43-42f2-9305-67de0b28fc23}"
    };

    // Hard-coded PropertyKeys. PSGetPropertyKeyFromName is unreliable on some
    // Windows 10 systems for System.Winx.Hash, so we never depend on it.
    private static readonly PropertyKey PkeyLinkTargetParsingPath = new()
    {
        GUID = new Guid("B9B4B3FC-2B51-4A42-B5D8-324146AFCF25"),
        PID = 2
    };

    private static readonly PropertyKey PkeyLinkArguments = new()
    {
        GUID = new Guid("436F2667-14E2-4FEB-B30A-146C53B5B674"),
        PID = 100
    };

    private static readonly PropertyKey PkeyWinXHash = new()
    {
        GUID = new Guid("FB8D2D7B-90D1-4E34-BF60-6EAC09922BBF"),
        PID = 2
    };

    private static readonly string PropertyKeyMode = "HardCoded";

    public static void HashLnk(string lnkPath)
    {
        var result = HashLnkWithResult(lnkPath);
        if (!result.Success)
        {
            throw new InvalidOperationException(result.Error ?? "Unable to write Win+X shortcut hash.");
        }
    }

    public static WinXHashResult HashLnkWithResult(
        string lnkPath,
        string? fallbackTargetPath = null,
        string? fallbackArguments = null)
    {
        string? targetPath = null;
        string? arguments = null;
        string? propertyStoreTargetPath = null;
        string? propertyStoreArguments = null;
        string? generalizedTargetPath = null;
        uint? hash = null;

        IShellItem? item = null;
        var storeRef = default(IPropertyStore);

        try
        {
            WinXLog($"WinXHashStart: Path={lnkPath}, PropertyKeyMode={PropertyKeyMode}.");

            SHCreateItemFromParsingName(lnkPath, null, typeof(IShellItem2).GUID, out item);
            var item2 = (IShellItem2)item;

            try { propertyStoreTargetPath = item2.GetString(PkeyLinkTargetParsingPath); }
            catch { propertyStoreTargetPath = null; }

            try { propertyStoreArguments = item2.GetString(PkeyLinkArguments); }
            catch { propertyStoreArguments = null; }

            ShortcutInfo? shortcutFallback = null;
            if (string.IsNullOrWhiteSpace(propertyStoreTargetPath)
                && string.IsNullOrWhiteSpace(fallbackTargetPath))
            {
                try { shortcutFallback = WinXShortcutFile.Read(lnkPath); }
                catch { shortcutFallback = null; }
            }

            targetPath = !string.IsNullOrWhiteSpace(propertyStoreTargetPath)
                ? propertyStoreTargetPath
                : !string.IsNullOrWhiteSpace(fallbackTargetPath)
                    ? fallbackTargetPath
                    : shortcutFallback?.TargetPath;
            arguments = propertyStoreArguments
                ?? fallbackArguments
                ?? shortcutFallback?.Arguments
                ?? string.Empty;

            WinXLog(
                $"WinXHashTargetRead: Path={lnkPath}, PropertyStoreTarget={FormatLogValue(propertyStoreTargetPath)}, " +
                $"FallbackTarget={FormatLogValue(fallbackTargetPath)}, FinalTarget={FormatLogValue(targetPath)}, " +
                $"WasTargetPathFromPropertyStore={!string.IsNullOrWhiteSpace(propertyStoreTargetPath)}, " +
                $"PropertyStoreArguments={FormatLogValue(propertyStoreArguments)}, FallbackArguments={FormatLogValue(fallbackArguments)}, " +
                $"FinalArguments={FormatLogValue(arguments)}.");
            if (string.IsNullOrWhiteSpace(targetPath))
            {
                const string error = "Unable to compute Win+X hash because shortcut target path could not be read.";
                WinXLog(RuntimeLogLevel.Warning, $"WinXHashFailed: Path={lnkPath}, Error={error}");
                return new WinXHashResult(false, null, targetPath, arguments, null, error);
            }

            generalizedTargetPath = GeneralizePath(targetPath);
            var blob = (generalizedTargetPath + arguments
                + "do not prehash links.  this should only be done by the user.").ToLowerInvariant();
            var input = Encoding.Unicode.GetBytes(blob);
            var output = new byte[input.Length];
            HashData(input, input.Length, output, output.Length);
            hash = BitConverter.ToUInt32(output, 0);

            WinXLog(
                $"WinXHashInput: Path={lnkPath}, GeneralizedTarget={generalizedTargetPath}, Arguments={FormatLogValue(arguments)}, " +
                $"BlobLength={blob.Length}, Hash={hash}.");

            var storeId = typeof(IPropertyStore).GUID;
            storeRef = item2.GetPropertyStore(GPS.READWRITE, ref storeId);
            var propVariant = new PropVariant { VarType = VarEnum.VT_UI4, uintVal = hash.Value };
            var winXHashKey = PkeyWinXHash;
            storeRef.SetValue(ref winXHashKey, ref propVariant);
            WinXLog($"WinXHashSetValue: Path={lnkPath}, Hash={hash}.");
            storeRef.Commit();
            WinXLog($"WinXHashCommitSucceeded: Path={lnkPath}, Hash={hash}.");
        }
        catch (Exception ex)
        {
            WinXLog(RuntimeLogLevel.Warning, $"WinXHashFailed: Path={lnkPath}, TargetPath={targetPath}, GeneralizedTargetPath={generalizedTargetPath}, Arguments={arguments}, Hash={hash}, Error={ex.Message}.");
            return new WinXHashResult(false, hash, targetPath, arguments, generalizedTargetPath, ex.Message);
        }
        finally
        {
            if (storeRef is not null) Marshal.ReleaseComObject(storeRef);
            if (item is not null) Marshal.ReleaseComObject(item);
        }

        // Diagnostic readback verification — does not affect Success.
        if (TryReadWinXHashWithRetry(lnkPath, out var readHash, out var readError))
        {
            if (readHash == hash)
            {
                return new WinXHashResult(true, hash, targetPath, arguments, generalizedTargetPath, null,
                    VerificationSucceeded: true, ReadBackHash: readHash);
            }

            WinXLog(RuntimeLogLevel.Warning, $"WinXHashVerifyWarning: Path={lnkPath}, Wrote={hash}, ReadBack={readHash}, Reason=HashMismatch.");
            return new WinXHashResult(true, hash, targetPath, arguments, generalizedTargetPath, null,
                VerificationSucceeded: false, ReadBackHash: readHash,
                VerificationWarning: $"Hash mismatch: wrote {hash}, read {readHash}.");
        }

        WinXLog(RuntimeLogLevel.Warning, $"WinXHashVerifyWarning: Path={lnkPath}, Wrote={hash}, ReadBack=<failed>, Reason={readError}.");
        return new WinXHashResult(true, hash, targetPath, arguments, generalizedTargetPath, null,
            VerificationSucceeded: false,
            VerificationWarning: $"Readback verification did not confirm written hash: {readError}");
    }

    public static bool TryReadWinXHash(string lnkPath, out uint hash, out string? error)
    {
        hash = 0;
        error = null;
        try
        {
            SHCreateItemFromParsingName(lnkPath, null, typeof(IShellItem2).GUID, out var item);
            var item2 = (IShellItem2)item;
            var storeId = typeof(IPropertyStore).GUID;
            var store = item2.GetPropertyStore(GPS.DEFAULT, ref storeId);
            var winXHashKey = PkeyWinXHash;
            var pv = new PropVariant();
            try
            {
                store.GetValue(ref winXHashKey, out pv);
                if (pv.VarType == VarEnum.VT_UI4)
                {
                    hash = (uint)pv.ulVal;
                }
                else
                {
                    error = $"System.Winx.Hash property found but type is {pv.VarType}, not VT_UI4.";
                    return false;
                }
            }
            finally
            {
                Marshal.ReleaseComObject(store);
                Marshal.ReleaseComObject(item);
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool TryReadWinXHashWithRetry(string lnkPath, out uint hash, out string? error)
    {
        hash = 0;
        error = null;
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            if (TryReadWinXHash(lnkPath, out hash, out error))
            {
                return true;
            }

            if (!IsSharingViolation(error))
            {
                break;
            }

            Thread.Sleep(50);
        }

        return false;
    }

    private static bool IsSharingViolation(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return false;
        }

        return error.Contains("0x80070020", StringComparison.OrdinalIgnoreCase)
               || error.Contains("being used by another process", StringComparison.OrdinalIgnoreCase);
    }

    private static string? GeneralizePath(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return filePath;
        }

        foreach (var pair in GeneralizePathMap)
        {
            var directory = Environment.ExpandEnvironmentVariables(pair.Key);
            if (filePath.StartsWith(directory + "\\", StringComparison.OrdinalIgnoreCase))
            {
                return filePath.Replace(directory, pair.Value, StringComparison.OrdinalIgnoreCase);
            }
        }

        return filePath;
    }

    private static string FormatLogValue(string? value) =>
        value is null ? "<null>" : string.IsNullOrEmpty(value) ? "<empty>" : value;

    private static void WinXLog(string message) => FileLoggerHost.Log(message);

    private static void WinXLog(RuntimeLogLevel level, string message) => FileLoggerHost.Log(level, message);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern int HashData(byte[] pbData, int cbData, byte[] pbHash, int cbHash);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern uint SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
        IBindCtx? pbc,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        out IShellItem ppv);

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
        void GetCount(out uint cProps);

        [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
        void GetAt(uint iProp, out PropertyKey pkey);

        [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
        void GetValue(ref PropertyKey key, out PropVariant pv);

        [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
        void SetValue(ref PropertyKey key, ref PropVariant pv);

        [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
        void Commit();
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
    private interface IShellItem
    {
        void BindToHandler(nint pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid bhid, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out nint ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(uint sigdnName, out nint ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItem psi, uint hint, out int piOrder);
    }

    [ComImport]
    [SuppressUnmanagedCodeSecurity]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("7e9fb0d3-919f-4307-ab2e-9b1860310c93")]
    private interface IShellItem2 : IShellItem
    {
        [return: MarshalAs(UnmanagedType.Interface)]
        object BindToHandler(IBindCtx pbc, [In] ref Guid bhid, [In] ref Guid riid);

        IShellItem GetParent();

        [return: MarshalAs(UnmanagedType.LPWStr)]
        string GetDisplayName(uint sigdnName);

        uint GetAttributes(uint sfgaoMask);

        int Compare(IShellItem psi, uint hint);

        [return: MarshalAs(UnmanagedType.Interface)]
        IPropertyStore GetPropertyStore(GPS flags, [In] ref Guid riid);

        [return: MarshalAs(UnmanagedType.Interface)]
        object GetPropertyStoreWithCreateObject(GPS flags, [MarshalAs(UnmanagedType.IUnknown)] object punkCreateObject, [In] ref Guid riid);

        [return: MarshalAs(UnmanagedType.Interface)]
        object GetPropertyStoreForKeys(nint rgKeys, uint cKeys, GPS flags, [In] ref Guid riid);

        [return: MarshalAs(UnmanagedType.Interface)]
        object GetPropertyDescriptionList(nint keyType, [In] ref Guid riid);

        void Update(IBindCtx pbc);

        [SecurityCritical]
        void GetProperty(nint key, [In][Out] PropVariant pv);

        Guid GetCLSID(nint key);

        FILETIME GetFileTime(nint key);

        int GetInt32(nint key);

        [return: MarshalAs(UnmanagedType.LPWStr)]
        string GetString(PropertyKey key);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PropertyKey
    {
        public Guid GUID;
        public int PID;
    }

    [StructLayout(LayoutKind.Explicit, Pack = 1)]
    private struct PropVariant
    {
        [FieldOffset(0)] public VarEnum VarType;
        [FieldOffset(2)] public ushort wReserved1;
        [FieldOffset(4)] public ushort wReserved2;
        [FieldOffset(6)] public ushort wReserved3;
        [FieldOffset(8)] public uint uintVal;
        [FieldOffset(8)] public ulong ulVal;
    }

    [Flags]
    private enum GPS
    {
        DEFAULT = 0x00000000,
        READWRITE = 0x00000002
    }

    // Lightweight file logger bridge for diagnostic WinX hash events. The
    // service-side FileLogger instance is resolved at runtime so this static
    // helper stays decoupled from the host composition root.
    internal static class FileLoggerHost
    {
        private static FileLogger? _logger;
        private static readonly object Sync = new();

        public static void Attach(FileLogger logger)
        {
            lock (Sync)
            {
                _logger = logger;
            }
        }

        public static void Log(string message)
        {
            var logger = Current();
            if (logger is null)
            {
                return;
            }

            logger.LogFireAndForget(message);
        }

        public static void Log(RuntimeLogLevel level, string message)
        {
            var logger = Current();
            if (logger is null)
            {
                return;
            }

            logger.LogFireAndForget(level, message);
        }

        private static FileLogger? Current()
        {
            lock (Sync)
            {
                return _logger;
            }
        }
    }
}
