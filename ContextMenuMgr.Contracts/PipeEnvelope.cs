namespace ContextMenuMgr.Contracts;

public sealed record PipeEnvelope
{
    public PipeMessageType MessageType { get; init; }

    public Guid CorrelationId { get; init; }

    public PipeRequest? Request { get; init; }

    public PipeResponse? Response { get; init; }

    public BackendNotification? Notification { get; init; }
}
