using ContextMenuMgr.Backend.Services;
using ContextMenuMgr.Contracts;
using Xunit;

namespace ContextMenuMgr.Tests;

public sealed class NamedPipeBackendServerResponseCorrelationTests
{
    [Fact]
    public void HandlerResponseWithoutOperationId_InheritsRequestOperationId()
    {
        var operationId = Guid.NewGuid();
        var request = new PipeRequest
        {
            Command = PipeCommand.SetEnabled,
            ClientOperationId = operationId
        };
        var response = new PipeResponse
        {
            Success = true,
            Item = new ContextMenuEntry { Id = "regular-item" }
        };

        var correlated = NamedPipeBackendServer.CorrelateResponseWithRequest(request, response);

        Assert.Equal(operationId, correlated.ClientOperationId);
        Assert.Null(response.ClientOperationId);
    }

    [Fact]
    public void HandlerResponseWithOperationId_IsNotOverwritten()
    {
        var requestOperationId = Guid.NewGuid();
        var responseOperationId = Guid.NewGuid();
        var request = new PipeRequest
        {
            Command = PipeCommand.SetEnabled,
            ClientOperationId = requestOperationId
        };
        var response = new PipeResponse
        {
            Success = true,
            ClientOperationId = responseOperationId
        };

        var correlated = NamedPipeBackendServer.CorrelateResponseWithRequest(request, response);

        Assert.Same(response, correlated);
        Assert.Equal(responseOperationId, correlated.ClientOperationId);
    }
}
