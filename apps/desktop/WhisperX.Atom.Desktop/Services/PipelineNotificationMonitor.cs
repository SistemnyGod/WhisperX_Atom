using WhisperX.Atom.Desktop;

namespace WhisperX_Atom_Desktop.Services;

internal enum PipelineNotificationMilestone
{
    ProcessingFile,
    Transcribing,
    TranscriptReady,
    SummaryPreparing,
    Completed,
    Failed
}

/// <summary>
/// Observes bounded server-owned pipeline snapshots. The first refresh only
/// seeds state, so starting Desktop never replays notifications for history.
/// </summary>
public sealed class PipelineNotificationMonitor(
    IBackendService backend,
    TransientNotificationService notifications)
{
    private readonly Dictionary<string, HashSet<PipelineNotificationMilestone>> _seen = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private bool _initialized;

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (!backend.HasSession || !await _refreshGate.WaitAsync(0, cancellationToken)) return;
        try
        {
            var meetings = await backend.GetMeetingsPageAsync(30, 0, cancellationToken);
            var ids = meetings.Select(item => Guid.TryParse(item.Id, out var id) ? id : Guid.Empty)
                .Where(id => id != Guid.Empty).ToArray();
            if (ids.Length == 0)
            {
                _initialized = true;
                return;
            }

            var metrics = await backend.GetMeetingMetricsAsync(ids, cancellationToken);
            var titles = meetings.ToDictionary(item => item.Id, item => item.DisplayTitle, StringComparer.OrdinalIgnoreCase);
            foreach (var item in metrics)
            {
                var milestones = Project(item);
                if (!_seen.TryGetValue(item.MeetingId, out var seen))
                {
                    seen = [];
                    _seen[item.MeetingId] = seen;
                }

                if (!_initialized)
                {
                    seen.UnionWith(milestones);
                    continue;
                }

                foreach (var milestone in milestones.Where(value => !seen.Contains(value)))
                {
                    seen.Add(milestone);
                    Publish(item.MeetingId, titles.GetValueOrDefault(item.MeetingId, "Совещание"), milestone, item.Summary);
                }
            }
            _initialized = true;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    internal static IReadOnlyList<PipelineNotificationMilestone> Project(DesktopMeetingMetrics metrics)
    {
        var result = new List<PipelineNotificationMilestone>();
        var jobs = metrics.Jobs ?? [];
        var pipeline = metrics.Pipeline ?? [];
        var active = jobs.Where(job => !IsTerminal(job.Status)).ToArray();
        var hasTranscript = pipeline.Any(run => run.TranscriptV1Id.HasValue || run.TranscriptV2Id.HasValue)
            || jobs.Any(job => (job.Type is "TRANSCRIBE" or "TRANSCRIBE_ASR")
                && job.Status.Equals("READY", StringComparison.OrdinalIgnoreCase));
        var hasSummaryWork = jobs.Any(job => job.Type.Equals("SUMMARIZE", StringComparison.OrdinalIgnoreCase));

        if (active.Any(job => job.Type.Equals("IMPORT", StringComparison.OrdinalIgnoreCase)
            || job.Stage.Contains("MEDIA", StringComparison.OrdinalIgnoreCase)
            || job.Stage.Contains("UPLOAD", StringComparison.OrdinalIgnoreCase)))
            result.Add(PipelineNotificationMilestone.ProcessingFile);
        if (active.Any(job => job.Type is "TRANSCRIBE" or "TRANSCRIBE_ASR" or "TRANSCRIPT_ENRICH"))
            result.Add(PipelineNotificationMilestone.Transcribing);
        if (hasTranscript)
            result.Add(PipelineNotificationMilestone.TranscriptReady);
        if (active.Any(job => job.Type.Equals("SUMMARIZE", StringComparison.OrdinalIgnoreCase)))
            result.Add(PipelineNotificationMilestone.SummaryPreparing);
        if (metrics.Summary is not null && metrics.Summary.Status is "READY" or "NEEDS_REVIEW")
            result.Add(PipelineNotificationMilestone.Completed);
        else if (hasTranscript && hasSummaryWork && jobs.Any(job => job.Type.Equals("SUMMARIZE", StringComparison.OrdinalIgnoreCase)
                     && job.Status is "FAILED" or "CANCELLED"))
            result.Add(PipelineNotificationMilestone.Failed);
        return result;
    }

    private void Publish(string meetingId, string title, PipelineNotificationMilestone milestone, DesktopSummary? summary)
    {
        var (notificationTitle, message, severity) = milestone switch
        {
            PipelineNotificationMilestone.ProcessingFile => ("Обработка файла", $"{title}: сервер подготавливает аудио.", TransientNotificationSeverity.Informational),
            PipelineNotificationMilestone.Transcribing => ("Идёт транскрибация", $"{title}: WhisperX распознаёт запись.", TransientNotificationSeverity.Informational),
            PipelineNotificationMilestone.TranscriptReady => ("Стенограмма получена", $"{title}: текст уже доступен для просмотра.", TransientNotificationSeverity.Success),
            PipelineNotificationMilestone.SummaryPreparing => ("Готовим саммари", $"{title}: формируются итоги совещания.", TransientNotificationSeverity.Informational),
            PipelineNotificationMilestone.Completed when summary?.Status.Equals("NEEDS_REVIEW", StringComparison.OrdinalIgnoreCase) == true
                => ("Обработка завершена", $"{title}: стенограмма и черновик саммари готовы, требуется проверка.", TransientNotificationSeverity.Warning),
            PipelineNotificationMilestone.Completed => ("Обработка завершена", $"{title}: стенограмма и саммари готовы.", TransientNotificationSeverity.Success),
            _ => ("Обработка требует внимания", $"{title}: саммари не удалось подготовить. Стенограмма сохранена.", TransientNotificationSeverity.Warning)
        };
        notifications.Publish($"pipeline:{meetingId}:{milestone}", notificationTitle, message, severity);
    }

    private static bool IsTerminal(string? status) => status is not null && status.ToUpperInvariant() is "READY" or "PARTIAL_READY" or "COMPLETED" or "FAILED" or "CANCELLED";
}
