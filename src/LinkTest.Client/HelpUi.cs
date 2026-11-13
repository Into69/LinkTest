using LinkTest.Core;

namespace LinkTest.WinUi;

/// <summary>
/// Explanations for statistics: ⓘ glyphs (hover for a summary, click for details), a popup for one statistic and
/// a dialog explaining all columns of a table. Shared by the client and the server GUI.
/// </summary>
static class HelpUi
{
    public const string Glyph = "ⓘ";
    static readonly Color GoodC = Color.FromArgb(26, 127, 55), FairC = Color.FromArgb(179, 89, 0), PoorC = Color.FromArgb(198, 40, 40),
        MutedC = Color.FromArgb(95, 107, 122), AccentC = Color.FromArgb(0, 95, 184);

    /// <summary>A small clickable ⓘ for one statistic.</summary>
    public static Label InfoLabel(string key, ToolTip tips, DockStyle dock = DockStyle.None)
    {
        var info = StatHelp.Get(key);
        var l = new Label
        {
            Text = Glyph, AutoSize = true, Cursor = Cursors.Hand, ForeColor = AccentC, Dock = dock,
            Font = new Font("Segoe UI", 9f), Padding = new Padding(0), Margin = new Padding(0), TabStop = false,
        };
        if (info != null)
        {
            tips.SetToolTip(l, info.Title + "\n" + info.Short + "\n(click for details)");
            l.Click += (_, _) => Show(l.FindForm(), info);
        }
        return l;
    }

    /// <summary>A link "ⓘ About these columns" that explains every entry of a help group.</summary>
    public static LinkLabel GroupLink(string group, string title, string text = "About these columns")
    {
        var l = new LinkLabel { Text = $"{Glyph} {text}", AutoSize = true, LinkColor = AccentC, Margin = new Padding(4, 4, 4, 2) };
        l.LinkClicked += (_, _) => ShowGroup(l.FindForm(), title, group);
        return l;
    }

    /// <summary>Details of one statistic.</summary>
    public static void Show(IWin32Window? owner, StatInfo info) => ShowDialog(owner, info.Title, new[] { info }, compact: true);

    public static void ShowGroup(IWin32Window? owner, string title, string group) => ShowDialog(owner, title, StatHelp.Group(group), compact: false);

    static void ShowDialog(IWin32Window? owner, string title, IReadOnlyList<StatInfo> items, bool compact)
    {
        using var f = new Form
        {
            Text = title, StartPosition = FormStartPosition.CenterParent, ShowInTaskbar = false, MinimizeBox = false, MaximizeBox = !compact,
            FormBorderStyle = FormBorderStyle.SizableToolWindow,
            AutoScaleMode = AutoScaleMode.Dpi, AutoScaleDimensions = new SizeF(96f, 96f), Font = new Font("Segoe UI", 9.5f),
            ClientSize = compact ? new Size(500, 330) : new Size(640, 560), KeyPreview = true,
        };
        var box = new RichTextBox
        {
            Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = Color.White,
            ScrollBars = RichTextBoxScrollBars.Vertical, DetectUrls = false,
        };
        var pad = new Panel { Dock = DockStyle.Fill, Padding = new Padding(14, 10, 10, 6), BackColor = Color.White };
        pad.Controls.Add(box);
        var ok = new Button { Text = "Close", DialogResult = DialogResult.OK, AutoSize = true, Anchor = AnchorStyles.Right };
        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(8) };
        bar.Controls.Add(ok);
        f.AcceptButton = ok;
        f.CancelButton = ok;
        f.Controls.Add(pad);
        f.Controls.Add(bar);

        var bold = new Font(f.Font, FontStyle.Bold);
        void Write(string s, Color? c = null, Font? font = null)
        {
            box.SelectionStart = box.TextLength;
            box.SelectionColor = c ?? Color.Black;
            box.SelectionFont = font ?? f.Font;
            box.AppendText(s);
        }
        bool first = true;
        foreach (var i in items)
        {
            if (!first) Write("\n");
            first = false;
            if (!compact) Write(i.Title + "\n", AccentC, bold);
            Write(i.What + "\n");
            if (i.Ideal.Length > 0) { Write("Ideal: ", GoodC, bold); Write(i.Ideal + "\n"); }
            foreach (var (level, text) in i.Ranges)
            {
                var c = level switch { StatLevel.Good => GoodC, StatLevel.Fair => FairC, StatLevel.Poor => PoorC, _ => MutedC };
                Write("   ● ", c);
                Write(StatInfo.LevelName(level) + ": ", c, bold);
                Write(text + "\n", level == StatLevel.Info ? MutedC : null);
            }
        }
        box.SelectionStart = 0;
        if (compact)
            f.Shown += (_, _) =>
            {
                // Grow until the text fits (layout is final once the window is shown).
                for (int i = 0; i < 12 && box.GetPositionFromCharIndex(Math.Max(0, box.TextLength - 1)).Y + box.Font.Height * 2 > box.ClientSize.Height; i++)
                    f.Height += box.Font.Height * 2;
            };
        f.ShowDialog(owner);
    }
}
