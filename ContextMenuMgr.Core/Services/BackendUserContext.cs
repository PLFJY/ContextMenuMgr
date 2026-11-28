using System.IO;
using System.Security.Principal;

namespace ContextMenuMgr.Backend.Services;

/// <summary>
/// Describes the interactive user that initiated a backend pipe request.
/// </summary>
public sealed record BackendUserContext(
    SecurityIdentifier UserSid,
    string UserName,
    string ProfilePath,
    string LocalAppDataPath,
    string RoamingAppDataPath,
    int? SessionId)
{
    public BackendUserContext(
        string Sid,
        string UserName,
        string ProfilePath,
        string LocalAppDataPath,
        string RoamingAppDataPath,
        int? SessionId)
        : this(new SecurityIdentifier(Sid), UserName, ProfilePath, LocalAppDataPath, RoamingAppDataPath, SessionId)
    {
    }

    public string Sid
    {
        get => UserSid.Value;
        init => UserSid = new SecurityIdentifier(value);
    }

    public string GetWinXPath() => Path.Combine(LocalAppDataPath, @"Microsoft\Windows\WinX");

    public string GetSendToPath() => Path.Combine(RoamingAppDataPath, @"Microsoft\Windows\SendTo");
}
