namespace Tests.Setup;

// the platform guard lives here rather than at each call site: callers skip Windows already, but the
// analyzer cannot see that through Assert.Ignore
public static class UnixMode
{
    public static UnixFileMode Of(string path) => OperatingSystem.IsWindows() ? default : File.GetUnixFileMode(path);

    public static void Set(string path, UnixFileMode mode)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, mode);
    }
}
