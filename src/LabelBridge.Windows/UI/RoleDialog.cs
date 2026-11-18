using LabelBridge.Core;

namespace LabelBridge.UI;

/// <summary>First-run choice: is this PC next to the printer, or is it a remote user?</summary>
internal sealed class RoleDialog : Form
{
    public string Role { get; private set; } = "client";

    private readonly RadioButton _client;
    private readonly RadioButton _host;

    public RoleDialog()
    {
        Text = "LabelBridge - first run";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        BackColor = Color.White;
        ClientSize = new Size(600, 400);
        Font = Theme.Body;
        Icon = IconFactory.AppIcon(32);

        var title = new Label
        {
            Text = "How will this computer use LabelBridge?",
            Font = Theme.H1,
            ForeColor = Theme.Text,
            AutoSize = true,
            Location = new Point(28, 24)
        };

        var sub = new Label
        {
            Text = "You can change this later in the app.",
            Font = Theme.Body,
            ForeColor = Theme.Muted,
            AutoSize = true,
            Location = new Point(30, 58)
        };

        _host = MakeOption(
            "This PC is next to the label printer  (Host)",
            "Receives jobs from the internet and prints them on the local printer. " +
            "Install this on the computer that can reach the printer.",
            new Point(24, 92), 84);

        _client = MakeOption(
            "This PC prints to a remote label printer  (Client)",
            "Installs the virtual printer. Anything this PC prints is sent securely " +
            "to the host and printed on the real label printer.",
            new Point(24, 190), 84);

        _client.Checked = true;

        var ok = Ui.Button("Continue", primary: true, width: 120);
        ok.Location = new Point(ClientSize.Width - 148, ClientSize.Height - 58);
        ok.Click += (_, _) =>
        {
            Role = _host.Checked ? "host" : "client";
            DialogResult = DialogResult.OK;
            Close();
        };

        Controls.AddRange(new Control[] { title, sub, _host.Parent!, _client.Parent!, ok });
        AcceptButton = ok;
    }

    private RadioButton MakeOption(string title, string description, Point location, int height)
    {
        var panel = new Panel
        {
            Location = location,
            Size = new Size(ClientSize.Width - 48, height),
            BackColor = Color.FromArgb(0xF8, 0xFA, 0xFD),
            BorderStyle = BorderStyle.FixedSingle,
            Cursor = Cursors.Hand
        };

        var radio = new RadioButton
        {
            Text = title,
            Font = Theme.BodyMedium,
            ForeColor = Theme.Text,
            AutoSize = true,
            Location = new Point(14, 12),
            Cursor = Cursors.Hand
        };

        var desc = new Label
        {
            Text = description,
            Font = Theme.Small,
            ForeColor = Theme.Muted,
            AutoSize = false,
            Size = new Size(panel.Width - 44, 44),
            Location = new Point(35, 36)
        };

        panel.Controls.Add(radio);
        panel.Controls.Add(desc);
        panel.Click += (_, _) => radio.Checked = true;
        desc.Click += (_, _) => radio.Checked = true;
        panel.Tag = radio;

        return radio;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Theme.Border);
        e.Graphics.DrawLine(pen, 0, ClientSize.Height - 76, ClientSize.Width, ClientSize.Height - 76);
    }
}
