using ContextMenuMgr.Contracts;
using Microsoft.Win32;

namespace ContextMenuMgr.Backend.Services;

/// <summary>Read-only references in manifests and physical Classes/CommandStore registrations.</summary>
internal sealed class Windows11ClsidImpactIndex
{
    private readonly Dictionary<Guid, List<Windows11ClsidReference>> _references = [];
    private readonly List<string> _diagnostics = [];
    private int _remainingKeys = 50000;

    public Windows11ClsidImpactIndex(IReadOnlyList<PackagedContextMenuDefinition> definitions)
    {
        if (definitions.Any(static definition => !definition.DiscoveryComplete))
            _diagnostics.Add("Package discovery was incomplete; other declarations may be unavailable.");
        foreach (var definition in definitions)
        {
            var clsid = Guid.Parse(definition.Clsid);
            if (!_references.TryGetValue(clsid, out var references))
                _references[clsid] = references = [];
            references.AddRange(definition.Verbs.Select(verb => new Windows11ClsidReference
            {
                Kind = "Package", PackageFullName = definition.Package.FullName,
                PackageDisplayName = definition.Package.DisplayName,
                PackageFamilyName = definition.Package.FamilyName, VerbId = verb.Id,
                ContextType = verb.ContextType, ComServerPath = definition.ComServer.Path,
                RegistryPath = definition.Package.ManifestPath
            }));
        }
    }

    public void ReadRegistry(string userSid, CancellationToken cancellationToken)
    {
        foreach (var (hive, path) in new[]
        {
            (RegistryHive.LocalMachine, @"SOFTWARE\Classes"),
            (RegistryHive.Users, $@"{userSid}\Software\Classes")
        })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, NativeView);
                using var classes = root.OpenSubKey(path);
                if (classes is null) { AddDiagnostic($"Missing Classes hive: {hive}\\{path}"); continue; }
                ReadClasses(classes, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { AddDiagnostic($"{hive}\\{path}: {ex.Message}"); }
        }
        try
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, NativeView);
            using var commands = machine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\CommandStore\shell");
            if (commands is not null) ReadCommands(commands, 0, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { AddDiagnostic(ex.Message); }
    }

    internal static RegistryView NativeView => Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Registry32;

    // Read physical Classes roots instead of the service account's merged HKCR view.
    internal void ReadClasses(RegistryKey classes, CancellationToken cancellationToken)
    {
        foreach (var name in classes.GetSubKeyNames())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_remainingKeys <= 0) { AddDiagnostic("Registry reference scan key budget exceeded."); return; }
            ReadChild(classes, name, key =>
            {
                ReadRegistration(key, cancellationToken);
                if (name.Equals("CLSID", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("SystemFileAssociations", StringComparison.OrdinalIgnoreCase))
                    foreach (var child in key.GetSubKeyNames())
                        ReadChild(key, child, nested => ReadRegistration(nested, cancellationToken));
            });
        }
    }

    private void ReadRegistration(RegistryKey key, CancellationToken cancellationToken)
    {
        ReadMenuLocations(key, cancellationToken);
        ReadChild(key, "Background", background => ReadMenuLocations(background, cancellationToken));
    }

    private void ReadMenuLocations(RegistryKey key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Most CLSID/ProgID keys have no menu registrations. Enumerate once rather
        // than repeatedly opening absent shell and shellex children.
        var children = key.GetSubKeyNames();
        if (children.Contains("shell", StringComparer.OrdinalIgnoreCase))
            ReadChild(key, "shell", shell => ReadCommands(shell, 0, cancellationToken));
        if (children.Contains("shellex", StringComparer.OrdinalIgnoreCase))
            ReadHandlers(key, cancellationToken);
    }

    private void ReadHandlers(RegistryKey key, CancellationToken cancellationToken)
    {
        ReadChild(key, "shellex", extensions =>
        {
            foreach (var kind in extensions.GetSubKeyNames())
                ReadChild(extensions, kind, handlers =>
                {
                    AddClassic(handlers.GetValue(null)?.ToString(), handlers.Name);
                    foreach (var handler in handlers.GetSubKeyNames())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        ReadChild(handlers, handler, registration =>
                        {
                            var value = registration.GetValue(null)?.ToString();
                            AddClassic(Guid.TryParse(value, out _) ? value : handler, registration.Name);
                        });
                    }
                });
        });
    }

    private void ReadCommands(RegistryKey shell, int depth, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (depth > 8) { AddDiagnostic($"Command nesting budget exceeded: {shell.Name}"); return; }
        foreach (var name in shell.GetSubKeyNames())
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadChild(shell, name, command =>
            {
                AddClassic(command.GetValue("ExplorerCommandHandler")?.ToString(), command.Name);
                AddClassic(command.GetValue("DelegateExecute")?.ToString(), command.Name);
                ReadChild(command, "command", execution => AddClassic(execution.GetValue("DelegateExecute")?.ToString(), execution.Name));
                ReadChild(command, "DropTarget", target => AddClassic(target.GetValue("CLSID")?.ToString(), target.Name));
                ReadChild(command, "shell", children => ReadCommands(children, depth + 1, cancellationToken));
            });
        }
    }

    private void ReadChild(RegistryKey parent, string name, Action<RegistryKey> read)
    {
        if (_remainingKeys <= 0) { AddDiagnostic("Registry reference scan key budget exceeded."); return; }
        try
        {
            using var child = parent.OpenSubKey(name);
            if (child is not null)
            {
                _remainingKeys--;
                read(child);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { AddDiagnostic($"{parent.Name}\\{name}: {ex.Message}"); }
    }

    private void AddClassic(string? value, string path)
    {
        if (Guid.TryParse(value, out var guid) && _references.TryGetValue(guid, out var references))
            references.Add(new Windows11ClsidReference { Kind = "Classic", RegistryPath = path });
    }

    private void AddDiagnostic(string message)
    {
        if (_diagnostics.Count < 8 && !_diagnostics.Contains(message)) _diagnostics.Add(message);
    }

    public Windows11ClsidImpact Get(string clsid) => new()
    {
        IsComplete = _diagnostics.Count == 0,
        References = _references[Guid.Parse(clsid)].Distinct().ToArray(),
        Diagnostics = _diagnostics.ToArray()
    };
}
