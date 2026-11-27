using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using PGPM.Lib.Helper;
using PGPM.Sync;
using static PGPM.Tests.Sync.SyncKit;

namespace PGPM.Tests.Sync;

/// <summary>Runs only with <c>PGPM_LOAD=1</c> (docs/07): the default test run leaves the load tests out.</summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class LoadTestAttribute : ConditionBaseAttribute
{
    public LoadTestAttribute()
        : base(ConditionMode.Include) =>
        IgnoreMessage = "A load test: PGPM_LOAD=1 dotnet test --project PGPM.Tests/PGPM.Tests.csproj --filter TestCategory=Load";

    public override bool IsConditionMet => Environment.GetEnvironmentVariable("PGPM_LOAD") == "1";

    public override string GroupName => "Load";
}

/// <summary>
/// The live feed under load (docs/05 §3 phase 7, docs/07 §1): 50 live connections of 50 users in one group receive the
/// notes 5 others push (each shared with the group), at a steady rate. Measures the delivery latency – from sending the
/// push to a reader receiving the row on its WebSocket –, the push's own time, the server's CPU and how many pulls and
/// table reads the live feed made. Clients and server share the process (TestServer), so the CPU includes the clients.
/// <para>
/// <c>PGPM_LOAD=1 PGPM_LOAD_PUSHES=200 PGPM_LOAD_RATE=20 dotnet test --project PGPM.Tests/PGPM.Tests.csproj --filter TestCategory=Load</c>
/// </para>
/// </summary>
[TestClass]
[TestCategory("Load")]
public sealed class LiveLoadTests
{
    private const string Notes = "Notes:Create";

    public TestContext TestContext { get; set; } = null!;

    private static int Setting(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value > 0 ? value : fallback;

    [TestMethod]
    [LoadTest]
    public async Task FiftyLiveClientsReceiveWhatFiveOthersPush()
    {
        var readers = Setting("PGPM_LOAD_READERS", 50);
        var writers = Setting("PGPM_LOAD_WRITERS", 5);
        var pushes = Setting("PGPM_LOAD_PUSHES", 200);
        var rate = Setting("PGPM_LOAD_RATE", 20);
        // PGPM_LOAD_BASELINE=1: every pull reads every table, as before docs/07's optimisation.
        var baseline = Environment.GetEnvironmentVariable("PGPM_LOAD_BASELINE") == "1";
        SyncReader.SkipUnchangedTables = !baseline;
        try
        {
            await RunAsync(readers, writers, pushes, rate, baseline);
        }
        finally
        {
            SyncReader.SkipUnchangedTables = true;
        }
    }

    private async Task RunAsync(int readers, int writers, int pushes, int rate, bool baseline)
    {
        using var app = new TestApp(new Dictionary<string, string?> { ["Sync:Live:AckTimeoutSeconds"] = "60" });
        var group = await GroupAsync(app, "load");

        var sockets = new List<LiveSocket>();
        var received = new ConcurrentDictionary<(int Reader, string Id), long>();
        var clock = Stopwatch.StartNew();
        var readerTasks = new List<Task>();
        using var stop = new CancellationTokenSource();
        for (var r = 0; r < readers; r++)
        {
            var user = await UserAsync(app, "reader", group);
            var client = new SyncClient(app.ClientFor(user, Notes));
            await client.SyncAllAsync();
            var socket = await LiveSocket.ConnectAsync(app, user, Notes);
            Assert.AreEqual("welcome", (await socket.HelloAsync(client)).Str("type"));
            sockets.Add(socket);
            var reader = r;
            readerTasks.Add(Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    var message = await socket.NextAsync(TimeSpan.FromSeconds(1));
                    if (message is null)
                    {
                        if (socket.IsClosed)
                        {
                            return;
                        }
                        continue;
                    }
                    if (message.Str("type") != "changes")
                    {
                        continue;
                    }
                    var at = clock.ElapsedTicks;
                    foreach (var row in message["changes"]?["items"] as JsonArray ?? [])
                    {
                        received.TryAdd((reader, row.Str("id")!), at);
                    }
                    await socket.AckAsync(message);
                }
            }));
        }
        var writerClients = new List<SyncClient>();
        for (var w = 0; w < writers; w++)
        {
            var user = await UserAsync(app, "writer", group);
            var client = new SyncClient(app.ClientFor(user, Notes));
            await client.SyncAsync(pull: false);
            writerClients.Add(client);
        }

        var sent = new ConcurrentDictionary<string, long>();
        var pushTimes = new ConcurrentBag<double>();
        var counters = SyncReader.Counters.Snapshot();
        var process = Process.GetCurrentProcess();
        var cpuBefore = process.TotalProcessorTime;
        var wallBefore = clock.Elapsed;
        var inFlight = new List<Task>();
        var interval = TimeSpan.FromSeconds(1.0 / rate);
        for (var p = 0; p < pushes; p++)
        {
            var writer = writerClients[p % writers];
            var id = Ids.New();
            var batch = Ids.New();
            inFlight.Add(Task.Run(async () =>
            {
                var started = clock.ElapsedTicks;
                sent[id.ToString()] = started;
                var answer = await writer.SyncAsync([Insert("items", id, Note($"Load {id}"), batch), Insert("item_shares", Ids.New(), Share(id, group, "edit"), batch)],
                    pull: false, apply: false);
                Assert.AreEqual("accepted", answer.Arr("results")[0].Str("status"), answer.ToJsonString());
                pushTimes.Add(Milliseconds(clock.ElapsedTicks - started));
            }));
            var due = wallBefore + interval * (p + 1);
            if (due > clock.Elapsed)
            {
                await Task.Delay(due - clock.Elapsed);
            }
        }
        await Task.WhenAll(inFlight);
        var expected = readers * pushes;
        var deadline = clock.Elapsed + TimeSpan.FromSeconds(120);
        while (received.Count(r => sent.ContainsKey(r.Key.Id)) < expected && clock.Elapsed < deadline)
        {
            await Task.Delay(50);
        }
        var wall = clock.Elapsed - wallBefore;
        process.Refresh();
        var cpu = process.TotalProcessorTime - cpuBefore;
        var reads = SyncReader.Counters.Snapshot() - counters;
        await stop.CancelAsync();
        foreach (var socket in sockets)
        {
            await socket.DisposeAsync();
        }
        await Task.WhenAll(readerTasks);

        var latencies = received.Where(r => sent.ContainsKey(r.Key.Id)).Select(r => Milliseconds(r.Value - sent[r.Key.Id])).Order().ToList();
        var pushList = pushTimes.Order().ToList();
        var report = $"""
            {(baseline ? "baseline (every table read)" : "tables without changes skipped")}: {readers} live readers, {writers} writers, {pushes} pushes at {rate}/s ({Environment.ProcessorCount} cores)
            delivered {latencies.Count} of {expected} in {wall.TotalSeconds:0.0} s
            delivery latency ms: p50 {Percentile(latencies, 50):0} · p95 {Percentile(latencies, 95):0} · p99 {Percentile(latencies, 99):0} · max {latencies.LastOrDefault():0}
            push ms: p50 {Percentile(pushList, 50):0} · p95 {Percentile(pushList, 95):0}
            CPU {cpu.TotalSeconds:0.0} s in {wall.TotalSeconds:0.0} s = {cpu.TotalSeconds / wall.TotalSeconds * 100:0} % of one core
            live/pull reads: {reads.Pulls} pulls, {reads.TableReads} table reads ({(reads.Pulls == 0 ? 0 : (double)reads.TableReads / reads.Pulls):0.0} per pull), {reads.Skipped} tables skipped
            """;
        TestContext.WriteLine(report);
        Console.WriteLine(report);
        Assert.AreEqual(expected, latencies.Count, report);
    }

    private static double Milliseconds(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    private static double Percentile(IReadOnlyList<double> sorted, int p) =>
        sorted.Count == 0 ? 0 : sorted[Math.Clamp((int)Math.Ceiling(p / 100.0 * sorted.Count) - 1, 0, sorted.Count - 1)];
}
