using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace ContextMenuMgr.Contracts;

/// <summary>
/// Sends bounded requests to the frontend control pipe.
/// </summary>
public static class FrontendControlPipeClient
{
    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan DefaultStartupGracePeriod = TimeSpan.FromMilliseconds(2500);
    public static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromMilliseconds(100);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static Task<bool> TrySendAsync(
        FrontendControlRequest request,
        CancellationToken cancellationToken)
        => TrySendAsync(
            request,
            PipeConstants.FrontendControlPipeName,
            DefaultRequestTimeout,
            cancellationToken);

    public static async Task<bool> TrySendAsync(
        FrontendControlRequest request,
        string pipeName,
        TimeSpan requestTimeout,
        CancellationToken cancellationToken)
    {
        try
        {
            using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            requestCts.CancelAfter(requestTimeout);
            var requestToken = requestCts.Token;
            var connectTimeoutMilliseconds = Math.Max(
                1,
                Math.Min(500, checked((int)Math.Ceiling(requestTimeout.TotalMilliseconds))));

            using var stream = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            await stream.ConnectAsync(connectTimeoutMilliseconds, requestToken).ConfigureAwait(false);

            using var reader = new StreamReader(
                stream,
                new UTF8Encoding(false),
                detectEncodingFromByteOrderMarks: false,
                leaveOpen: true);
            using var writer = new StreamWriter(
                stream,
                new UTF8Encoding(false),
                leaveOpen: true)
            {
                AutoFlush = true
            };

            var payload = JsonSerializer.Serialize(request, JsonOptions);
            await writer.WriteLineAsync(payload.AsMemory(), requestToken).ConfigureAwait(false);
            var line = await reader.ReadLineAsync(requestToken).ConfigureAwait(false);
            if (line is null)
            {
                return false;
            }

            var response = JsonSerializer.Deserialize<FrontendControlResponse>(line, JsonOptions);
            return response?.Success == true;
        }
        catch
        {
            return false;
        }
    }

    public static Task<bool> TrySendWithStartupRetryAsync(
        FrontendControlRequest request,
        CancellationToken cancellationToken)
        => TrySendWithStartupRetryAsync(
            request,
            PipeConstants.FrontendControlPipeName,
            DefaultStartupGracePeriod,
            DefaultRequestTimeout,
            DefaultRetryDelay,
            cancellationToken);

    public static async Task<bool> TrySendWithStartupRetryAsync(
        FrontendControlRequest request,
        string pipeName,
        TimeSpan startupGracePeriod,
        TimeSpan requestTimeout,
        TimeSpan retryDelay,
        CancellationToken cancellationToken,
        Action<int, bool>? attemptCompleted = null)
    {
        using var startupCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        startupCts.CancelAfter(startupGracePeriod);
        var startupToken = startupCts.Token;
        var stopwatch = Stopwatch.StartNew();
        var attempt = 0;

        while (!startupToken.IsCancellationRequested)
        {
            attempt++;
            var succeeded = await TrySendAsync(
                    request,
                    pipeName,
                    requestTimeout,
                    startupToken)
                .ConfigureAwait(false);
            attemptCompleted?.Invoke(attempt, succeeded);
            if (succeeded)
            {
                return true;
            }

            var remaining = startupGracePeriod - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero || startupToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                await Task.Delay(
                        remaining < retryDelay ? remaining : retryDelay,
                        startupToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        return false;
    }
}
