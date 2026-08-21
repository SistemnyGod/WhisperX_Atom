# GPU runtime recovery

`workers/ml_worker/worker.py` owns the single CUDA lease and durable NATS
delivery for ASR/enrichment. `workers/ml_worker/persistence.py` records inbox
ownership and job leases; `workers/gpu_lease.py` arbitrates ASR, Assistant,
enrichment and summary priority.

## Ownership

Inbox claims return `ACQUIRED`, `OWNED_BY_THIS_WORKER`,
`OWNED_BY_OTHER_WORKER` or `TERMINAL`. A serial worker may reclaim its own
orphan lease. A foreign live lease is expected redelivery, so the consumer
uses a delayed NAK (at least five seconds) and does not emit a traceback or
false `READY` heartbeat.

## Assistant while ASR owns the GPU

Assistant attempts the lease for ten seconds. If healthy ASR is still active,
the query stays durable in `QUEUED` with
`ASSISTANT_WAITING_FOR_GPU` and a fifteen-second `nextRetryAt`; `retry_count`
is not incremented. The queue expires after one hour with
`ASSISTANT_GPU_BUSY_TIMEOUT`, while infrastructure failures retain their
three-attempt retry policy.

## Recovery command

The GPU image contains a safe operator command. Preview is the default:

```powershell
./recover-gpu-runtime.ps1
./recover-gpu-runtime.ps1 -Apply
```

The apply transaction requeues unfinished GPU jobs, restores their input
stage, clears owner/lease/heartbeat, increments the attempt, expires related
inbox leases and records `WORKER_RESTART_RECOVERY`. Media, transcripts, V1/V2,
meetings and correlation IDs are never deleted.
