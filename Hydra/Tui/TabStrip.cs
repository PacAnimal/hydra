using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;

namespace Hydra.Tui;

// Hand-rolled replacement for Terminal.Gui's built-in Tabs view. Tabs' own border-junction
// rendering only comes out correct when the first tab is selected — for any other selection it
// merges adjacent inactive tabs' walls together or leaves them floating, disconnected from the
// content box below (confirmed across several Terminal.Gui 2.4.x TabLineStyle/TabSpacing
// combinations; none rendered correctly for a non-first selection). Drawing the tab strip and
// the surrounding content box as one shape means every junction between a tab wall and the box's
// border is computed by us, directly, so it's right in every selection position by construction.
//
// Layout: rows 0-1 are the tab headers (top corners, then labels); row 2 is the box's own top
// edge, open (blank) under the selected tab and closed (with a junction) under every other one;
// the remaining rows are the box's left/right walls and bottom edge, with content views inset
// inside it.
internal sealed class TabStrip : View
{
    private sealed class TabEntry(string mnemonicTitle, View content)
    {
        internal readonly string PlainTitle = mnemonicTitle.Replace("_", "");
        internal readonly View Content = content;

        // Exists solely so the app-wide Alt-mnemonic scanner (which walks the view tree looking
        // at Title) finds this tab and can fire Select() — never meant to be seen. Parked one cell
        // outside the viewport (never drawn, never clipped into view) so it can't interfere with
        // this view's own drawing: giving it any on-screen rectangle corrupts every tab's header,
        // since a subview's own viewport-clear reliably beats this view's content, which draws
        // after all subviews (see OnDrawingContent for the actual header rendering).
        internal readonly View HotKeyProxy = new() { Title = mnemonicTitle, BorderStyle = LineStyle.None, CanFocus = false, X = -1, Y = -1, Width = 1, Height = 1 };
        internal int Start;
        internal int Width;
    }

    private readonly List<TabEntry> _entries = [];
    private int _selected;

    internal event Action? SelectionChanged;

    internal TabStrip()
    {
        CanFocus = false;
    }

    internal void AddTab(string mnemonicTitle, View content)
    {
        var entry = new TabEntry(mnemonicTitle, content);
        _entries.Add(entry);

        content.X = 1;
        content.Y = 3;
        content.Width = Dim.Fill(1);
        content.Height = Dim.Fill(1);
        content.Visible = _entries.Count == 1;
        SetDescendantsEnabled(content, _entries.Count == 1);
        Add(content);

        // HotKey routes through Command.Activate (Activating/Activated), never Command.Accept
        // (Accepting) — HotKeyCommand is the event actually raised when the hotkey itself fires.
        var index = _entries.Count - 1;
        entry.HotKeyProxy.HotKeyCommand += (_, _) => Select(index);
        Add(entry.HotKeyProxy);

        Recompute();
    }

    private void Recompute()
    {
        var x = 1;
        foreach (var e in _entries)
        {
            e.Width = e.PlainTitle.Length + 2; // one space of padding either side of the label
            e.Start = x;
            x += e.Width + 1; // +1 for the '╮'/'│' border glyph OnDrawingContent draws after this tab
        }
    }

    internal void Select(int index)
    {
        if (index < 0 || index >= _entries.Count || index == _selected) return;
        SetDescendantsEnabled(_entries[_selected].Content, false);
        _entries[_selected].Content.Visible = false;
        _selected = index;
        _entries[_selected].Content.Visible = true;
        SetDescendantsEnabled(_entries[_selected].Content, true);
        SetNeedsDraw();
        SelectionChanged?.Invoke();
    }

    /// <summary>Selects the tab under the given screen position, if any. Returns whether one was hit.</summary>
    internal bool TrySelectAt(System.Drawing.Point screenPosition)
    {
        var origin = FrameToScreen().Location;
        var localX = screenPosition.X - origin.X;
        var localY = screenPosition.Y - origin.Y;
        if (localY is < 0 or > 2) return false;

        for (var i = 0; i < _entries.Count; i++)
        {
            if (localX < _entries[i].Start || localX >= _entries[i].Start + _entries[i].Width) continue;
            Select(i);
            return true;
        }
        return false;
    }

    // Recurses into every descendant (not the root itself, so a container's own Title mnemonic —
    // used to switch to it — keeps working even while its contents are disabled). Shared with
    // HydraTui.TuiController.SelectFormSection, which disables Configuration's own nested sections
    // the same way this disables backgrounded tabs.
    internal static void SetDescendantsEnabled(View root, bool enabled)
    {
        foreach (var child in root.GetSubViews())
        {
            child.Enabled = enabled;
            SetDescendantsEnabled(child, enabled);
        }
    }

    protected override bool OnDrawingContent(DrawContext? context)
    {
        var width = Viewport.Width;
        var height = Viewport.Height;
        if (width <= 0 || height <= 0 || _entries.Count == 0) return true;

        var normal = GetAttributeForRole(VisualRole.Normal);
        var dim = normal with { Foreground = new Color(ColorName16.Gray) };
        SetAttribute(normal);

        // Row 0: tab tops — "╭──────╮──────────╮...".
        Move(0, 0);
        AddRune('╭');
        foreach (var e in _entries)
        {
            AddStr(new string('─', e.Width));
            AddRune('╮');
        }

        // Row 1: labels — "│ Overview │ Peers & Screens │...". Only the selected tab's label
        // gets the normal (bright) foreground; the rest are dimmed so the active tab stands out.
        Move(0, 1);
        AddRune('│');
        for (var i = 0; i < _entries.Count; i++)
        {
            SetAttribute(i == _selected ? normal : dim);
            AddStr($" {_entries[i].PlainTitle} ");
            SetAttribute(normal);
            AddRune('│');
        }

        // Row 2: the content box's own top edge — open (blank) under the selected tab, a normal
        // line with a junction everywhere else. Column 0 is special: it's also the box's own left
        // wall, which keeps going for the rest of the box's height, so when tab 0 is closed here
        // this needs a three-way junction (up into the tab wall, right into its closed bottom,
        // down into the wall below) — a plain corner would leave the wall below disconnected.
        var lastEntry = _entries.Count - 1;
        Move(0, 2);
        AddRune(_selected == 0 ? '│' : '├');
        for (var i = 0; i < _entries.Count; i++)
        {
            var leftOpen = i == _selected;
            AddStr(leftOpen ? new string(' ', _entries[i].Width) : new string('─', _entries[i].Width));

            var rightOpen = i < lastEntry && i + 1 == _selected;
            AddRune(JunctionGlyph(leftOpen, rightOpen));
        }
        // Fill the remaining width (tabs never span the full box) with a plain line to the corner.
        var afterTabs = _entries[lastEntry].Start + _entries[lastEntry].Width + 1;
        if (afterTabs < width - 1)
            AddStr(new string('─', width - 1 - afterTabs));
        Move(width - 1, 2);
        AddRune('╮');

        // Left/right walls for the content box.
        for (var y = 3; y < height - 1; y++)
        {
            Move(0, y);
            AddRune('│');
            Move(width - 1, y);
            AddRune('│');
        }

        // Bottom edge.
        Move(0, height - 1);
        AddRune('╰');
        AddStr(new string('─', width - 2));
        AddRune('╯');

        return true;
    }

    // The glyph at a tab boundary: a wall always descends from above (every tab has side walls
    // in rows 0-1 regardless of selection), so this is always a "some kind of T or corner"
    // shape — which one depends on whether the box's top line continues to the left/right of it.
    // Corners always angle away from the open tab (into its closed neighbor), never into it.
    private static char JunctionGlyph(bool openLeft, bool openRight) => (openLeft, openRight) switch
    {
        (false, false) => '┴',
        (true, false) => '╰',
        (false, true) => '╯',
        (true, true) => '│', // a single-tab strip: open on both sides of its own boundary
    };
}
