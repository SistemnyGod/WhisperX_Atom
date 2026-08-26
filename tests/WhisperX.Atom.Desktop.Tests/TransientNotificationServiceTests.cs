using WhisperX_Atom_Desktop.Services;
using Xunit;

public sealed class TransientNotificationServiceTests
{
    [Fact]
    public void DurationIsBoundedToFiveAndTenSeconds()
    {
        var service = new TransientNotificationService();
        var received = new List<TransientNotification>();
        service.Requested += received.Add;

        service.Publish("short", "Загрузка", "Началась", duration: TimeSpan.FromSeconds(1));
        service.Publish("long", "Готово", "Завершено", duration: TimeSpan.FromMinutes(1));

        Assert.Equal(TimeSpan.FromSeconds(5), received[0].Duration);
        Assert.Equal(TimeSpan.FromSeconds(10), received[1].Duration);
    }

    [Fact]
    public void RepeatedPollingObservationIsPublishedOnce()
    {
        var service = new TransientNotificationService();
        var count = 0;
        service.Requested += _ => count++;

        service.Publish("meeting:one:transcribing", "Транскрибация", "Идёт обработка");
        service.Publish("meeting:one:transcribing", "Транскрибация", "Идёт обработка");

        Assert.Equal(1, count);
    }

    [Fact]
    public void DifferentPipelineStagesAreNotCollapsed()
    {
        var service = new TransientNotificationService();
        var received = new List<TransientNotification>();
        service.Requested += received.Add;

        service.Publish("meeting:one:transcribing", "Транскрибация", "Идёт обработка");
        service.Publish("meeting:one:transcript-ready", "Стенограмма получена", "Текст доступен", TransientNotificationSeverity.Success);

        Assert.Equal(2, received.Count);
        Assert.Equal(TransientNotificationSeverity.Success, received[1].Severity);
    }
}
