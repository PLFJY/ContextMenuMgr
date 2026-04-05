using System.Globalization;
using System.Resources;

namespace ContextMenuMgr.Frontend.Resources;

internal static class Strings
{
    private static readonly ResourceManager ResourceManagerInstance =
        new("ContextMenuMgr.Frontend.Resources.Strings", typeof(Strings).Assembly);

    public static ResourceManager ResourceManager => ResourceManagerInstance;

    public static CultureInfo? Culture { get; set; }
}
