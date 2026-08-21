using Xunit;

public sealed class AssistantRoutingTests
{
    [Theory]
    [InlineData("запусти запись", "LOCAL_COMMAND_REQUIRED")]
    [InlineData("апусти запись", "LOCAL_COMMAND_REQUIRED")]
    [InlineData("останови запись", "LOCAL_COMMAND_REQUIRED")]
    public void RecorderShapedImperativesNeverBecomeQwenQueries(string text, string errorCode)
    {
        var route = AssistantModeResolver.ResolveStatic(text, "AUTO", null, false);

        Assert.Equal(errorCode, route.ErrorCode);
        Assert.Equal("", route.ResolvedMode);
    }

    [Theory]
    [InlineData("запустить запись")]
    [InlineData("почему остановилась запись")]
    [InlineData("может запустить запись")]
    public void ConversationalRecorderWordingRemainsAssistantQuery(string text)
    {
        var route = AssistantModeResolver.ResolveStatic(text, "AUTO", null, false);

        Assert.Null(route.ErrorCode);
        Assert.Equal("GENERAL_CHAT", route.ResolvedMode);
    }
}
