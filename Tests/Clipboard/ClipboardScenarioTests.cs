using Hydra.Relay;
using Tests.Setup;

namespace Tests.Clipboard;

// whole journeys of the clipboard and X11's PRIMARY across hosts: CarryTo for each move from the master onto a slave,
// ReturnTo for each move back
[TestFixture]
public class ClipboardScenarioTests
{
    private static readonly byte[] Png = TestImages.Png;

    // an image whose push is over the stream threshold, so it crosses in several chunks
    private static byte[] LargeImage()
    {
        var image = new byte[ClipboardStreamSender.Threshold + ClipboardStreamSender.ChunkBytes];
        new Random(7).NextBytes(image);
        return image;
    }

    private static SimulatedClipboard Linux() => new(ClipboardOs.Linux);

    [TestCase(ClipboardOs.MacOs)]
    [TestCase(ClipboardOs.Windows)]
    public void TextCopiedOnAMacOrWindowsMaster_ReachesBothLinuxClipboards(ClipboardOs os)
    {
        var master = new SimulatedClipboard(os);
        var linux = Linux();

        master.Copy("copied");
        master.CarryTo(linux);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(linux.Text, Is.EqualTo("copied"));
            Assert.That(linux.Primary, Is.EqualTo("copied"));
        }
    }

    [TestCase(ClipboardOs.MacOs)]
    [TestCase(ClipboardOs.Windows)]
    public void TextCopiedOnAMacOrWindowsSlave_ReachesBothClipboardsOfTheLinuxMaster(ClipboardOs os)
    {
        var master = Linux();
        var slave = new SimulatedClipboard(os);

        master.Copy("linux clipboard");
        master.Select("linux selection");
        master.CarryTo(slave);
        slave.Copy("copied there");
        slave.ReturnTo(master);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(master.Text, Is.EqualTo("copied there"));
            Assert.That(master.Primary, Is.EqualTo("copied there"));
        }
    }

    [TestCase(ClipboardOs.MacOs)]
    [TestCase(ClipboardOs.Windows)]
    public void TextCopiedAgainOnAMacOrWindowsSlave_IsThePrimaryToo(ClipboardOs os)
    {
        // copying exactly what Hydra wrote there is still a copy
        var master = Linux();
        var slave = new SimulatedClipboard(os);

        master.Copy("linux clipboard");
        master.Select("linux selection");
        master.CarryTo(slave);
        slave.Copy("linux clipboard");
        slave.ReturnTo(master);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(master.Text, Is.EqualTo("linux clipboard"));
            Assert.That(master.Primary, Is.EqualTo("linux clipboard"));
        }
    }

    [Test]
    public void BetweenLinuxHosts_ClipboardAndSelectionStaySeparate()
    {
        var master = Linux();
        var slave = Linux();

        master.Copy("master clipboard");
        master.Select("master selection");
        master.CarryTo(slave);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(slave.Text, Is.EqualTo("master clipboard"));
            Assert.That(slave.Primary, Is.EqualTo("master selection"));
        }

        slave.Select("slave selection");
        slave.ReturnTo(master);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(master.Text, Is.EqualTo("master clipboard"));
            Assert.That(master.Primary, Is.EqualTo("slave selection"));
        }

        master.Copy("master copies again");
        master.CarryTo(slave);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(slave.Text, Is.EqualTo("master copies again"));
            Assert.That(slave.Primary, Is.EqualTo("slave selection"));
        }
    }

    [Test]
    public void BetweenLinuxHosts_SelectionTravelsWithAnImageCopy()
    {
        var master = Linux();
        var slave = Linux();

        master.CarryTo(slave);
        slave.Select("slave selection");
        slave.CopyImage(Png);
        slave.ReturnTo(master);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(master.ImagePng, Is.EqualTo(Png));
            Assert.That(master.Primary, Is.EqualTo("slave selection"));
        }
    }

    [TestCase(ClipboardOs.Linux)]
    [TestCase(ClipboardOs.MacOs)]
    [TestCase(ClipboardOs.Windows)]
    public void SelectionChangeAlone_NeverRewritesTheClipboard(ClipboardOs os)
    {
        // the clipboard may hold formats Hydra does not carry (Excel's), which a rewrite would lose
        var master = new SimulatedClipboard(os);
        var linux = Linux();
        master.Copy("cells");
        master.CarryTo(linux);
        linux.ReturnTo(master);
        var writes = master.ClipboardWrites;

        master.CarryTo(linux);
        linux.Select("highlighted");
        linux.ReturnTo(master);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(master.ClipboardWrites, Is.EqualTo(writes));
            Assert.That(master.Primary, Is.EqualTo("highlighted"));
        }
    }

    [Test]
    public void LargeSelectionChange_CrossesWithoutRewritingTheClipboard()
    {
        var master = Linux();
        var slave = Linux();
        var large = new string('s', 5000);
        master.Copy("master clipboard");
        master.CarryTo(slave);
        slave.ReturnTo(master);
        var writes = master.ClipboardWrites;

        master.CarryTo(slave);
        slave.Select(large);
        slave.ReturnTo(master);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(master.Primary, Is.EqualTo(large));
            Assert.That(master.ClipboardWrites, Is.EqualTo(writes));
        }
    }

    [TestCase(ClipboardOs.Windows, null)]
    [TestCase(ClipboardOs.Windows, "text beside the image")]
    [TestCase(ClipboardOs.MacOs, null)]
    [TestCase(ClipboardOs.MacOs, "text beside the image")]
    public void LinuxSlaveSelection_ThenImageOnAnotherSlave_LandOnTheLinuxMasterSeparately(ClipboardOs os, string? alongside)
    {
        var master = Linux();
        var linuxSlave = Linux();
        var otherSlave = new SimulatedClipboard(os);
        master.Copy("master clipboard");

        master.CarryTo(linuxSlave);
        linuxSlave.Select("selected on the linux slave");
        linuxSlave.ReturnTo(master);
        master.CarryTo(otherSlave);
        otherSlave.CopyImage(Png, alongside);
        otherSlave.ReturnTo(master);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(master.Primary, Is.EqualTo("selected on the linux slave"));
            Assert.That(master.ImagePng, Is.EqualTo(Png));
            Assert.That(master.Text, Is.Null);
        }
    }

    [TestCase(ClipboardOs.MacOs)]
    [TestCase(ClipboardOs.Windows)]
    public void CopyOnASlave_IsNotUndoneByAnOlderRelayedSelection(ClipboardOs os)
    {
        // a selection relayed to a Mac/Windows slave, then a copy there, then an image on its master: the stale
        // selection never comes back
        var master = new SimulatedClipboard(ClipboardOs.Windows);
        var slave = new SimulatedClipboard(os);
        var linux = Linux();

        master.CarryTo(linux);
        linux.Select("old selection");
        linux.ReturnTo(master);
        master.CarryTo(slave);
        slave.Copy("copied on the slave");
        slave.ReturnTo(master);
        master.CarryTo(linux);
        linux.ReturnTo(master);
        master.CopyImage(Png);
        master.CarryTo(slave);
        slave.ReturnTo(master);
        master.CarryTo(linux);

        Assert.That(linux.Primary, Is.EqualTo("copied on the slave"));
    }

    [TestCase(ClipboardOs.MacOs)]
    [TestCase(ClipboardOs.Windows)]
    public void SelectionRelayedAfterACopy_OutranksIt(ClipboardOs os)
    {
        // the copy came first, so the selection that arrived after it is the one the next Linux slave gets
        var master = new SimulatedClipboard(os);
        var selecting = Linux();
        var next = Linux();

        master.Copy("copied first");
        master.CarryTo(selecting);
        selecting.Select("selected later");
        selecting.ReturnTo(master);
        master.CarryTo(next);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(next.Text, Is.EqualTo("copied first"));
            Assert.That(next.Primary, Is.EqualTo("selected later"));
        }
    }

    [TestCase(ClipboardOs.MacOs)]
    [TestCase(ClipboardOs.Windows)]
    public void TextCopyThenImageCopyOnASlave_KeepsTheTextAsThePrimary(ClipboardOs os)
    {
        var master = Linux();
        var slave = new SimulatedClipboard(os);

        master.Select("old selection");
        master.CarryTo(slave);
        slave.Copy("copied there");
        slave.ReturnTo(master);
        master.CarryTo(slave);
        slave.CopyImage(Png);
        slave.ReturnTo(master);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(master.ImagePng, Is.EqualTo(Png));
            Assert.That(master.Primary, Is.EqualTo("copied there"));
        }
    }

    [Test]
    public void SelectionAlone_KeepsTheClipboardTravelling()
    {
        // a slave with an empty clipboard answers with just its selection; the master's clipboard still reaches the next
        var master = Linux();
        var copying = new SimulatedClipboard(ClipboardOs.MacOs);
        var selecting = Linux();
        var next = new SimulatedClipboard(ClipboardOs.Windows);

        copying.Copy("copied elsewhere");
        copying.ReturnTo(master);
        selecting.Select("selection only");
        selecting.ReturnTo(master);
        master.CarryTo(next);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(master.Text, Is.EqualTo("copied elsewhere"));
            Assert.That(next.Text, Is.EqualTo("copied elsewhere"));
            Assert.That(next.Primary, Is.EqualTo("selection only"));
        }
    }

    [Test]
    public void LargeImageCarriedToASlave_PastesNothingMidway_ThenTheImage([Values] ClipboardOs masterOs, [Values] ClipboardOs slaveOs)
    {
        var master = new SimulatedClipboard(masterOs);
        var slave = new SimulatedClipboard(slaveOs);
        var image = LargeImage();
        slave.Copy("old on the slave");

        master.CopyImage(image);
        string? pastedMidway = "not run";
        byte[]? imageMidway = [];
        master.CarryTo(slave, midway: () =>
        {
            pastedMidway = slave.Paste();
            imageMidway = slave.ImagePng;
        });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(pastedMidway, Is.Null);
            Assert.That(imageMidway, Is.Null);
            Assert.That(slave.ImagePng, Is.EqualTo(image));
        }
    }

    [Test]
    public void LargeImageReturnedToTheMaster_PastesNothingMidway_ThenTheImage([Values] ClipboardOs masterOs, [Values] ClipboardOs slaveOs)
    {
        var master = new SimulatedClipboard(masterOs);
        var slave = new SimulatedClipboard(slaveOs);
        var image = LargeImage();
        master.Copy("old on the master");
        master.CarryTo(slave);

        slave.CopyImage(image);
        string? pastedMidway = "not run";
        byte[]? imageMidway = [];
        slave.ReturnTo(master, midway: () =>
        {
            pastedMidway = master.Paste();
            imageMidway = master.ImagePng;
        });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(pastedMidway, Is.Null);
            Assert.That(imageMidway, Is.Null);
            Assert.That(master.ImagePng, Is.EqualTo(image));
        }
    }

    [Test]
    public void CopyOnASlaveMidwayThroughAStream_WinsAndReturnsToTheMaster([Values] ClipboardOs masterOs, [Values] ClipboardOs slaveOs)
    {
        var master = new SimulatedClipboard(masterOs);
        var slave = new SimulatedClipboard(slaveOs);

        master.CopyImage(LargeImage());
        master.CarryTo(slave, midway: () => slave.Copy("copied on the slave"));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(slave.Paste(), Is.EqualTo("copied on the slave"));
            Assert.That(slave.ImagePng, Is.Null);
            Assert.That(slave.AbandonedStreams, Is.EqualTo(1));
        }

        slave.ReturnTo(master);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(master.Paste(), Is.EqualTo("copied on the slave"));
            Assert.That(master.ImagePng, Is.Null);
        }
    }

    [Test]
    public void PullMidwayThroughAStream_IsAnsweredUnchanged_AndLeavesTheMastersClipboard([Values] ClipboardOs masterOs, [Values] ClipboardOs slaveOs)
    {
        var master = new SimulatedClipboard(masterOs);
        var slave = new SimulatedClipboard(slaveOs);
        var image = LargeImage();

        master.CopyImage(image);
        var writes = master.ClipboardWrites;
        ClipboardPullResponseMessage? answer = null;
        master.CarryTo(slave, midway: () => answer = slave.ReturnTo(master));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(answer?.Unchanged, Is.True);
            Assert.That(master.ImagePng, Is.EqualTo(image));
            Assert.That(master.ClipboardWrites, Is.EqualTo(writes));
            Assert.That(slave.ImagePng, Is.EqualTo(image));
        }
    }

    // -- a password manager's copy: relayed concealed, and its auto-clear relayed too --

    [Test]
    public void ConcealedCopyOnTheMaster_StaysConcealedOnTheSlave([Values] ClipboardOs masterOs, [Values] ClipboardOs slaveOs)
    {
        var master = new SimulatedClipboard(masterOs);
        var slave = new SimulatedClipboard(slaveOs);

        master.CopyConcealed("hunter2");
        master.CarryTo(slave);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(slave.Paste(), Is.EqualTo("hunter2"));
            Assert.That(slave.Concealed, Is.True);
        }
    }

    [Test]
    public void MastersPasswordManagerClears_TheSlaveIsClearedOnTheNextEntry([Values] ClipboardOs masterOs, [Values] ClipboardOs slaveOs)
    {
        var master = new SimulatedClipboard(masterOs);
        var slave = new SimulatedClipboard(slaveOs);

        master.CopyConcealed("hunter2");
        master.CarryTo(slave);
        master.AutoClear();
        master.CarryTo(slave);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(slave.Paste(), Is.Null);
            Assert.That(slave.Concealed, Is.False);
        }
    }

    [Test]
    public void SlavesPasswordManagerClears_TheMasterIsClearedOnTheNextPull_AndTheOtherSlaveOnItsEntry([Values] ClipboardOs masterOs,
        [Values] ClipboardOs slaveOs)
    {
        var master = new SimulatedClipboard(masterOs);
        var slave = new SimulatedClipboard(slaveOs);
        var other = Linux();

        master.Copy("on the master");
        slave.CopyConcealed("hunter2");
        slave.ReturnTo(master);
        master.CarryTo(other);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(master.Paste(), Is.EqualTo("hunter2"));
            Assert.That(master.Concealed, Is.True);
            Assert.That(other.Concealed, Is.True);
        }

        slave.AutoClear();
        var answer = slave.ReturnTo(master);
        master.CarryTo(other);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(answer.Cleared, Is.True);
            Assert.That(master.Paste(), Is.Null);
            Assert.That(other.Paste(), Is.Null);
        }
    }

    [Test]
    public void SlavesLaterCopy_IsNeverClearedByTheMastersAutoClear([Values] ClipboardOs masterOs, [Values] ClipboardOs slaveOs)
    {
        var master = new SimulatedClipboard(masterOs);
        var slave = new SimulatedClipboard(slaveOs);

        master.CopyConcealed("hunter2");
        master.CarryTo(slave);
        slave.Copy("copied on the slave");
        master.AutoClear();
        master.CarryTo(slave);

        Assert.That(slave.Paste(), Is.EqualTo("copied on the slave"));
    }

    [Test]
    public void MastersLaterCopy_IsNeverClearedByTheSlavesAutoClear([Values] ClipboardOs masterOs, [Values] ClipboardOs slaveOs)
    {
        var master = new SimulatedClipboard(masterOs);
        var slave = new SimulatedClipboard(slaveOs);

        slave.CopyConcealed("hunter2");
        slave.ReturnTo(master);
        master.Copy("copied on the master");
        slave.AutoClear();
        slave.ReturnTo(master);

        Assert.That(master.Paste(), Is.EqualTo("copied on the master"));
    }

    [Test]
    public void PlainCopy_EmptiedAtItsSource_IsNotCleared([Values] ClipboardOs masterOs, [Values] ClipboardOs slaveOs)
    {
        var master = new SimulatedClipboard(masterOs);
        var slave = new SimulatedClipboard(slaveOs);

        slave.Copy("plain");
        slave.ReturnTo(master);
        slave.AutoClear();
        slave.ReturnTo(master);

        Assert.That(master.Paste(), Is.EqualTo("plain"));
    }
}
