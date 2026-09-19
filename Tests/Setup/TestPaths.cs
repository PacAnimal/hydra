namespace Tests.Setup;

public static class TestPaths
{
    // one root for every test directory, so a stale run is one thing to delete rather than a scatter of
    // hydra-test-<guid> siblings in the raw temp dir
    public static readonly string TestRootDir = Path.Combine(Path.GetTempPath(), "hydra-test");

    /// <summary>
    /// This fixture's own directory, handed over EMPTY — what a <c>[SetUp]</c> wants, and the reason a
    /// directory per TEST is not needed.
    ///
    /// <para>Nothing in this suite is <c>[Parallelizable]</c> and the Linux lane pins
    /// <c>NumberOfTestWorkers=1</c>, so no two tests are ever inside one of these at the same time. What
    /// made a shared directory unsafe was never concurrency — it was a BEST-EFFORT clear: a delete that
    /// failed for any reason handed the next test whatever the last one wrote, in silence, and the damage
    /// surfaced somewhere else entirely as the product misbehaving. So this one REFUSES and names what it
    /// could not remove.</para>
    /// </summary>
    public static string FreshFixtureRoot(string fixture) => FreshDirectory(Path.Combine(TestRootDir, fixture));

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
