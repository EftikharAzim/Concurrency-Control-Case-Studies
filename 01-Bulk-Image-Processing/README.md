# Case 01 – Bulk Image Processing

> **Scenario**  
> Photographers dump hundreds of huge photos at once.  
> Each photo must become three sizes (thumbnail 🖼️, medium 📷, large 🖼️) and get a watermark before we save it.  
> The website must still feel snappy while all this happens.

---

## 1 Goals & Non‑negotiables

| What                                                    | Why it matters                               |
| ------------------------------------------------------- | -------------------------------------------- |
| 📷 Every photo is handled **once and only once**        | No duplicates, no missing shots.             |
| 🔄 Steps stay in **order** – resize ➜ watermark ➜ save  | A watermark on the wrong size looks bad.     |
| 🚀 **99 %** of photos finish in < *X* seconds           | Keeps upload spinner short.                  |
| 😌 The server must not run out of RAM or CPU            | Avoid a white‑screen meltdown.               |
| ♻️ **3 retries max** per failed step, with back‑off     | Flaky storage shouldn’t ruin a whole upload. |
| 🕵️ Logs always show **thread‑ID, task‑ID, line‑number** | Debugging is painless.                       |

---

## 2 Things That Can Bite Us (Hazards)

- **Race conditions** – two threads touching the same counter.  
  _Fix –_ use `Interlocked.*`.
- **Deadlocks** – everyone waits on everyone.  
  _Fix –_ channels only flow one way; no circular waits.
- **Memory blow‑up** – huge bursts fill the queue.  
  _Fix –_ bounded channels (they block writers instead of gobbling RAM).
- **Torn reads** – 64‑bit counters read half‑updated.  
  _Fix –_ `Interlocked.Read` for metrics.
- **Infinite retries** – stuck file loops forever.  
  _Fix –_ give up after 3 tries.

---

## 3 Design in Plain English

1. **Inbox channel** – where uploads land.
2. **Processor workers (CPU‑bound)** – one per CPU core.  
   They run heavy math: resize + watermark, then drop the job into…
3. **Saver workers (I/O‑bound)** – about 2× CPU cores.  
   They push bytes to disk/S3; if a save fails they retry up to 3 times.
4. **Results channel** – success/fail messages for a UI progress bar.
5. **Reporter** – prints queue depths & success counts every second.
6. **Structured Log Helper** – adds timestamp, thread, task, line.

💡 _Back‑pressure_ – if saver workers are slow the processors block, which slows uploads automatically instead of crashing memory.

---

## 4 Math Check (Can We Keep Up?)

- Average photo work = **CPU‑bound (3 × resize 200 ms + watermark 200 ms) + I/O‑bound (save 400 ms) ≈ 1 s**
- Worst case with 3 retries = 4 s.
- With **8 CPU cores**:
  - 8 processor workers ➜ ~8 photos/s CPU side.
  - 16 saver workers ➜ ~16 saves/s I/O side.
- 100 photos finish in ~13 s typical, ~50 s worst‑case burst.

---

## 5 How We Know It’s Healthy (Observability)

| Metric                   | Looks bad when…                       |
| ------------------------ | ------------------------------------- |
| `job_queue_depth`        | > queue size → CPU starved.           |
| `save_queue_depth`       | Climbing steadily → storage slow.     |
| `retry_count_total`      | Spikes → flaky disk/network.          |
| `processing_latency_p95` | > SLA → need more cores or tune code. |

Each log line:

```
2025‑08‑21T13:00:01Z [T14/Task26] [SAVER#3] [L95] save img123.jpg ok
```

---

## 6 Reference Code (C# 8+, cut‑down)

```csharp
record ImageJob(string File);
...
static void Log(string tag, string msg,[CallerLineNumber]int line=0){ ... }

Channel<ImageJob> inbox = Channel.CreateBounded<ImageJob>(Cpu*C2);
Channel<ImageJob> saveQ = Channel.CreateBounded<ImageJob>(Cpu*4);
Channel<Result>  outQ  = Channel.CreateUnbounded<Result>();

// Processor worker
async Task CpuWorker(int id){
    await foreach(var job in inbox.Reader.ReadAllAsync(ct)){
        Process(job);          // resize & watermark
        await saveQ.Writer.WriteAsync(job,ct);
    }
}

// Saver worker
async Task IoWorker(int id){
    await foreach(var job in saveQ.Reader.ReadAllAsync(ct)){
        await RetryAsync(t=>Save(job,t),3,TimeSpan.FromMs(200),ct);
        await outQ.Writer.WriteAsync(new(job,null),ct);
    }
}
```

_Full code lives in_ `/BulkImageProcessing/Program.cs` – includes retries, metrics, shutdown and line‑number logging.

---

## 7 Why This Stays Safe & Fast

- **No shared mutable state** except counters (atomic).
- **Channels close in order** → no writes after close.
- **Bounded queues** → memory bounded, instant back‑pressure.
- **Retries with back‑off** → transient errors smoothed out.
- **Thread/Task IDs in logs** → easy to trace weird interleaves.

---

That’s the whole picture, jargon‑free. Ready to tweak the worker counts or hook real image libraries? Open `Program.cs` and hack away! 🚀
