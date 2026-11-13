using LinkTest.Core.Tunnels;

namespace LinkTest.Client;

/// <summary>Property-grid editor for a tunnel profile.</summary>
sealed class TunnelEditForm : Form
{
    public TunnelProfile Profile { get; }

    public TunnelEditForm(TunnelProfile profile)
    {
        Profile = profile.Clone();
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "Tunnel simulation - " + profile.Name;
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 9f);
        Size = new Size(520, 600);
        MinimizeBox = false;
        MaximizeBox = false;
        ShowIcon = false;
        Icon = MainForm.AppIcon;

        var grid = new PropertyGrid { Dock = DockStyle.Fill, SelectedObject = Profile, ToolbarVisible = false, PropertySort = PropertySort.Categorized };
        grid.PropertyValueChanged += (_, _) => grid.Refresh();   // refresh computed detection time

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 42, Padding = new Padding(6) };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Width = 90 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 90 };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        AcceptButton = ok;
        CancelButton = cancel;
        ok.Click += (_, _) => Profile.Normalize();

        Controls.Add(grid);
        Controls.Add(buttons);
        ResumeLayout(false);
        PerformLayout();
    }
}
