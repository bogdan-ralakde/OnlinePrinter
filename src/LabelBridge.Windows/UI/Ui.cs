using System.Drawing.Drawing2D;

namespace LabelBridge.UI;

internal static class Theme
{
    public static readonly Color Bg = Color.FromArgb(0xF3, 0xF5, 0xF9);
    public static readonly Color Card = Color.White;
    public static readonly Color Border = Color.FromArgb(0xE2, 0xE8, 0xF0);
    public static readonly Color Text = Color.FromArgb(0x0F, 0x17, 0x2A);
    public static readonly Color Muted = Color.FromArgb(0x64, 0x74, 0x8B);
    public static readonly Color Faint = Color.FromArgb(0x94, 0xA3, 0xB8);
    public static readonly Color Accent = Color.FromArgb(0x25, 0x63, 0xEB);
    public static readonly Color AccentHover = Color.FromArgb(0x1D, 0x4E, 0xD8);
    public static readonly Color Green = Color.FromArgb(0x15, 0x80, 0x3D);
    public static readonly Color GreenSoft = Color.FromArgb(0xDC, 0xFC, 0xE7);
    public static readonly Color Amber = Color.FromArgb(0xB4, 0x53, 0x09);
    public static readonly Color AmberSoft = Color.FromArgb(0xFE, 0xF3, 0xC7);
    public static readonly Color Red = Color.FromArgb(0xB9, 0x1C, 0x1C);
    public static readonly Color RedSoft = Color.FromArgb(0xFE, 0xE2, 0xE2);
    public static readonly Color GraySoft = Color.FromArgb(0xEE, 0xF2, 0xF7);
    public static readonly Color HeaderLine = Color.FromArgb(0xE6, 0xEB, 0xF2);

    public static readonly Font Body = new("Segoe UI", 9.75f);
    public static readonly Font BodyMedium = new("Segoe UI Semibold", 9.75f);
    public static readonly Font Small = new("Segoe UI", 8.75f);
    public static readonly Font H3 = new("Segoe UI Semibold", 11f);
    public static readonly Font H2 = new("Segoe UI Semibold", 13f);
    public static readonly Font H1 = new("Segoe UI Semibold", 16f);
    public static readonly Font Mono = new("Consolas", 9f);

    public static GraphicsPath RoundRect(Rectangle rect, int radius)
    {
        var path = new GraphicsPath();
        int d = radius * 2;
        if (d <= 0 || rect.Width < d || rect.Height < d)
        {
            path.AddRectangle(rect);
            return path;
        }
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

/// <summary>White bordered container that grows with its content.</summary>
internal sealed class Card : Panel
{
    private readonly TableLayoutPanel _rows;

    public Card(string? title = null)
    {
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        BackColor = Theme.Card;
        Padding = new Padding(18, 15, 18, 14);
        Margin = new Padding(0, 0, 0, 14);
        DoubleBuffered = true;

        _rows = new TableLayoutPanel
        {
            ColumnCount = 1,
            RowCount = 0,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top,
            BackColor = Color.Transparent,
            Margin = new Padding(0)
        };
        _rows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Controls.Add(_rows);

        if (!string.IsNullOrEmpty(title)) AddTitle(title);
    }

    public TableLayoutPanel Rows => _rows;

    public void AddTitle(string title)
    {
        var label = new Label
        {
            Text = title,
            AutoSize = true,
            Font = Theme.H3,
            ForeColor = Theme.Text,
            Margin = new Padding(0, 0, 0, 9)
        };
        AddRow(label);
    }

    public void AddRow(Control control)
    {
        control.Dock = DockStyle.Top;
        if (control.Margin == Padding.Empty || control.Margin == new Padding(0))
            control.Margin = new Padding(0, 0, 0, 8);

        int row = _rows.RowCount;
        _rows.RowCount = row + 1;
        _rows.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _rows.Controls.Add(control, 0, row);
    }

    public void AddRow(params Control[] controls)
    {
        AddRow(Ui.Row(controls));
        _rows.Controls[_rows.Controls.Count - 1].Margin = new Padding(0, 0, 0, 8);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Theme.Border);
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }
}

internal sealed class StatCard : Control
{
    public string Caption { get; set; } = "";
    public string Value { get; set; } = "-";
    public string Hint { get; set; } = "";
    public Color Accent { get; set; } = Theme.Accent;

    public StatCard()
    {
        Height = 96;
        Dock = DockStyle.Fill;
        Margin = new Padding(0, 0, 12, 0);
        DoubleBuffered = true;
        BackColor = Theme.Card;
    }

    public void Set(string value, string hint = "")
    {
        if (Value == value && Hint == hint) return;
        Value = value;
        Hint = hint;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var bg = new SolidBrush(Theme.Card)) g.FillRectangle(bg, ClientRectangle);
        using (var pen = new Pen(Theme.Border)) g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        using (var accent = new SolidBrush(Accent)) g.FillRectangle(accent, 0, 0, 3, Height);

        TextRenderer.DrawText(g, Caption, Theme.Small, new Rectangle(16, 13, Width - 26, 18),
            Theme.Muted, TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(g, Value, Theme.H1, new Rectangle(15, 31, Width - 26, 32),
            Theme.Text, TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        if (!string.IsNullOrEmpty(Hint))
            TextRenderer.DrawText(g, Hint, Theme.Small, new Rectangle(16, 66, Width - 26, 18),
                Theme.Faint, TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
    }
}

internal sealed class Pill : Control
{
    private Color _fill = Theme.GraySoft;
    private Color _fore = Theme.Muted;

    public Pill()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Size = new Size(168, 28);
    }

    public void Set(string text, Color fill, Color fore)
    {
        Text = text;
        _fill = fill;
        _fore = fore;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using var path = Theme.RoundRect(new Rectangle(0, 0, Width - 1, Height - 1), Height / 2);
        using (var brush = new SolidBrush(_fill)) g.FillPath(brush, path);

        var dot = new Rectangle(12, Height / 2 - 3, 7, 7);
        using (var dotBrush = new SolidBrush(_fore)) g.FillEllipse(dotBrush, dot);

        TextRenderer.DrawText(g, Text, Theme.BodyMedium,
            new Rectangle(26, 0, Width - 34, Height), _fore,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
    }
}

/// <summary>Vertically stacked, auto-scrolling page body.</summary>
internal sealed class CardsStack : FlowLayoutPanel
{
    public CardsStack()
    {
        Dock = DockStyle.Fill;
        FlowDirection = FlowDirection.TopDown;
        WrapContents = false;
        AutoScroll = true;
        BackColor = Theme.Bg;
        Padding = new Padding(18, 16, 18, 18);
    }

    public void Add(Control control)
    {
        Controls.Add(control);
        SyncWidths();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        SyncWidths();
    }

    private void SyncWidths()
    {
        int scroll = VerticalScroll.Visible ? SystemInformation.VerticalScrollBarWidth : 0;
        int width = ClientSize.Width - Padding.Horizontal - scroll - 2;
        if (width < 200) return;

        foreach (Control control in Controls)
            if (control.Width != width) control.Width = width;
    }
}

internal static class Ui
{
    public static Button Button(string text, bool primary = false, int width = 0)
    {
        var b = new Button
        {
            Text = text,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(width > 0 ? width : 0, 34),
            Padding = new Padding(13, 0, 13, 0),
            FlatStyle = FlatStyle.Flat,
            Font = Theme.BodyMedium,
            Cursor = Cursors.Hand,
            UseVisualStyleBackColor = false,
            BackColor = primary ? Theme.Accent : Theme.Card,
            ForeColor = primary ? Color.White : Theme.Text,
            Margin = new Padding(0, 0, 8, 0)
        };

        b.FlatAppearance.BorderSize = primary ? 0 : 1;
        b.FlatAppearance.BorderColor = Theme.Border;
        b.FlatAppearance.MouseOverBackColor = primary ? Theme.AccentHover : Theme.GraySoft;
        b.FlatAppearance.MouseDownBackColor = primary ? Theme.AccentHover : Theme.GraySoft;
        return b;
    }

    public static FlowLayoutPanel Row(params Control[] controls)
    {
        var row = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top,
            BackColor = Color.Transparent,
            Margin = new Padding(0)
        };
        row.Controls.AddRange(controls);
        return row;
    }

    public static Label Text(string text, bool muted = false, Font? font = null)
    {
        return new Label
        {
            Text = text,
            AutoSize = true,
            MaximumSize = new Size(2000, 0),
            Font = font ?? Theme.Body,
            ForeColor = muted ? Theme.Muted : Theme.Text,
            Margin = new Padding(0, 0, 0, 6)
        };
    }

    public static Label Wrap(string text, bool muted = true)
    {
        var label = Text(text, muted);
        label.AutoSize = true;
        label.MaximumSize = new Size(900, 0);
        return label;
    }

    public static TextBox Input(string value = "", int width = 300)
    {
        return new TextBox
        {
            Text = value,
            Font = new Font("Segoe UI", 10f),
            BorderStyle = BorderStyle.FixedSingle,
            Width = width,
            Margin = new Padding(0, 0, 0, 6)
        };
    }

    public static TableLayoutPanel Field(string caption, Control input, int captionWidth = 175)
    {
        var table = new TableLayoutPanel
        {
            ColumnCount = 2,
            RowCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 0, 0, 8)
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, captionWidth));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var label = new Label
        {
            Text = caption,
            AutoSize = true,
            Font = Theme.Body,
            ForeColor = Theme.Muted,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 7, 12, 0)
        };

        input.Dock = DockStyle.Top;
        input.Margin = new Padding(0);

        table.Controls.Add(label, 0, 0);
        table.Controls.Add(input, 1, 0);
        return table;
    }

    public static CheckBox Check(string text, bool value = false)
    {
        return new CheckBox
        {
            Text = text,
            Checked = value,
            AutoSize = true,
            Font = Theme.Body,
            ForeColor = Theme.Text,
            Margin = new Padding(0, 0, 0, 6),
            Cursor = Cursors.Hand
        };
    }

    public static RadioButton Radio(string text, bool value = false)
    {
        return new RadioButton
        {
            Text = text,
            Checked = value,
            AutoSize = true,
            Font = Theme.Body,
            ForeColor = Theme.Text,
            Margin = new Padding(0, 0, 18, 0),
            Cursor = Cursors.Hand
        };
    }

    public static NumericUpDown Number(int value, int min, int max, int width = 110)
    {
        return new NumericUpDown
        {
            Value = Math.Clamp(value, min, max),
            Minimum = min,
            Maximum = max,
            Width = width,
            Font = new Font("Segoe UI", 10f),
            BorderStyle = BorderStyle.FixedSingle,
            Margin = new Padding(0)
        };
    }

    public static ComboBox Combo(int width = 300)
    {
        return new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = width,
            Font = new Font("Segoe UI", 10f),
            FlatStyle = FlatStyle.Flat,
            Margin = new Padding(0)
        };
    }

    public static void Info(IWin32Window owner, string message, string title = "LabelBridge") =>
        MessageBox.Show(owner, message, title, MessageBoxButtons.OK, MessageBoxIcon.Information);

    public static void Error(IWin32Window owner, string message, string title = "LabelBridge") =>
        MessageBox.Show(owner, message, title, MessageBoxButtons.OK, MessageBoxIcon.Error);

    public static bool Confirm(IWin32Window owner, string message, string title = "LabelBridge") =>
        MessageBox.Show(owner, message, title, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
}
