namespace Hydra.Platform.Linux.Wayland;

// Some portal backends restore a session from its token without the clipboard. Such a start is retried once a run
// without the token, so the user grants the clipboard again, as Deskflow does.
internal static class ClipboardRestoreRetry
{
    internal static bool ShouldRetry(bool usedToken, bool askedClipboard, bool clipboardSupported, bool clipboardEnabled, bool alreadyRetried) =>
        usedToken && askedClipboard && clipboardSupported && !clipboardEnabled && !alreadyRetried;
}
