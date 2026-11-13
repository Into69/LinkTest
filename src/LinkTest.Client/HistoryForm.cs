using System.Diagnostics;
using System.Globalization;
using LinkTest.Core;

namespace LinkTest.Client;

/// <summary>Saved test results: open or export the HTML report, export CSV, delete.</summary>
sealed class HistoryForm : Form
{
    readonly ResultStore _store;
    readonly ListView _list = new() { View = View.Details, FullRowSelect = true, GridLines = true, Dock = DockStyle.Fill, MultiSelect = true };
    readonly Label _info = new() { AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(8, 10, 8, 0) };
    List<SavedResult> _items = new();

    public HistoryForm(ResultStore store)
    {
        _store = store;
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "LinkTest - test history";
        Icon = MainForm.AppIcon;
        Font = new Font("Segoe UI", 9f);
        ClientSize = new Size(1150, 560);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;

        foreach (var (name, w) in new[] { ("Started", 140), ("Server", 200), ("Mode", 110), ("Duration", 70), ("Up Mbps", 70), ("Down Mbps", 75),
                                          ("Loss ↑ / ↓ %", 95), ("RTT ms", 60), ("Bufferbloat", 75), ("MOS", 45), ("Tunnel drops", 85), ("Ended", 140) })
            _list.Columns.Add(name, (int)(w * DeviceDpi / 96f));
        _list.DoubleClick += (_, _) => OpenReport();

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(8), WrapContents = true };
        buttons.Controls.Add(Btn("Open report", OpenReport));
        buttons.Controls.Add(Btn("Save report as…", SaveReport));
        buttons.Controls.Add(Btn("Export CSV…", ExportCsv));
        buttons.Controls.Add(Btn("Open results folder", () => Open(_store.Folder)));
        buttons.Controls.Add(Btn("Delete", Delete));
        buttons.Controls.Add(_info);

        Controls.Add(_list);
        Controls.Add(buttons);
        ResumeLayout(false);
        PerformLayout();
        Load += (_, _) => Reload();
    }

    Button Btn(string text, Action a)
    {
        var b = new Button { Text = text, AutoSize = true, Padding = new Padding(6, 2, 6, 2) };
        b.Click += (_, _) => a();
        return b;
    }

    void Reload()
    {
        _items = _store.List();
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var r in _items.Select(i => i.Summary))
        {
            static string F(double v, string f) => double.IsFinite(v) ? v.ToString(f, CultureInfo.CurrentCulture) : "–";
            _list.Items.Add(new ListViewItem(new[]
            {
                r.StartedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture), r.Server, r.Mode,
                Clock.FormatDuration(r.DurationSec), F(r.UpMbpsAvg, "0.0"), F(r.DownMbpsAvg, "0.0"),
                $"{F(r.UpLossPct, "0.##")} / {F(r.DownLossPct, "0.##")}", F(r.RttAvgMs, "0.0"), r.BloatGrade ?? "–", F(r.MosAvg, "0.0"),
                r.Tunnels.Count == 0 ? "–" : r.Tunnels.Sum(t => t.Drops).ToString(CultureInfo.CurrentCulture), r.EndReason,
            }));
        }
        _list.EndUpdate();
        _info.Text = $"{_items.Count} saved test(s) in {_store.Folder}";
        if (_list.Items.Count > 0) _list.Items[0].Selected = true;
    }

    SavedResult? Selected => _list.SelectedIndices.Count > 0 ? _items[_list.SelectedIndices[0]] : null;

    /// <summary>Writes the report to a temp file and opens it in the default browser.</summary>
    /// <returns>An error message, or null on success.</returns>
    public static string? OpenReport(TestRecord r)
    {
        try
        {
            Open(ReportBuilder.WriteFile(r, Path.Combine(Path.GetTempPath(), "LinkTest-reports"), "html"));
            return null;
        }
        catch (Exception e) { return e.Message; }
    }

    void OpenReport()
    {
        if (Selected is not { } s) return;
        Try("open the report", () =>
        {
            if (OpenReport(s.Load()) is { } err) throw new IOException(err);
        });
    }

    void SaveReport()
    {
        if (Selected is not { } s) return;
        using var d = new SaveFileDialog { FileName = s.Summary.SuggestedFileName("html"), Filter = "HTML report (*.html)|*.html", Title = "Save report" };
        if (d.ShowDialog(this) == DialogResult.OK) Try("save the report", () => File.WriteAllText(d.FileName, ReportBuilder.Build(s.Load())));
    }

    void ExportCsv()
    {
        if (Selected is not { } s) return;
        using var d = new SaveFileDialog { FileName = s.Summary.SuggestedFileName("csv"), Filter = "CSV (*.csv)|*.csv", Title = "Export per-second figures" };
        if (d.ShowDialog(this) == DialogResult.OK) Try("export the CSV", () => File.WriteAllText(d.FileName, s.Load().ToCsv()));
    }

    void Try(string what, Action a)
    {
        try { UseWaitCursor = true; a(); }
        catch (Exception e) { MessageBox.Show(this, $"Could not {what}:\n{e.Message}", "LinkTest", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        finally { UseWaitCursor = false; }
    }

    void Delete()
    {
        var sel = _list.SelectedIndices.Cast<int>().Select(i => _items[i]).ToList();
        if (sel.Count == 0) return;
        if (MessageBox.Show(this, $"Delete {sel.Count} saved test(s)?", "LinkTest", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        foreach (var x in sel) ResultStore.Delete(x.Path);
        Reload();
    }

    static void Open(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { }
    }
}
