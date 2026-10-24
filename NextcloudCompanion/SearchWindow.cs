using System.Collections;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace NextcloudCompanion;

/// <summary>
/// Stays open (unlike the status flyout) so paths can be copied. Shows the full
/// Nextcloud-internal path that the web UI search omits.
/// </summary>
internal sealed class SearchWindow : DpiForm
{
    private readonly Func<IReadOnlyList<FolderStatus>> _folders;
    private readonly ComboBox _query;
    private readonly Button _clearHistory;
    private readonly Button _searchButton;
    private readonly ListView _results;
    private readonly Label _status;
    private readonly Button _copyInternal;
    private readonly Button _openLocation;
    private readonly Button _openWeb;
    private CancellationTokenSource? _cts;
    private Point _placeNear = Cursor.Position;
    private int _sortColumn = -1;
    private bool _sortAscending = true;
    private static readonly string[] ColumnTitles = ["Name", "Path inside Nextcloud", "Type", "Time", "Account"];

    public SearchWindow(Func<IReadOnlyList<FolderStatus>> folders)
    {
        _folders = folders;

        Text = "Search Nextcloud files";
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = true;
        MinimizeBox = true;
        MaximizeBox = true;
        KeyPreview = true;

        int pad = Math.Max(8, Font.Height * 2 / 3);
        int gap = Math.Max(6, Font.Height / 2);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(pad)
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var prompt = new Label
        {
            Text = "Name contains (file or folder). Results show the full path inside Nextcloud.",
            AutoSize = true,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, gap / 2)
        };

        var searchRow = new TableLayoutPanel
        {
            ColumnCount = 3,
            RowCount = 1,
            AutoSize = true,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, gap)
        };
        searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        searchRow.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _query = new ComboBox
        {
            Dock = DockStyle.Fill,
            DropDownStyle = ComboBoxStyle.DropDown,
            AutoCompleteMode = AutoCompleteMode.SuggestAppend,
            AutoCompleteSource = AutoCompleteSource.ListItems,
            Margin = new Padding(0, 2, gap, 2)
        };
        _query.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                RunSearch();
            }
        };

        _searchButton = ActionButton("Search");
        _searchButton.Margin = new Padding(0);
        _searchButton.Click += (_, _) => RunSearch();

        _clearHistory = ActionButton("Clear history");
        _clearHistory.Click += (_, _) => ClearHistory();

        searchRow.Controls.Add(_query, 0, 0);
        searchRow.Controls.Add(_searchButton, 1, 0);
        searchRow.Controls.Add(_clearHistory, 2, 0);
        ReloadHistory();

        _results = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
            MultiSelect = true,
            HideSelection = false,
            ShowItemToolTips = true,
            Margin = new Padding(0, 0, 0, gap)
        };
        _results.Columns.Add(ColumnTitles[0], 160);
        _results.Columns.Add(ColumnTitles[1], 360);
        _results.Columns.Add(ColumnTitles[2], 90);
        _results.Columns.Add(ColumnTitles[3], 110);
        _results.Columns.Add(ColumnTitles[4], 160);
        _results.ColumnClick += OnColumnClick;
        _results.DoubleClick += (_, _) => OpenSelectedLocation();
        _results.KeyDown += OnResultsKeyDown;

        var menu = new ContextMenuStrip();
        menu.Items.Add("Copy internal path", null, (_, _) => CopySelected(internalPath: true));
        menu.Items.Add("Copy local path", null, (_, _) => CopySelected(internalPath: false));
        menu.Items.Add("Open location", null, (_, _) => OpenSelectedLocation());
        menu.Items.Add("Open location on the web", null, (_, _) => OpenSelectedOnWeb());
        _results.ContextMenuStrip = menu;

        var footer = new TableLayoutPanel
        {
            ColumnCount = 2,
            RowCount = 1,
            AutoSize = true,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _status = new Label
        {
            Text = "Type part of a file or folder name and press Enter.",
            AutoSize = false,
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 0, gap, 0)
        };

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };

        _copyInternal = ActionButton("Copy path");
        _copyInternal.Click += (_, _) => CopySelected(internalPath: true);
        _openLocation = ActionButton("Open location");
        _openLocation.Click += (_, _) => OpenSelectedLocation();
        _openWeb = ActionButton("Open location on the web");
        _openWeb.Click += (_, _) => OpenSelectedOnWeb();
        buttons.Controls.AddRange([_copyInternal, _openLocation, _openWeb]);

        footer.Controls.Add(_status, 0, 0);
        footer.Controls.Add(buttons, 1, 0);

        root.Controls.Add(prompt, 0, 0);
        root.Controls.Add(searchRow, 0, 1);
        root.Controls.Add(_results, 0, 2);
        root.Controls.Add(footer, 0, 3);
        Controls.Add(root);
        AcceptButton = _searchButton;
        Resize += (_, _) => FitColumns();
        CaptureDesign();
    }

    protected override void OnDpiApplied() => FitColumns();

    /// <summary>
    /// Size to the screen (never larger than the working area) and open
    /// tucked into the bottom-right corner, same as the status flyout.
    /// </summary>
    public void ShowNear(Point near)
    {
        _placeNear = near;
        FitAndPlaceBottomRight(near);
        Show();
        BringToFront();
        Activate();
        _query.Focus();
    }

    private void ReloadHistory()
    {
        string keep = _query.Text;
        _query.BeginUpdate();
        _query.Items.Clear();
        foreach (string previous in Settings.RecentSearches)
            _query.Items.Add(previous);
        _query.EndUpdate();
        _query.Text = keep;
        _clearHistory.Enabled = Settings.RecentSearches.Count > 0;
    }

    private void ClearHistory()
    {
        if (Settings.RecentSearches.Count == 0)
            return;
        if (MessageBox.Show(
                $"Clear {Settings.RecentSearches.Count} previous search{(Settings.RecentSearches.Count == 1 ? "" : "es")}?",
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;
        Settings.ClearSearches();
        ReloadHistory();
        _status.Text = "Search history cleared.";
    }

    private Button ActionButton(string text) => new()
    {
        Text = text,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        Margin = new Padding(Font.Height / 4, 0, 0, 0),
        Padding = new Padding(Font.Height / 3, 1, Font.Height / 3, 1)
    };

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        FitAndPlaceBottomRight(_placeNear);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        FitAndPlaceBottomRight(_placeNear);
        FitColumns();
        _query.Focus();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        FitAndPlaceBottomRight(_placeNear == Point.Empty ? Cursor.Position : _placeNear);
    }

    private void FitAndPlaceBottomRight(Point near)
    {
        if (near.IsEmpty)
            near = Cursor.Position;

        var wa = Dpi.WorkingArea(near);
        int margin = Math.Max(8, Dpi.Px(this, 10));
        int maxW = Math.Max(280, wa.Width - margin * 2);
        int maxH = Math.Max(200, wa.Height - margin * 2);

        MinimumSize = new Size(
            Math.Min(Dpi.Px(this, 520), maxW),
            Math.Min(Dpi.Px(this, 280), maxH));

        Size = Dpi.FitWindow(this, 900, 520, wa, margin);
        Location = Dpi.BottomRight(Size, wa, margin);
    }

    /// <summary>
    /// Name ~22%, path takes the rest (~49%), Type compact, Account ~20%.
    /// Matches the usual search-result layout (filename | long path | File/Folder | account).
    /// </summary>
    private void FitColumns()
    {
        if (_results.Columns.Count < 5)
            return;
        int inner = _results.ClientSize.Width - 4;
        if (inner < 80)
            return;

        int em = Math.Max(12, Font.Height);
        int type = Math.Max(em * 7, TextRenderer.MeasureText("Remote folder", Font).Width + em);
        int time = Math.Max(em * 8, TextRenderer.MeasureText("yesterday 00:00", Font).Width + em);
        int name = Math.Max(em * 8, inner * 18 / 100);
        int account = Math.Max(em * 10, inner * 18 / 100);
        int path = inner - name - type - time - account;
        int minPath = Math.Max(em * 10, inner * 32 / 100);
        if (path < minPath)
        {
            path = Math.Min(minPath, Math.Max(em * 10, inner / 3));
            int leftover = Math.Max(0, inner - path - type - time);
            name = Math.Max(em * 6, leftover * 18 / 36);
            account = Math.Max(em * 6, leftover - name);
        }

        _results.Columns[0].Width = name;
        _results.Columns[1].Width = Math.Max(em * 8, path);
        _results.Columns[2].Width = type;
        _results.Columns[3].Width = time;
        _results.Columns[4].Width = account;
    }

    private void OnColumnClick(object? sender, ColumnClickEventArgs e)
    {
        if (_sortColumn == e.Column)
            _sortAscending = !_sortAscending;
        else
        {
            _sortColumn = e.Column;
            _sortAscending = e.Column != 3;
        }
        ApplySort();
    }

    private void ApplySort()
    {
        for (int i = 0; i < _results.Columns.Count && i < ColumnTitles.Length; i++)
        {
            string mark = i == _sortColumn ? (_sortAscending ? "  \u25B2" : "  \u25BC") : "";
            _results.Columns[i].Text = ColumnTitles[i] + mark;
        }
        if (_sortColumn < 0)
            return;
        _results.ListViewItemSorter = new HitColumnSorter(_sortColumn, _sortAscending);
        _results.Sort();
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
        base.OnFormClosed(e);
    }

    private void RunSearch()
    {
        string q = _query.Text.Trim();
        if (q.Length == 0)
        {
            _status.Text = "Enter something to search for.";
            return;
        }

        Settings.RememberSearch(q);
        ReloadHistory();

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var folders = _folders() ?? [];

        _searchButton.Enabled = false;
        _status.Text = "Searching…";
        _results.BeginUpdate();
        _results.Items.Clear();
        _results.EndUpdate();

        Task.Run(() =>
        {
            try
            {
                var sw = Stopwatch.StartNew();
                var hits = FileSearch.Find(folders, q, FileSearch.DefaultLimit, token);
                sw.Stop();
                PostUi(() => ShowHits(hits, sw.Elapsed, q));
            }
            catch (OperationCanceledException)
            {
                // superseded by a newer query
            }
            catch (Exception ex)
            {
                ErrorLog.Write("Search failed", ex);
                PostUi(() =>
                {
                    _searchButton.Enabled = true;
                    _status.Text = "Search failed: " + ex.Message;
                });
            }
        }, token);
    }

    private void PostUi(Action action)
    {
        if (!IsHandleCreated || IsDisposed) return;
        try { BeginInvoke(action); }
        catch (ObjectDisposedException) { /* closed while searching */ }
    }

    private void ShowHits(IReadOnlyList<FileHit> hits, TimeSpan elapsed, string query)
    {
        if (IsDisposed) return;
        _searchButton.Enabled = true;
        _results.BeginUpdate();
        _results.Items.Clear();
        foreach (var hit in hits)
        {
            var item = new ListViewItem(hit.Name)
            {
                Tag = hit,
                ImageIndex = -1
            };
            item.SubItems.Add(hit.InternalPath);
            item.SubItems.Add(hit.KindLabel);
            item.SubItems.Add(hit.ModifiedLabel);
            item.SubItems.Add(hit.Account);
            item.ToolTipText = string.IsNullOrEmpty(hit.ModifiedLabel)
                ? hit.Name + Environment.NewLine + hit.InternalPath
                : hit.Name + Environment.NewLine + hit.InternalPath + Environment.NewLine + hit.ModifiedLabel;
            _results.Items.Add(item);
        }
        _results.EndUpdate();
        FitColumns();
        if (_sortColumn >= 0)
            ApplySort();

        _status.Text = hits.Count == 0
            ? $"No files or folders named like “{query}”."
            : hits.Count >= FileSearch.DefaultLimit
                ? $"Showing the first {hits.Count} matches in {elapsed.TotalSeconds:0.0}s. Refine the name to narrow it down."
                : $"{hits.Count} match{(hits.Count == 1 ? "" : "es")} in {elapsed.TotalSeconds:0.0}s.";
    }

    private IEnumerable<FileHit> SelectedHits()
        => _results.SelectedItems.Cast<ListViewItem>()
            .Select(i => i.Tag)
            .OfType<FileHit>();

    private void CopySelected(bool internalPath)
    {
        var paths = SelectedHits()
            .Select(h => internalPath ? h.InternalPath : h.LocalPath)
            .ToList();
        if (paths.Count == 0)
            return;
        try { Clipboard.SetText(string.Join(Environment.NewLine, paths)); }
        catch { /* clipboard busy */ }
        _status.Text = paths.Count == 1 ? "Copied." : $"Copied {paths.Count} paths.";
    }

    private void OpenSelectedLocation()
    {
        var hit = SelectedHits().FirstOrDefault();
        if (hit is null) return;

        try
        {
            string target = hit.LocalPath;
            bool localDir = Directory.Exists(target);
            bool localFile = File.Exists(target);

            if (hit.IsDirectory && localDir)
            {
                Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true });
                return;
            }

            if (!hit.IsDirectory && localFile)
            {
                string? dir = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(dir))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"/select,\"{target}\"",
                        UseShellExecute = true
                    });
                    return;
                }
            }

            // Journal can list items that were never downloaded or are excluded from sync.
            if (OpenOnWeb(hit, announce: true))
                return;

            MessageBox.Show(
                "That path is not on this PC (it may be excluded from sync), and no server URL is configured.\n\n"
                + hit.InternalPath,
                Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void OpenSelectedOnWeb()
    {
        var hit = SelectedHits().FirstOrDefault();
        if (hit is null) return;
        if (!OpenOnWeb(hit, announce: false))
        {
            MessageBox.Show("No server URL is configured for that account.", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    private bool OpenOnWeb(FileHit hit, bool announce)
    {
        string? url = hit.WebUrl;
        if (string.IsNullOrWhiteSpace(url))
            return false;
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            if (announce)
                _status.Text = "Not synced to this PC — opened on the web.";
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return true;
        }
    }

    private void OnResultsKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Control && e.KeyCode == Keys.C)
        {
            CopySelected(internalPath: true);
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.Enter)
        {
            OpenSelectedLocation();
            e.Handled = true;
        }
    }

    private sealed class HitColumnSorter : IComparer
    {
        private readonly int _column;
        private readonly bool _ascending;

        public HitColumnSorter(int column, bool ascending)
        {
            _column = column;
            _ascending = ascending;
        }

        public int Compare(object? x, object? y)
        {
            if (x is not ListViewItem a || y is not ListViewItem b)
                return 0;

            if (_column == 3 && a.Tag is FileHit ha && b.Tag is FileHit hb)
            {
                if (ha.Modified is null || hb.Modified is null)
                {
                    if (ha.Modified is null && hb.Modified is null) return 0;
                    return ha.Modified is null ? 1 : -1;
                }
                int byTime = ha.Modified.Value.CompareTo(hb.Modified.Value);
                return _ascending ? byTime : -byTime;
            }

            string sa = a.SubItems.Count > _column ? a.SubItems[_column].Text : "";
            string sb = b.SubItems.Count > _column ? b.SubItems[_column].Text : "";
            int cmp = string.Compare(sa, sb, StringComparison.CurrentCultureIgnoreCase);
            return _ascending ? cmp : -cmp;
        }
    }
}
