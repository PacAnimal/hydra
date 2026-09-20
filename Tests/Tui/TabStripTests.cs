using System.Drawing;
using Hydra.Tui;
using Terminal.Gui.ViewBase;

namespace Tests.Tui;

// TabStrip hand-rolls its own mouse hit-testing (Hydra/Tui/TabStrip.cs) instead of relying on
// Terminal.Gui's view-tree click routing, because tabs aren't separate subviews — they're glyphs
// this view paints itself. That means the hit-test boundaries (TrySelectAt) have to be kept in
// lockstep with the column math OnDrawingContent actually draws by hand, with nothing enforcing
// the two stay in sync. This file pins that relationship column-by-column so a future change to
// either side trips a test instead of silently drifting (as Recompute() previously did — it never
// accounted for the border glyph drawn after every tab, so clicks progressively drifted onto the
// next tab the further right they landed).
public class TabStripTests
{
    // Select() has no public "what's selected" query, so tests read it back the same way the real
    // UI does: which tab's content view is visible. Content views are handed to AddTab, so the
    // fixture keeps them to assert against instead of poking at TabStrip's internals.
    private sealed record Fixture(TabStrip Strip, View[] Contents)
    {
        internal int SelectedIndex => Array.FindIndex(Contents, c => c.Visible);
    }

    private static Fixture Build(params string[] titles)
    {
        var strip = new TabStrip { X = 0, Y = 0 };
        var contents = titles.Select(_ => new View()).ToArray();
        for (var i = 0; i < titles.Length; i++) strip.AddTab(titles[i], contents[i]);
        return new Fixture(strip, contents);
    }

    private static bool ClickAt(Fixture fixture, int x, int y = 1) => fixture.Strip.TrySelectAt(new Point(x, y));

    // three tabs, deliberately different label lengths so border-glyph drift (which grows with
    // preceding tab count) would show up differently on each one if it ever came back:
    // tab 0 "One"   label cols [1,6)  (" One "),   border glyph col 6
    // tab 1 "Two"   label cols [7,12) (" Two "),   border glyph col 12
    // tab 2 "Three" label cols [13,20) (" Three "), border glyph col 20
    private static Fixture ThreeTabs() => Build("One", "Two", "Three");

    [TestCase(1, 0)] // leftmost column of the label
    [TestCase(3, 0)] // middle of the label
    [TestCase(5, 0)] // rightmost column of the label
    public void ClickOnFirstTabLabel_SelectsFirstTab(int x, int expectedIndex)
    {
        var fixture = ThreeTabs();
        fixture.Strip.Select(2); // start on a different tab so a false "no-op" match can't pass silently
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ClickAt(fixture, x), Is.True);
            Assert.That(fixture.SelectedIndex, Is.EqualTo(expectedIndex));
        }
    }

    [TestCase(7, 1)]
    [TestCase(9, 1)]
    [TestCase(11, 1)]
    public void ClickOnMiddleTabLabel_SelectsMiddleTab(int x, int expectedIndex)
    {
        var fixture = ThreeTabs();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ClickAt(fixture, x), Is.True);
            Assert.That(fixture.SelectedIndex, Is.EqualTo(expectedIndex));
        }
    }

    [TestCase(13, 2)]
    [TestCase(16, 2)]
    [TestCase(19, 2)]
    public void ClickOnLastTabLabel_SelectsLastTab(int x, int expectedIndex)
    {
        var fixture = ThreeTabs();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ClickAt(fixture, x), Is.True);
            Assert.That(fixture.SelectedIndex, Is.EqualTo(expectedIndex));
        }
    }

    // this is the exact bug: before the fix, Recompute() never counted the border glyph after
    // each tab, so the cached bounds drifted left by one column per preceding tab. Clicking the
    // rightmost columns of tab 1 or tab 2's real on-screen label landed on the NEXT tab instead.
    [TestCase(11, 1, TestName = "RightmostColumnOfMiddleTabLabel_StaysOnMiddleTab")]
    [TestCase(19, 2, TestName = "RightmostColumnOfLastTabLabel_StaysOnLastTab")]
    public void ClickDoesNotDriftOntoNextTab(int x, int expectedIndex)
    {
        var fixture = ThreeTabs();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ClickAt(fixture, x), Is.True);
            Assert.That(fixture.SelectedIndex, Is.EqualTo(expectedIndex));
        }
    }

    [TestCase(0)]  // the strip's own '╭' left corner
    [TestCase(6)]  // border glyph between tab 0 and tab 1
    [TestCase(12)] // border glyph between tab 1 and tab 2
    [TestCase(20)] // border glyph after the last tab
    [TestCase(21)] // past everything — open space before the box's right edge
    public void ClickOnBorderGlyphsAndDeadSpace_SelectsNothing(int x)
    {
        var fixture = ThreeTabs();
        fixture.Strip.Select(1);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ClickAt(fixture, x), Is.False);
            Assert.That(fixture.SelectedIndex, Is.EqualTo(1)); // selection unchanged
        }
    }

    [TestCase(-1)]
    [TestCase(3)]
    public void ClickOutsideTheHeaderRows_SelectsNothing(int y)
    {
        var fixture = ThreeTabs();
        Assert.That(fixture.Strip.TrySelectAt(new Point(3, y)), Is.False);
    }

    [Test]
    public void ClickOnCurrentlySelectedTab_ReportsHitWithoutRaisingSelectionChanged()
    {
        var fixture = ThreeTabs();
        var raised = false;
        fixture.Strip.SelectionChanged += () => raised = true;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ClickAt(fixture, 3), Is.True); // tab 0, already selected
            Assert.That(raised, Is.False);
        }
    }

    [Test]
    public void ClickOnAnotherTab_RaisesSelectionChangedExactlyOnce()
    {
        var fixture = ThreeTabs();
        var raisedCount = 0;
        fixture.Strip.SelectionChanged += () => raisedCount++;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ClickAt(fixture, 9), Is.True); // tab 1
            Assert.That(raisedCount, Is.EqualTo(1));
            Assert.That(fixture.SelectedIndex, Is.EqualTo(1));
        }
    }

    // full-sweep regression guard: every single column across the whole rendered header row must
    // land on exactly the tab whose label actually covers that column on screen (or none, for a
    // border/dead-space column) — not "close enough". Re-derives the expected owner independently
    // from label lengths rather than reusing TabStrip's own Recompute(), so a bug reintroduced into
    // both in the same way still gets caught.
    [Test]
    public void EveryColumnOfTheHeaderRow_MapsToItsOwnRenderedTab()
    {
        string[] titles = ["One", "Two", "Three", "Four"];
        var fixture = Build(titles);

        var column = 1; // column 0 is the strip's own '╭' corner
        for (var i = 0; i < titles.Length; i++)
        {
            var labelWidth = titles[i].Length + 2; // one padding space either side, as TabStrip does
            for (var offset = 0; offset < labelWidth; offset++)
            {
                fixture.Strip.Select(0);
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(ClickAt(fixture, column + offset), Is.True,
                                    $"column {column + offset} (tab {i} offset {offset}) should hit a tab");
                    Assert.That(fixture.SelectedIndex, Is.EqualTo(i),
                        $"column {column + offset} (tab {i} offset {offset}) selected the wrong tab");
                }
            }
            column += labelWidth;

            fixture.Strip.Select(0);
            Assert.That(ClickAt(fixture, column), Is.False, $"column {column} is tab {i}'s border glyph — should miss");
            column += 1; // the border glyph column itself
        }
    }

    [Test]
    public void SingleTab_ClickAnywhereOnItsLabelSelectsIt()
    {
        var fixture = Build("Only");
        for (var x = 1; x <= 5; x++) // "Only" is 4 chars + 2 padding = columns 1..6
            Assert.That(ClickAt(fixture, x), Is.True, $"column {x} should hit the only tab");
    }

    [Test]
    public void SelectingOutOfRangeIndex_IsANoOp()
    {
        var fixture = ThreeTabs();
        var raised = false;
        fixture.Strip.SelectionChanged += () => raised = true;

        fixture.Strip.Select(-1);
        fixture.Strip.Select(99);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(raised, Is.False);
            Assert.That(fixture.SelectedIndex, Is.Zero);
        }
    }
}
