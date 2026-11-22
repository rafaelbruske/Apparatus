using Apparatus.Core.Start;

namespace Apparatus.Core.Tests;

public class StartInterceptPolicyTests
{
    [Theory]
    [InlineData(UserNotificationState.RunningD3dFullScreen, true)]
    [InlineData(UserNotificationState.PresentationMode, true)]
    [InlineData(UserNotificationState.Busy, false)]            // F11 in a browser, a video player
    [InlineData(UserNotificationState.AcceptsNotifications, false)]
    [InlineData(UserNotificationState.QuietTime, false)]
    [InlineData(UserNotificationState.NotPresent, false)]
    [InlineData(UserNotificationState.App, false)]
    public void StepAside_OnlyForGamesAndPresentations(UserNotificationState state, bool expected) =>
        Assert.Equal(expected, StartInterceptPolicy.StepAside(state));

    [Fact]
    public void AnUnknownState_DoesNotStepAside() =>
        Assert.False(StartInterceptPolicy.StepAside((UserNotificationState)99));
}
