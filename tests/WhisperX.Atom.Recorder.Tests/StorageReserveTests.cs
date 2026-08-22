using WhisperX.Atom.Recorder;
using Xunit;

public sealed class StorageReserveTests
{
    [Fact]
    public void StartReserveIncludesCaptureAndPostProcessing()
    {
        var policy = new StorageRetentionPolicy(
            TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero,
            TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, 15, 8,
            2_000, 1_000, 1_000, 1_128);

        var watermark = policy.Evaluate(1_999, 10_000);

        Assert.False(watermark.AllowsRecording);
        Assert.Equal(StorageWatermarkState.BLOCK_RECORDING, watermark.State);
        Assert.Equal(2_000, watermark.BlockFreeBytes);
    }

    [Fact]
    public void ActiveCaptureEmergencyIsDistinct()
    {
        var policy = new StorageRetentionPolicy(
            TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero,
            TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, 15, 8,
            2_000, 1_000, 1_000, 1_128);

        var watermark = policy.EvaluateDuringRecording(1_127, 10_000);

        Assert.True(watermark.IsEmergency);
        Assert.False(watermark.AllowsRecording);
    }
}
