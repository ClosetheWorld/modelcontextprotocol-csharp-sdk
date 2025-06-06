using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol.Messages;
using ModelContextProtocol.Utils;
using ModelContextProtocol.Utils.Json;
using System.Text;
using System.Text.Json;

namespace ModelContextProtocol.Protocol.Transport;

/// <summary>
/// Provides an <see cref="ITransport"/> implemented using a pair of input and output streams.
/// </summary>
/// <remarks>
/// The <see cref="StreamServerTransport"/> class implements bidirectional JSON-RPC messaging over arbitrary
/// streams, allowing MCP communication with clients through various I/O channels such as network sockets,
/// memory streams, or pipes.
/// </remarks>
public class StreamServerTransport : TransportBase
{
    private static readonly byte[] s_newlineBytes = "\n"u8.ToArray();

    private readonly ILogger _logger;

    private readonly TextReader _inputReader;
    private readonly Stream _outputStream;

    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _shutdownCts = new();

    private readonly Task _readLoopCompleted;
    private int _disposed = 0;

    /// <summary>
    /// Initializes a new instance of the <see cref="StreamServerTransport"/> class with explicit input/output streams.
    /// </summary>
    /// <param name="inputStream">The input <see cref="Stream"/> to use as standard input.</param>
    /// <param name="outputStream">The output <see cref="Stream"/> to use as standard output.</param>
    /// <param name="serverName">Optional name of the server, used for diagnostic purposes, like logging.</param>
    /// <param name="loggerFactory">Optional logger factory used for logging employed by the transport.</param>
    /// <exception cref="ArgumentNullException"><paramref name="inputStream"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="outputStream"/> is <see langword="null"/>.</exception>
    public StreamServerTransport(Stream inputStream, Stream outputStream, string? serverName = null, ILoggerFactory? loggerFactory = null)
        : base(serverName is not null ? $"Server (stream) ({serverName})" : "Server (stream)", loggerFactory)
    {
        Throw.IfNull(inputStream);
        Throw.IfNull(outputStream);

        _logger = loggerFactory?.CreateLogger(GetType()) ?? NullLogger.Instance;

        _inputReader = new StreamReader(inputStream, Encoding.UTF8);
        _outputStream = outputStream;

        SetConnected(true);
        _readLoopCompleted = Task.Run(ReadMessagesAsync, _shutdownCts.Token);
    }

    /// <inheritdoc/>
    public override async Task SendMessageAsync(IJsonRpcMessage message, CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
        {
            throw new McpTransportException("Transport is not connected");
        }

        using var _ = await _sendLock.LockAsync(cancellationToken).ConfigureAwait(false);

        string id = "(no id)";
        if (message is IJsonRpcMessageWithId messageWithId)
        {
            id = messageWithId.Id.ToString();
        }

        try
        {
            await JsonSerializer.SerializeAsync(_outputStream, message, McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(IJsonRpcMessage)), cancellationToken).ConfigureAwait(false);
            await _outputStream.WriteAsync(s_newlineBytes, cancellationToken).ConfigureAwait(false);
            await _outputStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogTransportSendFailed(Name, id, ex);
            throw new McpTransportException("Failed to send message", ex);
        }
    }

    private async Task ReadMessagesAsync()
    {
        CancellationToken shutdownToken = _shutdownCts.Token;
        try
        {
            LogTransportEnteringReadMessagesLoop(Name);

            while (!shutdownToken.IsCancellationRequested)
            {
                var line = await _inputReader.ReadLineAsync(shutdownToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(line))
                {
                    if (line is null)
                    {
                        LogTransportEndOfStream(Name);
                        break;
                    }

                    continue;
                }

                LogTransportReceivedMessageSensitive(Name, line);

                try
                {
                    if (JsonSerializer.Deserialize(line, McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(IJsonRpcMessage))) is IJsonRpcMessage message)
                    {
                        string messageId = "(no id)";
                        if (message is IJsonRpcMessageWithId messageWithId)
                        {
                            messageId = messageWithId.Id.ToString();
                        }

                        LogTransportReceivedMessage(Name, messageId);
                        await WriteMessageAsync(message, shutdownToken).ConfigureAwait(false);
                        LogTransportMessageWritten(Name, messageId);
                    }
                    else
                    {
                        LogTransportMessageParseUnexpectedTypeSensitive(Name, line);
                    }
                }
                catch (JsonException ex)
                {
                    if (Logger.IsEnabled(LogLevel.Trace))
                    {
                        LogTransportMessageParseFailedSensitive(Name, line, ex);
                    }
                    else
                    {
                        LogTransportMessageParseFailed(Name, ex);
                    }

                    // Continue reading even if we fail to parse a message
                }
            }
        }
        catch (OperationCanceledException)
        {
            LogTransportReadMessagesCancelled(Name);
        }
        catch (Exception ex)
        {
            LogTransportReadMessagesFailed(Name, ex);
        }
        finally
        {
            SetConnected(false);
        }
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            LogTransportShuttingDown(Name);

            // Signal to the stdin reading loop to stop.
            await _shutdownCts.CancelAsync().ConfigureAwait(false);
            _shutdownCts.Dispose();

            // Dispose of stdin/out. Cancellation may not be able to wake up operations
            // synchronously blocked in a syscall; we need to forcefully close the handle / file descriptor.
            _inputReader?.Dispose();
            _outputStream?.Dispose();

            // Make sure the work has quiesced.
            try
            {
                await _readLoopCompleted.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                LogTransportCleanupReadTaskFailed(Name, ex);
            }
        }
        finally
        {
            SetConnected(false);
            LogTransportShutDown(Name);
        }

        GC.SuppressFinalize(this);
    }
}
