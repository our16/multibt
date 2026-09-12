using System.IO;
using System.IO.Pipes;

namespace MultiBT.App.Services;

/// <summary>
/// Ensures only one instance of the application is running.
/// </summary>
/// <remarks>
/// <para>
/// Uses a named mutex with a pipe-based signaling mechanism. When a second instance starts,
/// it signals the first instance via a named pipe and then exits. The first instance receives
/// the signal and brings its window to the foreground.
/// </para>
/// </remarks>
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = "MultiBT_SingleInstance";
    private const string PipeName = "MultiBT_SingleInstance_Pipe";
    private const string ActivateCommand = "ACTIVATE";

    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _listenerTask;
    private readonly bool _isFirstInstance;
    private bool _disposed;

    /// <summary>
    /// Raised when a second instance requests activation.
    /// </summary>
    public event EventHandler? ActivateRequested;

    /// <summary>
    /// Initializes the single instance mechanism.
    /// </summary>
    /// <param name="isFirstInstance">Set to true if this is the first instance.</param>
    public SingleInstance(out bool isFirstInstance)
    {
        _mutex = new Mutex(true, MutexName, out bool createdNew);
        isFirstInstance = createdNew;

        // Cached, because the naive `_mutex.WaitOne(0)` implementation of this is wrong twice
        // over: the first instance already OWNS the mutex (initiallyOwned: true), so WaitOne(0)
        // from a different thread returns false and reports "not first" for the first instance —
        // and each successful call increments the recursion count, so one ReleaseMutex can no
        // longer release it.
        _isFirstInstance = createdNew;

        if (!isFirstInstance)
        {
            // Signal the first instance and exit
            SignalFirstInstance();
            _listenerTask = Task.CompletedTask;
        }
        else
        {
            // Start listening for second instances
            _listenerTask = ListenForActivationAsync(_cts.Token);
        }
    }

    /// <summary>
    /// Returns true if this is the first instance. Settled once in the constructor.
    /// </summary>
    public bool IsFirstInstance => _isFirstInstance;

    private void SignalFirstInstance()
    {
        try
        {
            using var client = new NamedPipeClientStream(
                ".",
                PipeName,
                PipeDirection.Out,
                PipeOptions.None);

            client.Connect(1000); // 1 second timeout
            using var writer = new StreamWriter(client);
            writer.Write(ActivateCommand);
            writer.Flush();
        }
        catch (Exception)
        {
            // First instance might have exited - ignore
        }
    }

    private async Task ListenForActivationAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.None);

                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

                using var reader = new StreamReader(server);
                string? command = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);

                if (command == ActivateCommand)
                {
                    ActivateRequested?.Invoke(this, EventArgs.Empty);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                // Pipe error - retry after a delay
                await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();
        _cts.Dispose();

        // Only the instance that actually acquired the mutex may release it. Releasing from the
        // second instance throws; releasing when ReleaseMutex was never matched to an owning
        // WaitOne leaves the mutex owned, which would block the next launch.
        if (_isFirstInstance)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (Exception)
            {
                // Already released, or not owned by this thread.
            }
        }

        _mutex.Dispose();
    }
}
