namespace Apparatus.Core.Start;

/// <summary>Mirrors the shell's QUERY_USER_NOTIFICATION_STATE (SHQueryUserNotificationState).</summary>
public enum UserNotificationState
{
    NotPresent = 1,
    Busy = 2,
    RunningD3dFullScreen = 3,
    PresentationMode = 4,
    AcceptsNotifications = 5,
    QuietTime = 6,
    App = 7,
}

/// <summary>When Apparatus' Start menu should not take the Windows key or the Start button, leaving them to Windows.</summary>
public static class StartInterceptPolicy
{
    /// <summary>
    /// A full-screen game (Direct3D exclusive mode) or a presentation owns the screen: Windows decides what the Windows key does
    /// there (it usually minimises the game first), and a menu drawn over a running game is not what anyone wants. A browser or
    /// video player in "full screen" (state <see cref="UserNotificationState.Busy"/>) is an ordinary program, so it is not skipped.
    /// </summary>
    public static bool StepAside(UserNotificationState state) =>
        state is UserNotificationState.RunningD3dFullScreen or UserNotificationState.PresentationMode;
}
