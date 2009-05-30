using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Data.SQLite;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Meebey.SmartIrc4net;

namespace Claymore.WikiLive
{
    public partial class MainForm : Form
    {
        private static IrcClient _irc = new IrcClient();
        private volatile bool _stop;
        private readonly HashSet<string> _watchlist;
        private SQLiteConnection _connection;
        string baseName = "WikiEdits.db";
        private AutoResetEvent _autoEvent;

        public MainForm()
        {
            InitializeComponent();

            _irc.Encoding = System.Text.Encoding.UTF8;
            _irc.SendDelay = 200;
            _irc.OnChannelMessage += new IrcEventHandler(OnChannelMessage);
            
            _stop = false;

            _watchlist = new HashSet<string>();

            if (!File.Exists(baseName))
            {
                SQLiteConnection.CreateFile(baseName);
            }

            SQLiteFactory factory = (SQLiteFactory)DbProviderFactories.GetFactory("System.Data.SQLite");
            _connection = (SQLiteConnection)factory.CreateConnection();
            _connection.ConnectionString = "Data Source = " + baseName;
            _connection.Open();

            using (SQLiteCommand command = new SQLiteCommand(_connection))
            {
                command.CommandText = @"CREATE TABLE IF NOT EXISTS [edits] (
                        [id] integer PRIMARY KEY AUTOINCREMENT NOT NULL,
                        [timestamp] TEXT NOT NULL,
                        [author] TEXT NOT NULL,
                        [page] TEXT NOT NULL,
                        [flags] INTEGER NOT NULL,
                        [oldid] INTEGER NOT NULL,
                        [diff] INTEGER NOT NULL,
                        [size] INTEGER NOT NULL,
                        [summary] TEXT NOT NULL
                    );";
                command.CommandType = CommandType.Text;
                command.ExecuteNonQuery();
            }

            using (SQLiteCommand command = new SQLiteCommand(_connection))
            {
                command.CommandText = @"CREATE TABLE IF NOT EXISTS [watched_pages] (
                        [id] integer PRIMARY KEY AUTOINCREMENT NOT NULL,
                        [page] TEXT NOT NULL
                    );";
                command.CommandType = CommandType.Text;
                command.ExecuteNonQuery();
            }

            UpdateViews();
            timer.Interval = 100 * 300;

            _autoEvent = new AutoResetEvent(true);
        }

        delegate void StringParameterDelegate();

        void UpdateViews()
        {
            if (InvokeRequired)
            {
                BeginInvoke(new StringParameterDelegate(UpdateViews), new object[] {});
            }
            else
            {
                EditFlags mask = EditFlags.None;
                if (reviewedEditsToolStripMenuItem.Checked)
                {
                    mask |= EditFlags.Unreviewed;
                }
                if (newArticlesToolStripMenuItem.Checked)
                {
                    mask |= EditFlags.New;
                }
                if (minorEditsToolStripMenuItem.Checked)
                {
                    mask |= EditFlags.Minor;
                }
                if (botEditsToolStripMenuItem.Checked)
                {
                    mask |= EditFlags.Bot;
                }
                recentChangesListView.BeginUpdate();
                recentChangesListView.Items.Clear();
                recentChangesListView.Groups.Clear();

                using (SQLiteCommand command = new SQLiteCommand(_connection))
                {
                    SQLiteParameter maskValue = new SQLiteParameter("@mask");
                    maskValue.Value = (int)mask;
                    command.Parameters.Add(maskValue);
                    command.CommandText = @"SELECT max(timestamp), sum(flags & 4), page, count(timestamp), sum(size), group_concat(author), max(diff), min(oldid)
                                            FROM [edits]
                                            WHERE (flags | @mask) == @mask
                                            GROUP BY page
                                            ORDER by max(timestamp) DESC";
                    using (SQLiteDataReader reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            DateTime time = DateTime.Parse(reader[0].ToString());
                            string day = time.ToLongDateString();
                            string t = time.ToShortTimeString();
                            bool found = false;
                            ListViewGroup group = null;
                            for (int i = 0; i < recentChangesListView.Groups.Count; ++i)
                            {
                                if (recentChangesListView.Groups[i].Header == day)
                                {
                                    group = recentChangesListView.Groups[i];
                                    found = true;
                                    break;
                                }
                            }
                            if (!found)
                            {
                                group = new ListViewGroup(day);
                                recentChangesListView.Groups.Add(group);
                            }

                            string[] authorsList = reader[5].ToString().Split(new char[] { ',' });
                            string authors = string.Join(", ", authorsList.Distinct().ToArray());
                            int size;
                            int.TryParse(reader[4].ToString(), out size);
                            int flags;
                            int.TryParse(reader[1].ToString(), out flags);
                            int changes;
                            int.TryParse(reader[3].ToString(), out changes);
                            long diff;
                            long.TryParse(reader[6].ToString(), out diff);
                            long oldId;
                            long.TryParse(reader[7].ToString(), out oldId);
                            PageListViewItem item = new PageListViewItem(t,
                                WikiEdit.FlagsToString((EditFlags)flags),
                                size,
                                authors,
                                changes,
                                diff,
                                oldId,
                                reader[2].ToString());
                            item.Group = group;
                            recentChangesListView.Items.Add(item);
                        }
                    }
                }
                recentChangesListView.EndUpdate();

                watchListView.BeginUpdate();
                watchListView.Items.Clear();
                watchListView.Groups.Clear();

                using (SQLiteCommand command = new SQLiteCommand(_connection))
                {
                    command.CommandText = @"SELECT max(timestamp), sum(flags & 4), edits.page, count(timestamp), sum(size), group_concat(author), max(diff), min(oldid)
                                            FROM [edits], [watched_pages]
                                            WHERE edits.page = watched_pages.page
                                            GROUP BY edits.page
                                            ORDER by max(timestamp) DESC";
                    using (SQLiteDataReader reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            DateTime time = DateTime.Parse(reader[0].ToString());
                            string day = time.ToLongDateString();
                            string t = time.ToShortTimeString();
                            bool found = false;
                            ListViewGroup group = null;
                            for (int i = 0; i < watchListView.Groups.Count; ++i)
                            {
                                if (watchListView.Groups[i].Header == day)
                                {
                                    group = watchListView.Groups[i];
                                    found = true;
                                    break;
                                }
                            }
                            if (!found)
                            {
                                group = new ListViewGroup(day);
                                watchListView.Groups.Add(group);
                            }

                            string[] authorsList = reader[5].ToString().Split(new char[] { ',' });
                            string authors = string.Join(", ", authorsList.Distinct().ToArray());
                            int size;
                            int.TryParse(reader[4].ToString(), out size);
                            int flags;
                            int.TryParse(reader[1].ToString(), out flags);
                            int changes;
                            int.TryParse(reader[3].ToString(), out changes);
                            long diff;
                            long.TryParse(reader[6].ToString(), out diff);
                            long oldId;
                            long.TryParse(reader[7].ToString(), out oldId);
                            PageListViewItem item = new PageListViewItem(t,
                                WikiEdit.FlagsToString((EditFlags)flags),
                                size,
                                authors,
                                changes,
                                diff,
                                oldId,
                                reader[2].ToString());
                            item.Group = group;
                            watchListView.Items.Add(item);
                        }
                    }
                }
                watchListView.EndUpdate();
            }
        }

        private void OnChannelMessage(object sender, IrcEventArgs e)
        {
            var edit = WikiEdit.Parse(e.Data.RawMessage);
            if (edit != null)
            {
                using (SQLiteCommand command = new SQLiteCommand(_connection))
                {
                    command.CommandText = @"INSERT INTO [edits]
                        (timestamp, author, page, flags, diff, oldid, size, summary)
                        VALUES (datetime('now'), @author, @page, @flags, @diff, @oldid, @size, @summary)";
                    SQLiteParameter author = new SQLiteParameter("@author");
                    author.Value = edit.Author;
                    SQLiteParameter page = new SQLiteParameter("@page");
                    page.Value = edit.Article;
                    SQLiteParameter flags = new SQLiteParameter("@flags");
                    flags.Value = (int)edit.Flags;
                    SQLiteParameter diff = new SQLiteParameter("@diff");
                    diff.Value = edit.Diff;
                    SQLiteParameter size = new SQLiteParameter("@size");
                    size.Value = edit.Size;
                    SQLiteParameter summary = new SQLiteParameter("@summary");
                    summary.Value = edit.Summary;
                    SQLiteParameter oldid = new SQLiteParameter("@oldid");
                    oldid.Value = edit.OldId;
                    
                    command.Parameters.Add(author);
                    command.Parameters.Add(page);
                    command.Parameters.Add(flags);
                    command.Parameters.Add(diff);
                    command.Parameters.Add(size);
                    command.Parameters.Add(summary);
                    command.Parameters.Add(oldid);

                    command.CommandType = CommandType.Text;
                    command.ExecuteNonQuery();
                }
            }
        }

        private void connectButton_Click(object sender, EventArgs e)
        {
            timer.Start();
            new Thread(new ThreadStart(Listen)).Start();
            disconnectToolStripMenuItem.Enabled = true;
            connectToolStripMenuItem.Enabled = false;
        }

        void Listen()
        {
            int port = 6667;
            string channel = "#ru.wikipedia";
            try
            {
                _irc.Connect("irc.wikimedia.org", port);
            }
            catch (ConnectionException err)
            {
                System.Console.WriteLine("couldn't connect! Reason: " + err.Message);
                return;
            }

            try
            {
                _autoEvent.Reset();
                _irc.Login("ClaymoreBot", "WikiLive IRC Bot");
                _irc.RfcJoin(channel);
                _stop = false;
                while (!_stop)
                {
                    _irc.ListenOnce();
                }
                _irc.Disconnect();
                _autoEvent.Set();
            }
            catch (ConnectionException)
            {
            }
        }

        private void disconnectButton_Click(object sender, EventArgs e)
        {
            _stop = true;
            _autoEvent.WaitOne();
            timer.Stop();
            connectToolStripMenuItem.Enabled = true;
            disconnectToolStripMenuItem.Enabled = false;
        }

        private void recentChangesListView_SelectedIndexChanged(object sender, EventArgs e)
        {
            ListViewItem selectedItem = recentChangesListView.SelectedItems.Count > 0 ? recentChangesListView.SelectedItems[0] : null;
            if (selectedItem != null)
            {
                string page = selectedItem.SubItems[2].Text;
                detailsListView.BeginUpdate();
                detailsListView.SuspendLayout();
                detailsListView.Items.Clear();

                using (SQLiteCommand command = new SQLiteCommand(_connection))
                {
                    SQLiteParameter pageValue = new SQLiteParameter("@page");
                    pageValue.Value = page;
                    command.Parameters.Add(pageValue);
                    command.CommandText = @"SELECT timestamp, flags, size, author, summary, diff, oldid
                                            FROM [edits]
                                            WHERE page=@page
                                            ORDER by timestamp DESC";
                    using (SQLiteDataReader reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            int size;
                            int.TryParse(reader[2].ToString(), out size);
                            int flags;
                            int.TryParse(reader[1].ToString(), out flags);
                            long diff;
                            long.TryParse(reader[5].ToString(), out diff);
                            long oldid;
                            long.TryParse(reader[6].ToString(), out oldid);

                            EditListViewItem item = new EditListViewItem(reader[0].ToString(),
                                WikiEdit.FlagsToString((EditFlags)flags),
                                size,
                                reader[3].ToString(),
                                reader[4].ToString(),
                                diff,
                                oldid);

                            detailsListView.Items.Add(item);
                        }
                        detailsListView.AutoResizeColumns(ColumnHeaderAutoResizeStyle.ColumnContent);
                    }
                }
                detailsListView.ResumeLayout();
                detailsListView.EndUpdate();
            }
        }

        private void MainForm_FormClosed(object sender, FormClosedEventArgs e)
        {
            _stop = true;
            _autoEvent.WaitOne();
            _connection.Close();
        }

        private void timer_Tick(object sender, EventArgs e)
        {
            UpdateViews();
        }

        private void openPageToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (recentChangesListView.SelectedItems.Count != 0)
            {
                ListViewItem selectedItem = recentChangesListView.SelectedItems[0];
                string page = selectedItem.SubItems[2].Text;
                System.Diagnostics.Process process = new System.Diagnostics.Process();
                process.StartInfo.UseShellExecute = true;
                process.StartInfo.FileName = "http://ru.wikipedia.org/w/index.php?title=" + Uri.EscapeDataString(page);
                process.Start();
            }
        }

        private void openDiffToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (detailsListView.SelectedItems.Count != 0)
            {
                EditListViewItem selectedItem = detailsListView.SelectedItems[0] as EditListViewItem;
                System.Diagnostics.Process process = new System.Diagnostics.Process();
                process.StartInfo.UseShellExecute = true;
                process.StartInfo.FileName = selectedItem.Diff;
                process.Start();
            }
        }

        private void exitToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Close();
        }

        private void importWatchlistToolStripMenuItem_Click(object sender, EventArgs e)
        {
            OpenFileDialog dlg = new OpenFileDialog();
            dlg.Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*";
            if (dlg.ShowDialog() != DialogResult.OK)
            {
                return;
            }
            using (SQLiteCommand command = new SQLiteCommand(_connection))
            {
                command.CommandText = @"DELETE FROM [watched_pages]";
                command.ExecuteNonQuery();
            }

            using (TextReader sr = new StreamReader(dlg.FileName))
            using (SQLiteTransaction transaction = _connection.BeginTransaction())
            using (SQLiteCommand command = new SQLiteCommand(_connection))
            {
                command.CommandText = @"INSERT INTO [watched_pages] (page) VALUES (@page)";
                SQLiteParameter page = new SQLiteParameter("@page");
                command.Parameters.Add(page);
                string line;
                while ((line = sr.ReadLine()) != null)
                {
                    page.Value = line;
                    command.ExecuteNonQuery();
                }
                transaction.Commit();
            }
        }

        private void watchListView_SelectedIndexChanged(object sender, EventArgs e)
        {
            ListViewItem selectedItem = watchListView.SelectedItems.Count > 0 ? watchListView.SelectedItems[0] : null;
            if (selectedItem != null)
            {
                string page = selectedItem.SubItems[2].Text;
                watchListEditsView.BeginUpdate();
                watchListEditsView.Items.Clear();

                using (SQLiteCommand command = new SQLiteCommand(_connection))
                {
                    SQLiteParameter pageValue = new SQLiteParameter("@page");
                    pageValue.Value = page;
                    command.Parameters.Add(pageValue);
                    command.CommandText = @"SELECT timestamp, flags, size, author, summary, diff, oldid
                                            FROM [edits]
                                            WHERE page=@page
                                            ORDER by timestamp DESC";
                    using (SQLiteDataReader reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            int size;
                            int.TryParse(reader[2].ToString(), out size);
                            int flags;
                            int.TryParse(reader[1].ToString(), out flags);

                            long diff;
                            long.TryParse(reader[5].ToString(), out diff);
                            long oldid;
                            long.TryParse(reader[6].ToString(), out oldid);

                            EditListViewItem item = new EditListViewItem(reader[0].ToString(),
                                WikiEdit.FlagsToString((EditFlags)flags),
                                size,
                                reader[3].ToString(),
                                reader[4].ToString(),
                                diff,
                                oldid);

                            watchListEditsView.Items.Add(item);
                        }
                        watchListEditsView.AutoResizeColumns(ColumnHeaderAutoResizeStyle.ColumnContent);
                    }
                }
                watchListEditsView.EndUpdate();
            }
        }

        private void viewHistoryToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (recentChangesListView.SelectedItems.Count != 0)
            {
                ListViewItem selectedItem = recentChangesListView.SelectedItems[0];
                string page = selectedItem.SubItems[2].Text;
                System.Diagnostics.Process process = new System.Diagnostics.Process();
                process.StartInfo.UseShellExecute = true;
                process.StartInfo.FileName = "http://ru.wikipedia.org/w/index.php?title=" + Uri.EscapeDataString(page) + "&action=history";
                process.Start();
            }
        }

        private void botEditsToolStripMenuItem_CheckedChanged(object sender, EventArgs e)
        {
            UpdateViews();
        }

        private void viewDiffToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (recentChangesListView.SelectedItems.Count != 0)
            {
                PageListViewItem selectedItem = recentChangesListView.SelectedItems[0] as PageListViewItem;
                System.Diagnostics.Process process = new System.Diagnostics.Process();
                process.StartInfo.UseShellExecute = true;
                process.StartInfo.FileName = selectedItem.Diff;
                process.Start();
            }
        }

        private void aboutToolStripMenuItem_Click(object sender, EventArgs e)
        {
            AboutForm dlg = new AboutForm();
            dlg.ShowDialog();
        }
    }

    internal class EditListViewItem : ListViewItem
    {
        string _time;
        string _flags;
        int _size;
        string _author;
        string _summary;
        long _diffNum;
        long _oldId;

        public string Diff
        {
            get
            {
                return _diffNum != 0
                    ? string.Format("http://ru.wikipedia.org/w/index.php?diff={0}&oldid={1}", _diffNum, _oldId)
                    : string.Format("http://ru.wikipedia.org/w/index.php?&oldid={0}", _oldId);
            }
        }

        public EditListViewItem(string time,
            string flags,
            int size,
            string author,
            string summary,
            long diff,
            long oldId)
            : base("")
        {
            _time = time;
            _flags = flags;
            _size = size;
            _author = author;
            _summary = summary;
            _diffNum = diff;
            _oldId = oldId;

            DateTime timeStamp = DateTime.Parse(_time, null,
                System.Globalization.DateTimeStyles.AssumeUniversal);
            string t = timeStamp.ToShortTimeString();
            string strSize = size >= 0 ? "+" + size.ToString() : size.ToString();

            SubItems[0].Text = t;
            SubItems.Add(_flags);
            SubItems.Add(strSize);
            SubItems.Add(_author);
            SubItems.Add(_summary);

            UseItemStyleForSubItems = false;
            SubItems[0].ForeColor = Color.Gray;
            SubItems[3].ForeColor = Color.Blue;
            SubItems[4].ForeColor = Color.Gray;

            if (strSize.Contains('+'))
            {
                SubItems[2].ForeColor = Color.Green;
            }
            else if (strSize.Contains('-'))
            {
                SubItems[2].ForeColor = Color.Red;
            }
        }
    }

    internal class PageListViewItem : ListViewItem
    {
        string _time;
        string _flags;
        int _size;
        string _authors;
        long _diffNum;
        long _oldId;
        int _changes;
        string _page;

        public string Diff
        {
            get
            {
                return _diffNum != 0
                    ? string.Format("http://ru.wikipedia.org/w/index.php?diff={0}&oldid={1}", _diffNum, _oldId)
                    : string.Format("http://ru.wikipedia.org/w/index.php?&oldid={0}", _oldId);
            }
        }

        public PageListViewItem(string time,
            string flags,
            int size,
            string authors,
            int changes,
            long diff,
            long oldId,
            string page)
            : base("")
        {
            _time = time;
            _flags = flags;
            _size = size;
            _authors = authors;
            _changes = changes;
            _diffNum = diff;
            _oldId = oldId;
            _page = page;

            DateTime timeStamp = DateTime.Parse(_time, null,
                System.Globalization.DateTimeStyles.AssumeUniversal);
            string t = timeStamp.ToShortTimeString();
            string strSize = size >= 0 ? "+" + size.ToString() : size.ToString();

            SubItems[0].Text = t;
            SubItems.Add(_flags);
            SubItems.Add(_page);
            SubItems.Add(_changes.ToString());
            SubItems.Add(strSize);
            SubItems.Add(_authors);

            UseItemStyleForSubItems = false;
            SubItems[0].ForeColor = Color.Gray;
            SubItems[2].ForeColor = Color.Blue;
            SubItems[3].ForeColor = Color.Gray;

            if (strSize.Contains('+'))
            {
                SubItems[4].ForeColor = Color.Green;
            }
            else if (strSize.Contains('-'))
            {
                SubItems[4].ForeColor = Color.Red;
            }
            SubItems[5].ForeColor = Color.Gray;
        }
    }

    internal class WikiEdit
    {
        private static Regex _messageRE = new Regex(@"\u000314\[\[\u000307(.+?)\u000314\]\]\u00034 (.*?)\u000310 \u000302(.+?)\u0003 \u00035\*\u0003 \u000303(.+?)\u0003 \u00035\*\u0003 \(([+-]?\d+?)\) \u000310(.*)\u0003");
        static private Regex diffRE = new Regex(@"http://ru\.wikipedia\.org/w/index\.php\?(diff=(\d+)&)?oldid=(\d+)");

        private string _article;
        private string _author;
        private long _diff;
        private long _oldId;
        private string _diffLink;
        private DateTime _time;
        private int _size;
        private string _summary;
        private EditFlags _flags;
        private string _flagsString;

        private WikiEdit()
        {
            _time = DateTime.Now;
        }

        public string Author
        {
            get { return _author; }
        }

        public string Summary
        {
            get { return _summary; }
        }

        public string Article
        {
            get { return _article; }
        }

        public EditFlags Flags
        {
            get { return _flags; }
        }

        public string DiffLink
        {
            get { return _diffLink; }
        }

        public long Diff
        {
            get { return _diff; }
        }

        public long OldId
        {
            get { return _oldId; }
        }

        public int Size
        {
            get { return _size; }
        }

        public static string FlagsToString(EditFlags flags)
        {
            string result = "";
            if ((flags & EditFlags.Unreviewed) == EditFlags.Unreviewed)
            {
                result += "!";
            }
            if ((flags & EditFlags.New) == EditFlags.New)
            {
                result += "Н";
            }
            if ((flags & EditFlags.Minor) == EditFlags.Minor)
            {
                result += "м";
            }
            if ((flags & EditFlags.Bot) == EditFlags.Bot)
            {
                result += "б";
            }
            return result;
        }

        public static WikiEdit Parse(string message)
        {
            Match m = _messageRE.Match(message);
            if (m.Success)
            {
                WikiEdit edit = new WikiEdit();
                edit._article = m.Groups[1].Value;
                edit._diffLink = m.Groups[3].Value;
                edit._author = m.Groups[4].Value;
                int.TryParse(m.Groups[5].Value, out edit._size);
                edit._summary = m.Groups[6].Value;
                string flags = m.Groups[2].Value;
                edit._flags = EditFlags.None;
                edit._flagsString = flags;

                m = diffRE.Match(m.Groups[3].Value);
                if (m.Success)
                {
                    long.TryParse(m.Groups[3].Value, out edit._oldId);
                    if (!string.IsNullOrEmpty(m.Groups[2].Value))
                    {
                        long.TryParse(m.Groups[2].Value, out edit._diff);
                    }
                }
                for (int i = 0; i < flags.Length; ++i)
                {
                    switch (flags[i])
                    {
                        case '!':
                            edit._flags |= EditFlags.Unreviewed;
                            break;
                        case 'B':
                            edit._flags |= EditFlags.Bot;
                            break;
                        case 'M':
                            edit._flags |= EditFlags.Minor;
                            break;
                        case 'N':
                            edit._flags |= EditFlags.New;
                            break;
                        default:
                            break;
                    }
                }
                return edit;
            }
            else
            {
                return null;
            }
        }
    }

    [Flags]
    internal enum EditFlags
    {
        None = 0,
        Bot = 1,
        Minor = 1 << 1,
        New = 1 << 2,
        Unreviewed = 1 << 3
    }
}
