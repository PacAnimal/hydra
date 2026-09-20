using System.Text.RegularExpressions;
using Terminal.Gui.Drawing;
using Terminal.Gui.Editor.Highlighting;

namespace Hydra.Tui;

// A minimal IHighlightingDefinition backed by a flat list of (regex, color) rules — the read-only
// Overview/Peers/Diagnostics/Logs panes are plain formatted text, not a real language, so they need
// single-token colouring rather than AvalonEdit's full span/keyword machinery.
internal sealed class RegexHighlightingDefinition : IHighlightingDefinition
{
    public string Name { get; }
    public HighlightingRuleSet MainRuleSet { get; } = new();
    public IEnumerable<HighlightingColor> NamedHighlightingColors => [];
    public IDictionary<string, string> Properties => new Dictionary<string, string>();
    public HighlightingRuleSet? GetNamedRuleSet(string ruleSetName) => null;
    public HighlightingColor? GetNamedColor(string colorName) => null;

    private RegexHighlightingDefinition(string name, IEnumerable<(Regex Pattern, HighlightingColor Color)> rules)
    {
        Name = name;
        foreach (var (pattern, color) in rules)
            MainRuleSet.Rules.Add(new HighlightingRule { Regex = pattern, Color = color });
    }

    private static HighlightingColor Fg(ColorName16 color, bool bold = false) => new()
    {
        Foreground = new HighlightingBrush(new Color(color)),
        Bold = bold,
    };

    // Overview/Peers/Diagnostics: status dots, section headers, metrics, addresses, and the
    // dim placeholder text ("(none)", "unavailable", …) that fills in for missing data.
    internal static readonly RegexHighlightingDefinition Status = new("HydraStatus",
    [
        // section headers have no leading indent; every data row does — distinguishes them without
        // having to enumerate the header text itself
        (new Regex(@"^[A-Z].*$", RegexOptions.Compiled), Fg(ColorName16.BrightCyan, bold: true)),
        (new Regex(@"●", RegexOptions.Compiled), Fg(ColorName16.BrightGreen, bold: true)),
        (new Regex(@"○", RegexOptions.Compiled), Fg(ColorName16.DarkGray)),
        (new Regex(@"\b\d+(\.\d+)?\s?(GiB|MiB|KiB|Mbps|Gbps|ms|msg)\b|\b\d{2,5}[×x]\d{2,5}\b", RegexOptions.Compiled), Fg(ColorName16.BrightYellow)),
        (new Regex(@"\b\d{1,3}(\.\d{1,3}){3}(:\d+)?\b|\b[0-9a-f:]*:[0-9a-f:]+\b|\b[\w.-]+\.[a-z]{2,}(:\d+)?\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase), Fg(ColorName16.BrightBlue)),
        (new Regex(@"\[(MacOS|Windows|Linux)\]", RegexOptions.Compiled), Fg(ColorName16.BrightMagenta)),
        (new Regex(@"\((none|none detected|not hosting an embedded relay|collecting samples|no peers online)\)|\bunavailable\b|\bn/a\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase), Fg(ColorName16.DarkGray)),
    ]);

    // Logs: colour by level word — the fixed-width field the TUI pads every log line with.
    internal static readonly RegexHighlightingDefinition Logs = new("HydraLogs",
    [
        (new Regex(@"\bWarning\b", RegexOptions.Compiled), Fg(ColorName16.BrightYellow)),
        (new Regex(@"\b(Error|Critical)\b", RegexOptions.Compiled), Fg(ColorName16.BrightRed, bold: true)),
    ]);
}
