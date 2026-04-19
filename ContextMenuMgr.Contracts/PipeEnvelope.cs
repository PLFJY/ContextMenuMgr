namespace ContextMenuMgr.Contracts;

/// <summary>
/// Represents the pipe Envelope.
/// </summary>
public sealed record PipeEnvelope
{
    /// <summary>
    /// Gets or sets the message Type.
    /// </summary>
    public PipeMessageType MessageType { get; init; }

    /// <summary>
    /// Gets or sets the correlation Id.
    /// </summary>
    public Guid CorrelationId { get; init; }

    /// <summary>
    /// Gets or sets the request.
    /// </summary>
    public PipeRequest? Request { get; init; }

    /// <summary>
    /// Gets or sets the response.
    /// </summary>
    public PipeResponse? Response { get; init; }

    /// <summary>
    /// Gets or sets the notification.
    /// </summary>
    public BackendNotification? Notification { get; init; }
}
