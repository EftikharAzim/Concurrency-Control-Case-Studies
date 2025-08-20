using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

// --- Data contracts ---
record ImageJob(string FileName);

record Result(ImageJob Job, Exception? Error);

class Program
{
    // --- Metrics (atomic counters) ---
    static long JobEnq,
        JobDeq,
        SaveEnq,
        SaveDeq;
    static long ProcessOk,
        ProcessFail,
        SaveOk,
        SaveFail;

    static void Log(string tag, string msg, [CallerLineNumber] int line = 0)
    {
        int tid = Thread.CurrentThread.ManagedThreadId;
        int? task = Task.CurrentId;
        Console.WriteLine(
            $"[{DateTime.UtcNow:O}] [T{tid:D2}/Task{task?.ToString() ?? "-"}] [{tag}] [L{line}] {msg}"
        );
    }

    static async Task Main(string[] args)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (s, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
        var ct = cts.Token;
        if (
            Environment.GetEnvironmentVariable("APP_TIMEOUT_SECONDS") is { } env
            && int.TryParse(env, out var secs)
        )
            cts.CancelAfter(TimeSpan.FromSeconds(secs));

        int cpu = Environment.ProcessorCount;
        int procW = cpu;
        int saveW = cpu * 2;

        var jobQ = Channel.CreateBounded<ImageJob>(procW * 2);
        var saveQ = Channel.CreateBounded<ImageJob>(saveW * 2);
        var results = Channel.CreateUnbounded<Result>();

        Log("BOOT", $"cpu={cpu} procW={procW} saveW={saveW}");

        // Processor workers
        var procs = Enumerable
            .Range(0, procW)
            .Select(id =>
                Task.Run(
                    async () =>
                    {
                        Log($"PROC#{id}", "start");
                        await foreach (var job in jobQ.Reader.ReadAllAsync(ct))
                        {
                            Log($"PROC#{id}", $"dequeue {job.FileName}");
                            Interlocked.Increment(ref JobDeq);
                            try
                            {
                                Log($"PROC#{id}", $"process {job.FileName} begin");
                                await ProcessImage(job, ct);
                                Log($"PROC#{id}", $"process {job.FileName} done; enqueue saveQ");
                                await saveQ.Writer.WriteAsync(job, ct);
                                Log($"PROC#{id}", $"enqueued {job.FileName}");
                                Interlocked.Increment(ref SaveEnq);
                                Interlocked.Increment(ref ProcessOk);
                            }
                            catch (OperationCanceledException)
                            {
                                Log($"PROC#{id}", "cancelled");
                                break;
                            }
                            catch (Exception ex)
                            {
                                Interlocked.Increment(ref ProcessFail);
                                await results.Writer.WriteAsync(new(job, ex), ct);
                            }
                        }
                        Log($"PROC#{id}", "end");
                    },
                    ct
                )
            )
            .ToArray();

        // Saver workers
        var savers = Enumerable
            .Range(0, saveW)
            .Select(id =>
                Task.Run(
                    async () =>
                    {
                        Log($"SAVER#{id}", "start");
                        await foreach (var job in saveQ.Reader.ReadAllAsync(ct))
                        {
                            Log($"SAVER#{id}", $"dequeue {job.FileName}");
                            Interlocked.Increment(ref SaveDeq);
                            try
                            {
                                Log($"SAVER#{id}", $"save {job.FileName} begin");
                                await RetryAsync(
                                    tok => SaveImage(job, tok),
                                    1,
                                    TimeSpan.FromMilliseconds(200),
                                    ct
                                );
                                Log($"SAVER#{id}", $"save {job.FileName} ok");
                                Interlocked.Increment(ref SaveOk);
                                await results.Writer.WriteAsync(new(job, null), ct);
                            }
                            catch (OperationCanceledException)
                            {
                                Log($"SAVER#{id}", "cancelled");
                                break;
                            }
                            catch (Exception ex)
                            {
                                Interlocked.Increment(ref SaveFail);
                                await results.Writer.WriteAsync(new(job, ex), ct);
                            }
                        }
                        Log($"SAVER#{id}", "end");
                    },
                    ct
                )
            )
            .ToArray();

        // Producer
        Log("PRODUCER", "start");
        var files =
            args.Length > 0
                ? args
                : new[]
                {
                    "wedding.jpg",
                    "birthday.jpg",
                    "vacation.jpg",
                    "nature1.jpg",
                    "fashion.jpg",
                };
        _ = Task.Run(
            async () =>
            {
                foreach (var f in files)
                {
                    Log("PRODUCER", $"enqueue {f}");
                    await jobQ.Writer.WriteAsync(new(f), ct);
                    Log("PRODUCER", $"enqueued {f}");
                    Interlocked.Increment(ref JobEnq);
                }
                Log("PRODUCER", "complete; close writer");
                jobQ.Writer.TryComplete();
            },
            ct
        );

        // Reporter
        Log("REPORTER", "start");
        var reporter = Task.Run(
            async () =>
            {
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(1000, ct);
                    Log(
                        "METRIC",
                        $"jobQ={JobEnq - JobDeq} saveQ={SaveEnq - SaveDeq} pOK={ProcessOk} pFail={ProcessFail} sOK={SaveOk} sFail={SaveFail}"
                    );
                }
                Log("REPORTER", "end");
            },
            ct
        );

        // Results
        Log("RESULTS", "consumer start");
        var resTask = Task.Run(
            async () =>
            {
                await foreach (var r in results.Reader.ReadAllAsync(ct))
                    Console.WriteLine(
                        r.Error is null
                            ? $"✅ {r.Job.FileName} saved"
                            : $"❌ {r.Job.FileName} failed: {r.Error.Message}"
                    );
                Log("RESULTS", "consumer end");
            },
            ct
        );

        // Shutdown choreography
        Log("SHUTDOWN", "await processors");
        await Task.WhenAll(procs);
        Log("SHUTDOWN", "close saveQ");
        saveQ.Writer.TryComplete();
        Log("SHUTDOWN", "await savers");
        await Task.WhenAll(savers);
        Log("SHUTDOWN", "close results writer");
        results.Writer.TryComplete();

        cts.Cancel();
        await Task.WhenAny(reporter, Task.Delay(500));
        try
        {
            await resTask;
        }
        catch (OperationCanceledException)
        {
            Log("RESULTS", "consumer cancelled");
        }

        Log(
            "SUMMARY",
            $"files={files.Length} "
                + $"pOK={Interlocked.Read(ref ProcessOk)} pFail={Interlocked.Read(ref ProcessFail)} "
                + $"sOK={Interlocked.Read(ref SaveOk)} sFail={Interlocked.Read(ref SaveFail)}"
        );
        Log("DONE", "exit");
    }

    static async Task ProcessImage(ImageJob job, CancellationToken ct)
    {
        foreach (var s in new[] { "thumbnail", "medium", "large" })
        {
            await Task.Delay(300, ct);
            if (Random.Shared.NextDouble() < 0.05)
                throw new($"resize {s} failed");
        }
        await Task.Delay(200, ct);
        if (Random.Shared.NextDouble() < 0.05)
            throw new("watermark failed");
    }

    static async Task SaveImage(ImageJob job, CancellationToken ct)
    {
        await Task.Delay(500, ct);
        if (Random.Shared.NextDouble() < 0.1)
            throw new("save failed");
    }

    static async Task RetryAsync(
        Func<CancellationToken, Task> action,
        int max,
        TimeSpan baseDelay,
        CancellationToken ct
    )
    {
        for (int i = 1; ; i++)
        {
            Log("RETRY", $"attempt={i}");
            try
            {
                await action(ct);
                Log("RETRY", "success");
                return;
            }
            catch when (i < max)
            {
                var jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(0, 200));
                var d =
                    TimeSpan.FromMilliseconds(baseDelay.TotalMilliseconds * Math.Pow(2, i - 1))
                    + jitter;
                Log("RETRY", $"delay {d.TotalMilliseconds:F0}ms");
                await Task.Delay(d, ct);
            }
        }
    }
}
