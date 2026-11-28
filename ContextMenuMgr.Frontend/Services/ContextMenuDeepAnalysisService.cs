using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using ContextMenuMgr.Contracts;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace ContextMenuMgr.Frontend.Services;

public sealed class ContextMenuDeepAnalysisService
{
    private const int MaximumResultFileBytes = 4 * 1024 * 1024;
    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 0x00000001;
    private const uint OpenExisting = 3;
    private const uint OpenReparsePoint = 0x00200000;
    private const int NativeAccessViolationExitCode = -1073741819;
    private const int StackBufferOverrunExitCode = -1073740791;
    private const string ProbeHostArchitectureMismatchMessage = "解析辅助进程架构与目录标识不一致，无法启动。请重新构建或安装完整程序包。";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private readonly LocalizationService _localization;

    public ContextMenuDeepAnalysisService(LocalizationService localization)
    {
        _localization = localization;
    }

    public async Task<ContextMenuDeepAnalysisResult> AnalyzeAsync(
        ContextMenuDeepAnalysisRequest request,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        if (request.OperationId == Guid.Empty)
        {
            request = request with { OperationId = Guid.NewGuid() };
        }

        var startedAt = DateTimeOffset.UtcNow;
        FrontendDebugLog.Operation(
            "FrontendOperation",
            $"DeepAnalysisStart: OperationId={request.OperationId}, ItemId={request.ItemId}, DisplayName={request.DisplayName}, EntryKind={request.EntryKind}, Category={request.Category}, HandlerClsid={request.HandlerClsid}, ProbeMode={request.ProbeMode}, Timestamp={startedAt:O}.");

        ProbeHostSelection selection;
        try
        {
            selection = SelectProbeHost(request);
        }
        catch (Exception ex) when (RuntimeHosting.IsInProcess)
        {
            FrontendDebugLog.Warning("ContextMenuDeepAnalysisService", $"Portable user context unavailable for ProbeHost selection: {ex.Message}");
            return Failure("InteractiveUserUnavailable", ex.Message, request);
        }
        FrontendDebugLog.Info(
            "ContextMenuDeepAnalysisService",
            $"ProbeHostSelection: OSArchitecture={RuntimeInformation.OSArchitecture}, FrontendProcessArchitecture={RuntimeInformation.ProcessArchitecture}, HandlerFilePath={selection.HandlerFilePath ?? "<null>"}, HandlerFileExists={selection.HandlerFileExists}, HandlerMachineType={selection.HandlerMachineType}, HandlerMachineRawValue={selection.HandlerMachineRawValue ?? "<null>"}, SelectedProbeHostPath={selection.SelectedProbeHostPath ?? "<null>"}, SelectedProbeHostArchitecture={selection.SelectedProbeHostArchitecture}, Reason={selection.Reason}.");

        if (selection.FailureCode is not null || selection.SelectedProbeHostPath is null)
        {
            FrontendDebugLog.Operation(
                "FrontendOperation",
                $"DeepAnalysisEnd: OperationId={request.OperationId}, ItemId={request.ItemId}, ProbeMode={request.ProbeMode}, Success=false, ErrorCode={selection.FailureCode ?? "ProbeHostNotFound"}, ItemCount=0, ElapsedMs={(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds:F0}.");
            return Failure(selection.FailureCode ?? "ProbeHostNotFound", GetMissingHostMessage(selection), request) with
            {
                HandlerFilePath = selection.HandlerFilePath ?? request.HandlerFilePath,
                HandlerFileExists = selection.HandlerFileExists,
                HandlerMachineType = selection.HandlerMachineType,
                HandlerFileMachineType = selection.HandlerMachineType,
                HandlerMachineRawValue = selection.HandlerMachineRawValue,
                SelectedProbeHostArchitecture = selection.SelectedProbeHostArchitecture.ToString(),
                ArchitectureSelectionReason = selection.Reason,
                OSArchitecture = RuntimeInformation.OSArchitecture.ToString(),
                FrontendProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
                DiagnosticDetails = $"ProbeHostSelection: {selection.Reason}"
            };
        }

        var architectureFailure = ValidateSelectedProbeHostArchitecture(selection, request);
        if (architectureFailure is not null)
        {
            FrontendDebugLog.Operation(
                "FrontendOperation",
                $"DeepAnalysisEnd: OperationId={request.OperationId}, ItemId={request.ItemId}, ProbeMode={request.ProbeMode}, Success=false, ErrorCode={architectureFailure.ErrorCode}, ItemCount=0, ElapsedMs={(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds:F0}.");
            return architectureFailure;
        }

        var operationRoot = RuntimeHosting.IsInProcess
            ? Path.Combine(RuntimePaths.DataDirectory, "DeepAnalysis")
            : Path.Combine(Path.GetTempPath(), "ContextMenuMgr.DeepAnalysis");
        return await RunProbeHostAsync(request, selection, operationRoot, startedAt, timeout,
            CreateOperationDirectory, StartProbeHostProcess, cancellationToken);
    }

    internal async Task<ContextMenuDeepAnalysisResult> RunProbeHostAsync(
        ContextMenuDeepAnalysisRequest request, ProbeHostSelection selection, string operationRoot,
        DateTimeOffset startedAt, TimeSpan? timeout, Action<string> createOperationDirectory,
        Func<ProcessStartInfo, Process?> startProcess, CancellationToken cancellationToken)
    {
        using var timeoutCts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(5));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        string? operationDirectory = null;
        Process? process = null;
        Task<string>? outputTask = null;
        Task<string>? errorTask = null;
        var failureCode = "ProbeHostFailed";
        try
        {
            linkedCts.Token.ThrowIfCancellationRequested();
            Directory.CreateDirectory(operationRoot);
            if (RuntimeHosting.IsInProcess && (File.GetAttributes(operationRoot) & FileAttributes.ReparsePoint) != 0)
                return Failure("ProbeHostUnavailable", "Deep Analysis data directory must not be a reparse point.", request);
            operationDirectory = Path.Combine(operationRoot, Guid.NewGuid().ToString("N"));
            createOperationDirectory(operationDirectory);
            var requestPath = Path.Combine(operationDirectory, "request.json");
            var resultPath = Path.Combine(operationDirectory, "result.json");
            await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(request, JsonOptions), Utf8NoBom, linkedCts.Token);

            var probeHostPath = selection.SelectedProbeHostPath!;
            var startInfo = new ProcessStartInfo
            {
                FileName = probeHostPath,
                WorkingDirectory = Path.GetDirectoryName(probeHostPath) ?? AppContext.BaseDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = Utf8NoBom,
                StandardOutputEncoding = Utf8NoBom,
                StandardErrorEncoding = Utf8NoBom
            };
            startInfo.ArgumentList.Add("--request");
            startInfo.ArgumentList.Add(requestPath);
            startInfo.ArgumentList.Add("--result");
            startInfo.ArgumentList.Add(resultPath);

            linkedCts.Token.ThrowIfCancellationRequested();
            FrontendDebugLog.Info(
                "ContextMenuDeepAnalysisService",
                $"ProbeHostStart: Path={probeHostPath}, OperationId={request.OperationId}, ItemId={request.ItemId}, ProbeMode={request.ProbeMode}, TimeoutMs={(timeout ?? TimeSpan.FromSeconds(5)).TotalMilliseconds:F0}, RequestPath={requestPath}, ResultPath={resultPath}.");

            failureCode = "ProbeHostStartFailed";
            process = startProcess(startInfo) ?? throw new InvalidOperationException("Failed to start ProbeHost.");
            failureCode = "ProbeHostFailed";
            if (!RuntimeHosting.IsInProcess)
            {
                process.StandardInput.Close();
                outputTask = process.StandardOutput.ReadToEndAsync();
                errorTask = process.StandardError.ReadToEndAsync();
            }
            else
            {
                outputTask = Task.FromResult(string.Empty);
                errorTask = Task.FromResult("ProbeHost was started under the verified, non-elevated interactive user token. Standard output and error are not redirected in In-process mode.");
            }
            await process.WaitForExitAsync(linkedCts.Token);

            var output = await outputTask;
            var error = await errorTask;
            var exitCode = process.ExitCode;
            var elapsedMs = (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds;
            var stdoutBytes = Utf8NoBom.GetByteCount(output);
            var stderrBytes = Utf8NoBom.GetByteCount(error);
            FrontendDebugLog.Info(
                "ContextMenuDeepAnalysisService",
                $"ProbeHostExit: OperationId={request.OperationId}, ItemId={request.ItemId}, ExitCode={exitCode}, ElapsedMs={elapsedMs:F0}, StdoutBytes={stdoutBytes}, StderrBytes={stderrBytes}, ResultFileExists={File.Exists(resultPath)}.");
            FrontendDebugLog.Info(
                "ContextMenuDeepAnalysisService",
                $"ProbeHostCapturedOutput: ItemId={request.ItemId}, StdoutExcerpt={Excerpt(output, 300)}, StderrExcerpt={Excerpt(error, 1000)}.");

            var resultJson = await TryReadResultFileAsync(resultPath, cancellationToken);

            if (!string.IsNullOrWhiteSpace(resultJson))
            {
                var parsed = EnrichWithSelection(TryParseResultJson(resultJson, exitCode, error, request, resultPath, source: "result file"), selection);
                var jsonValid = parsed.ErrorCode is not "InvalidProbeHostJson" and not "InvalidProbeHostOutput";
                FrontendDebugLog.Info(
                    "ContextMenuDeepAnalysisService",
                    $"ProbeHostResult: ItemId={request.ItemId}, Source=ResultFile, JsonValid={jsonValid}, Success={parsed.Success}, ErrorCode={parsed.ErrorCode ?? "<none>"}, ItemCount={parsed.Items.Count}.");
                FrontendDebugLog.Operation(
                    "FrontendOperation",
                    $"DeepAnalysisEnd: OperationId={parsed.OperationId}, ItemId={request.ItemId}, ProbeMode={request.ProbeMode}, Success={parsed.Success}, ErrorCode={parsed.ErrorCode ?? "<none>"}, ItemCount={parsed.Items.Count}, ExitCode={exitCode}, ElapsedMs={elapsedMs:F0}.");
                return parsed;
            }

            var trimmedStdout = TrimBomAndWhitespace(output);
            if (trimmedStdout.StartsWith("{", StringComparison.Ordinal))
            {
                var parsed = EnrichWithSelection(TryParseResultJson(trimmedStdout, exitCode, error, request, "stdout", source: "stdout"), selection);
                var jsonValid = parsed.ErrorCode is not "InvalidProbeHostJson" and not "InvalidProbeHostOutput";
                FrontendDebugLog.Info(
                    "ContextMenuDeepAnalysisService",
                    $"ProbeHostResult: ItemId={request.ItemId}, Source=Stdout, JsonValid={jsonValid}, Success={parsed.Success}, ErrorCode={parsed.ErrorCode ?? "<none>"}, ItemCount={parsed.Items.Count}.");
                FrontendDebugLog.Operation(
                    "FrontendOperation",
                    $"DeepAnalysisEnd: OperationId={parsed.OperationId}, ItemId={request.ItemId}, ProbeMode={request.ProbeMode}, Success={parsed.Success}, ErrorCode={parsed.ErrorCode ?? "<none>"}, ItemCount={parsed.Items.Count}, ExitCode={exitCode}, ElapsedMs={elapsedMs:F0}.");
                return parsed;
            }

            var nativeCrash = TryBuildNativeCrashFailure(exitCode, output, error, request, requestPath, resultPath, selection);
            if (nativeCrash is not null)
            {
                FrontendDebugLog.Warning(
                    "ContextMenuDeepAnalysisService",
                    $"ProbeHostExitFailure: ItemId={request.ItemId}, Category={request.Category}, HandlerClsid={request.HandlerClsid}, ErrorCode={nativeCrash.ErrorCode}, ExitCode={exitCode}, ExitCodeHex={FormatExitCodeHex(exitCode)}, ProbeHostArchitecture={ExtractProbeHostArchitecture(error)}, OSArchitecture={RuntimeInformation.OSArchitecture}, StdoutExcerpt={Excerpt(output, 300)}, StderrExcerpt={Excerpt(error, 1000)}.");
                FrontendDebugLog.Operation(
                    "FrontendOperation",
                    $"DeepAnalysisEnd: OperationId={nativeCrash.OperationId}, ItemId={request.ItemId}, ProbeMode={request.ProbeMode}, Success=false, ErrorCode={nativeCrash.ErrorCode}, ItemCount=0, ExitCode={exitCode}, ExitCodeHex={FormatExitCodeHex(exitCode)}, ElapsedMs={elapsedMs:F0}.");
                return nativeCrash;
            }

            var invalidOutput = EnrichWithSelection(BuildInvalidOutputFailure(exitCode, output, error, request, requestPath, resultPath), selection);
            FrontendDebugLog.Warning(
                "ContextMenuDeepAnalysisService",
                $"ProbeHostInvalidOutput: ItemId={request.ItemId}, ExitCode={exitCode}, StdoutExcerpt={Excerpt(output, 300)}, StderrExcerpt={Excerpt(error, 1000)}.");
            FrontendDebugLog.Operation(
                "FrontendOperation",
                $"DeepAnalysisEnd: OperationId={invalidOutput.OperationId}, ItemId={request.ItemId}, ProbeMode={request.ProbeMode}, Success=false, ErrorCode={invalidOutput.ErrorCode}, ItemCount=0, ExitCode={exitCode}, ElapsedMs={elapsedMs:F0}.");
            return invalidOutput;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            KillProcess(process);
            var output = await TryReadTaskAsync(outputTask);
            var error = await TryReadTaskAsync(errorTask);
            FrontendDebugLog.Warning(
                "ContextMenuDeepAnalysisService",
                $"ProbeHostExit: ItemId={request.ItemId}, ExitCode=<timeout>, ElapsedMs={(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds:F0}, StdoutBytes={Utf8NoBom.GetByteCount(output)}, StderrBytes={Utf8NoBom.GetByteCount(error)}, ResultJsonValid=false.");
            FrontendDebugLog.Operation(
                "FrontendOperation",
                $"DeepAnalysisEnd: OperationId={request.OperationId}, ItemId={request.ItemId}, ProbeMode={request.ProbeMode}, Success=false, ErrorCode=Timeout, ItemCount=0, ElapsedMs={(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds:F0}.");
            return Failure("Timeout", _localization.Translate("DeepAnalyzeTimeoutText"), request);
        }
        catch (Exception ex)
        {
            KillProcess(process);
            FrontendDebugLog.Error(
                "FrontendOperation",
                ex,
                $"DeepAnalysisEnd: OperationId={request.OperationId}, ItemId={request.ItemId}, ProbeMode={request.ProbeMode}, Success=false, ErrorCode={failureCode}, ItemCount=0, ElapsedMs={(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds:F0}.");
            return Failure(failureCode, ex.Message, request);
        }
        finally
        {
            process?.Dispose();
            if (operationDirectory is not null) TryDeleteDirectory(operationDirectory);
        }
    }

    private static void CreateOperationDirectory(string directory)
    {
#if CONTEXTMENUMGR_IN_PROCESS
        if (RuntimeHosting.IsInProcess)
        {
            InProcessBootstrap.User.CreatePrivateDirectory(directory);
            return;
        }
#endif
        Directory.CreateDirectory(directory);
    }

    private static Process? StartProbeHostProcess(ProcessStartInfo startInfo)
    {
#if CONTEXTMENUMGR_IN_PROCESS
        return InProcessBootstrap.User.StartNonElevatedProcess(startInfo.FileName,
            startInfo.ArgumentList, startInfo.WorkingDirectory);
#else
        return Process.Start(startInfo);
#endif
    }

    private ProbeHostSelection SelectProbeHost(ContextMenuDeepAnalysisRequest request)
    {
        var handlerFilePath = NormalizeComServerPath(request.HandlerFilePath) ?? ResolveHandlerPathFromClsid(request.HandlerClsid);
        var machine = PeMachineTypeDetector.Detect(handlerFilePath);
        var architecture = machine.Architecture == ProbeHostArchitecture.Unknown
            ? CurrentProcessProbeHostArchitecture()
            : machine.Architecture;
        var path = LocateProbeHost(architecture, allowRootFallback: machine.Architecture == ProbeHostArchitecture.Unknown);
        var reason = machine.Architecture == ProbeHostArchitecture.Unknown
            ? $"UnknownHandlerArchitectureFallback: {machine.Error ?? "unknown machine type"}"
            : $"HandlerMachineType={machine.MachineType}";

        if (path is not null)
        {
            return new ProbeHostSelection(
                path,
                architecture,
                handlerFilePath,
                !string.IsNullOrWhiteSpace(handlerFilePath) && File.Exists(handlerFilePath),
                machine.MachineType,
                machine.RawValue,
                reason,
                null);
        }

        var failureCode = architecture switch
        {
            ProbeHostArchitecture.X86 => "MissingX86ProbeHost",
            ProbeHostArchitecture.X64 => "MissingX64ProbeHost",
            ProbeHostArchitecture.Arm64 => "MissingArm64ProbeHost",
            _ => "ProbeHostNotFound"
        };

        return new ProbeHostSelection(
            null,
            architecture,
            handlerFilePath,
            !string.IsNullOrWhiteSpace(handlerFilePath) && File.Exists(handlerFilePath),
            machine.MachineType,
            machine.RawValue,
            $"{reason}; Missing host for {architecture}.",
            failureCode);
    }

    private static ProbeHostArchitecture CurrentProcessProbeHostArchitecture() => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X86 => ProbeHostArchitecture.X86,
        Architecture.X64 => ProbeHostArchitecture.X64,
        Architecture.Arm64 => ProbeHostArchitecture.Arm64,
        _ => ProbeHostArchitecture.Unknown
    };

    private static string? LocateProbeHost(ProbeHostArchitecture architecture, bool allowRootFallback)
    {
        var baseDirectory = AppContext.BaseDirectory;
        var architectureName = architecture switch
        {
            ProbeHostArchitecture.X86 => "x86",
            ProbeHostArchitecture.X64 => "x64",
            ProbeHostArchitecture.Arm64 => "arm64",
            _ => null
        };

        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(architectureName))
        {
            candidates.Add(Path.Combine(baseDirectory, "ProbeHost", architectureName, "ContextMenuMgr.ProbeHost.exe"));
        }

        if (allowRootFallback)
        {
            candidates.Add(Path.Combine(baseDirectory, "ContextMenuMgr.ProbeHost.exe"));
            candidates.Add(Path.Combine(baseDirectory, "probehost", "ContextMenuMgr.ProbeHost.exe"));
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    private string GetMissingHostMessage(ProbeHostSelection selection)
    {
        return selection.FailureCode switch
        {
            "MissingX86ProbeHost" => _localization.Translate("DeepAnalysisMissingX86ProbeHost"),
            "MissingX64ProbeHost" => _localization.Translate("DeepAnalysisMissingX64ProbeHost"),
            "MissingArm64ProbeHost" => _localization.Translate("DeepAnalysisMissingArm64ProbeHost"),
            _ => "ContextMenuMgr.ProbeHost.exe was not found."
        };
    }

    private ContextMenuDeepAnalysisResult? ValidateSelectedProbeHostArchitecture(
        ProbeHostSelection selection,
        ContextMenuDeepAnalysisRequest request)
    {
        if (string.IsNullOrWhiteSpace(selection.SelectedProbeHostPath)
            || selection.SelectedProbeHostArchitecture == ProbeHostArchitecture.Unknown)
        {
            return null;
        }

        var machine = PeMachineTypeDetector.Detect(selection.SelectedProbeHostPath);
        var folderLabel = Path.GetFileName(Path.GetDirectoryName(selection.SelectedProbeHostPath) ?? string.Empty);
        FrontendDebugLog.Info(
            "ContextMenuDeepAnalysisService",
            $"ProbeHostExecutableArchitecture: SelectedProbeHostPath={selection.SelectedProbeHostPath}, FolderLabel={folderLabel}, SelectedProbeHostArchitecture={selection.SelectedProbeHostArchitecture}, ActualProbeHostMachineType={machine.MachineType}, ActualProbeHostMachineRawValue={machine.RawValue ?? "<null>"}, Error={machine.Error ?? "<none>"}.");

        if (machine.Architecture == selection.SelectedProbeHostArchitecture)
        {
            return null;
        }

        var diagnosticDetails = string.Join(
            Environment.NewLine,
            new[]
            {
                $"SelectedProbeHostPath={selection.SelectedProbeHostPath}",
                $"SelectedProbeHostArchitecture={selection.SelectedProbeHostArchitecture}",
                $"ProbeHostFolderLabel={folderLabel}",
                $"ActualProbeHostMachineType={machine.MachineType}",
                $"ActualProbeHostMachineRawValue={machine.RawValue}",
                $"ActualProbeHostMachineError={machine.Error}",
                $"HandlerFilePath={selection.HandlerFilePath ?? request.HandlerFilePath}",
                $"HandlerMachineType={selection.HandlerMachineType}",
                $"HandlerMachineRawValue={selection.HandlerMachineRawValue}",
                $"OSArchitecture={RuntimeInformation.OSArchitecture}",
                $"FrontendProcessArchitecture={RuntimeInformation.ProcessArchitecture}"
            }.Where(static line => !string.IsNullOrWhiteSpace(line)));

        return Failure(
            "ProbeHostExecutableArchitectureMismatch",
            ProbeHostArchitectureMismatchMessage,
            request) with
        {
            HandlerFilePath = selection.HandlerFilePath ?? request.HandlerFilePath,
            HandlerFileExists = selection.HandlerFileExists,
            HandlerMachineType = selection.HandlerMachineType,
            HandlerFileMachineType = selection.HandlerMachineType,
            HandlerMachineRawValue = selection.HandlerMachineRawValue,
            SelectedProbeHostArchitecture = selection.SelectedProbeHostArchitecture.ToString(),
            SelectedProbeHostPath = selection.SelectedProbeHostPath,
            ActualProbeHostMachineType = machine.MachineType,
            ArchitectureSelectionReason = selection.Reason,
            OSArchitecture = RuntimeInformation.OSArchitecture.ToString(),
            FrontendProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            DiagnosticDetails = diagnosticDetails
        };
    }

    private string? ResolveHandlerPathFromClsid(string? handlerClsid)
    {
        if (!Guid.TryParse(handlerClsid, out var clsid))
        {
            return null;
        }

        var clsidText = clsid.ToString("B");
        foreach (var valuePath in new[]
                 {
                     $@"CLSID\{clsidText}\InprocServer32",
                     $@"CLSID\{clsidText}\LocalServer32"
                 })
        {
            try
            {
                using var classesRoot = OpenClassesRootForFrontend();
                using var key = classesRoot.OpenSubKey(valuePath);
                if (key?.GetValue(null) is string value && !string.IsNullOrWhiteSpace(value))
                {
                    return NormalizeComServerPath(value);
                }
            }
            catch
            {
            }
        }

        return null;
    }

    private string? NormalizeComServerPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var expanded = ExpandEnvironmentVariables(value.Trim());
        if (expanded.StartsWith('"'))
        {
            var closingQuoteIndex = expanded.IndexOf('"', 1);
            if (closingQuoteIndex > 1)
            {
                return expanded[1..closingQuoteIndex];
            }
        }

        foreach (var extension in new[] { ".dll", ".exe" })
        {
            var index = expanded.IndexOf(extension, StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
            {
                return expanded[..(index + extension.Length)];
            }
        }

        return expanded;
    }

    private RegistryKey OpenClassesRootForFrontend()
    {
#if CONTEXTMENUMGR_IN_PROCESS
        if (RuntimeHosting.IsInProcess)
            return InProcessBootstrap.User.OpenClassesRoot(RegistryView.Default);
#endif
        return RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, RegistryView.Default);
    }

    private string ExpandEnvironmentVariables(string value)
    {
#if CONTEXTMENUMGR_IN_PROCESS
        if (RuntimeHosting.IsInProcess)
            return InProcessBootstrap.User.ExpandEnvironmentVariables(value);
#endif
        return Environment.ExpandEnvironmentVariables(value);
    }

    private static async Task<string> TryReadResultFileAsync(string resultPath, CancellationToken cancellationToken)
    {
        using var handle = OpenFileWithoutFollowingReparsePoints(resultPath, GenericRead, FileShareRead,
            IntPtr.Zero, OpenExisting, 0x00000080 | OpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            if (error is 2 or 3) return string.Empty;
            throw new Win32Exception(error, "Open the ProbeHost result file.");
        }

        if (!GetFileInformationByHandleEx(handle, 9 /* FileAttributeTagInfo */, out var tagInfo,
                (uint)Marshal.SizeOf<FileAttributeTagInfo>()))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Inspect the ProbeHost result file.");
        if ((tagInfo.FileAttributes & ((uint)FileAttributes.ReparsePoint | (uint)FileAttributes.Directory)) != 0)
            throw new InvalidDataException("ProbeHost result must be a regular file.");
        if (!GetFileSizeEx(handle, out var length))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Read the ProbeHost result size.");
        if (length is <= 0 or > MaximumResultFileBytes)
            throw new InvalidDataException($"ProbeHost result size is outside the supported range ({length} bytes).");

        await using var stream = new FileStream(handle, FileAccess.Read, 64 * 1024, isAsync: true);
        using var reader = new StreamReader(stream, Utf8NoBom, detectEncodingFromByteOrderMarks: true,
            bufferSize: 64 * 1024, leaveOpen: true);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInfo
    {
        public uint FileAttributes;
        public uint ReparseTag;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle OpenFileWithoutFollowingReparsePoints(string path, uint desiredAccess,
        uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int informationClass,
        out FileAttributeTagInfo information, uint bufferSize);

    [DllImport("kernel32.dll", EntryPoint = "GetFileSizeEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileSizeEx(SafeFileHandle file, out long fileSize);

    private static ContextMenuDeepAnalysisResult Failure(
        string errorCode,
        string message,
        ContextMenuDeepAnalysisRequest request)
    {
        return new ContextMenuDeepAnalysisResult
        {
            OperationId = request.OperationId,
            Success = false,
            ErrorCode = errorCode,
            Message = message,
            DisplayName = request.DisplayName,
            HandlerClsid = request.HandlerClsid,
            HandlerFilePath = request.HandlerFilePath,
            SamplePath = request.SamplePath,
            HandlerFileExists = !string.IsNullOrWhiteSpace(request.HandlerFilePath) && File.Exists(request.HandlerFilePath),
            OSArchitecture = RuntimeInformation.OSArchitecture.ToString(),
            FrontendProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            ProbeMode = request.ProbeMode,
            IsSpecificHandlerResult = false,
            IsWholeContextMenuResult = false,
            SpecificHandlerFailedButWholeContextAvailable = request.ProbeMode == ContextMenuDeepAnalysisProbeMode.SpecificHandler,
            SpecificHandlerFailureCode = request.ProbeMode == ContextMenuDeepAnalysisProbeMode.SpecificHandler ? errorCode : null,
            SpecificHandlerFailureMessage = request.ProbeMode == ContextMenuDeepAnalysisProbeMode.SpecificHandler ? message : null
        };
    }

    private static ContextMenuDeepAnalysisResult TryParseResultJson(
        string json,
        int exitCode,
        string stderr,
        ContextMenuDeepAnalysisRequest request,
        string sourcePath,
        string source)
    {
        var trimmedJson = TrimBomAndWhitespace(json);
        if (!trimmedJson.StartsWith("{", StringComparison.Ordinal))
        {
            return BuildInvalidOutputFailure(exitCode, json, stderr, request, sourcePath, sourcePath);
        }

        try
        {
            var result = JsonSerializer.Deserialize<ContextMenuDeepAnalysisResult>(trimmedJson, JsonOptions);
            if (result is not null)
            {
                return result.OperationId == Guid.Empty
                    ? result with { OperationId = request.OperationId }
                    : result;
            }

            return Failure(
                "InvalidProbeHostJson",
                BuildInvalidJsonMessage("ProbeHost returned an empty JSON result.", exitCode, json, stderr, source, sourcePath),
                request);
        }
        catch (JsonException ex)
        {
            FrontendDebugLog.Warning(
                "ContextMenuDeepAnalysisService",
                $"ProbeHostInvalidJson: ItemId={request.ItemId}, Source={source}, SourcePath={sourcePath}, ExitCode={exitCode}, JsonError={ex.Message}, StdoutOrResultExcerpt={Excerpt(json, 300)}, StderrExcerpt={Excerpt(stderr, 1000)}.");
            return Failure(
                "InvalidProbeHostJson",
                BuildInvalidJsonMessage(ex.Message, exitCode, json, stderr, source, sourcePath),
                request);
        }
    }

    private static ContextMenuDeepAnalysisResult EnrichWithSelection(
        ContextMenuDeepAnalysisResult result,
        ProbeHostSelection selection)
    {
        return result with
        {
            HandlerFilePath = result.HandlerFilePath ?? selection.HandlerFilePath,
            HandlerFileExists = result.HandlerFileExists || selection.HandlerFileExists,
            HandlerMachineType = result.HandlerMachineType ?? selection.HandlerMachineType,
            HandlerFileMachineType = result.HandlerFileMachineType ?? selection.HandlerMachineType,
            HandlerMachineRawValue = result.HandlerMachineRawValue ?? selection.HandlerMachineRawValue,
            SelectedProbeHostArchitecture = selection.SelectedProbeHostArchitecture.ToString(),
            SelectedProbeHostPath = selection.SelectedProbeHostPath,
            ActualProbeHostMachineType = result.ActualProbeHostMachineType ?? PeMachineTypeDetector.Detect(selection.SelectedProbeHostPath).MachineType,
            ArchitectureSelectionReason = selection.Reason,
            OSArchitecture = result.OSArchitecture ?? RuntimeInformation.OSArchitecture.ToString(),
            FrontendProcessArchitecture = result.FrontendProcessArchitecture ?? RuntimeInformation.ProcessArchitecture.ToString()
        };
    }

    private ContextMenuDeepAnalysisResult? TryBuildNativeCrashFailure(
        int exitCode,
        string stdout,
        string stderr,
        ContextMenuDeepAnalysisRequest request,
        string requestPath,
        string resultPath,
        ProbeHostSelection selection)
    {
        var errorCode = exitCode switch
        {
            NativeAccessViolationExitCode => "ProbeHostNativeAccessViolation",
            StackBufferOverrunExitCode => "ProbeHostStackBufferOverrun",
            < 0 => "ProbeHostNativeCrash",
            _ => null
        };

        if (errorCode is null)
        {
            return null;
        }

        var message = errorCode switch
        {
            "ProbeHostNativeAccessViolation" => _localization.Translate("DeepAnalyzeNativeAccessViolationText"),
            "ProbeHostStackBufferOverrun" => _localization.Translate("DeepAnalyzeStackBufferOverrunText"),
            _ => _localization.Translate("DeepAnalyzeNativeCrashText")
        };

        return Failure(errorCode, message, request) with
        {
            ProbeHostProcessArchitecture = ExtractProbeHostArchitecture(stderr),
            SelectedProbeHostArchitecture = selection.SelectedProbeHostArchitecture.ToString(),
            SelectedProbeHostPath = selection.SelectedProbeHostPath,
            HandlerMachineType = selection.HandlerMachineType,
            HandlerFileMachineType = selection.HandlerMachineType,
            HandlerMachineRawValue = selection.HandlerMachineRawValue,
            ArchitectureSelectionReason = selection.Reason,
            ActualProbeHostMachineType = PeMachineTypeDetector.Detect(selection.SelectedProbeHostPath).MachineType,
            OSArchitecture = RuntimeInformation.OSArchitecture.ToString(),
            Is64BitProcess = Environment.Is64BitProcess,
            DiagnosticDetails = BuildProbeHostDiagnostics(exitCode, stdout, stderr, requestPath, resultPath)
        };
    }

    private static ContextMenuDeepAnalysisResult BuildInvalidOutputFailure(
        int exitCode,
        string stdout,
        string stderr,
        ContextMenuDeepAnalysisRequest request,
        string requestPath,
        string resultPath)
    {
        return Failure(
            "InvalidProbeHostOutput",
            $"ProbeHost returned non-JSON output. ExitCode={exitCode}. Stdout={Excerpt(stdout, 300)} Stderr={Excerpt(stderr, 1000)}",
            request) with
        {
            DiagnosticDetails = BuildProbeHostDiagnostics(exitCode, stdout, stderr, requestPath, resultPath)
        };
    }

    private static string BuildInvalidJsonMessage(
        string jsonError,
        int exitCode,
        string json,
        string stderr,
        string source,
        string sourcePath)
    {
        return $"ProbeHost returned invalid JSON from {source}. ExitCode={exitCode}. Source={sourcePath}. JsonError={jsonError}. Output={Excerpt(json, 300)} Stderr={Excerpt(stderr, 1000)}";
    }

    private static string TrimBomAndWhitespace(string value)
    {
        return (value ?? string.Empty).TrimStart('\uFEFF').Trim();
    }

    private static string Excerpt(string? value, int maxChars)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "<empty>";
        }

        var normalized = value.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
        return normalized.Length <= maxChars
            ? normalized
            : normalized[..maxChars] + "...";
    }

    private static string BuildProbeHostDiagnostics(
        int exitCode,
        string stdout,
        string stderr,
        string requestPath,
        string resultPath)
    {
        return $"ExitCode={exitCode} ({FormatExitCodeHex(exitCode)}){Environment.NewLine}"
               + $"RequestPath={requestPath}{Environment.NewLine}"
               + $"ResultPath={resultPath}{Environment.NewLine}"
               + $"Stdout={Excerpt(stdout, 300)}{Environment.NewLine}"
               + $"Stderr={Excerpt(stderr, 1000)}";
    }

    private static string FormatExitCodeHex(int exitCode) => $"0x{unchecked((uint)exitCode):X8}";

    private static string? ExtractProbeHostArchitecture(string stderr)
    {
        const string marker = "ProcessArchitecture=";
        if (string.IsNullOrWhiteSpace(stderr))
        {
            return null;
        }

        var index = stderr.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return null;
        }

        var start = index + marker.Length;
        var end = stderr.IndexOf(',', start);
        return end > start
            ? stderr[start..end].Trim()
            : stderr[start..].Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
    }

    private static async Task<string> TryReadTaskAsync(Task<string>? task)
    {
        if (task is null)
        {
            return string.Empty;
        }

        try
        {
            return await task.WaitAsync(TimeSpan.FromMilliseconds(500));
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch
        {
        }
    }

    private static void KillProcess(Process? process)
    {
        try
        {
            if (process is not null && !process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(500);
            }
        }
        catch
        {
        }
    }
}
