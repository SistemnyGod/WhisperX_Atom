using System.Reflection;
using WhisperX.Atom.Desktop;
using WhisperX_Atom_Desktop.Services;
using Xunit;

public sealed class ProcessingJobTrackerSemanticsTests
{
    [Fact]
    public async Task RunningSnapshotIsObservedAsRunningNotBlocked()
    {
        var job = Job("RUNNING");
        var proxy = BackendProxy.Create(job, Array.Empty<DesktopPipelineRun>());
        var tracker = new ProcessingJobTracker(proxy);
        var observations = new List<ProcessingJobObservation>();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        try
        {
            await tracker.WaitForTerminalAsync(job, observation: observations.Add, cancellationToken: cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is expected here; the assertion is about the
            // observation emitted before polling was cancelled.
        }

        Assert.Contains(observations, item => item.State == ProcessingJobState.Running);
        Assert.DoesNotContain(observations, item => item.State == ProcessingJobState.Blocked);
    }

    [Fact]
    public async Task DegradedPipelineWithOwnerIsBlocked()
    {
        var job = Job("RUNNING");
        var snapshot = Snapshot("DEGRADED", "ASR", "GPU_WORKER", true, "GPU_WORKER_UNAVAILABLE");
        var run = new DesktopPipelineRun(
            Guid.NewGuid(), Guid.Parse(job.MeetingId), Guid.NewGuid(), "READY",
            Guid.Parse(job.Id), "RUNNING", "TRANSCRIBING",
            Guid.NewGuid(), "READY", Guid.NewGuid(), "READY", "READY", Guid.NewGuid(), "READY",
            null, null, null, null, null, "test", Snapshot: snapshot);
        var proxy = BackendProxy.Create(job, new[] { run });
        var tracker = new ProcessingJobTracker(proxy);
        var observations = new List<ProcessingJobObservation>();

        var result = await tracker.WaitForTerminalAsync(job, observation: observations.Add);

        Assert.NotNull(result);
        Assert.Contains(observations, item => item.State == ProcessingJobState.Blocked);
    }

    private static DesktopJob Job(string status) => new(
        Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), "TRANSCRIBE_ASR", status,
        "TRANSCRIBING", 40, 1, null, null, null, DateTime.UtcNow, DateTime.UtcNow);

    private static DesktopPipelineSnapshot Snapshot(string overall, string stage, string? blockedBy, bool retryable, string? errorCode) =>
        new(Guid.NewGuid(), Guid.NewGuid(), "test", overall, stage, blockedBy, retryable, errorCode,
            new("CONFIRMED"), new("READY"), new("RUNNING"), new("READY"), new("READY"), new("READY"), new("READY"));

    private class BackendProxy : DispatchProxy
    {
        private DesktopJob _job = null!;
        private IReadOnlyList<DesktopPipelineRun> _runs = Array.Empty<DesktopPipelineRun>();

        public static IBackendService Create(DesktopJob job, IReadOnlyList<DesktopPipelineRun> runs)
        {
            var proxy = Create<IBackendService, BackendProxy>();
            var implementation = (BackendProxy)(object)proxy;
            implementation._job = job;
            implementation._runs = runs;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is null) return null;
            if (targetMethod.Name is "GetJobAsync" or "WaitForJobEventsAsync")
                return Task.FromResult<DesktopJob?>(_job);
            if (targetMethod.Name == "GetMeetingPipelineAsync")
                return Task.FromResult(_runs);
            if (targetMethod.ReturnType == typeof(void)) return null;
            if (targetMethod.ReturnType.IsGenericType && targetMethod.ReturnType.GetGenericTypeDefinition() == typeof(Task<>))
                return Task.FromResult(CreateDefault(targetMethod.ReturnType.GetGenericArguments()[0]));
            return null;
        }

        private static object? CreateDefault(Type type) => type.IsValueType ? Activator.CreateInstance(type) : null;
    }
}
