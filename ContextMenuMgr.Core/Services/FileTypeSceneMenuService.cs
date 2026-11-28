using ContextMenuMgr.Contracts;
using Microsoft.Win32;

namespace ContextMenuMgr.Backend.Services;

/// <summary>
/// Handles file type scene analysis and scene-scoped menu creation.
/// </summary>
public sealed class FileTypeSceneMenuService
{
    private readonly ContextMenuRegistryCatalog _catalog;
    private readonly ContextMenuStateStore _stateStore;
    private readonly FileLogger _logger;

    public FileTypeSceneMenuService(
        ContextMenuRegistryCatalog catalog,
        ContextMenuStateStore stateStore,
        FileLogger logger)
    {
        _catalog = catalog;
        _stateStore = stateStore;
        _logger = logger;
    }

    public Task<IReadOnlyList<FileTypeAnalysisResult>> AnalyzeAsync(FileTypeAnalysisRequest request, CancellationToken cancellationToken)
    {
        var inputPath = request.Path.Trim();
        var path = Environment.ExpandEnvironmentVariables(inputPath);
        var results = new List<FileTypeAnalysisResult>();
        _logger.LogFireAndForget($"FileTypeAnalyzeStart: InputPath={inputPath}, ExpandedPath={path}.");

        if (File.Exists(path))
        {
            _logger.LogFireAndForget($"FileTypeAnalyzePathKind: ExpandedPath={path}, Kind=File.");
            AnalyzeFile(path, results, includeLnkScene: true);
        }
        else if (Directory.Exists(path))
        {
            _logger.LogFireAndForget($"FileTypeAnalyzePathKind: ExpandedPath={path}, Kind=Directory.");
            AnalyzeDirectory(path, results);
        }
        else
        {
            _logger.LogFireAndForget(RuntimeLogLevel.Warning, $"FileTypeAnalyzePathKind: ExpandedPath={path}, Kind=Missing.");
            throw new FileNotFoundException("The selected file or folder does not exist.", path);
        }

        _logger.LogFireAndForget($"FileTypeAnalyzeEnd: InputPath={inputPath}, ExpandedPath={path}, ResultCount={results.Count}, Results={string.Join(";", results.Select(static item => $"{item.SceneKind}:{item.ScopeValue}"))}.");
        return Task.FromResult<IReadOnlyList<FileTypeAnalysisResult>>(results);
    }

    public async Task<PipeResponse> CreateSceneMenuItemAsync(
        CreateSceneMenuItemRequest request,
        Guid? operationId,
        CancellationToken cancellationToken)
    {
        try
        {
            var relativeRoot = ResolveSceneRoot(request.SceneKind, request.ScopeValue);
            await _logger.LogAsync($"CreateSceneMenuItemStart: SceneKind={request.SceneKind}, ScopeValue={request.ScopeValue}, ResolvedRelativeRoot={relativeRoot}, ItemKind={request.ItemKind}, KeyName={request.KeyName}.", cancellationToken);
            if (string.IsNullOrWhiteSpace(relativeRoot))
            {
                return new PipeResponse
                {
                    Success = false,
                    Message = "The target scene is missing a required scope value.",
                    ClientOperationId = operationId
                };
            }

            var keyName = SanitizeKeyName(request.KeyName);
            if (string.IsNullOrWhiteSpace(keyName))
            {
                return Failure("The key name is required.", operationId);
            }

            if (request.ItemKind == SceneMenuItemKind.ShellVerb)
            {
                if (string.IsNullOrWhiteSpace(request.Command))
                {
                    return Failure("Shell verb items require a command.", operationId);
                }

                var targetRoot = $@"{relativeRoot}\shell";
                var preflight = await _catalog.CreateRegistryWriteProtectionPreflightFailureAsync(
                    "CreateSceneShellVerb",
                    [targetRoot],
                    cancellationToken);
                if (preflight is not null)
                {
                    return preflight with { ClientOperationId = operationId };
                }

                keyName = WriteShellVerb(relativeRoot, keyName, request);
            }
            else
            {
                if (!Guid.TryParse(request.GuidText, out var guid))
                {
                    return Failure("The GUID format is invalid.", operationId);
                }

                var targetRoot = $@"{relativeRoot}\shellex\ContextMenuHandlers";
                var preflight = await _catalog.CreateRegistryWriteProtectionPreflightFailureAsync(
                    "CreateSceneShellExHandler",
                    [targetRoot],
                    cancellationToken);
                if (preflight is not null)
                {
                    return preflight with { ClientOperationId = operationId };
                }

                Registry.SetValue(
                    $@"HKEY_CLASSES_ROOT\{relativeRoot}\shellex\ContextMenuHandlers\{keyName}",
                    string.Empty,
                    guid.ToString("B"),
                    RegistryValueKind.String);
                await _logger.LogAsync(DiagnosticLogFormatter.BuildRegistryOperationLog("CreateSceneShellExHandler", $@"HKEY_CLASSES_ROOT\{relativeRoot}\shellex\ContextMenuHandlers\{keyName}", null, RegistryValueKind.String, guid.ToString("B"), writable: true, result: "SetValue Success"), cancellationToken);
            }

            ShellChangeNotifier.NotifyAssociationsChanged();
            var snapshot = await _catalog.GetSceneSnapshotAsync(request.SceneKind, request.ScopeValue, cancellationToken);
            var created = snapshot.FirstOrDefault(item => string.Equals(item.KeyName, keyName, StringComparison.OrdinalIgnoreCase))
                ?? snapshot.FirstOrDefault(item => string.Equals(item.HandlerClsid, request.GuidText, StringComparison.OrdinalIgnoreCase));

            if (created is not null)
            {
                await SuppressDetectionAsync(created, cancellationToken);
                await _logger.LogAsync($"CreateSceneMenuItemSuppressDetection: CreatedItemId={created.Id}, RegistryPath={created.RegistryPath}, Result=Success.", cancellationToken);
            }

            await _logger.LogAsync($"Created scene menu item. Scene={request.SceneKind}, Scope={request.ScopeValue}, KeyName={keyName}.", cancellationToken);
            return new PipeResponse
            {
                Success = true,
                Message = "Scene menu item created.",
                Item = created,
                ClientOperationId = operationId
            };
        }
        catch (Exception ex)
        {
            await _logger.LogAsync(RuntimeLogLevel.Error, $"Failed to create scene menu item: {ex}", cancellationToken);
            if (ex is UnauthorizedAccessException or System.Security.SecurityException)
            {
                var relativeRoot = ResolveSceneRoot(request.SceneKind, request.ScopeValue);
                var targetRoot = request.ItemKind == SceneMenuItemKind.ShellVerb
                    ? $@"{relativeRoot}\shell"
                    : $@"{relativeRoot}\shellex\ContextMenuHandlers";
                var fallback = await _catalog.CreateRegistryWriteProtectionPreflightFailureAsync(
                    "CreateSceneMenuItemFallback",
                    [targetRoot],
                    cancellationToken);
                if (fallback is not null)
                {
                    return fallback with { ClientOperationId = operationId };
                }
            }

            return Failure(ex.Message, operationId);
        }
    }

    private static void AnalyzeFile(string filePath, List<FileTypeAnalysisResult> results, bool includeLnkScene)
    {
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = ".";
        }

        if (includeLnkScene && extension == ".lnk")
        {
            try
            {
                var shortcut = ShortcutFile.Read(filePath);
                if (File.Exists(shortcut.TargetPath))
                {
                    AnalyzeFile(shortcut.TargetPath, results, includeLnkScene: false);
                }
                else if (Directory.Exists(shortcut.TargetPath))
                {
                    AnalyzeDirectory(shortcut.TargetPath, results);
                }
            }
            catch
            {
            }

            results.Add(new FileTypeAnalysisResult
            {
                SceneKind = ContextMenuSceneKind.LnkFile,
                ScopeValue = "lnkfile",
                DisplayName = "Shortcut (.lnk)",
                Reason = "The selected item is a shortcut."
            });
            return;
        }

        results.Add(new FileTypeAnalysisResult
        {
            SceneKind = ContextMenuSceneKind.CustomRegistryPath,
            ScopeValue = @"HKCR\*\shell",
            DisplayName = "All files (*)",
            Reason = "Applies to every file."
        });
        results.Add(new FileTypeAnalysisResult
        {
            SceneKind = ContextMenuSceneKind.CustomRegistryPath,
            ScopeValue = @"HKCR\AllFilesystemObjects\shell",
            DisplayName = "All filesystem objects",
            Reason = "Applies to files and folders."
        });

        if (extension == ".exe")
        {
            results.Add(new FileTypeAnalysisResult
            {
                SceneKind = ContextMenuSceneKind.ExeFile,
                ScopeValue = "exefile",
                DisplayName = "Executable files",
                Reason = "The target file is an executable."
            });
        }
        else
        {
            results.Add(new FileTypeAnalysisResult
            {
                SceneKind = ContextMenuSceneKind.CustomExtension,
                ScopeValue = extension,
                DisplayName = $"Extension {extension}",
                Reason = "Applies to this extension."
            });
        }

        using var extensionKey = Registry.ClassesRoot.OpenSubKey(extension, writable: false);
        var progId = extensionKey?.GetValue(null)?.ToString();
        if (string.IsNullOrWhiteSpace(progId))
        {
            results.Add(new FileTypeAnalysisResult
            {
                SceneKind = ContextMenuSceneKind.UnknownType,
                ScopeValue = "Unknown",
                DisplayName = "Unknown file type",
                Reason = "The extension has no ProgId/open mode."
            });
        }

        var perceivedType = extensionKey?.GetValue("PerceivedType")?.ToString();
        if (!string.IsNullOrWhiteSpace(perceivedType))
        {
            results.Add(new FileTypeAnalysisResult
            {
                SceneKind = ContextMenuSceneKind.PerceivedType,
                ScopeValue = perceivedType,
                DisplayName = $"Perceived type {perceivedType}",
                Reason = "Applies to files with this perceived type."
            });
        }
    }

    private static void AnalyzeDirectory(string directoryPath, List<FileTypeAnalysisResult> results)
    {
        results.Add(new FileTypeAnalysisResult
        {
            SceneKind = ContextMenuSceneKind.CustomRegistryPath,
            ScopeValue = @"HKCR\Folder\shell",
            DisplayName = "Folders",
            Reason = "Applies to shell folders."
        });

        if (Path.GetPathRoot(directoryPath)?.TrimEnd('\\').Equals(directoryPath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) == true)
        {
            results.Add(new FileTypeAnalysisResult
            {
                SceneKind = ContextMenuSceneKind.CustomRegistryPath,
                ScopeValue = @"HKCR\Drive\shell",
                DisplayName = "Drives",
                Reason = "The selected folder is a drive root."
            });
            return;
        }

        results.Add(new FileTypeAnalysisResult
        {
            SceneKind = ContextMenuSceneKind.CustomRegistryPath,
            ScopeValue = @"HKCR\Directory\shell",
            DisplayName = "Directories",
            Reason = "Applies to file-system directories."
        });
        results.Add(new FileTypeAnalysisResult
        {
            SceneKind = ContextMenuSceneKind.CustomRegistryPath,
            ScopeValue = @"HKCR\AllFilesystemObjects\shell",
            DisplayName = "All filesystem objects",
            Reason = "Applies to files and folders."
        });
        results.Add(new FileTypeAnalysisResult
        {
            SceneKind = ContextMenuSceneKind.DirectoryType,
            ScopeValue = "Document",
            DisplayName = "Directory type",
            Reason = "Use a specific directory type if one matches this folder."
        });
    }

    private async Task SuppressDetectionAsync(ContextMenuEntry created, CancellationToken cancellationToken)
    {
        var states = await _stateStore.LoadAsync(cancellationToken);
        var state = PersistedContextMenuState.FromEntry(created);
        state.ObservedEnabled = created.IsEnabled;
        state.DesiredEnabled = created.IsEnabled;
        state.SuppressNextDetection = true;
        state.IsPendingApproval = false;
        states[created.Id] = state;
        await _stateStore.SaveAsync(states, cancellationToken);
    }

    private string WriteShellVerb(string relativeRoot, string keyName, CreateSceneMenuItemRequest request)
    {
        keyName = CreateUniqueShellVerbKeyName(relativeRoot, keyName);
        var path = $@"HKEY_CLASSES_ROOT\{relativeRoot}\shell\{keyName}";
        if (!string.IsNullOrWhiteSpace(request.DisplayName))
        {
            Registry.SetValue(path, "MUIVerb", request.DisplayName, RegistryValueKind.String);
            _logger.LogFireAndForget(DiagnosticLogFormatter.BuildRegistryOperationLog("WriteShellVerb", path, "MUIVerb", RegistryValueKind.String, request.DisplayName, writable: true, result: "SetValue Success"));
        }

        if (!string.IsNullOrWhiteSpace(request.Icon))
        {
            Registry.SetValue(path, "Icon", request.Icon, RegistryValueKind.String);
            _logger.LogFireAndForget(DiagnosticLogFormatter.BuildRegistryOperationLog("WriteShellVerb", path, "Icon", RegistryValueKind.String, request.Icon, writable: true, result: "SetValue Success"));
        }

        SetFlag(path, "Extended", request.Extended);
        SetFlag(path, "OnlyInBrowserWindow", request.OnlyInBrowserWindow);
        SetFlag(path, "NoWorkingDirectory", request.NoWorkingDirectory);
        SetFlag(path, "NeverDefault", request.NeverDefault);
        SetFlag(path, "ShowAsDisabledIfHidden", request.ShowAsDisabledIfHidden);
        Registry.SetValue($@"{path}\command", string.Empty, request.Command ?? string.Empty, RegistryValueKind.String);
        _logger.LogFireAndForget(DiagnosticLogFormatter.BuildRegistryOperationLog("WriteShellVerb", $@"{path}\command", null, RegistryValueKind.String, request.Command ?? string.Empty, writable: true, result: "SetValue Success"));
        return keyName;
    }

    private void SetFlag(string path, string valueName, bool enabled)
    {
        if (enabled)
        {
            Registry.SetValue(path, valueName, string.Empty, RegistryValueKind.String);
            _logger.LogFireAndForget(DiagnosticLogFormatter.BuildRegistryOperationLog("SetFlag", path, valueName, RegistryValueKind.String, string.Empty, writable: true, result: "SetValue Success"));
        }
        else
        {
            _logger.LogFireAndForget($"SetFlag: Path={path}, ValueName={valueName}, Enabled=false, Operation=Skipped.");
        }
    }

    private static string? ResolveSceneRoot(ContextMenuSceneKind sceneKind, string? scopeValue)
    {
        return sceneKind switch
        {
            ContextMenuSceneKind.LnkFile => "lnkfile",
            ContextMenuSceneKind.UwpShortcut => "Launcher.ImmersiveApplication",
            ContextMenuSceneKind.ExeFile => @"SystemFileAssociations\.exe",
            ContextMenuSceneKind.UnknownType => "Unknown",
            ContextMenuSceneKind.CustomExtension => string.IsNullOrWhiteSpace(scopeValue) ? null : $@"SystemFileAssociations\{NormalizeExtension(scopeValue)}",
            ContextMenuSceneKind.PerceivedType => string.IsNullOrWhiteSpace(scopeValue) ? null : $@"SystemFileAssociations\{scopeValue.Trim()}",
            ContextMenuSceneKind.DirectoryType => string.IsNullOrWhiteSpace(scopeValue) ? null : $@"SystemFileAssociations\Directory.{scopeValue.Trim()}",
            ContextMenuSceneKind.CustomRegistryPath => NormalizeClassesRootRelativePath(scopeValue)?.Replace(@"\shell", string.Empty, StringComparison.OrdinalIgnoreCase),
            _ => null
        };
    }

    private static string NormalizeExtension(string value)
    {
        var extension = value.Trim();
        if (!extension.StartsWith('.'))
        {
            extension = "." + extension;
        }

        return extension.ToLowerInvariant();
    }

    private static string SanitizeKeyName(string value)
    {
        foreach (var invalid in new[] { '\\', '/', '*', '?', '"', '<', '>', '|' })
        {
            value = value.Replace(invalid.ToString(), string.Empty, StringComparison.Ordinal);
        }

        var keyName = value.Trim();
        return string.IsNullOrWhiteSpace(keyName) ? "CustomMenuItem" : keyName;
    }

    private string CreateUniqueShellVerbKeyName(string relativeRoot, string keyName)
    {
        var baseName = string.IsNullOrWhiteSpace(keyName) ? "CustomMenuItem" : keyName.Trim();
        var candidate = baseName;
        var index = 2;

        while (ShellVerbKeyExists(relativeRoot, candidate))
        {
            candidate = $"{baseName}-{index}";
            index++;
        }

        if (!string.Equals(candidate, keyName, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogFireAndForget($"CreateSceneMenuItemUniqueKey: RequestedKeyName={keyName}, ActualKeyName={candidate}.");
        }

        return candidate;
    }

    private static bool ShellVerbKeyExists(string relativeRoot, string keyName)
    {
        using var key = Registry.ClassesRoot.OpenSubKey($@"{relativeRoot}\shell\{keyName}", writable: false);
        return key is not null;
    }

    private static string? NormalizeClassesRootRelativePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim().Replace('/', '\\').Trim('\\');
        foreach (var prefix in new[] { @"HKEY_CLASSES_ROOT\", @"HKCR\" })
        {
            if (normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized[prefix.Length..];
                break;
            }
        }

        return normalized;
    }

    private static PipeResponse Failure(string message, Guid? operationId) => new()
    {
        Success = false,
        Message = message,
        ClientOperationId = operationId
    };
}
