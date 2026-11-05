using System.Drawing;
using System.Windows.Forms;

namespace NextcloudCompanion;

/// <summary>An account of the desktop client, as listed in account drop-downs.</summary>
internal sealed class AccountChoice(FolderStatus folder)
{
    public FolderStatus Folder { get; } = folder;

    public override string ToString() => string.IsNullOrEmpty(Folder.DavUser)
        ? Folder.ServerHost
        : Folder.DavUser + "  @  " + Folder.ServerHost;

    /// <summary>One entry per account (a client can have several synced folders per account).</summary>
    public static List<AccountChoice> From(IEnumerable<FolderStatus> folders)
    {
        var accounts = new List<AccountChoice>();
        foreach (var folder in folders)
        {
            if (string.IsNullOrWhiteSpace(folder.ServerUrl) || string.IsNullOrWhiteSpace(folder.DavUser))
                continue;
            if (accounts.Any(a => a.Folder.ServerUrl == folder.ServerUrl && a.Folder.DavUser == folder.DavUser))
                continue;
            accounts.Add(new AccountChoice(folder));
        }
        return accounts;
    }
}

/// <summary>
/// Where the app passwords are managed, one per account. They are used wherever this app talks to the server
/// itself: creating meetings and searching files on the server. The desktop client's login token is not reused.
/// Passwords are stored encrypted for the Windows user and never shown again.
/// </summary>
internal sealed class AppPasswordsWindow : DpiForm
{
    private readonly Func<IReadOnlyList<FolderStatus>> _folders;
    private readonly TableLayoutPanel _root;
    private readonly List<Label> _wrapLabels = [];
    private readonly ComboBox _account;
    private readonly TextBox _password;
    private readonly Label _state;
    private readonly Label _status;
    private readonly List<AccountChoice> _accounts = [];
    private Point _near = Cursor.Position;

    public AppPasswordsWindow(Func<IReadOnlyList<FolderStatus>> folders)
    {
        _folders = folders;
        Text = "App passwords";
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = true;
        MinimizeBox = true;
        MaximizeBox = false;
        KeyPreview = true;

        int gap = Math.Max(6, Font.Height / 2);
        _root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(gap * 2)
        };
        _root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        for (int i = 0; i < 4; i++)
            _root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        _root.SizeChanged += (_, _) => UpdateWrapWidths();

        _root.Controls.Add(Hint("An app password lets this app use your account for the meeting form and for searching files on the server. "
            + "Create one in Nextcloud under Settings → Security → Devices & sessions and paste it here. "
            + "It is stored encrypted for your Windows user."), 0, 0);

        _account = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
        _account.SelectedIndexChanged += (_, _) => OnAccountChanged();
        _root.Controls.Add(Labeled("Account", _account), 0, 1);

        _password = new TextBox { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
        _password.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            SavePassword();
        };
        var save = SmallButton("Save password");
        save.Click += (_, _) => SavePassword();
        var forget = SmallButton("Forget");
        forget.Click += (_, _) => ForgetPassword();
        _state = new Label
        {
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            UseMnemonic = false,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(gap, 0, 0, 0)
        };
        var row = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 4, RowCount = 1, Margin = new Padding(0, gap, 0, 0) };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));     // status text: its natural width, never wrapped
        row.Controls.Add(_password, 0, 0);
        row.Controls.Add(save, 1, 0);
        row.Controls.Add(forget, 2, 0);
        row.Controls.Add(_state, 3, 0);
        _root.Controls.Add(Labeled("App password", row), 0, 2);

        _status = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, gap, gap, 0)
        };
        var close = SmallButton("Close");
        close.Click += (_, _) => Close();
        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Margin = new Padding(0, gap, 0, 0) };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.Controls.Add(_status, 0, 0);
        footer.Controls.Add(close, 1, 0);
        _root.Controls.Add(footer, 0, 3);

        Controls.Add(_root);
        AcceptButton = null;
        CancelButton = close;
        CaptureDesign();
    }

    /// <summary>Opens next to <paramref name="near"/> (the click), on that monitor, fully on screen.</summary>
    public void ShowNear(Point near)
    {
        _near = near;
        Place();
        LoadAccounts();
        if (!Visible)
        {
            HideUntilSettled();
            Show();
        }
        BringToFront();
        Activate();
    }

    protected override void OnLoad(EventArgs e) { base.OnLoad(e); Place(); }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Place();
        _password.Focus();
    }

    protected override void OnDpiApplied() => UpdateWrapWidths();

    private void Place()
    {
        var wa = Screen.FromPoint(_near).WorkingArea;
        int dpi = DpiNear(_near);
        int margin = Math.Max(8, Dpi.Px(dpi, 10));
        var size = new Size(Math.Min(Dpi.Px(dpi, 600), wa.Width - 2 * margin), Math.Min(Dpi.Px(dpi, 300), wa.Height - 2 * margin));
        MinimumSize = new Size(Math.Min(Dpi.Px(dpi, 460), size.Width), Math.Min(Dpi.Px(dpi, 260), size.Height));
        Size = size;
        Location = Dpi.BottomRight(size, wa, margin);
    }

    private void LoadAccounts()
    {
        var previous = Current();
        _accounts.Clear();
        _account.Items.Clear();
        foreach (var account in AccountChoice.From(_folders() ?? []))
        {
            _accounts.Add(account);
            _account.Items.Add(account);
        }
        if (_account.Items.Count == 0)
        {
            _state.Text = "";
            _status.Text = "No Nextcloud account found in nextcloud.cfg.";
            return;
        }
        int keep = previous is null ? -1 : _accounts.FindIndex(a => a.Folder.ServerUrl == previous.Folder.ServerUrl && a.Folder.DavUser == previous.Folder.DavUser);
        _account.SelectedIndex = keep >= 0 ? keep : 0;
    }

    private AccountChoice? Current() => _account.SelectedItem as AccountChoice;

    private void OnAccountChanged()
    {
        _password.Text = "";
        if (Current() is not { } account)
            return;
        bool saved = Settings.GetAppPassword(account.Folder.ServerUrl, account.Folder.DavUser) is not null;
        _state.Text = saved ? "Password saved" : "No password saved yet";
        _status.Text = saved ? "" : "Paste an app password for this account, then Save password.";
    }

    private void SavePassword()
    {
        if (Current() is not { } account)
            return;
        if (_password.Text.Length == 0)
        {
            _status.Text = "Paste the app password first.";
            return;
        }
        Settings.SetAppPassword(account.Folder.ServerUrl, account.Folder.DavUser, _password.Text);
        _password.Text = "";
        _state.Text = "Password saved";
        _status.Text = "App password saved for " + account + ".";
    }

    private void ForgetPassword()
    {
        if (Current() is not { } account)
            return;
        Settings.ClearAppPassword(account.Folder.ServerUrl, account.Folder.DavUser);
        _state.Text = "No password saved yet";
        _status.Text = "App password forgotten for " + account + ".";
    }

    private Label Hint(string text)
    {
        var label = new Label
        {
            Text = text,
            AutoSize = true,
            UseMnemonic = false,          // "Devices & sessions" must not turn the '&' into an access key
            Dock = DockStyle.Fill,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 0, 0, Font.Height / 3)
        };
        _wrapLabels.Add(label);
        LeaveMaxSizeAlone(label);         // its maximum width follows the window, see UpdateWrapWidths
        return label;
    }

    /// <summary>Hints wrap at the width of the window (an AutoSize label only wraps if it has a maximum width).</summary>
    private void UpdateWrapWidths()
    {
        int width = Math.Max(Font.Height * 10, _root.ClientSize.Width - _root.Padding.Horizontal);
        foreach (var label in _wrapLabels)
        {
            int w = width - label.Margin.Horizontal;
            if (label.MaximumSize.Width != w)
                label.MaximumSize = new Size(w, 0);
        }
    }

    private Control Labeled(string caption, Control input)
    {
        var box = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 1,
            RowCount = 2,
            Margin = new Padding(0, Font.Height / 4, 0, 0)
        };
        box.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        box.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        box.Controls.Add(new Label { Text = caption, AutoSize = true, Margin = new Padding(0, 0, 0, 2) }, 0, 0);
        input.Margin = new Padding(0);
        box.Controls.Add(input, 0, 1);
        return box;
    }

    private Button SmallButton(string text) => new()
    {
        Text = text,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        Margin = new Padding(Font.Height / 3, 0, 0, 0),
        Padding = new Padding(Font.Height / 3, 2, Font.Height / 3, 2)
    };
}
