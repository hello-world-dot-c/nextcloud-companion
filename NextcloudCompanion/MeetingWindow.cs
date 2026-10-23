using System.Drawing;
using System.Windows.Forms;

namespace NextcloudCompanion;

/// <summary>
/// Creates an event on a calendar the signed-in account can write, including calendars
/// shared with that account, and adds internal users or groups as attendees.
/// </summary>
internal sealed class MeetingWindow : Form
{
    private readonly Func<IReadOnlyList<FolderStatus>> _folders;
    private readonly ComboBox _account;
    private readonly Panel _passwordRow;
    private readonly TextBox _password;
    private readonly Label _passwordState;
    private readonly ComboBox _calendar;
    private readonly TextBox _title;
    private readonly DateTimePicker _startDate;
    private readonly DateTimePicker _startTime;
    private readonly DateTimePicker _endDate;
    private readonly DateTimePicker _endTime;
    private readonly TextBox _location;
    private readonly TextBox _description;
    private readonly TextBox _personQuery;
    private readonly ListBox _matches;
    private readonly ListBox _selected;
    private readonly Label _status;
    private readonly Button _create;
    private readonly List<AccountChoice> _accounts = [];
    private readonly List<Invitee> _invitees = [];
    private CalendarClient? _client;
    private string _organizerName = "";
    private string _organizerEmail = "";
    private CancellationTokenSource? _cts;
    private Point _placeNear = Cursor.Position;

    public MeetingWindow(Func<IReadOnlyList<FolderStatus>> folders)
    {
        _folders = folders;
        Text = "New Nextcloud meeting";
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = true;
        MinimizeBox = true;
        MaximizeBox = true;
        AutoScaleMode = AutoScaleMode.None;
        Font = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point);
        KeyPreview = true;

        int gap = Math.Max(6, Font.Height / 2);
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 13,
            Padding = new Padding(gap * 2)
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        for (int i = 0; i < 13; i++)
            root.RowStyles.Add(new RowStyle(i is 9 or 11 ? SizeType.Percent : SizeType.AutoSize, i is 9 or 11 ? 50f : 0f));

        root.Controls.Add(Hint("Puts the meeting on a calendar you can write, including ones shared with you. People are looked up through sharing search, not the calendar app's attendee box."), 0, 0);
        root.Controls.Add(Labeled("Account", _account = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList }), 0, 1);

        _password = new TextBox { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
        _password.KeyDown += async (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            await SavePasswordAsync();
        };
        var savePassword = SmallButton("Save password");
        savePassword.Click += async (_, _) => await SavePasswordAsync();
        var forget = SmallButton("Forget");
        forget.Click += (_, _) => ForgetPassword();
        _passwordState = new Label
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 4, gap, 0)
        };
        var passwordInner = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 4,
            RowCount = 1,
            Margin = new Padding(0, gap, 0, 0)
        };
        passwordInner.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        passwordInner.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        passwordInner.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        passwordInner.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40f));
        passwordInner.Controls.Add(_password, 0, 0);
        passwordInner.Controls.Add(savePassword, 1, 0);
        passwordInner.Controls.Add(forget, 2, 0);
        passwordInner.Controls.Add(_passwordState, 3, 0);
        _passwordRow = new Panel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            MinimumSize = new Size(0, Font.Height + 8),
            Margin = new Padding(0, 0, 0, gap)
        };
        _passwordRow.Controls.Add(passwordInner);
        var passwordBlock = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, RowCount = 2 };
        passwordBlock.Controls.Add(Hint("App password from Nextcloud → Settings → Security → Devices & sessions. It is stored encrypted for your Windows user."), 0, 0);
        passwordBlock.Controls.Add(_passwordRow, 0, 1);
        root.Controls.Add(passwordBlock, 0, 2);

        _calendar = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
        var reload = SmallButton("Reload");
        reload.Click += async (_, _) => await LoadCalendarsAsync();
        var calendarRow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Margin = new Padding(0, gap, 0, 0) };
        calendarRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        calendarRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        calendarRow.Controls.Add(_calendar, 0, 0);
        calendarRow.Controls.Add(reload, 1, 0);
        root.Controls.Add(Labeled("Calendar", calendarRow), 0, 3);

        _title = new TextBox { Dock = DockStyle.Fill };
        root.Controls.Add(Labeled("Title", _title), 0, 4);

        var start = DateTime.Today.AddHours(DateTime.Now.Hour + 1);
        _startDate = DatePicker(start, DateTimePickerFormat.Short, Font.Height * 8);
        _startTime = DatePicker(start, DateTimePickerFormat.Time, Font.Height * 6);
        _endDate = DatePicker(start.AddHours(1), DateTimePickerFormat.Short, Font.Height * 8);
        _endTime = DatePicker(start.AddHours(1), DateTimePickerFormat.Time, Font.Height * 6);
        var when = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = false,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = new Padding(0, gap, 0, 0)
        };
        when.Controls.AddRange([_startDate, _startTime, new Label { Text = "to", AutoSize = true, Margin = new Padding(gap, 6, gap, 0) }, _endDate, _endTime]);
        root.Controls.Add(Labeled("When (this PC's local time)", when), 0, 5);

        _location = new TextBox { Dock = DockStyle.Fill };
        root.Controls.Add(Labeled("Location", _location), 0, 6);

        _description = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            AcceptsReturn = true,
            ScrollBars = ScrollBars.Vertical,
            MinimumSize = new Size(0, Font.Height * 4)
        };
        root.Controls.Add(Labeled("Description", _description), 0, 7);

        _personQuery = new TextBox { Dock = DockStyle.Fill };
        _personQuery.KeyDown += async (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            await SearchPeopleAsync();
        };
        var find = SmallButton("Find");
        find.Click += async (_, _) => await SearchPeopleAsync();
        var addEmail = SmallButton("Add typed email");
        addEmail.Click += (_, _) => AddTypedEmail();
        var findRow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3, Margin = new Padding(0, gap, 0, 0) };
        findRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        findRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        findRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        findRow.Controls.Add(_personQuery, 0, 0);
        findRow.Controls.Add(find, 1, 0);
        findRow.Controls.Add(addEmail, 2, 0);
        root.Controls.Add(Labeled("Participants (user, group, or email)", findRow), 0, 8);

        _matches = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false, Margin = new Padding(0, gap / 2, 0, 0) };
        _matches.DoubleClick += async (_, _) => await AddSelectedMatchAsync();
        root.Controls.Add(_matches, 0, 9);

        var add = SmallButton("Add selected");
        add.Click += async (_, _) => await AddSelectedMatchAsync();
        var remove = SmallButton("Remove selected");
        remove.Click += (_, _) => RemoveSelected();
        var editRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, Margin = new Padding(0, gap / 2, 0, 0) };
        editRow.Controls.Add(add);
        editRow.Controls.Add(remove);
        root.Controls.Add(editRow, 0, 10);

        _selected = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false, Margin = new Padding(0, gap / 2, 0, 0) };
        root.Controls.Add(_selected, 0, 11);

        _status = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            ForeColor = SystemColors.GrayText,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0, gap, gap, 0),
            Text = "Choose an account. A saved app password is reused."
        };
        var cancel = SmallButton("Cancel");
        cancel.Click += (_, _) => Close();
        _create = SmallButton("Create meeting");
        _create.Click += async (_, _) => await CreateAsync();
        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3, Margin = new Padding(0, gap / 2, 0, 0) };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.Controls.Add(_status, 0, 0);
        footer.Controls.Add(cancel, 1, 0);
        footer.Controls.Add(_create, 2, 0);
        root.Controls.Add(footer, 0, 12);

        Controls.Add(root);
        _account.SelectedIndexChanged += async (_, _) => await OnAccountChangedAsync();
        AcceptButton = _create;
        CancelButton = cancel;
    }

    public void ShowNear(Point near)
    {
        _placeNear = near;
        if (!IsHandleCreated)
        {
            var wa = Screen.FromPoint(near).WorkingArea;
            StartPosition = FormStartPosition.Manual;
            Location = new Point(wa.Left + Math.Max(0, wa.Width / 2), wa.Top + Math.Max(0, wa.Height / 2));
            _ = Handle;
        }
        Fit(near);
        if (!Visible)
            Show();
        BringToFront();
        Activate();
        if (_accounts.Count == 0)
            LoadAccounts();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Fit(_placeNear);
        _title.Focus();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape)
        {
            Close();
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _cts?.Cancel();
        _client?.Dispose();
        base.OnFormClosed(e);
    }

    private void LoadAccounts()
    {
        _accounts.Clear();
        _account.Items.Clear();
        foreach (var folder in _folders() ?? [])
        {
            if (string.IsNullOrWhiteSpace(folder.ServerUrl) || string.IsNullOrWhiteSpace(folder.DavUser))
                continue;
            if (_accounts.Any(a => a.Folder.ServerUrl == folder.ServerUrl && a.Folder.DavUser == folder.DavUser))
                continue;
            var choice = new AccountChoice(folder);
            _accounts.Add(choice);
            _account.Items.Add(choice);
        }
        if (_account.Items.Count > 0)
            _account.SelectedIndex = 0;
        else
            _status.Text = "No Nextcloud account found in nextcloud.cfg.";
    }

    private async Task OnAccountChangedAsync()
    {
        _password.Text = "";
        bool saved = Current() is { } account && Settings.GetAppPassword(account.Folder.ServerUrl, account.Folder.DavUser) is not null;
        _passwordState.Text = saved ? "Password saved" : "No password saved yet";
        _calendar.Items.Clear();
        if (saved)
            await LoadCalendarsAsync();
        else
            _status.Text = "Paste an app password for this account, then Save password.";
    }

    private async Task SavePasswordAsync()
    {
        var account = Current();
        if (account is null)
            return;
        if (_password.Text.Length == 0)
        {
            _status.Text = "Paste the app password first.";
            return;
        }
        Settings.SetAppPassword(account.Folder.ServerUrl, account.Folder.DavUser, _password.Text);
        _password.Text = "";
        _passwordState.Text = "Password saved";
        await LoadCalendarsAsync();
    }

    private void ForgetPassword()
    {
        var account = Current();
        if (account is null)
            return;
        Settings.ClearAppPassword(account.Folder.ServerUrl, account.Folder.DavUser);
        _passwordState.Text = "No password saved yet";
        _calendar.Items.Clear();
        _client?.Dispose();
        _client = null;
        _status.Text = "App password forgotten for this account.";
    }

    private async Task LoadCalendarsAsync()
    {
        var account = Current();
        if (account is null)
            return;
        string? password = Settings.GetAppPassword(account.Folder.ServerUrl, account.Folder.DavUser);
        if (string.IsNullOrEmpty(password))
        {
            _status.Text = "Save an app password for this account first.";
            return;
        }

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _create.Enabled = false;
        _status.Text = "Loading calendars…";
        try
        {
            _client?.Dispose();
            _client = new CalendarClient(account.Folder.ServerUrl, account.Folder.DavUser, password);
            var me = await _client.GetMeAsync(token);
            var calendars = await _client.ListCalendarsAsync(token);
            if (IsDisposed || token.IsCancellationRequested)
                return;
            _organizerName = me.Name;
            _organizerEmail = me.Email;
            _calendar.Items.Clear();
            foreach (var calendar in calendars)
                _calendar.Items.Add(calendar);
            if (_calendar.Items.Count > 0)
                _calendar.SelectedIndex = 0;
            _status.Text = calendars.Count == 0
                ? "No writable calendar on this account."
                : $"{calendars.Count} writable calendar{(calendars.Count == 1 ? "" : "s")}. Organizer {_organizerEmail}.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ErrorLog.Write("Load calendars failed", ex);
            _status.Text = ex.Message;
        }
        finally
        {
            _create.Enabled = true;
        }
    }

    private async Task SearchPeopleAsync()
    {
        string query = _personQuery.Text.Trim();
        if (query.Length == 0)
        {
            _status.Text = "Type part of a name, group, or email.";
            return;
        }
        if (!EnsureClient())
            return;

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _status.Text = "Searching…";
        try
        {
            var hits = await _client!.SearchPeopleAsync(query, token);
            if (IsDisposed || token.IsCancellationRequested)
                return;
            _matches.BeginUpdate();
            _matches.Items.Clear();
            foreach (var hit in hits)
                _matches.Items.Add(hit);
            _matches.EndUpdate();
            _status.Text = hits.Count == 0
                ? $"No users or groups like “{query}”."
                : $"{hits.Count} match{(hits.Count == 1 ? "" : "es")}. Double-click to add.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ErrorLog.Write("Participant search failed", ex);
            _status.Text = ex.Message;
        }
    }

    private async Task AddSelectedMatchAsync()
    {
        if (_matches.SelectedItem is not PersonHit hit)
        {
            _status.Text = "Select a person or group in the list first.";
            return;
        }
        if (!EnsureClient())
            return;

        try
        {
            if (hit.IsGroup)
            {
                _status.Text = "Loading members of " + hit.Name + "…";
                var expanded = await _client!.ExpandGroupAsync(hit, CancellationToken.None);
                int added = AddInvitees(expanded);
                int people = expanded.Count(i => !i.IsGroup);
                _status.Text = people == 0
                    ? $"Added the group “{hit.Name}”, but this account cannot list its members. Add people one by one."
                    : $"Added {hit.Name} and {people} member{(people == 1 ? "" : "s")} ({added} new).";
            }
            else
            {
                string? email = hit.Email;
                string name = hit.Name;
                if (string.IsNullOrWhiteSpace(email) && !string.IsNullOrWhiteSpace(hit.UserId))
                {
                    var resolved = await _client!.LookupUserAsync(hit.UserId, CancellationToken.None);
                    email = resolved.Email;
                    if (!string.IsNullOrWhiteSpace(resolved.Name))
                        name = resolved.Name;
                }
                if (string.IsNullOrWhiteSpace(email))
                {
                    _status.Text = $"{hit.Name} has no email address, so they cannot be invited.";
                    return;
                }
                int added = AddInvitees([new Invitee { Name = name, Email = email }]);
                _status.Text = added == 0 ? $"{name} is already on the list." : $"Added {name}.";
            }
            RefreshSelected();
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Add participant failed", ex);
            _status.Text = ex.Message;
        }
    }

    private void AddTypedEmail()
    {
        string text = _personQuery.Text.Trim();
        if (!text.Contains('@') || text.Contains(' '))
        {
            _status.Text = "Type a full email address, then Add typed email.";
            return;
        }
        int added = AddInvitees([new Invitee { Name = text, Email = text }]);
        RefreshSelected();
        _status.Text = added == 0 ? "That email is already on the list." : "Added " + text + ".";
    }

    private int AddInvitees(IEnumerable<Invitee> people)
    {
        int added = 0;
        foreach (var person in people)
        {
            if (_invitees.Any(i => i.Email.Equals(person.Email, StringComparison.OrdinalIgnoreCase) && i.IsGroup == person.IsGroup))
                continue;
            _invitees.Add(person);
            added++;
        }
        return added;
    }

    private void RemoveSelected()
    {
        if (_selected.SelectedItem is not Invitee invitee)
            return;
        _invitees.Remove(invitee);
        RefreshSelected();
    }

    private void RefreshSelected()
    {
        _selected.BeginUpdate();
        _selected.Items.Clear();
        foreach (var invitee in _invitees)
            _selected.Items.Add(invitee);
        _selected.EndUpdate();
    }

    private async Task CreateAsync()
    {
        var account = Current();
        if (account is null)
            return;
        if (_calendar.SelectedItem is not CloudCalendar calendar)
        {
            _status.Text = "Choose a calendar.";
            return;
        }
        string title = _title.Text.Trim();
        if (title.Length == 0)
        {
            _status.Text = "Enter a title.";
            _title.Focus();
            return;
        }
        var start = _startDate.Value.Date + _startTime.Value.TimeOfDay;
        var end = _endDate.Value.Date + _endTime.Value.TimeOfDay;
        if (end <= start)
        {
            _status.Text = "The end must be after the start.";
            return;
        }
        if (!EnsureClient())
            return;
        if (string.IsNullOrWhiteSpace(_organizerEmail))
        {
            var me = await _client!.GetMeAsync(CancellationToken.None);
            _organizerName = me.Name;
            _organizerEmail = me.Email;
        }

        _create.Enabled = false;
        _status.Text = "Creating the meeting…";
        try
        {
            await _client!.CreateMeetingAsync(calendar, new MeetingDraft
            {
                Title = title,
                Location = _location.Text.Trim(),
                Description = _description.Text.Trim(),
                StartLocal = start,
                EndLocal = end,
                OrganizerName = _organizerName,
                OrganizerEmail = _organizerEmail,
                Invitees = _invitees.ToList()
            }, CancellationToken.None);
            _status.Text = _invitees.Count == 0
                ? $"Created “{title}” on {calendar.Name}."
                : $"Created “{title}” on {calendar.Name}. The server sends the invitations.";
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Create meeting failed", ex);
            _status.Text = ex.Message;
        }
        finally
        {
            _create.Enabled = true;
        }
    }

    private bool EnsureClient()
    {
        if (_client is not null)
            return true;
        _status.Text = "Save an app password and wait for the calendars to load.";
        return false;
    }

    private AccountChoice? Current()
        => _account.SelectedItem as AccountChoice;

    private void Fit(Point near)
    {
        if (near.IsEmpty)
            near = Cursor.Position;
        var wa = Dpi.WorkingArea(near);
        int margin = Math.Max(8, Dpi.Px(this, 10));
        int maxW = Math.Max(360, wa.Width - margin * 2);
        int maxH = Math.Max(280, wa.Height - margin * 2);
        MinimumSize = new Size(Math.Min(Dpi.Px(this, 640), maxW), Math.Min(Dpi.Px(this, 520), maxH));
        Size = Dpi.FitWindow(this, 760, 720, wa, margin);
        Location = Dpi.BottomRight(Size, wa, margin);
    }

    private Label Hint(string text) => new()
    {
        Text = text,
        AutoSize = true,
        MaximumSize = new Size(640, 0),
        Dock = DockStyle.Fill,
        ForeColor = SystemColors.GrayText,
        Margin = new Padding(0, 0, 0, Font.Height / 3)
    };

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

    private static DateTimePicker DatePicker(DateTime value, DateTimePickerFormat format, int width) => new()
    {
        Format = format,
        ShowUpDown = format == DateTimePickerFormat.Time,
        Value = value,
        Margin = new Padding(0, 0, 4, 0),
        Width = width
    };

    private sealed class AccountChoice(FolderStatus folder)
    {
        public FolderStatus Folder { get; } = folder;
        public override string ToString() => string.IsNullOrEmpty(Folder.DavUser)
            ? Folder.ServerHost
            : Folder.DavUser + "  @  " + Folder.ServerHost;
    }
}
