using System.Diagnostics;
using System.Text;
using Hydra.Platform;
using Hydra.Platform.Linux;
using Microsoft.Extensions.Logging.Abstractions;
using Tests.Setup;

namespace Tests.Clipboard;

// the real Xlib code against Xvfb, with xclip as the peer; sequential, since CLIPBOARD is global
[TestFixture]
[Category("Linux")]
[NonParallelizable]
public class XorgClipboardSyncTests
{
    [OneTimeSetUp]
    public void RequireX11() => X11Required.OrIgnore();

    [TearDown]
    public void KillStrayXclip()
    {
        // xclip daemonises to serve a selection it owns; drop any leftover so the next test starts clean
        try { Process.Start(new ProcessStartInfo("pkill", "-x xclip") { RedirectStandardError = true })?.WaitForExit(2000); }
        catch { /* pkill may be absent; a new owner evicts the old xclip anyway */ }
    }

    // -- serve side: our instance owns the selection, xclip reads a specific target --

    [Test]
    public void Serve_String_IsLatin1_Utf8String_IsUtf8()
    {
        using var owner = new XorgClipboardSync();
        owner.SetClipboard(new ClipboardSnapshot("café", null, null));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(XclipRead("STRING"), Is.EqualTo(new byte[] { 0x63, 0x61, 0x66, 0xE9 }), "STRING must be ISO-8859-1");
            Assert.That(XclipRead("UTF8_STRING"), Is.EqualTo(Encoding.UTF8.GetBytes("café")), "UTF8_STRING must be UTF-8");
        }
    }

    [Test]
    public void Serve_Html_AndText()
    {
        using var owner = new XorgClipboardSync();
        owner.SetClipboard(new ClipboardSnapshot("café", null, null, Html: "<b>café</b>"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(XclipRead("text/html"), Is.EqualTo(Encoding.UTF8.GetBytes("<b>café</b>")));
            Assert.That(XclipRead("UTF8_STRING"), Is.EqualTo(Encoding.UTF8.GetBytes("café")));
        }
    }

    [Test]
    public void Serve_ImagePng()
    {
        var png = TestImages.Png;
        using var owner = new XorgClipboardSync();
        owner.SetClipboard(new ClipboardSnapshot(null, null, png));

        Assert.That(XclipRead("image/png"), Is.EqualTo(png));
    }

    [Test]
    public void Serve_Concealed_ListsThePasswordHintAndServesSecret()
    {
        using var owner = new XorgClipboardSync();
        owner.SetClipboard(new ClipboardSnapshot("hunter2", null, null, Concealed: true));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Targets(), Does.Contain("x-kde-passwordManagerHint"));
            Assert.That(XclipRead("x-kde-passwordManagerHint"), Is.EqualTo("secret"u8.ToArray()));
            Assert.That(XclipRead("UTF8_STRING"), Is.EqualTo("hunter2"u8.ToArray()));
        }
    }

    [Test]
    public void Serve_Plain_HasNoPasswordHint()
    {
        using var owner = new XorgClipboardSync();
        owner.SetClipboard(new ClipboardSnapshot("plain", null, null));

        Assert.That(Targets(), Does.Not.Contain("x-kde-passwordManagerHint"));
    }

    [Test]
    public void Read_SecretPasswordHint_FromForeignOwner_IsConcealed()
    {
        XclipWrite("x-kde-passwordManagerHint", "secret"u8.ToArray());

        using var reader = new XorgClipboardSync();
        Assert.That(reader.ReadAll().Concealed, Is.True);
    }

    [Test]
    public void RoundTrip_Concealed_BetweenInstances()
    {
        using var owner = new XorgClipboardSync();
        using var reader = new XorgClipboardSync();
        owner.SetClipboard(new ClipboardSnapshot("hunter2", null, null, Concealed: true));
        XclipRead("UTF8_STRING"); // barrier, as in RoundTrip_HtmlAndText_BetweenInstances

        var read = ClipboardUtils.ReadWithFallback(reader, null, NullLogger.Instance, "test");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(read?.Text, Is.EqualTo("hunter2"));
            Assert.That(read?.Concealed, Is.True);
        }
    }

    private static string[] Targets() =>
        Encoding.UTF8.GetString(XclipRead("TARGETS")).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    // -- read side: xclip owns the selection, our instance reads it back --

    [Test]
    public void Read_Utf8Text_FromForeignOwner()
    {
        const string s = "café ☕ 日本語";
        XclipWrite("UTF8_STRING", Encoding.UTF8.GetBytes(s));

        using var reader = new XorgClipboardSync();
        Assert.That(reader.ReadAll().Text, Is.EqualTo(s));
    }

    [Test]
    public void Read_Html_FromForeignOwner()
    {
        XclipWrite("text/html", Encoding.UTF8.GetBytes("<i>café</i>"));

        using var reader = new XorgClipboardSync();
        Assert.That(reader.ReadAll().Html, Is.EqualTo("<i>café</i>"));
    }

    [Test]
    public void Read_ImagePng_FromForeignOwner()
    {
        var png = TestImages.Png;
        XclipWrite("image/png", png);

        using var reader = new XorgClipboardSync();
        Assert.That(reader.ReadAll().ImagePng, Is.EqualTo(png));
    }

    // -- round-trip between two of our own instances (no external peer) --

    [Test]
    public void RoundTrip_HtmlAndText_BetweenInstances()
    {
        using var owner = new XorgClipboardSync();
        using var reader = new XorgClipboardSync();
        owner.SetClipboard(new ClipboardSnapshot("plain", null, null, Html: "<b>rich</b>"));

        // barrier: block until the owner actually serves the selection to a third connection,
        // which proves ownership has propagated server-side before the C# reader converts it
        XclipRead("UTF8_STRING");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(reader.ReadAll().Text, Is.EqualTo("plain"));
            Assert.That(reader.ReadAll().Html, Is.EqualTo("<b>rich</b>"));
        }
    }

    // -- PRIMARY: a selection of its own, read as it stands even when Hydra set it --

    [Test]
    public void Primary_SetByHydra_ReadsBackAsIs()
    {
        using var owner = new XorgClipboardSync();
        owner.SetPrimaryText("selected");

        Assert.That(owner.GetPrimaryText(), Is.EqualTo("selected"));
    }

    [Test]
    public void Primary_FromForeignOwner()
    {
        using var reader = new XorgClipboardSync();
        XclipWrite("UTF8_STRING", Encoding.UTF8.GetBytes("highlighted"), "primary");

        Assert.That(reader.GetPrimaryText(), Is.EqualTo("highlighted"));
    }

    [Test]
    public void Primary_AlreadyHeldElsewhere_IsLeftWithItsOwner()
    {
        // taking PRIMARY back with the same text would clear the owner's highlight
        using var owner = new XorgClipboardSync();
        using var receiver = new XorgClipboardSync();
        owner.SetPrimaryText("highlighted");
        XclipRead("UTF8_STRING", "primary"); // barrier: the server has the owner

        receiver.SetPrimaryText("highlighted");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(owner.OwnsPrimary, Is.True);
            Assert.That(receiver.OwnsPrimary, Is.False);
        }
    }

    [Test]
    public void SetClipboard_ServesClipboardAndPrimarySeparately()
    {
        using var owner = new XorgClipboardSync();
        owner.SetClipboard(new ClipboardSnapshot("copied", "selected", null));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(XclipRead("UTF8_STRING"), Is.EqualTo(Encoding.UTF8.GetBytes("copied")));
            Assert.That(XclipRead("UTF8_STRING", "primary"), Is.EqualTo(Encoding.UTF8.GetBytes("selected")));
        }
    }

    [Test]
    public void EchoFallback_OnlyWhileClipboardIsStillOurs()
    {
        using var owner = new XorgClipboardSync();
        owner.SetClipboard(new ClipboardSnapshot("ours", null, null));
        Assert.That(owner.CanUseEchoFallback(), Is.True);

        XclipWrite("UTF8_STRING", Encoding.UTF8.GetBytes("theirs"));

        Assert.That(LosesOwnership(owner), Is.True);
    }

    // SelectionClear reaches the owner's event thread on its own time
    private static bool LosesOwnership(XorgClipboardSync owner) => SpinWait.SpinUntil(() => !owner.CanUseEchoFallback(), Bound);

    [Test]
    public void ChangeCount_MovesOnAWriteAndOnAnotherClientsCopy()
    {
        using var owner = new XorgClipboardSync();
        var start = owner.ChangeCount();
        owner.SetClipboard(new ClipboardSnapshot("ours", null, null));
        var afterWrite = owner.ChangeCount();

        XclipWrite("UTF8_STRING", Encoding.UTF8.GetBytes("ours"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(afterWrite, Is.Not.EqualTo(start));
            Assert.That(LosesOwnership(owner), Is.True);
            Assert.That(Moves(owner, afterWrite), Is.True, "an identical copy moves it too");
        }
    }

    [Test]
    public void ChangeCount_MovesWhenAnOwnerTakesTheClipboardAgainWithTheSameContent()
    {
        using var hydra = new XorgClipboardSync();
        using var otherApp = new XorgClipboardSync();
        hydra.SetClipboard(new ClipboardSnapshot("ours", null, null));
        otherApp.SetClipboard(new ClipboardSnapshot("theirs", null, null));
        Assert.That(LosesOwnership(hydra), Is.True, "pre-condition: the other client owns it");
        var before = Settled(hydra);

        // the same client, the same content: no SelectionClear reaches hydra, only XFixes' owner notice
        otherApp.SetClipboard(new ClipboardSnapshot("theirs", null, null));

        Assert.That(Moves(hydra, before), Is.True);
    }

    [Test]
    public void ChangeCount_OneOwnerChange_MovesOnce_CountedBeforeTheNotice()
    {
        using var hydra = new XorgClipboardSync();
        using var otherApp = new XorgClipboardSync();
        hydra.SetClipboard(new ClipboardSnapshot("ours", null, null));
        var before = Settled(hydra);

        otherApp.SetClipboard(new ClipboardSnapshot("theirs", null, null));
        _ = hydra.ChangeCount(); // most likely before SelectionClear and the owner notice land

        using (Assert.EnterMultipleScope())
        {
            Assert.That(LosesOwnership(hydra), Is.True);
            Assert.That(Settled(hydra), Is.EqualTo(before + 1));
        }
    }

    [Test]
    public void ChangeCount_OneOwnerChange_MovesOnce_CountedAfterTheNotice()
    {
        using var hydra = new XorgClipboardSync();
        hydra.SetClipboard(new ClipboardSnapshot("ours", null, null));
        var before = Settled(hydra);

        XclipWrite("UTF8_STRING", Encoding.UTF8.GetBytes("theirs"));

        var lost = LosesOwnership(hydra);
        Thread.Sleep(300); // the owner notice lands before the counter is next asked

        using (Assert.EnterMultipleScope())
        {
            Assert.That(lost, Is.True);
            Assert.That(Settled(hydra), Is.EqualTo(before + 1));
        }
    }

    // with XFixes too, the counter moves as soon as another client owns CLIPBOARD (a read sees it at once), never waiting
    // on the owner notice, and the notice for that same change counts nothing more
    [Test]
    public void ChangeCount_AnotherClientOwnsTheClipboard_MovesAtOnce_AndOnlyOnce()
    {
        using var hydra = new XorgClipboardSync();
        using var otherApp = new XorgClipboardSync();
        hydra.SetClipboard(new ClipboardSnapshot("ours", null, null));
        var before = Settled(hydra);

        otherApp.SetClipboard(new ClipboardSnapshot("theirs", null, null));
        // a round trip on its own connection: the server has made it the owner before hydra asks
        _ = otherApp.ChangeCount();
        var atOnce = hydra.ChangeCount();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(atOnce, Is.EqualTo(before + 1), "counted on the first ask after the owner changed");
            Assert.That(LosesOwnership(hydra), Is.True);
            Assert.That(Settled(hydra), Is.EqualTo(before + 1));
        }
    }

    [Test]
    public void WroteAt_IsTheCountRightAfterTheWrite_AndAForeignCopyMovesPastIt()
    {
        using var hydra = new XorgClipboardSync();
        hydra.SetClipboard(new ClipboardSnapshot("ours", null, null));
        var afterWrite = hydra.ChangeCount();
        var wroteAt = hydra.WroteAt();

        XclipWrite("UTF8_STRING", Encoding.UTF8.GetBytes("ours"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(wroteAt, Is.Not.Null.And.EqualTo(afterWrite));
            Assert.That(Moves(hydra, afterWrite), Is.True);
            Assert.That(hydra.ChangeCount(), Is.GreaterThan(wroteAt ?? long.MaxValue));
            Assert.That(hydra.WroteAt(), Is.EqualTo(wroteAt), "another client's copy is no write of Hydra's");
        }
    }

    [Test]
    public void ReadAll_AnotherClientsCopyEqualToHydrasLastWrite_IsRead()
    {
        using var hydra = new XorgClipboardSync();
        hydra.SetClipboard(new ClipboardSnapshot("foo", null, null));

        XclipWrite("UTF8_STRING", Encoding.UTF8.GetBytes("foo"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(LosesOwnership(hydra), Is.True);
            Assert.That(hydra.ReadAll().Text, Is.EqualTo("foo"));
        }
    }

    // XFixes' notice reaches the event thread on its own time
    private static bool Moves(XorgClipboardSync sync, long? from) => SpinWait.SpinUntil(() => sync.ChangeCount() != from, Bound);

    // the counter once every notice already on its way has landed
    private static long? Settled(XorgClipboardSync sync)
    {
        var last = sync.ChangeCount();
        while (true)
        {
            Thread.Sleep(300);
            var now = sync.ChangeCount();
            if (now == last) return now;
            last = now;
        }
    }

    [Test]
    public void Clear_LeavesThePlaceholderForOtherClients_NothingForHydra_AndLeavesPrimary()
    {
        using var owner = new XorgClipboardSync();
        owner.SetClipboard(new ClipboardSnapshot("copied", "selected", null));

        ((IClipboardSync)owner).ClearClipboard();

        var targets = Targets();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(XclipRead("UTF8_STRING"), Is.EqualTo(Encoding.UTF8.GetBytes(ClipboardSnapshot.PlaceholderText)));
            Assert.That(targets, Does.Contain("UTF8_STRING"));
            Assert.That(targets, Does.Contain("x-kde-passwordManagerHint"), "so Klipper never keeps the placeholder");
            Assert.That(XclipRead("x-kde-passwordManagerHint"), Is.EqualTo("secret"u8.ToArray()));
            Assert.That(ClipboardUtils.ReadWithFallback(owner, null, NullLogger.Instance, "test")!.HasClipboard, Is.False);
            Assert.That(XclipRead("UTF8_STRING", "primary"), Is.EqualTo(Encoding.UTF8.GetBytes("selected")));
        }
    }

    [Test]
    public void Clear_ReadByAnotherInstance_IsThePlaceholder_ReadAsEmpty()
    {
        using var owner = new XorgClipboardSync();
        using var reader = new XorgClipboardSync();
        ((IClipboardSync)owner).ClearClipboard();
        XclipRead("UTF8_STRING"); // barrier, as in RoundTrip_HtmlAndText_BetweenInstances

        using (Assert.EnterMultipleScope())
        {
            Assert.That(reader.ReadAll().Text, Is.EqualTo(ClipboardSnapshot.PlaceholderText));
            Assert.That(ClipboardUtils.ReadWithFallback(reader, null, NullLogger.Instance, "test"), Is.EqualTo(ClipboardSnapshot.Empty));
        }
    }

    // an owner that does not list a format has none of it, and is never asked for it
    [Test]
    public async Task ReadAll_OnlyAsksForTheListedTargets()
    {
        await WithForeignOwner(async owner =>
        {
            using var reader = new XorgClipboardSync();
            var read = Task.Run(reader.ReadAll);
            var asked = new List<nint>();
            var served = Task.Run(() =>
            {
                var utf8 = NativeMethods.XInternAtom(owner, "UTF8_STRING", false);
                ServeTargets(owner, NextRequest(owner), [utf8]);
                var request = NextRequest(owner);
                asked.Add(request.SelectionRequestTarget);
                ServeBytes(owner, request, "listed"u8.ToArray());
            });

            await served.WaitAsync(Bound);
            var all = await read.WaitAsync(Bound);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(all, Is.EqualTo(new ClipboardRead("listed", null, null, null, false)));
                Assert.That(asked, Is.EqualTo([NativeMethods.XInternAtom(owner, "UTF8_STRING", false)]));
            }
        });
    }

    // a copy taken over by another owner mid-read is read again from the new owner, whole
    [Test]
    public async Task ReadAll_OwnerChangesDuringTheRead_ReadsTheNewCopy()
    {
        await WithForeignOwner(async owner =>
        {
            var clipboard = NativeMethods.XInternAtom(owner, "CLIPBOARD", false);
            var utf8 = NativeMethods.XInternAtom(owner, "UTF8_STRING", false);
            var hint = NativeMethods.XInternAtom(owner, "x-kde-passwordManagerHint", false);
            var second = NativeMethods.XCreateSimpleWindow(owner, NativeMethods.XDefaultRootWindow(owner), 0, 0, 1, 1, 0, nint.Zero, nint.Zero);
            using var reader = new XorgClipboardSync();
            var read = Task.Run(reader.ReadAll);
            var served = Task.Run(() =>
            {
                ServeTargets(owner, NextRequest(owner), [utf8]);
                var text = NextRequest(owner);
                // the password manager copies before the first owner answers for its text
                _ = NativeMethods.XSetSelectionOwner(owner, clipboard, second, NativeMethods.CurrentTime);
                ServeBytes(owner, text, "username"u8.ToArray());
                ServeTargets(owner, NextRequest(owner), [utf8, hint]);
                ServeBytes(owner, NextRequest(owner), "password"u8.ToArray());
                ServeBytes(owner, NextRequest(owner), "secret"u8.ToArray());
            });

            await served.WaitAsync(Bound);
            Assert.That(await read.WaitAsync(Bound), Is.EqualTo(new ClipboardRead("password", null, null, null, true)));
        });
    }

    // an owner that never answers for its TARGETS leaves the clipboard unreadable, never empty
    [Test]
    public async Task ReadAll_SilentTargets_IsUnreadable()
    {
        await WithForeignOwner(async owner =>
        {
            using var reader = new XorgClipboardSync();
            var read = Task.Run(reader.ReadAll);
            var answered = Task.Run(() => NextRequest(owner));

            await answered.WaitAsync(Bound);
            Assert.That(async () => await read.WaitAsync(Bound), Throws.TypeOf<ClipboardUnreadableException>());
        });
    }

    // an owner that refuses TARGETS is asked for its text directly, as UTF8_STRING
    [Test]
    public async Task ReadAll_RefusedTargets_AsksForUtf8Text()
    {
        await WithForeignOwner(async owner =>
        {
            using var reader = new XorgClipboardSync();
            var read = Task.Run(reader.ReadAll);
            var asked = new List<nint>();
            var served = Task.Run(() =>
            {
                var targets = NextRequest(owner);
                Notify(owner, targets, targets.SelectionRequestTarget, nint.Zero);
                var text = NextRequest(owner);
                asked.Add(text.SelectionRequestTarget);
                ServeBytes(owner, text, "plain ünïcode"u8.ToArray());
            });

            await served.WaitAsync(Bound);
            var all = await read.WaitAsync(Bound);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(all, Is.EqualTo(new ClipboardRead("plain ünïcode", null, null, null, false)));
                Assert.That(asked, Is.EqualTo([NativeMethods.XInternAtom(owner, "UTF8_STRING", false)]));
            }
        });
    }

    // an owner that refuses TARGETS and UTF8_STRING is asked for STRING, read as Latin-1
    [Test]
    public async Task ReadAll_RefusedTargetsAndUtf8_FallsBackToString()
    {
        await WithForeignOwner(async owner =>
        {
            using var reader = new XorgClipboardSync();
            var read = Task.Run(reader.ReadAll);
            var served = Task.Run(() =>
            {
                var targets = NextRequest(owner);
                Notify(owner, targets, targets.SelectionRequestTarget, nint.Zero);
                var utf8 = NextRequest(owner);
                Notify(owner, utf8, utf8.SelectionRequestTarget, nint.Zero);
                var latin1 = NextRequest(owner);
                Assert.That(latin1.SelectionRequestTarget, Is.EqualTo(NativeMethods.XA_STRING));
                ServeBytes(owner, latin1, [0x63, 0x61, 0x66, 0xE9]);
            });

            await served.WaitAsync(Bound);
            Assert.That(await read.WaitAsync(Bound), Is.EqualTo(new ClipboardRead("café", null, null, null, false)));
        });
    }

    // an owner that refuses TARGETS and every text target has nothing to read
    [Test]
    public async Task ReadAll_RefusedTargetsAndText_IsEmpty()
    {
        await WithForeignOwner(async owner =>
        {
            using var reader = new XorgClipboardSync();
            var read = Task.Run(reader.ReadAll);
            var refused = Task.Run(() =>
            {
                for (var i = 0; i < 3; i++)
                {
                    var request = NextRequest(owner);
                    Notify(owner, request, request.SelectionRequestTarget, nint.Zero);
                }
            });

            await refused.WaitAsync(Bound);
            Assert.That(await read.WaitAsync(Bound), Is.EqualTo(new ClipboardRead(null, null, null, null, false)));
        });
    }

    // a property larger than one XGetWindowProperty read, sent whole rather than by INCR, is read to its end
    [Test]
    public async Task Read_LargePropertyWithoutIncr_IsReadWhole()
    {
        await WithForeignOwner(async owner =>
        {
            var big = new byte[1_500_000];
            for (var i = 0; i < big.Length; i++) big[i] = (byte)('a' + i % 26);
            using var reader = new XorgClipboardSync();
            var read = ReadOffThread(reader, r => r.Text);
            var served = Task.Run(() =>
            {
                ServeTargets(owner, NextRequest(owner), [NativeMethods.XInternAtom(owner, "UTF8_STRING", false)]);
                ServeBytes(owner, NextRequest(owner), big);
            });

            await served.WaitAsync(Bound);
            Assert.That(await read.WaitAsync(Bound), Is.EqualTo(Encoding.UTF8.GetString(big)));
        });
    }

    // a late answer to a text read that already gave up: its data in its own property, or its refusal
    [TestCase(false)]
    [TestCase(true)]
    public async Task Read_IgnoresANotifyForAnotherTarget(bool refusal)
    {
        await WithForeignOwner(async owner =>
        {
            using var reader = new XorgClipboardSync();
            var read = ReadOffThread(reader, r => r.ImagePng);

            // the read's request has reached us, so it is waiting for an answer
            var answered = Task.Run(() =>
            {
                ServeTargets(owner, NextRequest(owner), [NativeMethods.XInternAtom(owner, "image/png", false)]);
                var request = NextRequest(owner);
                var staleProperty = refusal ? nint.Zero : NativeMethods.XInternAtom(owner, "HYDRA_STALE", false);
                Notify(owner, request, NativeMethods.XInternAtom(owner, "UTF8_STRING", false), staleProperty);
                ServeBytes(owner, request, TestImages.Png);
            });

            await answered.WaitAsync(Bound);
            Assert.That(await read.WaitAsync(Bound), Is.EqualTo(TestImages.Png));
        });
    }

    // an owner that never answers leaves the clipboard unreadable, never empty; one that refuses the target has none of it
    [TestCase(false)]
    [TestCase(true)]
    public async Task Read_SilentOwner_IsUnreadable_ARefusingOneIsEmpty(bool refuses)
    {
        await WithForeignOwner(async owner =>
        {
            using var reader = new XorgClipboardSync();
            var read = ReadOffThread(reader, r => r.ImagePng);
            var answered = Task.Run(() =>
            {
                ServeTargets(owner, NextRequest(owner), [NativeMethods.XInternAtom(owner, "image/png", false)]);
                var request = NextRequest(owner);
                if (refuses) Notify(owner, request, request.SelectionRequestTarget, nint.Zero);
            });

            await answered.WaitAsync(Bound);
            if (refuses)
                Assert.That(await read.WaitAsync(Bound), Is.Null);
            else
                Assert.That(async () => await read.WaitAsync(Bound), Throws.TypeOf<ClipboardUnreadableException>());
        });
    }

    // a large copy whose owner sends it slowly, each chunk inside the 2 s wait though the whole takes longer
    [Test]
    public async Task Read_SlowButProgressingIncr_IsNotGivenUp()
    {
        await WithForeignOwner(async owner =>
        {
            byte[][] chunks = ["slow "u8.ToArray(), "but "u8.ToArray(), "steady"u8.ToArray()];
            using var reader = new XorgClipboardSync();
            var read = ReadOffThread(reader, r => r.Text);
            var served = Task.Run(() =>
            {
                ServeTargets(owner, NextRequest(owner), [NativeMethods.XInternAtom(owner, "UTF8_STRING", false)]);
                ServeIncr(owner, NextRequest(owner), chunks, 900);
            });

            await served.WaitAsync(Bound);
            Assert.That(await read.WaitAsync(Bound), Is.EqualTo("slow but steady"));
        });
    }

    // a read that gave up mid-INCR: the owner's late chunks never feed, nor keep alive, the next read
    [Test]
    public async Task Read_GivenUpMidIncr_LateChunksNeverFeedTheNextRead()
    {
        await WithForeignOwner(async owner =>
        {
            var utf8 = NativeMethods.XInternAtom(owner, "UTF8_STRING", false);
            using var reader = new XorgClipboardSync();
            var first = ReadOffThread(reader, r => r.Text);
            XEvent stalled = default;
            var started = Task.Run(() =>
            {
                ServeTargets(owner, NextRequest(owner), [utf8]);
                stalled = NextRequest(owner);
                StartIncr(owner, stalled, 64);
                AwaitDeleted(owner, stalled.SelectionRequestRequestor, stalled.SelectionRequestProperty);
            });
            await started.WaitAsync(Bound);
            Assert.That(async () => await first.WaitAsync(Bound), Throws.TypeOf<ClipboardUnreadableException>());

            var second = ReadOffThread(reader, r => r.Text);
            var served = Task.Run(() =>
            {
                var targets = NextRequest(owner);
                // the stale transfer's late chunk, to the property the first read used
                ChangeProperty(owner, stalled, "stale"u8.ToArray());
                ServeTargets(owner, targets, [utf8]);
                ServeBytes(owner, NextRequest(owner), "fresh"u8.ToArray());
            });

            await served.WaitAsync(Bound);
            Assert.That(await second.WaitAsync(Bound), Is.EqualTo("fresh"));
        });
    }

    // one field of a read on another thread, while the test serves it
    private static Task<T> ReadOffThread<T>(XorgClipboardSync reader, Func<ClipboardRead, T> field) => Task.Run(() => field(reader.ReadAll()));

    // the requestor took the last chunk, so the owner may send the next
    private static void AwaitDeleted(nint display, nint window, nint property)
    {
        while (true)
        {
            _ = NativeMethods.XNextEvent(display, out var ev);
            if (ev.Type == NativeMethods.PropertyNotify && ev.EventWindow == window && ev.PropertyNotifyAtom == property
                && ev.PropertyNotifyState == NativeMethods.PropertyDelete)
                return;
        }
    }

    // an owner that never answers for the password hint leaves the copy unknown, never "not concealed"
    [Test]
    public async Task ReadAll_HintSilentOwner_IsUnreadable()
    {
        await WithForeignOwner(async owner =>
        {
            using var reader = new XorgClipboardSync();
            var read = Task.Run(reader.ReadAll);
            var answered = Task.Run(() =>
            {
                var hint = NativeMethods.XInternAtom(owner, "x-kde-passwordManagerHint", false);
                ServeTargets(owner, NextRequest(owner), [hint]);
            });

            await answered.WaitAsync(Bound);
            Assert.That(async () => await read.WaitAsync(Bound), Throws.TypeOf<ClipboardUnreadableException>());
        });
    }

    // a raw client owning CLIPBOARD, for the test to answer by hand
    private static async Task WithForeignOwner(Func<nint, Task> test)
    {
        var owner = NativeMethods.XOpenDisplay(null);
        try
        {
            var window = NativeMethods.XCreateSimpleWindow(owner, NativeMethods.XDefaultRootWindow(owner), 0, 0, 1, 1, 0, nint.Zero, nint.Zero);
            var clipboard = NativeMethods.XInternAtom(owner, "CLIPBOARD", false);
            _ = NativeMethods.XSetSelectionOwner(owner, clipboard, window, NativeMethods.CurrentTime);
            Assert.That(NativeMethods.XGetSelectionOwner(owner, clipboard), Is.EqualTo(window));
            await test(owner);
        }
        finally
        {
            _ = NativeMethods.XCloseDisplay(owner);
        }
    }

    // the next selection request, past any other event
    private static XEvent NextRequest(nint display)
    {
        while (true)
        {
            _ = NativeMethods.XNextEvent(display, out var ev);
            if (ev.Type == NativeMethods.SelectionRequest) return ev;
        }
    }

    private static void ServeTargets(nint display, XEvent request, nint[] listed)
    {
        nint[] atoms = [NativeMethods.XInternAtom(display, "TARGETS", false), .. listed];
        _ = NativeMethods.XChangeProperty(display, request.SelectionRequestRequestor, request.SelectionRequestProperty, NativeMethods.XA_ATOM, 32,
            NativeMethods.PropModeReplace, atoms, atoms.Length);
        Notify(display, request, request.SelectionRequestTarget, request.SelectionRequestProperty);
    }

    private static void ServeBytes(nint display, XEvent request, byte[] data)
    {
        ChangeProperty(display, request, data);
        Notify(display, request, request.SelectionRequestTarget, request.SelectionRequestProperty);
    }

    private static void ChangeProperty(nint display, XEvent request, byte[] data)
    {
        _ = NativeMethods.XChangeProperty(display, request.SelectionRequestRequestor, request.SelectionRequestProperty, request.SelectionRequestTarget, 8,
            NativeMethods.PropModeReplace, data, data.Length);
        _ = NativeMethods.XFlush(display);
    }

    private static void StartIncr(nint display, XEvent request, int size)
    {
        _ = NativeMethods.XSelectInput(display, request.SelectionRequestRequestor, NativeMethods.PropertyChangeMask);
        _ = NativeMethods.XChangeProperty(display, request.SelectionRequestRequestor, request.SelectionRequestProperty,
            NativeMethods.XInternAtom(display, "INCR", false), 32, NativeMethods.PropModeReplace, [size], 1);
        Notify(display, request, request.SelectionRequestTarget, request.SelectionRequestProperty);
    }

    // by INCR, each chunk after the requestor took the last, delayMs apart
    private static void ServeIncr(nint display, XEvent request, byte[][] chunks, int delayMs)
    {
        StartIncr(display, request, chunks.Sum(c => c.Length));
        foreach (var chunk in chunks.Append([]))
        {
            AwaitDeleted(display, request.SelectionRequestRequestor, request.SelectionRequestProperty);
            Thread.Sleep(delayMs);
            ChangeProperty(display, request, chunk);
        }
    }

    private static void Notify(nint display, XEvent request, nint target, nint property)
    {
        var notify = default(XEvent);
        notify.Type = NativeMethods.SelectionNotify;
        notify.SendEvent = 1;
        notify.EventDisplay = display;
        notify.SelectionNotifyRequestor = request.SelectionRequestRequestor;
        notify.SelectionNotifySelection = request.SelectionRequestSelection;
        notify.SelectionNotifyTarget = target;
        notify.SelectionNotifyProperty = property;
        notify.SelectionNotifyTime = request.SelectionRequestTime;
        _ = NativeMethods.XSendEvent(display, request.SelectionRequestRequestor, false, nint.Zero, ref notify);
        _ = NativeMethods.XFlush(display);
    }

    // -- xclip helpers (raw bytes in/out) --

    private static byte[] XclipRead(string target, string selection = "clipboard")
    {
        using var p = Process.Start(new ProcessStartInfo("xclip", $"-selection {selection} -o -t {target}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        using var ms = new MemoryStream();
        p.StandardOutput.BaseStream.CopyTo(ms);
        p.WaitForExit(5000);
        return ms.ToArray();
    }

    private static void XclipWrite(string target, byte[] data, string selection = "clipboard")
    {
        using var p = Process.Start(new ProcessStartInfo("xclip", $"-selection {selection} -i -t {target}")
        {
            RedirectStandardInput = true,
            RedirectStandardError = true,
        })!;
        p.StandardInput.BaseStream.Write(data);
        p.StandardInput.BaseStream.Close();
        p.WaitForExit(5000);
    }
}
