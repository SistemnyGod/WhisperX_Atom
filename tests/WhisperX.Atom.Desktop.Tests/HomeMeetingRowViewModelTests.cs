using WhisperX.Atom.Desktop;
using WhisperX_Atom_Desktop.ViewModels;
using Xunit;

public sealed class HomeMeetingRowViewModelTests
{
    [Fact]
    public void ProcessingStepperExposesAllSixStages()
    {
        var meeting = Meeting("QUEUED");
        var metrics = new HomeViewModel.MeetingMetrics(meeting.Id, meeting.Title, true, false, 0, "TRANSCRIBING", 25, [], null, null);

        var row = HomeMeetingRowViewModel.Create(meeting, metrics, null);

        Assert.Equal(6, row.Steps.Count);
        Assert.Equal("Получение стенограммы", row.CurrentStageText);
        Assert.Equal(2, row.CurrentIndex);
    }

    [Fact]
    public void ErrorIsShownSeparatelyAndDoesNotLookReady()
    {
        var meeting = Meeting("ADMIN_REVIEW");
        var metrics = new HomeViewModel.MeetingMetrics(meeting.Id, meeting.Title, false, false, 0, "ADMIN_REVIEW", 0, [], null, "TRANSCRIPT_LOW_QUALITY");

        var row = HomeMeetingRowViewModel.Create(meeting, metrics, null);

        Assert.StartsWith("Требует проверки", row.ErrorText);
        Assert.NotEqual(5, row.CurrentIndex);
        Assert.False(row.CanCancel);
    }

    [Fact]
    public void DeterministicDraftPendingEnhancementIsInformationalNotAnError()
    {
        var meeting = Meeting("PARTIAL_READY");
        var metrics = new HomeViewModel.MeetingMetrics(meeting.Id, meeting.Title, false, true, 0, "PARTIAL_READY", 100, [], null, "LLM_ENHANCEMENT_PENDING");

        var row = HomeMeetingRowViewModel.Create(meeting, metrics, null);

        Assert.Null(row.ErrorText);
        Assert.Contains("Черновик саммари готов", row.InfoText);
        Assert.Equal(5, row.CurrentIndex);
    }

    private static DesktopMeeting Meeting(string status)
    {
        var id = Guid.NewGuid().ToString();
        return new DesktopMeeting(id, "Тестовая встреча", "Описание", status, DateTimeOffset.UtcNow);
    }
}
