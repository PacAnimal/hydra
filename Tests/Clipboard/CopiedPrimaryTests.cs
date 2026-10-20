using Hydra.Platform;
using Microsoft.Extensions.Logging.Abstractions;

namespace Tests.Clipboard;

[TestFixture]
public class CopiedPrimaryTests
{
    // the OS clipboard: its change counter and the text of what is on it
    private long _version;
    private string? _text;
    private bool _busy;
    private int _reads;
    private CopiedPrimary _primary = null!;

    [SetUp]
    public void SetUp()
    {
        _version = 1;
        _text = null;
        _busy = false;
        _reads = 0;
        _primary = new CopiedPrimary(() => _version, () =>
        {
            _reads++;
            return _busy ? throw new IOException("busy") : _text;
        }, NullLogger.Instance);
    }

    private void UserCopies(string? text)
    {
        _version++;
        _text = text;
    }

    private void HydraWrites(string? incoming, string written) => _primary.Write(incoming, leftAt =>
    {
        _version++;
        _text = written;
        leftAt(_version);
        return true;
    });

    [Test]
    public void NothingYet_IsTheUsersTextCopy()
    {
        _text = "copied";

        Assert.That(_primary.Read(), Is.EqualTo("copied"));
    }

    [Test]
    public void RelayedSelection_StandsWhileTheClipboardIsUnchanged()
    {
        _text = "older copy";
        _primary.Relay("selected on linux");

        Assert.That(_primary.Read(), Is.EqualTo("selected on linux"));
    }

    [Test]
    public void CopyAfterTheRelay_IsThePrimary()
    {
        _primary.Relay("selected on linux");
        UserCopies("copied since");

        Assert.That(_primary.Read(), Is.EqualTo("copied since"));
    }

    [Test]
    public void CopyWithoutText_LeavesTheRelayedSelection()
    {
        _primary.Relay("selected on linux");
        UserCopies(null);

        Assert.That(_primary.Read(), Is.EqualTo("selected on linux"));
    }

    [Test]
    public void HydrasOwnWrite_IsNotACopy()
    {
        HydraWrites("selected on linux", "written");

        Assert.That(_primary.Read(), Is.EqualTo("selected on linux"));
    }

    [Test]
    public void SameTextCopiedAgain_IsACopy()
    {
        // only the change counter says whether a copy happened, so copying Hydra's text again counts
        HydraWrites("selected on linux", "written");
        UserCopies("written");

        Assert.That(_primary.Read(), Is.EqualTo("written"));
    }

    [Test]
    public void Owns_OnlyWhileTheClipboardIsHydrasLastWrite()
    {
        Assert.That(_primary.Owns(), Is.False, "nothing written yet");

        HydraWrites(null, "written");
        Assert.That(_primary.Owns(), Is.True);

        UserCopies("written");
        Assert.That(_primary.Owns(), Is.False, "the same text copied again is the user's");
    }

    [Test]
    public void Owns_NotAfterAWriteThatNeverSaidWhereItLeftTheClipboard()
    {
        // Windows reports no version when another app owns the clipboard after the write
        _primary.Write(null, _ =>
        {
            _version++;
            return true;
        });

        Assert.That(_primary.Owns(), Is.False);
    }

    [Test]
    public void Read_SameVersion_ReadsTheClipboardOnce()
    {
        UserCopies("copied");
        _primary.Read();
        _primary.Read();

        Assert.That(_reads, Is.EqualTo(1));
    }

    [Test]
    public void Read_TextlessCopy_ReadsTheClipboardOnce()
    {
        UserCopies(null);
        _primary.Read();
        _primary.Read();

        Assert.That(_reads, Is.EqualTo(1));
    }

    [Test]
    public void BusyClipboard_IsTriedAgain()
    {
        _primary.Relay("selected on linux");
        UserCopies("copied");
        _busy = true;
        var whileBusy = _primary.Read();
        _busy = false;
        var afterwards = _primary.Read();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(whileBusy, Is.EqualTo("selected on linux"));
            Assert.That(afterwards, Is.EqualTo("copied"));
        }
    }

    [Test]
    public void CopySeen_StaysThePrimaryAfterALaterImageCopy()
    {
        // once noticed, a text copy is the newest selection until something newer comes
        _primary.Relay("selected on linux");
        UserCopies("copied");
        _primary.Read();
        UserCopies(null);

        Assert.That(_primary.Read(), Is.EqualTo("copied"));
    }

    [Test]
    public void Write_KeepsACopyMadeSinceAsThePrimary()
    {
        // the clipboard is about to be overwritten: the user's copy stays the primary, an older relay cannot return
        _primary.Relay("selected on linux");
        UserCopies("copied since");
        HydraWrites(null, "written");

        Assert.That(_primary.Read(), Is.EqualTo("copied since"));
    }

    [Test]
    public void Write_WithASelection_OutranksACopyFoundThere()
    {
        UserCopies("copied");
        HydraWrites("incoming selection", "written");

        Assert.That(_primary.Read(), Is.EqualTo("incoming selection"));
    }

    [Test]
    public void Read_DuringHydrasWrite_IsNotACopy()
    {
        // a read racing the write sees the half-written clipboard under a new version
        string? during = null;
        _primary.Write("relayed selection", _ =>
        {
            UserCopies("half written");
            during = _primary.Read();
            return true;
        });

        Assert.That(during, Is.EqualTo("relayed selection"));
    }

    [Test]
    public void Read_DuringTheSecondOfTwoOverlappingWrites_IsNotACopy()
    {
        string? during = null;
        _primary.Write("relayed selection", _ =>
        {
            _primary.Write(null, leftAt =>
            {
                leftAt(2);
                return true;
            });
            UserCopies("half written");
            during = _primary.Read();
            return true;
        });

        Assert.That(during, Is.EqualTo("relayed selection"));
    }

    [Test]
    public void FailedWrite_LeavesTheVersionItBeganAt()
    {
        // a write that never learned its version must not make the clipboard Hydra wrote earlier look like a copy
        HydraWrites("relayed selection", "written");
        _primary.Write(null, _ => false);

        Assert.That(_primary.Read(), Is.EqualTo("relayed selection"));
    }

    [Test]
    public void SelectionRelayedDuringAWrite_IsNotUndoneByIt()
    {
        // the write finishing reports an older version than the relay saw; the newer one stands
        _primary.Write(null, leftAt =>
        {
            UserCopies("older copy");
            _primary.Relay("relayed during the write");
            leftAt(_version - 1);
            return false;
        });

        Assert.That(_primary.Read(), Is.EqualTo("relayed during the write"));
    }

    [Test]
    public void WriteThatThrows_IsAFailedWriteThatStillEnds()
    {
        var replaced = _primary.Write("relayed selection", _ => throw new InvalidOperationException("write failed"));
        UserCopies("copied after");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(replaced, Is.False);
            Assert.That(_primary.Read(), Is.EqualTo("copied after"));
        }
    }

    [Test]
    public void CopyUnreadableWhenAWriteBegan_IsReadOnceTheWriteNeverHappened()
    {
        UserCopies("copied");
        _busy = true;
        _primary.Write(null, _ => false);
        _busy = false;

        Assert.That(_primary.Read(), Is.EqualTo("copied"));
    }

    [Test]
    public void RelayedSelection_OutranksACopyMadeBeforeIt()
    {
        UserCopies("older copy");
        _primary.Relay("newer selection");

        Assert.That(_primary.Read(), Is.EqualTo("newer selection"));
    }
}
