using ContextMenuMgr.Contracts;

namespace ContextMenuMgr.Backend.Services;

/// <summary>Resolves the authenticated user's context lazily for each route that requires it.</summary>
internal sealed class BackendRequestContext(Func<BackendUserContext?> resolveTrustedUser, FileLogger logger)
{
    internal async Task<BackendUserContext?> ResolveFrontendUserContextAsync(CancellationToken cancellationToken)
    {
        var userContext = resolveTrustedUser();
        if (userContext is null)
        {
            MenuDiscoveryCoverage.MarkIncomplete();
            await logger.LogAsync(
                RuntimeLogLevel.Warning,
                "Frontend user context could not be resolved for user-scoped request.",
                cancellationToken);
        }

        if (userContext is not null)
        {
            using var frontendHive = Microsoft.Win32.Registry.Users.OpenSubKey(userContext.Sid, writable: false);
            if (frontendHive is null) MenuDiscoveryCoverage.MarkIncomplete();
        }
        return userContext;
    }

    // Resolve only for context-aware special menus. OpenWith requires the authenticated caller.
    // Do not call this from ordinary catalog, Win11, registry protection,
    // auto-start, file-type scene, or restart routes.
    internal async Task<BackendUserContext?> ResolveSpecialMenuUserContextAsync(CancellationToken cancellationToken)
    {
        var userContext = resolveTrustedUser();
        if (userContext is null)
        {
            MenuDiscoveryCoverage.MarkIncomplete();
            await logger.LogAsync(RuntimeLogLevel.Warning, "Context-aware special menu request could not resolve frontend user context.", cancellationToken);
        }

        return userContext;
    }
}
