using Hydra.Keyboard;

namespace Tests.Keyboard;

[TestFixture]
public class NumLockRestoreTests
{
    [Test]
    public void NothingSynced_NothingToRestore() => Assert.That(new NumLockRestore().Take(), Is.Null);

    [Test]
    public void Synced_GivesBackWhatItHadFirst()
    {
        var restore = new NumLockRestore();
        restore.Synced(had: false);
        restore.Synced(had: true);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(restore.Take(), Is.False, "the state before the visit's first sync");
            Assert.That(restore.Take(), Is.Null, "taken once");
        }
    }

    [Test]
    public void AForwardedNumLockKey_IsTheUsersChoice_AndKept()
    {
        var restore = new NumLockRestore();
        restore.Synced(had: false);
        restore.Toggled();
        Assert.That(restore.Take(), Is.Null);
    }
}
