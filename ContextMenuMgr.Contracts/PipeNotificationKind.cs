namespace ContextMenuMgr.Contracts;

/// <summary>
/// Defines the available pipe Notification Kind values.
/// </summary>
public enum PipeNotificationKind
{
    ItemDetected,
    ItemStateChanged,
    ServiceMessage,
    ServiceStopping
}
