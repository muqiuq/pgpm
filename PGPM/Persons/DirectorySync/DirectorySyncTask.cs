using System.Threading.Channels;

namespace PGPM.Persons.DirectorySync;

/// <summary>
/// The directory sync in the background (docs/03 §8.2, GGITSV's worker and signal): every 5 minutes it looks whether a run
/// is due (<c>Directory:IntervalHours</c> after the last start), and <i>Run now</i> wakes it at once. At most one wake-up
/// waits; more are the same one. A singleton, hosted with the other background tasks; the tests start it by hand.
/// </summary>
public sealed class DirectorySyncTask(IServiceScopeFactory scopes, ILogger<DirectorySyncTask> logger) : BackgroundService
{
    public static readonly TimeSpan CheckEvery = TimeSpan.FromMinutes(5);

    private readonly Channel<bool> signal = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    /// <summary>0: nothing asked; 1: a run; 2: a full run.</summary>
    private int requested;

    private int running;

    /// <summary>A run is under way.</summary>
    public bool Running => Volatile.Read(ref running) == 1;

    /// <summary>A <i>Run now</i> waits for its turn.</summary>
    public bool Pending => Volatile.Read(ref requested) != 0;

    /// <summary><i>Run now</i>: the task wakes and runs (a full run if asked, or due).</summary>
    public void RequestRun(bool full)
    {
        var wanted = full ? 2 : 1;
        int current;
        do
        {
            current = Volatile.Read(ref requested);
        }
        while (current < wanted && Interlocked.CompareExchange(ref requested, wanted, current) != current);
        signal.Writer.TryWrite(true);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var sync = scope.ServiceProvider.GetRequiredService<DirectorySyncService>();
                    var asked = Interlocked.Exchange(ref requested, 0);
                    if ((asked != 0 && sync.Credentials() is not null) || await sync.IsDueAsync(stoppingToken))
                    {
                        Volatile.Write(ref running, 1);
                        try
                        {
                            await sync.RunAsync(asked == 2 ? true : null, stoppingToken);
                        }
                        finally
                        {
                            Volatile.Write(ref running, 0);
                        }
                    }
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    logger.LogWarning(e, "Directory sync failed");
                }
                await WaitAsync(CheckEvery, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    private async Task WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timer.CancelAfter(timeout);
        try
        {
            await signal.Reader.ReadAsync(timer.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The timeout: look anyway.
        }
    }
}
