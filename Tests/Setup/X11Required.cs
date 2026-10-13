namespace Tests.Setup;

// the guard for [Category("Linux")] fixtures: they drive libX11 against a live X server, so elsewhere they skip before
// anything loads it. they run under Xvfb in the test-linux CI job and run-tests.sh linux.
public static class X11Required
{
    public static void OrIgnore()
    {
        if (!OperatingSystem.IsLinux() || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")))
            Assert.Ignore("Requires Linux with an X server (run ./run-tests.sh linux)");
    }
}
