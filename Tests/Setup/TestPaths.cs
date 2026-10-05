using System.Globalization;

namespace Tests.Setup;

public static class TestPaths
{
    /// <summary>
    /// This run's root, <c>hydra-test/&lt;pid&gt;</c>: one place for every test directory, so a stale run is one
    /// thing to delete, and one per PROCESS, because two <c>dotnet test</c> runs at once are supported and
    /// the management endpoint's socket or pipe name is derived from the config path under it.
    ///
    /// <para>The directories of runs that have ended are removed on the way in, never a live one's. The pid
    /// alone identifies a run: the temp dir belongs to one machine, unlike test-output, which the Linux
    /// container shares with its host.</para>
    /// </summary>
    public static readonly string TestRootDir = RunRoot(Path.Combine(Path.GetTempPath(), "hydra-test"));

    /// <summary>
    /// This fixture's own directory, handed over EMPTY — what a <c>[SetUp]</c> wants, and the reason a
    /// directory per TEST is not needed.
    ///
    /// <para>Nothing in this suite is <c>[Parallelizable]</c>, so NUnit runs one test at a time and no two
    /// are ever inside one of these together. Mark a fixture parallel and that stops holding. What
    /// made a shared directory unsafe was never concurrency — it was a BEST-EFFORT clear: a delete that
    /// failed for any reason handed the next test whatever the last one wrote, in silence, and the damage
    /// surfaced somewhere else entirely as the product misbehaving. So this one REFUSES and names what it
    /// could not remove.</para>
    /// </summary>
    public static string FreshFixtureRoot(string fixture) => FreshDirectory(Path.Combine(TestRootDir, fixture));

    private static string RunRoot(string parent)
    {
        Directory.CreateDirectory(parent);
        foreach (var run in Directory.EnumerateDirectories(parent))
        {
            if (!int.TryParse(Path.GetFileName(run), out var processId) || processId == Environment.ProcessId) continue;
            if (LiveProcess.IsRunning(processId)) continue;
            // best effort: whatever survives is retried by the next run
            try { Directory.Delete(run, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return Path.Combine(parent, Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// The same guarantee for a directory that must live somewhere specific — see
    /// <see cref="FreshFixtureRoot"/>. The CONTENTS are removed rather than the directory itself, which
    /// sidesteps the delete-then-recreate window Windows is entitled to fail in.
    /// </summary>
    public static string FreshDirectory(string root)
    {
        Directory.CreateDirectory(root);

        foreach (var entry in Directory.EnumerateFileSystemEntries(root))
        {
            try
            {
                if (Directory.Exists(entry)) Directory.Delete(entry, recursive: true);
                else File.Delete(entry);
            }
            catch (Exception ex)
            {
                throw new IOException($"'{root}' could not be cleared for this fixture — '{Path.GetFileName(entry)}' "
                                      + $"is still there ({ex.Message}). Something the previous test opened was never closed, "
                                      + "and continuing would test against its leftovers.", ex);
            }
        }

        var survivors = Directory.EnumerateFileSystemEntries(root).Select(Path.GetFileName).ToArray();
        if (survivors.Length > 0)
            throw new IOException($"'{root}' still holds [{string.Join(", ", survivors)}] after being cleared");

        return root;
    }
}
