using System.IO.Pipes;
using System.Text;
using Hydra.Platform;
using Hydra.Platform.Linux;
using Hydra.Platform.Linux.Wayland;
using Microsoft.Extensions.Logging.Abstractions;
using Tests.Setup;
using Tmds.DBus.Protocol;

namespace Tests.Clipboard;

// The portal's clipboard against a fake portal: what a read takes of another client's offer, what a write offers and
// serves, and how the change counter follows the owners
[TestFixture]
public class PortalClipboardSyncTests
{
    private static readonly byte[] Png = TestImages.Png;

    // what a write of text, HTML, RTF and an image offers
    private static readonly string[] EveryFormat = ["text/plain;charset=utf-8", "UTF8_STRING", "text/plain", "text/html", "text/rtf", "image/png"];
    // what a clear offers: the placeholder's text and the no-history hint
    private static readonly string[] PlaceholderFormats = ["text/plain;charset=utf-8", "UTF8_STRING", "text/plain", "x-kde-passwordManagerHint"];

    private FakePortalClipboard _portal = null!;
    private StartedTimers _time = null!;
    private PortalClipboardSync _sync = null!;

    [SetUp]
    public void SetUp()
    {
        _portal = new FakePortalClipboard();
        _time = new StartedTimers();
        _sync = Create(_portal);
        Assert.That(_sync.Connected(), Is.True);
    }

    [TearDown]
    public async Task TearDown() => await _sync.DisposeAsync();

    private PortalClipboardSync Create(IPortalClipboard? portal, IClipboardSync? fallback = null) =>
        new(_ => Task.FromResult(portal), () => fallback ?? new NullClipboardSync(), _time, NullLogger.Instance);

    // -- reads --

    [TestCase("text/plain;charset=utf-8")]
    [TestCase("UTF8_STRING")]
    [TestCase("text/plain")]
    public void Read_TakesTheText_OfferedAsAnyTextType(string mimeType)
    {
        _portal.OtherAppCopies(new() { [mimeType] = "copied"u8.ToArray() });

        Assert.That(_sync.ReadAll(), Is.EqualTo(new ClipboardRead("copied", null, null, null, false)));
    }

    [Test]
    public void Read_PrefersUtf8Text()
    {
        _portal.OtherAppCopies(new() { ["text/plain"] = "plain"u8.ToArray(), ["text/plain;charset=utf-8"] = "utf-8"u8.ToArray() });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_sync.ReadAll().Text, Is.EqualTo("utf-8"));
            Assert.That(_portal.ReadTypes, Does.Not.Contain("text/plain"));
        }
    }

    // GNOME live: an owner offering several text types whose UTF8_STRING transfer fails
    [Test]
    public void Read_OfATextTypeThatFails_TakesTheNextOffered()
    {
        _portal.OtherAppCopies(new()
        {
            ["UTF8_STRING"] = "broken"u8.ToArray(),
            ["text/plain;charset=utf-8"] = "copied"u8.ToArray(),
            ["STRING"] = "copied"u8.ToArray(),
        });
        _portal.Failing.UnionWith(["UTF8_STRING", "text/plain;charset=utf-8"]);

        var read = ClipboardUtils.ReadWithFallback(_sync, null, NullLogger.Instance, "test");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(read?.Text, Is.EqualTo("copied"), "read, not unknown");
            Assert.That(_portal.ReadTypes, Is.EqualTo(["text/plain;charset=utf-8", "UTF8_STRING", "STRING"]));
        }
    }

    [Test]
    public void Read_OfAnImageThatFails_KeepsTheText()
    {
        _portal.OtherAppCopies(new() { ["text/plain;charset=utf-8"] = "copied"u8.ToArray(), ["image/png"] = Png });
        _portal.Failing.Add("image/png");

        Assert.That(_sync.ReadAll(), Is.EqualTo(new ClipboardRead("copied", null, null, null, false)));
    }

    [Test]
    public void Read_WhereEveryOfferedTypeFails_IsUnreadable()
    {
        _portal.OtherAppCopies(new() { ["text/plain;charset=utf-8"] = "copied"u8.ToArray(), ["UTF8_STRING"] = "copied"u8.ToArray() });
        _portal.Failing.UnionWith(["text/plain;charset=utf-8", "UTF8_STRING"]);

        Assert.That(ClipboardUtils.ReadWithFallback(_sync, null, NullLogger.Instance, "test"), Is.Null, "unknown, never empty");
    }

    [Test]
    public void Read_BeforeAnyOwnerChange_OfATextTypeThatFails_TakesTheNext()
    {
        _portal.AlreadyCopied(new() { ["text/plain;charset=utf-8"] = "broken"u8.ToArray(), ["UTF8_STRING"] = "copied"u8.ToArray() });
        _portal.Failing.Add("text/plain;charset=utf-8");

        Assert.That(_sync.ReadAll().Text, Is.EqualTo("copied"));
    }

    [Test]
    public void TextCopyWhoseFirstTypeFails_IsStillThePrimary()
    {
        _portal.OtherAppCopies(new() { ["text/plain;charset=utf-8"] = "broken"u8.ToArray(), ["text/plain"] = "copied"u8.ToArray() });
        _portal.Failing.Add("text/plain;charset=utf-8");

        Assert.That(_sync.GetPrimaryText(), Is.EqualTo("copied"));
    }

    [Test]
    public void Read_TakesTheImage()
    {
        _portal.OtherAppCopies(new() { ["image/png"] = Png });

        Assert.That(_sync.ReadAll().ImagePng, Is.EqualTo(Png));
    }

    [Test]
    public void Read_TakesHtmlAndRtfBesideTheText()
    {
        var rtf = @"{\rtf1 copied}"u8.ToArray();
        _portal.OtherAppCopies(new()
        {
            ["text/plain;charset=utf-8"] = "copied"u8.ToArray(),
            ["text/html"] = "<b>copied</b>"u8.ToArray(),
            ["text/rtf"] = rtf,
        });

        var read = _sync.ReadAll();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(read.Text, Is.EqualTo("copied"));
            Assert.That(read.Html, Is.EqualTo("<b>copied</b>"));
            Assert.That(read.Rtf, Is.EqualTo(rtf));
            Assert.That(read.Concealed, Is.False);
        }
    }

    [Test]
    public void Read_OnlyAsksForTheTypesOffered()
    {
        _portal.OtherAppCopies(new() { ["text/plain;charset=utf-8"] = "copied"u8.ToArray(), ["application/x-other"] = [1] });

        _sync.ReadAll();

        Assert.That(_portal.ReadTypes, Is.EqualTo(["text/plain;charset=utf-8"]));
    }

    [Test]
    public void Read_OfAPasswordManagersCopy_IsConcealed()
    {
        _portal.OtherAppCopies(new() { ["text/plain;charset=utf-8"] = "hunter2"u8.ToArray(), ["x-kde-passwordManagerHint"] = "secret"u8.ToArray() });

        var read = ClipboardUtils.ReadWithFallback(_sync, null, NullLogger.Instance, "test");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(read?.Text, Is.EqualTo("hunter2"));
            Assert.That(read?.Concealed, Is.True);
        }
    }

    [Test]
    public void Read_OfAHintOtherThanSecret_IsNotConcealed()
    {
        _portal.OtherAppCopies(new() { ["text/plain;charset=utf-8"] = "copied"u8.ToArray(), ["x-kde-passwordManagerHint"] = "public"u8.ToArray() });

        Assert.That(_sync.ReadAll().Concealed, Is.False);
    }

    [Test]
    public void Read_ThatFails_IsUnreadable()
    {
        _portal.OtherAppCopies(new() { ["text/plain;charset=utf-8"] = "copied"u8.ToArray() });
        _portal.ReadFails = true;

        using (Assert.EnterMultipleScope())
        {
            Assert.Throws<ClipboardUnreadableException>(() => _sync.ReadAll());
            Assert.That(ClipboardUtils.ReadWithFallback(_sync, null, NullLogger.Instance, "test"), Is.Null, "unknown, never empty");
        }
    }

    [Test]
    public void Read_ThatTimesOut_IsUnreadable()
    {
        _portal.OtherAppCopies(new() { ["image/png"] = Png });
        _portal.ReadTimesOut = true;

        Assert.Throws<ClipboardUnreadableException>(() => _sync.ReadAll());
    }

    // -- the selection already there when the session started, which the portal never announces --

    [Test]
    public void Read_BeforeAnyOwnerChange_OfNoSelection_IsEmpty()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_sync.ReadAll(), Is.EqualTo(ClipboardRead.Empty));
            Assert.That(ClipboardUtils.ReadWithFallback(_sync, null, NullLogger.Instance, "test"), Is.Not.Null, "empty, not unknown");
        }
    }

    [Test]
    public void Read_BeforeAnyOwnerChange_TakesTheTextAlreadyCopied()
    {
        _portal.AlreadyCopied(new() { ["text/plain"] = "copied before"u8.ToArray() });

        Assert.That(_sync.ReadAll(), Is.EqualTo(new ClipboardRead("copied before", null, null, null, false)));
    }

    [Test]
    public void Read_BeforeAnyOwnerChange_TakesEveryFormatAlreadyCopied()
    {
        var rtf = @"{\rtf1 copied}"u8.ToArray();
        _portal.AlreadyCopied(new()
        {
            ["text/plain;charset=utf-8"] = "copied"u8.ToArray(),
            ["text/html"] = "<b>copied</b>"u8.ToArray(),
            ["text/rtf"] = rtf,
            ["image/png"] = Png,
        });

        Assert.That(_sync.ReadAll(), Is.EqualTo(new ClipboardRead("copied", Png, "<b>copied</b>", rtf, false)));
    }

    [Test]
    public void Read_BeforeAnyOwnerChange_OfAPasswordManagersCopy_IsConcealed()
    {
        _portal.AlreadyCopied(new() { ["text/plain;charset=utf-8"] = "hunter2"u8.ToArray(), ["x-kde-passwordManagerHint"] = "secret"u8.ToArray() });

        var read = _sync.ReadAll();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(read.Text, Is.EqualTo("hunter2"));
            Assert.That(read.Concealed, Is.True);
        }
    }

    [Test]
    public void Read_BeforeAnyOwnerChange_ThatFails_IsUnreadable()
    {
        _portal.AlreadyCopied(new() { ["text/plain;charset=utf-8"] = "copied"u8.ToArray() });
        _portal.ReadFails = true;

        Assert.Throws<ClipboardUnreadableException>(() => _sync.ReadAll());
    }

    [Test]
    public void Read_BeforeAnyOwnerChange_ThatTimesOut_IsUnreadable()
    {
        _portal.AlreadyCopied(new() { ["text/plain;charset=utf-8"] = "copied"u8.ToArray() });
        _portal.ReadTimesOut = true;

        Assert.Throws<ClipboardUnreadableException>(() => _sync.ReadAll());
    }

    [Test]
    public void Read_BeforeAnyOwnerChange_ThatIsDenied_IsUnreadable()
    {
        _portal.ReadDenied = true;

        Assert.Throws<ClipboardUnreadableException>(() => _sync.ReadAll());
    }

    [Test]
    public void Read_OnceAnOwnerChangeIsSeen_AsksOnlyForTheTypesOffered()
    {
        _sync.ReadAll();
        _portal.OtherAppCopies(new() { ["text/plain;charset=utf-8"] = "copied"u8.ToArray() });
        _portal.ReadTypes.Clear();

        _sync.ReadAll();

        Assert.That(_portal.ReadTypes, Is.EqualTo(["text/plain;charset=utf-8"]));
    }

    [Test]
    public void TextAlreadyCopied_IsThePrimary()
    {
        _portal.AlreadyCopied(new() { ["text/plain;charset=utf-8"] = "copied before"u8.ToArray() });

        Assert.That(_sync.GetPrimaryText(), Is.EqualTo("copied before"));
    }

    [Test]
    public void Read_OfAnOwnerGone_IsEmpty()
    {
        _portal.OtherAppCopies(new() { ["text/plain;charset=utf-8"] = "copied"u8.ToArray() });
        _portal.OwnerGone();

        Assert.That(_sync.ReadAll(), Is.EqualTo(ClipboardRead.Empty));
    }

    [Test]
    public void Read_AsksForAtMostTheCap()
    {
        _portal.OtherAppCopies(new() { ["image/png"] = Png });

        _sync.ReadAll();

        Assert.That(_portal.MaxBytes, Is.EqualTo([(int)ClipboardUtils.MaxClipboardBytes]));
    }

    [Test]
    public void Read_OverTheCap_LeavesThatFormatOut()
    {
        _portal.OtherAppCopies(new() { ["text/plain;charset=utf-8"] = "copied"u8.ToArray(), ["image/png"] = Png });
        _portal.OverCap.Add("image/png");

        var read = _sync.ReadAll();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(read.Text, Is.EqualTo("copied"));
            Assert.That(read.ImagePng, Is.Null);
        }
    }

    [Test]
    public void Read_ThatAnOwnerChangeStraddles_IsReadAgain()
    {
        _portal.OtherAppCopies(new() { ["text/plain;charset=utf-8"] = "first"u8.ToArray() });
        _portal.DuringRead = () =>
        {
            _portal.DuringRead = null;
            _portal.OtherAppCopies(new() { ["text/plain;charset=utf-8"] = "second"u8.ToArray() });
        };

        Assert.That(_sync.ReadAll().Text, Is.EqualTo("second"));
    }

    // GNOME live: the visit's claim sets the selection while the entry hash reads the clipboard, and mutter refuses the
    // read in flight ("Tried to read own selection")
    private async Task<ClipboardRead> ReadWhileAClaimLands()
    {
        _portal.ConfirmsWrites = true;
        var setting = new TaskCompletionSource();
        var release = new ManualResetEventSlim();
        _portal.DuringSetSelection = () =>
        {
            _portal.DuringSetSelection = null;
            setting.SetResult();
            release.Wait(Bound);
        };
        var claim = Task.Run(_sync.Claim);
        await setting.Task.WaitAsync(Bound);
        _portal.DuringRead = () =>
        {
            _portal.DuringRead = null;
            release.Set();
            claim.Wait(Bound);
            _portal.ReadFails = true;
        };

        var read = await Task.Run(_sync.ReadAll).WaitAsync(Bound);
        await claim.WaitAsync(Bound);
        return read;
    }

    [Test]
    public async Task Read_ThatFailsAsAClaimsSelectionLands_IsReadAgain_AsTheClaimsOwnCopy()
    {
        _portal.OtherAppCopies(new() { ["text/plain;charset=utf-8"] = "copied"u8.ToArray() });

        var read = await ReadWhileAClaimLands();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(read, Is.EqualTo(new ClipboardRead("copied", null, null, null, false)), "the claim's own copy, not unreadable");
            Assert.That(_portal.Offers, Has.Count.EqualTo(1));
        }
    }

    [Test]
    public async Task Read_BeforeAnyOwnerChange_ThatFailsAsAClaimsSelectionLands_IsReadAgain_AsTheClaimsOwnCopy()
    {
        _portal.AlreadyCopied(new() { ["text/plain;charset=utf-8"] = "copied before"u8.ToArray() });

        var read = await ReadWhileAClaimLands();

        Assert.That(read, Is.EqualTo(new ClipboardRead("copied before", null, null, null, false)));
    }

    [Test]
    public void Read_ThatFailsMidRead_WithNothingChanged_IsUnreadable()
    {
        _portal.OtherAppCopies(new() { ["text/plain;charset=utf-8"] = "copied"u8.ToArray() });
        _portal.DuringRead = () => _portal.ReadFails = true;

        using (Assert.EnterMultipleScope())
        {
            Assert.Throws<ClipboardUnreadableException>(() => _sync.ReadAll());
            Assert.That(_portal.ReadTypes, Has.Count.EqualTo(1), "read once, not again");
        }
    }

    // -- own writes --

    [Test]
    public void OwnWrite_ReadsAsNothing_AndAllowsTheEchoFallback()
    {
        _portal.OtherAppCopies(new() { ["text/plain;charset=utf-8"] = "copied"u8.ToArray() });

        _sync.SetClipboard(new ClipboardSnapshot("written", null, null));
        _portal.ReadTypes.Clear();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_sync.ReadAll(), Is.EqualTo(ClipboardRead.Empty));
            Assert.That(_sync.CanUseEchoFallback(), Is.True);
            Assert.That(_portal.ReadTypes, Is.Empty, "Hydra's own write is never read back");
        }
    }

    [Test]
    public void OwnWrite_StillOwns_WhenThePortalConfirmsIt()
    {
        _portal.ConfirmsWrites = true;

        _sync.SetClipboard(new ClipboardSnapshot("written", null, null));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_sync.ReadAll(), Is.EqualTo(ClipboardRead.Empty));
            Assert.That(_sync.CanUseEchoFallback(), Is.True);
        }
    }

    [Test]
    public void SameTextCopiedAgain_IsNoEcho()
    {
        _portal.ConfirmsWrites = true;
        _sync.SetClipboard(new ClipboardSnapshot("written", null, null));

        _portal.OtherAppCopies(new() { ["text/plain;charset=utf-8"] = "written"u8.ToArray() });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_sync.ReadAll().Text, Is.EqualTo("written"));
            Assert.That(_sync.CanUseEchoFallback(), Is.False);
        }
    }

    [Test]
    public void Write_OffersEveryFormatAtOnce()
    {
        var rtf = @"{\rtf1 written}"u8.ToArray();
        var contents = new ClipboardSnapshot("written", "selected", Png, Html: "<b>written</b>", Rtf: rtf);

        var written = _sync.SetClipboard(contents);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(written, Is.EqualTo(contents), "every format kept");
            Assert.That(_portal.Offers, Has.Count.EqualTo(1));
            Assert.That(_portal.Offers[0], Is.EquivalentTo(EveryFormat));
        }
    }

    [Test]
    public void Transfer_IsAnsweredWithTheBytesOfThatFormat()
    {
        var rtf = @"{\rtf1 written}"u8.ToArray();
        _sync.SetClipboard(new ClipboardSnapshot("wrïtten", null, Png, Html: "<b>written</b>", Rtf: rtf));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_portal.Transfer("UTF8_STRING", 1), Is.EqualTo(Encoding.UTF8.GetBytes("wrïtten")));
            Assert.That(_portal.Transfer("text/plain;charset=utf-8", 2), Is.EqualTo(Encoding.UTF8.GetBytes("wrïtten")));
            Assert.That(_portal.Transfer("text/html", 3), Is.EqualTo("<b>written</b>"u8.ToArray()));
            Assert.That(_portal.Transfer("text/rtf", 4), Is.EqualTo(rtf));
            Assert.That(_portal.Transfer("image/png", 5), Is.EqualTo(Png));
            Assert.That(_portal.Failed, Is.Empty);
        }
    }

    [Test]
    public void Transfer_OfAFormatNotOffered_Fails()
    {
        _sync.SetClipboard(new ClipboardSnapshot("written", null, null));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_portal.Transfer("image/png", 7), Is.Null);
            Assert.That(_portal.Failed, Is.EqualTo(new uint[] { 7 }));
        }
    }

    [Test]
    public void Transfer_ServesTheNewestWrite()
    {
        _sync.SetClipboard(new ClipboardSnapshot("first", null, null));
        _sync.SetClipboard(new ClipboardSnapshot(null, null, Png));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_portal.Transfer("image/png", 1), Is.EqualTo(Png));
            Assert.That(_portal.Transfer("text/plain", 2), Is.Null);
        }
    }

    [Test]
    public void ConcealedWrite_OffersTheHint_AndServesSecret()
    {
        _sync.SetClipboard(new ClipboardSnapshot("hunter2", null, null, Concealed: true));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_portal.Offers[^1], Does.Contain("x-kde-passwordManagerHint"));
            Assert.That(_portal.Transfer("x-kde-passwordManagerHint", 1), Is.EqualTo("secret"u8.ToArray()));
        }
    }

    [Test]
    public void PlainWrite_OffersNoHint()
    {
        _sync.SetClipboard(new ClipboardSnapshot("plain", null, null));

        Assert.That(_portal.Offers[^1], Does.Not.Contain("x-kde-passwordManagerHint"));
    }

    [Test]
    public void ConcealedWrite_ReadsAsConcealedInAnotherClient()
    {
        _sync.SetClipboard(new ClipboardSnapshot("hunter2", null, null, Concealed: true));
        var other = new FakePortalClipboard();
        var otherSync = Create(other);

        try
        {
            Assert.That(otherSync.Connected(), Is.True);
            other.OtherAppCopies(_portal.Offers[^1].ToDictionary(t => t, t => _portal.Transfer(t, 1) ?? []));
            var read = otherSync.ReadAll();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(read.Text, Is.EqualTo("hunter2"));
                Assert.That(read.Concealed, Is.True);
            }
        }
        finally
        {
            otherSync.DisposeAsync().AsTask().Wait();
        }
    }

    [Test]
    public void Clear_OffersThePlaceholderWithTheHint()
    {
        _sync.SetClipboard(new ClipboardSnapshot("hunter2", "selected", Png, Concealed: true));

        Assert.That(_sync.ClearClipboard(), Is.True);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_portal.Offers[^1], Is.EquivalentTo(PlaceholderFormats));
            Assert.That(_portal.Transfer("text/plain", 1), Is.EqualTo(Encoding.UTF8.GetBytes(ClipboardSnapshot.PlaceholderText)));
            Assert.That(_portal.Transfer("image/png", 2), Is.Null);
            Assert.That(_sync.CanUseEchoFallback(), Is.True);
            Assert.That(_sync.GetPrimaryText(), Is.EqualTo("selected"));
        }
    }

    [Test]
    public void Write_ThePortalRefuses_ReplacesNothing()
    {
        _portal.OtherAppCopies(new() { ["text/plain;charset=utf-8"] = "copied"u8.ToArray() });
        var before = _sync.ChangeCount();
        _portal.SetSelectionFails = true;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_sync.SetClipboard(new ClipboardSnapshot("written", null, null)), Is.Null);
            Assert.That(_sync.ChangeCount(), Is.EqualTo(before));
            Assert.That(_sync.CanUseEchoFallback(), Is.False);
            Assert.That(_sync.ReadAll().Text, Is.EqualTo("copied"));
        }
    }

    [Test]
    public void SelectionOnly_LeavesTheClipboard()
    {
        _portal.OtherAppCopies(new() { ["text/plain;charset=utf-8"] = "copied"u8.ToArray() });

        ClipboardUtils.ApplyPreservingFiles(_sync, new ClipboardSnapshot(null, "selected", null), NullLogger.Instance, "test");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_portal.Offers, Is.Empty);
            Assert.That(_sync.ReadAll().Text, Is.EqualTo("copied"));
            Assert.That(_sync.GetPrimaryText(), Is.EqualTo("selected"));
        }
    }

    // -- the change counter --

    [Test]
    public void ChangeCount_MovesOnAWriteAndOnAnotherClientsCopy_NotOnARead()
    {
        _portal.ConfirmsWrites = true;
        var start = _sync.ChangeCount();
        _sync.SetClipboard(new ClipboardSnapshot("written", null, null));
        var afterWrite = _sync.ChangeCount();
        _portal.OtherAppCopies(new() { ["text/plain;charset=utf-8"] = "written"u8.ToArray() });
        var afterCopy = _sync.ChangeCount();
        _sync.ReadAll();
        var afterRead = _sync.ChangeCount();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(start, Is.Not.Null);
            Assert.That(afterWrite, Is.GreaterThan(start ?? long.MaxValue));
            Assert.That(afterCopy, Is.GreaterThan(afterWrite ?? long.MaxValue), "an identical copy moves it too");
            Assert.That(afterRead, Is.EqualTo(afterCopy));
        }
    }

    [Test]
    public void ChangeCount_IsUnknown_UntilThePortalIsConnected()
    {
        var pending = new TaskCompletionSource<IPortalClipboard?>();
        var sync = new PortalClipboardSync(_ => pending.Task, () => new NullClipboardSync(), _time, NullLogger.Instance);
        try
        {
            Assert.That(sync.ChangeCount(), Is.Null);
            pending.SetResult(new FakePortalClipboard());
            using (Assert.EnterMultipleScope())
            {
                Assert.That(sync.Connected(), Is.True);
                Assert.That(sync.ChangeCount(), Is.Not.Null);
            }
        }
        finally
        {
            sync.DisposeAsync().AsTask().Wait();
        }
    }

    [Test]
    public void ChangeCount_DoesNotMove_WhenTheOwnerGoes()
    {
        _portal.OtherAppCopies(new() { ["text/plain;charset=utf-8"] = "copied"u8.ToArray() });
        var before = _sync.ChangeCount();

        _portal.OwnerGone();

        Assert.That(_sync.ChangeCount(), Is.EqualTo(before));
    }

    [Test]
    public void WroteAt_IsTheCountRightAfterTheWrite_AndAForeignCopyMovesPastIt()
    {
        _portal.ConfirmsWrites = true;
        _sync.SetClipboard(new ClipboardSnapshot("written", null, null));
        var afterWrite = _sync.ChangeCount();
        var wroteAt = _sync.WroteAt();

        _portal.OtherAppCopies(new() { ["text/plain;charset=utf-8"] = "written"u8.ToArray() });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(wroteAt, Is.Not.Null.And.EqualTo(afterWrite));
            Assert.That(_sync.WroteAt(), Is.EqualTo(wroteAt), "another client's copy is no write of Hydra's");
            Assert.That(_sync.ChangeCount(), Is.GreaterThan(wroteAt ?? long.MaxValue));
        }
    }

    [Test]
    public void WroteAt_FollowsAClear()
    {
        _sync.SetClipboard(new ClipboardSnapshot("written", null, null));
        _sync.ClearClipboard();

        Assert.That(_sync.WroteAt(), Is.EqualTo(_sync.ChangeCount()));
    }

    [Test]
    public void WroteAt_IsNull_BeforeAnyWrite()
    {
        _portal.OtherAppCopies(new() { ["text/plain;charset=utf-8"] = "copied"u8.ToArray() });

        Assert.That(_sync.WroteAt(), Is.Null);
    }

    // a notice of the owner Hydra's write replaced, delivered after the write, is older than it
    [Test]
    public void OwnerChange_BeforeTheWriteIsConfirmed_IsOlderThanIt()
    {
        _sync.SetClipboard(new ClipboardSnapshot("written", null, null));
        var wroteAt = _sync.WroteAt();

        _portal.OtherAppCopies(new() { ["text/plain;charset=utf-8"] = "older"u8.ToArray() });
        _portal.ConfirmOwnWrite();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_sync.ChangeCount(), Is.EqualTo(wroteAt));
            Assert.That(_sync.CanUseEchoFallback(), Is.True);
            Assert.That(_sync.ReadAll(), Is.EqualTo(ClipboardRead.Empty));
        }
    }

    [Test]
    public void OwnerChange_ToAWriteNeverConfirmed_CountsOnceTheWaitIsOver()
    {
        _sync.SetClipboard(new ClipboardSnapshot("written", null, null));
        var wroteAt = _sync.WroteAt();
        _portal.OtherAppCopies(new() { ["text/plain;charset=utf-8"] = "copied"u8.ToArray() });

        _time.Advance(TimeSpan.FromSeconds(2));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_sync.ChangeCount(), Is.GreaterThan(wroteAt ?? long.MaxValue));
            Assert.That(_sync.CanUseEchoFallback(), Is.False);
            Assert.That(_sync.ReadAll().Text, Is.EqualTo("copied"));
        }
    }

    [Test]
    public void OwnerChange_AfterTheWriteIsConfirmed_CountsAtOnce()
    {
        _portal.ConfirmsWrites = true;
        _sync.SetClipboard(new ClipboardSnapshot("written", null, null));
        var wroteAt = _sync.WroteAt();

        _portal.OtherAppCopies(new() { ["text/plain;charset=utf-8"] = "copied"u8.ToArray() });

        Assert.That(_sync.ChangeCount(), Is.EqualTo(wroteAt + 1));
    }

    // -- the selection, which the portal has none of --

    [Test]
    public void Selection_IsKeptHere_BesideAWrite()
    {
        _portal.ConfirmsWrites = true;
        _sync.SetClipboard(new ClipboardSnapshot("written", "relayed selection", null));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_sync.GetPrimaryText(), Is.EqualTo("relayed selection"));
            Assert.That(_portal.Offers, Has.Count.EqualTo(1), "no selection is offered to the portal");
        }
    }

    [Test]
    public void TextCopyAfterwards_IsThePrimary()
    {
        _portal.ConfirmsWrites = true;
        _sync.SetClipboard(new ClipboardSnapshot("written", "relayed selection", null));

        _portal.OtherAppCopies(new() { ["text/plain;charset=utf-8"] = "copied"u8.ToArray() });

        Assert.That(_sync.GetPrimaryText(), Is.EqualTo("copied"));
    }

    [Test]
    public void ImageCopyAfterwards_LeavesTheRelayedSelection()
    {
        _portal.ConfirmsWrites = true;
        _sync.SetClipboard(new ClipboardSnapshot("written", "relayed selection", null));

        _portal.OtherAppCopies(new() { ["text/plain;charset=utf-8"] = "beside the image"u8.ToArray(), ["image/png"] = Png });

        Assert.That(_sync.GetPrimaryText(), Is.EqualTo("relayed selection"));
    }

    [Test]
    public void ConcealedCopyAfterwards_LeavesTheRelayedSelection()
    {
        _portal.ConfirmsWrites = true;
        _sync.SetClipboard(new ClipboardSnapshot("written", "relayed selection", null));

        _portal.OtherAppCopies(new() { ["text/plain;charset=utf-8"] = "hunter2"u8.ToArray(), ["x-kde-passwordManagerHint"] = "secret"u8.ToArray() });

        Assert.That(_sync.GetPrimaryText(), Is.EqualTo("relayed selection"));
    }

    [Test]
    public void SetPrimaryText_IsKept()
    {
        _portal.OtherAppCopies(new() { ["text/plain;charset=utf-8"] = "copied"u8.ToArray() });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_sync.SetPrimaryText("selected"), Is.True);
            Assert.That(_sync.GetPrimaryText(), Is.EqualTo("selected"));
        }
    }

    // -- the shared MIME helper --

    // what ReadOffered asks for, each answer from a table: bytes, or a type that could not be read
    private static ClipboardRead ReadOffered(Dictionary<string, byte[]> offered, params string[] failing) =>
        WaylandMime.ReadOffered(offered.Keys.Concat(failing).ToHashSet(),
            (type, _) => failing.Contains(type) ? throw new ClipboardUnreadableException($"{type} broke") : offered.GetValueOrDefault(type),
            NullLogger.Instance);

    [Test]
    public void Mime_PrefersTheMimeTypes_OverTheX11Atoms()
    {
        var read = ReadOffered(new()
        {
            ["UTF8_STRING"] = "atom"u8.ToArray(),
            ["STRING"] = "latin"u8.ToArray(),
            ["text/plain"] = "mime"u8.ToArray(),
        });

        Assert.That(read.Text, Is.EqualTo("mime"));
    }

    [Test]
    public void Mime_TakesALegacyAtom_WhenThatIsAllThereIs() =>
        Assert.That(ReadOffered(new() { ["STRING"] = "latin"u8.ToArray() }).Text, Is.EqualTo("latin"));

    [Test]
    public void Mime_ATextTypeThatFails_FallsThroughToTheNext()
    {
        var read = ReadOffered(new() { ["text/plain"] = "next"u8.ToArray() }, "text/plain;charset=utf-8");

        Assert.That(read.Text, Is.EqualTo("next"));
    }

    [Test]
    public void Mime_AnEmptyTextType_FallsThroughToTheNext()
    {
        var read = ReadOffered(new() { ["text/plain;charset=utf-8"] = [], ["UTF8_STRING"] = "next"u8.ToArray() });

        Assert.That(read.Text, Is.EqualTo("next"));
    }

    [Test]
    public void Mime_EveryTextTypeFailing_IsUnreadable() =>
        Assert.Throws<ClipboardUnreadableException>(() => ReadOffered([], "text/plain;charset=utf-8", "UTF8_STRING", "text/plain"));

    [Test]
    public void Mime_AnImageThatFails_IsLeftOut_BesideTheText()
    {
        var read = ReadOffered(new() { ["text/plain"] = "text"u8.ToArray(), ["text/html"] = "<b>text</b>"u8.ToArray() }, "image/png");

        Assert.That(read, Is.EqualTo(new ClipboardRead("text", null, "<b>text</b>", null, false)));
    }

    [Test]
    public void Mime_TheImageAlone_WhenTheTextFails()
    {
        var read = ReadOffered(new() { ["image/png"] = Png }, "text/plain");

        Assert.That(read, Is.EqualTo(new ClipboardRead(null, Png, null, null, false)));
    }

    [Test]
    public void Mime_OnlyFailures_IsUnreadable_NeverEmpty() =>
        Assert.Throws<ClipboardUnreadableException>(() => ReadOffered([], "image/png", "text/html"));

    [Test]
    public void Mime_NothingOffered_IsEmpty() => Assert.That(ReadOffered([]), Is.SameAs(ClipboardRead.Empty));

    // a password manager's mark that could not be read is taken as there, so its copy is never relayed as plain text
    [Test]
    public void Mime_AHintThatFails_Conceals()
    {
        var read = ReadOffered(new() { ["text/plain"] = "hunter2"u8.ToArray() }, "x-kde-passwordManagerHint");

        Assert.That(read.Concealed, Is.True);
    }

    [Test]
    public void Mime_ReadsTheHint_UnderItsOwnCap()
    {
        var caps = new Dictionary<string, int>();

        WaylandMime.ReadOffered(new HashSet<string> { "text/plain", "x-kde-passwordManagerHint" }, (type, max) =>
        {
            caps[type] = max;
            return "x"u8.ToArray();
        }, NullLogger.Instance);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(caps["x-kde-passwordManagerHint"], Is.EqualTo(WaylandMime.HintMaxBytes));
            Assert.That(caps["text/plain"], Is.EqualTo(int.MaxValue));
        }
    }

    [TestCase("secret", true)]
    [TestCase("secret\n", true)]
    [TestCase("public", false)]
    [TestCase("", false)]
    public void Mime_TheHintConceals_OnlyWhenSecret(string hint, bool secret) =>
        Assert.That(WaylandMime.IsSecret(Encoding.UTF8.GetBytes(hint)), Is.EqualTo(secret));

    [Test]
    public void Mime_NoHintIsNotSecret() => Assert.That(WaylandMime.IsSecret(null), Is.False);

    [Test]
    public void Mime_ReadOfNothing_IsEmpty() =>
        Assert.That(WaylandMime.Read(null, [], null, [], concealed: true), Is.SameAs(ClipboardRead.Empty));

    [Test]
    public void Mime_ReadKeepsEveryFormat()
    {
        var rtf = @"{\rtf1 x}"u8.ToArray();

        Assert.That(WaylandMime.Read("x"u8.ToArray(), Png, "<b>x</b>"u8.ToArray(), rtf, concealed: true),
            Is.EqualTo(new ClipboardRead("x", Png, "<b>x</b>", rtf, true)));
    }

    [Test]
    public void Mime_CappedRead_TakesAll_UpToTheCap()
    {
        var data = new byte[WaylandMime.ChunkBytes * 2 + 5];
        Random.Shared.NextBytes(data);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(WaylandMime.ReadCapped(new MemoryStream(data), data.Length), Is.EqualTo(data));
            Assert.That(WaylandMime.ReadCapped(new MemoryStream(data), data.Length - 1), Is.Null);
        }
    }

    [Test]
    public void Mime_CappedRead_WaitsBeforeEachRead()
    {
        var waits = 0;

        WaylandMime.ReadCapped(new MemoryStream(new byte[WaylandMime.ChunkBytes + 1]), long.MaxValue, () => waits++);

        Assert.That(waits, Is.EqualTo(3), "two chunks and the end");
    }

    // -- the transfer write's deadline, against a real pipe --

    [Test]
    public void TransferWrite_ToAReader_WritesItAll()
    {
        if (OperatingSystem.IsWindows()) Assert.Ignore("poll() is POSIX");
        var data = new byte[256 * 1024];
        Random.Shared.NextBytes(data);
        Task<byte[]> read;
        // the server is closed once written, so the reader sees the end of the pipe; the reader owns the client end
        using (var server = new AnonymousPipeServerStream(PipeDirection.Out))
        {
            var clientHandle = server.ClientSafePipeHandle;
            read = Task.Run(() =>
            {
                using var client = new AnonymousPipeClientStream(PipeDirection.In, clientHandle);
                using var all = new MemoryStream();
                client.CopyTo(all);
                return all.ToArray();
            });

            PortalClipboard.WriteAll(server.SafePipeHandle, data, DateTime.UtcNow + TimeSpan.FromSeconds(10));
        }

        Assert.That(read.Wait(TimeSpan.FromSeconds(10)) ? read.Result : null, Is.EqualTo(data));
    }

    [Test]
    public void TransferWrite_ToARequesterThatStopsReading_GivesUpAtTheDeadline()
    {
        if (OperatingSystem.IsWindows()) Assert.Ignore("poll() is POSIX");
        using var server = new AnonymousPipeServerStream(PipeDirection.Out);
        using var client = new AnonymousPipeClientStream(PipeDirection.In, server.ClientSafePipeHandle);
        // far more than any pipe buffer, never read
        var data = new byte[4 * 1024 * 1024];

        var serverHandle = server.SafePipeHandle;
        var write = Task.Run(() => PortalClipboard.WriteAll(serverHandle, data, DateTime.UtcNow + TimeSpan.FromMilliseconds(300)));

        Assert.That(() => write.Wait(TimeSpan.FromSeconds(10)), Throws.InstanceOf<AggregateException>().With.InnerException.InstanceOf<TimeoutException>());
    }

    // -- a portal request's answer, on the predicted path or a pre-0.9 portal's own --

    [Test]
    public void RequestAnswer_OnThePredictedPath_AnswersAtOnce()
    {
        var answer = new PortalBus.RequestAnswer("/request/1");

        answer.Answered("/request/1", new PortalResponse(0, []));

        Assert.That(answer.Response.IsCompletedSuccessfully, Is.True);
    }

    [Test]
    public void RequestAnswer_OnAnOldPortalsPath_BeforeTheCallReturns_IsKept()
    {
        var answer = new PortalBus.RequestAnswer("/request/1");
        var response = new PortalResponse(1, []);

        answer.Answered("/old/7", response);
        var early = answer.Response.IsCompleted;
        answer.Answering("/old/7");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(early, Is.False, "not taken before the call names its path");
            Assert.That(answer.Response.Result, Is.SameAs(response));
            Assert.That(answer.Path, Is.EqualTo("/old/7"));
        }
    }

    [Test]
    public void RequestAnswer_OnAnOldPortalsPath_AfterTheCallReturns()
    {
        var answer = new PortalBus.RequestAnswer("/request/1");
        var response = new PortalResponse(0, []);

        answer.Answering("/old/7");
        answer.Answered("/request/1", new PortalResponse(2, []));
        answer.Answered("/old/7", response);

        Assert.That(answer.Response.Result, Is.SameAs(response), "another request's answer is not ours");
    }

    [Test]
    public void RequestAnswer_ForAnotherRequest_IsNotOurs()
    {
        var answer = new PortalBus.RequestAnswer("/request/1");

        answer.Answered("/request/2", new PortalResponse(0, []));
        answer.Answering("/request/1");

        Assert.That(answer.Response.IsCompleted, Is.False);
    }

    [Test]
    public void RequestAnswer_OnALostBus_Fails()
    {
        var answer = new PortalBus.RequestAnswer("/request/1");

        answer.Lost(new IOException("the bus went"));

        Assert.That(answer.Response.IsFaulted, Is.True);
    }

    // -- no portal clipboard --

    [Test]
    public async Task WithoutAPortalClipboard_EveryCallGoesToTheFallback()
    {
        var fallback = new FakeClipboardSync();
        fallback.SetupText("from the fallback");
        var sync = Create(null, fallback);
        try
        {
            Assert.That(sync.Connected(), Is.True);
            var written = sync.SetClipboard(new ClipboardSnapshot("written", "selected", null));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(written?.Text, Is.EqualTo("written"));
                Assert.That(fallback.Text, Is.EqualTo("written"));
                Assert.That(fallback.SetClipboardCallCount, Is.EqualTo(1));
                Assert.That(sync.ReadAll().Text, Is.EqualTo("written"));
                Assert.That(fallback.ReadCallCount, Is.EqualTo(1));
                Assert.That(sync.GetPrimaryText(), Is.EqualTo("selected"));
                Assert.That(sync.ChangeCount(), Is.EqualTo(fallback.ChangeCount()));
                Assert.That(sync.WroteAt(), Is.EqualTo(fallback.WroteAt()));
                Assert.That(sync.ClearClipboard(), Is.True);
                Assert.That(fallback.ClearClipboardCallCount, Is.EqualTo(1));
            }
            fallback.HasFileClipboardValue = true;
            fallback.CanUseEchoFallbackValue = false;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(sync.HasFileClipboard(), Is.True);
                Assert.That(sync.CanUseEchoFallback(), Is.False);
            }
        }
        finally
        {
            await sync.DisposeAsync();
        }
    }

    [Test]
    public async Task WithoutAnyClipboard_BehavesAsTheNullSync()
    {
        var sync = Create(null, new NullClipboardSync());
        try
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(sync.ReadAll(), Is.EqualTo(ClipboardRead.Empty));
                Assert.That(sync.SetClipboard(new ClipboardSnapshot("written", null, null)), Is.Null);
                Assert.That(sync.ClearClipboard(), Is.False);
                Assert.That(sync.ChangeCount(), Is.Null);
                Assert.That(sync.GetPrimaryText(), Is.Null);
                Assert.That(sync.SetPrimaryText("selected"), Is.False);
            }
        }
        finally
        {
            await sync.DisposeAsync();
        }
    }

    // -- a visit's claim, and serving after the release --

    // a capture on, the session claims the selection; then the capture is released, and the portal sets no selection
    private void ClaimThenRelease()
    {
        _sync.Claim();
        _portal.CaptureActive = false;
    }

    [Test]
    public void Claim_ReadsTheMastersOwnCopy_AndOffersOnlyWhatItCanServe()
    {
        _portal.OtherAppCopies(new() { ["text/plain;charset=utf-8"] = "copied"u8.ToArray(), ["text/html"] = "<i>copied</i>"u8.ToArray() });
        var count = _sync.ChangeCount();

        _sync.Claim();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_portal.Offers.Single(), Is.EquivalentTo((string[])[.. WaylandMime.Text, "text/html"]), "its text as every text type, and its html");
            Assert.That(_portal.Offers.Single(), Does.Not.Contain("text/rtf").And.Not.Contain("image/png").And.Not.Contain("x-kde-passwordManagerHint"));
            Assert.That(_sync.ReadAll(), Is.EqualTo(new ClipboardRead("copied", null, "<i>copied</i>", null, false)), "the own copy reads as itself");
            Assert.That(_sync.ChangeCount(), Is.EqualTo(count), "the claim is no copy and no write");
            Assert.That(_sync.CanUseEchoFallback(), Is.False, "the own copy is the user's, not Hydra's write");
            Assert.That(_sync.GetPrimaryText(), Is.EqualTo("copied"), "a text copy is still the primary");
        }
    }

    [Test]
    public void Claim_KeepsTheMastersOwnFormats_BesideThoseHydraCarries()
    {
        _portal.OtherAppCopies(new()
        {
            ["text/plain;charset=utf-8"] = "file:///tmp/a"u8.ToArray(),
            ["text/uri-list"] = "file:///tmp/a\r\n"u8.ToArray(),
            ["image/jpeg"] = [1, 2, 3],
            ["STRING"] = "file:///tmp/a"u8.ToArray(),
        });

        ClaimThenRelease();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_portal.Offers.Single(), Is.SupersetOf(["text/uri-list", "image/jpeg", "STRING"]));
            Assert.That(_portal.Transfer("text/uri-list", 1), Is.EqualTo("file:///tmp/a\r\n"u8.ToArray()));
            Assert.That(_portal.Transfer("image/jpeg", 2), Is.EqualTo(new byte[] { 1, 2, 3 }));
            Assert.That(_portal.Transfer("STRING", 3), Is.EqualTo("file:///tmp/a"u8.ToArray()));
        }
    }

    [Test]
    public void Claim_WhenHydraAlreadyHoldsTheSelection_ReadsNothing()
    {
        _portal.OtherAppCopies(new() { ["text/plain;charset=utf-8"] = "copied"u8.ToArray() });
        _sync.Claim();
        var reads = _portal.ReadTypes.Count;

        _sync.Claim();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_portal.Offers, Has.Count.EqualTo(1));
            Assert.That(_portal.ReadTypes, Has.Count.EqualTo(reads));
        }
    }

    [Test]
    public void Claim_AgainWithNothingChanged_ReadsAndSetsNothing()
    {
        _portal.ConfirmsWrites = true;
        _portal.OtherAppCopies(new() { ["text/plain;charset=utf-8"] = "copied"u8.ToArray() });
        ClaimThenRelease();
        var reads = _portal.ReadTypes.Count;
        _portal.CaptureActive = true;

        _sync.Claim();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_portal.Offers, Has.Count.EqualTo(1));
            Assert.That(_portal.ReadTypes, Has.Count.EqualTo(reads));
            Assert.That(_sync.ReadAll().Text, Is.EqualTo("copied"));
        }
    }

    [Test]
    public void Claim_AfterTheSessionsSelectionWentAway_SetsTheLastClaimsCopyWithoutReading()
    {
        _portal.OtherAppCopies(new() { ["text/plain;charset=utf-8"] = "copied"u8.ToArray(), ["text/html"] = "<i>copied</i>"u8.ToArray() });
        ClaimThenRelease();
        _time.Advance(TimeSpan.FromSeconds(1));
        _portal.OwnerGone();
        var reads = _portal.ReadTypes.Count;
        var count = _sync.ChangeCount();
        _portal.CaptureActive = true;

        _sync.Claim();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_portal.ReadTypes, Has.Count.EqualTo(reads), "no copy since the last claim: its read still stands");
            Assert.That(_portal.Offers, Has.Count.EqualTo(2), "the session holds the selection no longer, so it is set again");
            Assert.That(_portal.Offers[^1], Is.EqualTo(_portal.Offers[0]));
            Assert.That(_sync.ChangeCount(), Is.EqualTo(count));
            Assert.That(_sync.ReadAll(), Is.EqualTo(new ClipboardRead("copied", null, "<i>copied</i>", null, false)));
            Assert.That(_portal.Transfer("text/html", 1), Is.EqualTo("<i>copied</i>"u8.ToArray()));
        }
    }

    [Test]
    public void Claim_AfterAnotherAppsCopy_ReadsAgain()
    {
        _portal.OtherAppCopies(new() { ["text/plain;charset=utf-8"] = "copied"u8.ToArray() });
        ClaimThenRelease();
        _time.Advance(TimeSpan.FromSeconds(1));
        _portal.OtherAppCopies(new() { ["text/plain;charset=utf-8"] = "copied again"u8.ToArray() });
        var reads = _portal.ReadTypes.Count;
        _portal.CaptureActive = true;

        _sync.Claim();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_portal.ReadTypes, Has.Count.GreaterThan(reads));
            Assert.That(_portal.Offers, Has.Count.EqualTo(2));
            Assert.That(_sync.ReadAll().Text, Is.EqualTo("copied again"));
            Assert.That(_portal.Transfer("text/plain;charset=utf-8", 1), Is.EqualTo("copied again"u8.ToArray()));
        }
    }

    [Test]
    public void Claim_OfAClipboardThatCannotBeRead_ClaimsNothing()
    {
        _portal.OtherAppCopies(new() { ["text/plain;charset=utf-8"] = "copied"u8.ToArray() });
        _portal.ReadFails = true;

        _sync.Claim();

        Assert.That(_portal.Offers, Is.Empty);
    }

    [Test]
    public void AfterAnUnchangedReturn_TheOwnCopyIsServed()
    {
        _portal.OtherAppCopies(new() { ["text/plain;charset=utf-8"] = "copied"u8.ToArray(), ["text/html"] = "<i>copied</i>"u8.ToArray() });
        ClaimThenRelease();
        _sync.PullSent(Task.CompletedTask);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_portal.Transfer("text/plain;charset=utf-8", 1), Is.EqualTo("copied"u8.ToArray()));
            Assert.That(_portal.Transfer("UTF8_STRING", 2), Is.EqualTo("copied"u8.ToArray()));
            Assert.That(_portal.Transfer("text/html", 3), Is.EqualTo("<i>copied</i>"u8.ToArray()));
        }
    }

    [Test]
    public void AfterAChangedReturn_ThePullsAnswerIsServed_AsHydrasWrite()
    {
        _portal.OtherAppCopies(new() { ["text/plain;charset=utf-8"] = "copied"u8.ToArray() });
        ClaimThenRelease();
        var count = _sync.ChangeCount();

        var written = _sync.SetClipboard(new ClipboardSnapshot("from the slave", null, null));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(written, Is.Not.Null, "the session's selection serves it");
            Assert.That(_portal.Offers, Has.Count.EqualTo(1), "set once, at the claim");
            Assert.That(_portal.Transfer("text/plain", 1), Is.EqualTo("from the slave"u8.ToArray()));
            Assert.That(_portal.Transfer("STRING", 2), Is.EqualTo("from the slave"u8.ToArray()), "every text type the claim offered");
            Assert.That(_sync.ChangeCount(), Is.EqualTo(count + 1), "a write of Hydra's");
            Assert.That(_sync.WroteAt(), Is.EqualTo(_sync.ChangeCount()));
            Assert.That(_sync.ReadAll(), Is.SameAs(ClipboardRead.Empty), "Hydra's own write reads as nothing");
            Assert.That(_sync.CanUseEchoFallback(), Is.True);
        }
    }

    [Test]
    public void AnAnswerAfterTheReturn_WithTheOffersFormats_IsServed()
    {
        _portal.OtherAppCopies(new() { ["text/plain;charset=utf-8"] = "copied"u8.ToArray(), ["image/png"] = Png });
        ClaimThenRelease();

        var written = _sync.SetClipboard(new ClipboardSnapshot("from the slave", null, [9, 9]));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(written, Is.Not.Null);
            Assert.That(_portal.Transfer("image/png", 1), Is.EqualTo("\t\t"u8.ToArray()));
            Assert.That(_portal.Transfer("UTF8_STRING", 2), Is.EqualTo("from the slave"u8.ToArray()));
        }
    }

    [Test]
    public void AnAnswerAfterTheReturn_WithAnExtraHtml_IsServedAsItsText_TheHtmlDropped()
    {
        _portal.OtherAppCopies(new() { ["text/plain;charset=utf-8"] = "copied"u8.ToArray() });
        ClaimThenRelease();
        var count = _sync.ChangeCount();

        var written = _sync.SetClipboard(new ClipboardSnapshot("from the browser", null, null, Html: "<b>from the browser</b>"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(written, Is.Not.Null);
            Assert.That(_sync.ChangeCount(), Is.EqualTo(count + 1));
            Assert.That(_portal.Transfer("text/plain;charset=utf-8", 1), Is.EqualTo("from the browser"u8.ToArray()));
            Assert.That(_portal.Transfer("TEXT", 2), Is.EqualTo("from the browser"u8.ToArray()));
            Assert.That(_portal.Transfer("text/html", 3), Is.Null, "never offered, so not served");
            Assert.That(_portal.Offers, Has.Count.EqualTo(1), "the offer stands as the claim set it");
        }
    }

    [Test]
    public void AnAnswerAfterTheReturn_WithoutTheOfferedHtmlAndRtf_HasThemMadeFromItsText()
    {
        _portal.OtherAppCopies(new()
        {
            ["text/plain;charset=utf-8"] = "copied"u8.ToArray(),
            ["text/html"] = "<i>copied</i>"u8.ToArray(),
            ["text/rtf"] = @"{\rtf1 copied}"u8.ToArray(),
        });
        ClaimThenRelease();

        var written = _sync.SetClipboard(new ClipboardSnapshot("a < b & \"c\" {d}\\é\n😀", null, null));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(written, Is.Not.Null);
            Assert.That(Encoding.UTF8.GetString(_portal.Transfer("text/html", 1)!),
                Is.EqualTo("<meta charset=\"utf-8\"><div>a &lt; b &amp; &quot;c&quot; {d}\\é<br>😀</div>"));
            Assert.That(Encoding.ASCII.GetString(_portal.Transfer("text/rtf", 2)!),
                Is.EqualTo(@"{\rtf1\ansi\uc1 a < b & ""c"" \{d\}\\\u233?\par \u-10179?\u-8704?}"));
            Assert.That(_portal.Failed, Is.Empty);
        }
    }

    [Test]
    public void AnAnswerAfterTheReturn_WithTextForAnOfferedImage_Fails_AndLeavesTheOwnCopyServed()
    {
        _portal.OtherAppCopies(new() { ["text/plain;charset=utf-8"] = "copied"u8.ToArray(), ["image/png"] = Png });
        ClaimThenRelease();
        var count = _sync.ChangeCount();

        var written = _sync.SetClipboard(new ClipboardSnapshot("from the slave", null, null));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(written, Is.Null, "the selection offers an image the answer has none of, and the portal sets none without the capture");
            Assert.That(_sync.ChangeCount(), Is.EqualTo(count));
            Assert.That(_portal.Transfer("image/png", 1), Is.EqualTo(Png));
            Assert.That(_portal.Transfer("text/plain;charset=utf-8", 2), Is.EqualTo("copied"u8.ToArray()));
        }
    }

    [Test]
    public void AnAnswerAfterTheReturn_WithAnImageForOfferedText_Fails()
    {
        _portal.OtherAppCopies(new() { ["text/plain;charset=utf-8"] = "copied"u8.ToArray() });
        ClaimThenRelease();

        var written = _sync.SetClipboard(new ClipboardSnapshot(null, null, Png));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(written, Is.Null);
            Assert.That(_portal.Transfer("text/plain;charset=utf-8", 1), Is.EqualTo("copied"u8.ToArray()));
        }
    }

    [Test]
    public void AnAnswerAfterTheReturn_LackingAnOwnersType_IsServed_ThatTypeAloneRefused()
    {
        _portal.OtherAppCopies(new() { ["text/plain;charset=utf-8"] = "copied"u8.ToArray(), ["text/uri-list"] = "file:///tmp/a\r\n"u8.ToArray() });
        ClaimThenRelease();

        var written = _sync.SetClipboard(new ClipboardSnapshot("from the slave", null, null));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(written, Is.Not.Null);
            Assert.That(_portal.Transfer("text/uri-list", 1), Is.Null, "the old copy's URI list is not served beside the new text");
            Assert.That(_portal.Failed, Is.EqualTo([1u]));
            Assert.That(_portal.Transfer("text/plain;charset=utf-8", 2), Is.EqualTo("from the slave"u8.ToArray()));
        }
    }

    [Test]
    public async Task ATransferBeforeThePullsAnswer_WaitsForIt_ThenServesIt()
    {
        _portal.OtherAppCopies(new() { ["text/plain;charset=utf-8"] = "copied"u8.ToArray() });
        ClaimThenRelease();
        var settled = new TaskCompletionSource();
        _sync.PullSent(settled.Task);

        var pasted = Task.Run(() => _portal.Transfer("text/plain;charset=utf-8", 1));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(await _time.Started.Next("the transfer's wait for the pull"), Is.EqualTo(PortalClipboardSync.TransferWait));
            Assert.That(pasted.IsCompleted, Is.False);
        }
        _sync.SetClipboard(new ClipboardSnapshot("from the slave", null, null));
        settled.SetResult();

        Assert.That(await pasted.WaitAsync(Bound), Is.EqualTo("from the slave"u8.ToArray()));
    }

    [Test]
    public async Task ATransferWhosePullNeverSettles_ServesWhatItHas_OnceTheWaitIsOver()
    {
        _portal.OtherAppCopies(new() { ["text/plain;charset=utf-8"] = "copied"u8.ToArray() });
        ClaimThenRelease();
        _sync.PullSent(new TaskCompletionSource().Task);

        var pasted = Task.Run(() => _portal.Transfer("text/plain;charset=utf-8", 1));
        await _time.Started.Next("the transfer's wait for the pull");
        _time.Advance(PortalClipboardSync.TransferWait);

        Assert.That(await pasted.WaitAsync(Bound), Is.EqualTo("copied"u8.ToArray()));
    }

    [Test]
    public async Task APullThatNeverSettles_IsWaitedOutOnce_NotOncePerTransfer()
    {
        _portal.OtherAppCopies(new() { ["text/plain;charset=utf-8"] = "copied"u8.ToArray(), ["text/html"] = "<i>copied</i>"u8.ToArray() });
        ClaimThenRelease();
        _sync.PullSent(new TaskCompletionSource().Task);
        var first = Task.Run(() => _portal.Transfer("text/plain;charset=utf-8", 1));
        await _time.Started.Next("the first transfer's wait for the pull");
        _time.Advance(PortalClipboardSync.TransferWait);
        await first.WaitAsync(Bound);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_portal.Transfer("text/html", 2), Is.EqualTo("<i>copied</i>"u8.ToArray()), "served at once");
            Assert.That(_time.Started.Count, Is.Zero, "no second wait");
        }

        // a new pull is waited for again
        var settled = new TaskCompletionSource();
        _sync.PullSent(settled.Task);
        var third = Task.Run(() => _portal.Transfer("text/plain;charset=utf-8", 3));
        Assert.That(await _time.Started.Next("the next pull's wait"), Is.EqualTo(PortalClipboardSync.TransferWait));
        settled.SetResult();
        await third.WaitAsync(Bound);
    }

    [Test]
    public void ATypeTheClipboardLacks_IsNeverOffered()
    {
        _portal.OtherAppCopies(new() { ["text/plain;charset=utf-8"] = "copied"u8.ToArray() });
        ClaimThenRelease();

        Assert.That(_portal.Offers.Single(), Is.EquivalentTo(WaylandMime.Text), "no html, rtf, image or hint a plain text copy cannot serve");
    }

    [Test]
    public void AConcealedOwnCopy_IsLeftWithItsOwner()
    {
        _portal.OtherAppCopies(new() { ["text/plain;charset=utf-8"] = "hunter2"u8.ToArray(), ["x-kde-passwordManagerHint"] = "secret"u8.ToArray() });
        var count = _sync.ChangeCount();

        _sync.Claim();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_portal.Offers, Is.Empty, "the selection stays the password manager's");
            Assert.That(_sync.ReadAll(), Is.EqualTo(new ClipboardRead("hunter2", null, null, null, true)), "still read as the user's concealed copy");
            Assert.That(_sync.ChangeCount(), Is.EqualTo(count));
        }
    }

    [Test]
    public void ASessionLostDuringTheClaimsSetting_LeavesTheNextSessionOwningNothing()
    {
        var first = new FakePortalClipboard();
        var second = new FakePortalClipboard();
        second.AlreadyCopied(new() { ["text/plain;charset=utf-8"] = "copied meanwhile"u8.ToArray() });
        var portals = new Queue<IPortalClipboard?>([first, second]);
        var sync = new PortalClipboardSync(_ => Task.FromResult(portals.Dequeue()), () => new NullClipboardSync(), _time, NullLogger.Instance);
        try
        {
            Assert.That(sync.Connected(), Is.True);
            first.OtherAppCopies(new() { ["text/plain;charset=utf-8"] = "copied"u8.ToArray() });
            first.DuringSetSelection = sync.OnSessionLost;

            sync.Claim();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(first.Offers, Has.Count.EqualTo(1));
                Assert.That(sync.ReadAll().Text, Is.EqualTo("copied meanwhile"), "the next session's clipboard, not an owned one read as nothing");
                Assert.That(second.Offers, Is.Empty);
            }
        }
        finally
        {
            sync.DisposeAsync().AsTask().Wait();
        }
    }

    [Test]
    public async Task AWriteDuringTheClaimsRead_IsNotHeldBehindIt_AndTheClaimStandsDown()
    {
        _portal.OtherAppCopies(new() { ["text/plain;charset=utf-8"] = "copied"u8.ToArray() });
        // the primary has seen this copy, so the write's CopiedPrimary reads nothing before it begins
        _sync.GetPrimaryText();
        var reading = new TaskCompletionSource();
        var release = new ManualResetEventSlim();
        _portal.DuringRead = () =>
        {
            _portal.DuringRead = null;
            reading.SetResult();
            release.Wait(Bound);
        };

        var claim = Task.Run(_sync.Claim);
        await reading.Task.WaitAsync(Bound);
        var written = await Task.Run(() => _sync.SetClipboard(new ClipboardSnapshot("from the slave", null, Png))).WaitAsync(Bound);
        release.Set();
        await claim.WaitAsync(Bound);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(written, Is.Not.Null, "set while the claim was still reading");
            Assert.That(_portal.Offers, Has.Count.EqualTo(1), "the claim, read before the write, set nothing over it");
            Assert.That(_portal.Transfer("image/png", 1), Is.EqualTo(Png));
        }
    }

    [Test]
    public void AClearAfterTheReturn_WithoutTheHintOffered_IsServedAsThePlaceholderText()
    {
        _portal.OtherAppCopies(new() { ["text/plain;charset=utf-8"] = "copied"u8.ToArray() });
        ClaimThenRelease();
        var count = _sync.ChangeCount();

        var cleared = _sync.ClearClipboard();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cleared, Is.True);
            Assert.That(_sync.ChangeCount(), Is.EqualTo(count + 1), "a write of Hydra's");
            Assert.That(_portal.Transfer("text/plain;charset=utf-8", 1), Is.EqualTo(Encoding.UTF8.GetBytes(ClipboardSnapshot.PlaceholderText)));
            Assert.That(_portal.Transfer("x-kde-passwordManagerHint", 2), Is.Null, "never offered");
            Assert.That(_sync.ReadAll(), Is.SameAs(ClipboardRead.Empty), "Hydra's own write reads as nothing");
            Assert.That(_sync.CanUseEchoFallback(), Is.True);
        }
    }

    [Test]
    public void AConcealedAnswerAfterTheReturn_IsNotServable_WithoutTheHintOffered()
    {
        _portal.OtherAppCopies(new() { ["text/plain;charset=utf-8"] = "copied"u8.ToArray() });
        ClaimThenRelease();

        Assert.That(_sync.SetClipboard(new ClipboardSnapshot("hunter2", null, null, Concealed: true)), Is.Null);
    }

    [Test]
    public void ACopyOnTheMasterAfterTheReturn_EndsTheSessionsSelection_AndIsTheUsersCopy()
    {
        _portal.ConfirmsWrites = true;
        _portal.OtherAppCopies(new() { ["text/plain;charset=utf-8"] = "copied"u8.ToArray() });
        ClaimThenRelease();
        var count = _sync.ChangeCount();

        _portal.OtherAppCopies(new() { ["text/plain;charset=utf-8"] = "copied again"u8.ToArray() });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_sync.ChangeCount(), Is.EqualTo(count + 1));
            Assert.That(_sync.ReadAll().Text, Is.EqualTo("copied again"));
            Assert.That(_sync.CanUseEchoFallback(), Is.False);
            Assert.That(_sync.SetClipboard(new ClipboardSnapshot("from the slave", null, null)), Is.Null,
                "the session no longer holds the selection, and the portal sets none without the capture");
            Assert.That(_portal.Transfer("text/plain;charset=utf-8", 1), Is.Null);
        }
    }

    [Test]
    public async Task OnTheFallback_AClaimTouchesNothing()
    {
        var fallback = new FakeClipboardSync();
        var sync = Create(null, fallback);
        try
        {
            Assert.That(sync.Connected(), Is.True);
            sync.Claim();
            Assert.That(sync.ReadAll(), Is.EqualTo(fallback.ReadAll()));
        }
        finally
        {
            await sync.DisposeAsync();
        }
    }

    [Test]
    public void Portal_IsAskedOnce()
    {
        var asked = 0;
        var sync = new PortalClipboardSync(_ =>
        {
            asked++;
            return Task.FromResult<IPortalClipboard?>(new FakePortalClipboard());
        }, () => new NullClipboardSync(), _time, NullLogger.Instance);
        try
        {
            sync.Connected();
            sync.ChangeCount();
            sync.SetClipboard(new ClipboardSnapshot("written", null, null));

            Assert.That(asked, Is.EqualTo(1));
        }
        finally
        {
            sync.DisposeAsync().AsTask().Wait();
        }
    }

    [Test]
    public void SessionLost_ForgetsHydrasWrite_AndReadsTheNextSessionsClipboardAsItStands()
    {
        var first = new FakePortalClipboard();
        var second = new FakePortalClipboard();
        second.AlreadyCopied(new() { ["text/plain;charset=utf-8"] = "copied meanwhile"u8.ToArray() });
        var portals = new Queue<IPortalClipboard?>([first, second]);
        var sync = new PortalClipboardSync(_ => Task.FromResult(portals.Dequeue()), () => new NullClipboardSync(), _time, NullLogger.Instance);
        try
        {
            sync.SetClipboard(new ClipboardSnapshot("written", null, null));

            sync.OnSessionLost();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(sync.CanUseEchoFallback(), Is.False);
                Assert.That(sync.ReadAll().Text, Is.EqualTo("copied meanwhile"));
                Assert.That(first.Subscribed, Is.False);
                Assert.That(second.Subscribed, Is.True);
            }
        }
        finally
        {
            sync.DisposeAsync().AsTask().Wait();
        }
    }

    // the portal's clipboard as a test drives it: another client's copy is a set of MIME types and their bytes
    private sealed class FakePortalClipboard : IPortalClipboard
    {
        private Dictionary<string, byte[]> _offered = [];

        public event Func<string, uint, Task>? SelectionTransfer;
        public event Action<IReadOnlyList<string>, bool>? SelectionOwnerChanged;

        // each SetSelection's types
        public List<string[]> Offers { get; } = [];
        // each Read's type and cap, in order
        public List<string> ReadTypes { get; } = [];
        public List<int> MaxBytes { get; } = [];
        // what each answered transfer wrote, and the serials answered as failed
        private readonly Dictionary<uint, byte[]> _written = [];
        public List<uint> Failed { get; } = [];

        // the portal reports the session as the owner as soon as a write takes
        public bool ConfirmsWrites { get; set; }
        // whether an input capture is on: xdg-desktop-portal sets an InputCapture session's selection only then
        public bool CaptureActive { get; set; } = true;
        public bool SetSelectionFails { get; set; }
        public bool ReadFails { get; set; }
        // types whose read alone fails, as an owner whose UTF8_STRING transfer breaks
        public HashSet<string> Failing { get; } = [];
        public bool ReadTimesOut { get; set; }
        // the portal refuses the read outright, as for a clipboard it never enabled
        public bool ReadDenied { get; set; }
        // types that run past any cap
        public HashSet<string> OverCap { get; } = [];
        // runs inside each read, as an owner change landing mid-read
        public Action? DuringRead { get; set; }
        // runs inside each accepted SetSelection, as the session lost mid-call
        public Action? DuringSetSelection { get; set; }

        public bool Subscribed => SelectionOwnerChanged != null && SelectionTransfer != null;

        public void OtherAppCopies(Dictionary<string, byte[]> offered)
        {
            _offered = offered;
            SelectionOwnerChanged?.Invoke([.. offered.Keys], false);
        }

        public void OwnerGone()
        {
            _offered = [];
            SelectionOwnerChanged?.Invoke([], false);
        }

        // a copy made before the session started, which the portal never announces
        public void AlreadyCopied(Dictionary<string, byte[]> offered) => _offered = offered;

        public void ConfirmOwnWrite() => SelectionOwnerChanged?.Invoke(Offers[^1], true);

        // asks the session for its selection as mimeType; what it wrote, null when it answered with a failure
        public byte[]? Transfer(string mimeType, uint serial)
        {
            SelectionTransfer?.Invoke(mimeType, serial).GetAwaiter().GetResult();
            return _written.GetValueOrDefault(serial);
        }

        public Task SetSelection(IReadOnlyList<string> mimeTypes)
        {
            if (SetSelectionFails) return Task.FromException(new InvalidOperationException("the portal refused it"));
            if (!CaptureActive) return Task.FromException(new DBusErrorReplyException("org.freedesktop.DBus.Error.AccessDenied", "Clipboard not enabled"));
            Offers.Add([.. mimeTypes]);
            DuringSetSelection?.Invoke();
            if (ConfirmsWrites) SelectionOwnerChanged?.Invoke(mimeTypes, true);
            return Task.CompletedTask;
        }

        public Task SelectionWriteDone(uint serial, bool success)
        {
            if (!success) Failed.Add(serial);
            return Task.CompletedTask;
        }

        public Task<byte[]?> Read(string mimeType, int maxBytes, TimeSpan timeout, CancellationToken cancel)
        {
            ReadTypes.Add(mimeType);
            MaxBytes.Add(maxBytes);
            DuringRead?.Invoke();
            if (ReadFails || Failing.Contains(mimeType)) return Task.FromException<byte[]?>(new IOException("the pipe broke"));
            if (ReadTimesOut) return Task.FromException<byte[]?>(new TimeoutException("The clipboard source did not finish writing in time"));
            if (ReadDenied) return Task.FromException<byte[]?>(new DBusErrorReplyException("org.freedesktop.DBus.Error.AccessDenied", "Clipboard not enabled"));
            // as xdg-desktop-portal answers mutter's "No selection owner available"
            if (_offered.Count == 0) return Task.FromException<byte[]?>(new DBusErrorReplyException("org.freedesktop.portal.Error.Failed", "Internal error"));
            if (OverCap.Contains(mimeType)) return Task.FromResult<byte[]?>(null);
            // an owner asked for a type it does not hold closes the pipe unwritten
            return Task.FromResult<byte[]?>(_offered.GetValueOrDefault(mimeType) ?? []);
        }

        public Task Write(uint serial, byte[] data)
        {
            _written[serial] = data;
            return Task.CompletedTask;
        }
    }
}
