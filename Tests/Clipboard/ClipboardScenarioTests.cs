using Tests.Setup;

namespace Tests.Clipboard;

// whole journeys of the clipboard and X11's PRIMARY across hosts: CarryTo for each move from the master onto a slave,
// ReturnTo for each move back
[TestFixture]
public class ClipboardScenarioTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3];

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
}
