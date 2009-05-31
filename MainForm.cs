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
using Claymore.WikiLive.Properties;
using Claymore.SharpMediaWiki;
using System.Xml;

namespace Claymore.WikiLive
{
    public partial class MainForm : Form
    {
        private static IrcClient _irc = new IrcClient();
        private volatile bool _stop;
        private SQLiteConnection _connection;
        private string _baseName;
        private AutoResetEvent _autoEvent;

        public MainForm()
        {
            InitializeComponent();

            _irc.Encoding = System.Text.Encoding.UTF8;
            _irc.SendDelay = 200;
            _irc.OnChannelMessage += new IrcEventHandler(OnChannelMessage);

            _autoEvent = new AutoResetEvent(true);
            _stop = false;
            timer.Interval = 100 * 300;

            SwitchToLanguage(Settings.Default.Language);
            UpdateViews();
        }

        private void SwitchToLanguage(string language)
        {
            string path = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            path += @"\WikiLive\" + language;
            Directory.CreateDirectory(path);

            _baseName = path + @"\WikiLive.db";
            if (!File.Exists(_baseName))
            {
                SQLiteConnection.CreateFile(_baseName);
            }

            if (_connection != null && _connection.State != ConnectionState.Closed)
            {
                _connection.Close();
            }

            SQLiteFactory factory = (SQLiteFactory)DbProviderFactories.GetFactory("System.Data.SQLite");
            _connection = (SQLiteConnection)factory.CreateConnection();
            _connection.ConnectionString = "Data Source = " + _baseName;
            _connection.Open();

            using (SQLiteCommand command = new SQLiteCommand(_connection))
            {
                command.CommandText = @"CREATE TABLE IF NOT EXISTS [edits] (
                        [id] integer PRIMARY KEY NOT NULL,
                        [timestamp] TEXT NOT NULL,
                        [user] TEXT NOT NULL,
                        [page] TEXT NOT NULL,
                        [namespace] INTEGER NOT NULL,
                        [flags] INTEGER NOT NULL,
                        [oldid] INTEGER NOT NULL,
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
                        [page] TEXT NOT NULL,
                        [namespace] INTEGER NOT NULL
                    );";
                command.CommandType = CommandType.Text;
                command.ExecuteNonQuery();
            }

            if (!File.Exists(path + @"\namespaces.dat"))
            {
                Wiki wiki = new Wiki("http://" + language + ".wikipedia.org");
                ParameterCollection parameters = new ParameterCollection();
                parameters.Add("meta", "siteinfo");
                parameters.Add("siprop", "namespaces");
                XmlDocument xml = wiki.Enumerate(parameters, true);
                XmlNodeList nodes = xml.SelectNodes("//ns[@id > 0]");
                Serializer serializer = new Serializer();
                serializer.Put(nodes.Count);
                foreach (XmlNode node in nodes)
                {
                    serializer.Put(node.Attributes["id"].Value);
                    serializer.Put(node.FirstChild.Value);
                }

                using (FileStream fs = new FileStream(path + @"\namespaces.dat", FileMode.CreateNew))
                using (BinaryWriter streamWriter = new BinaryWriter(fs))
                {
                    streamWriter.Write(serializer.ToArray());
                }
            }
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

                EditFlags onlyMask = EditFlags.None;
                if (onlyNewToolStripMenuItem.Checked)
                {
                    mask |= EditFlags.New;
                    onlyMask |= EditFlags.New;
                }
                if (onlyBotEditToolStripMenuItem.Checked)
                {
                    mask |= EditFlags.Bot;
                    onlyMask |= EditFlags.Bot;
                }
                if (onlyMinToolStripMenuItem.Checked)
                {
                    mask |= EditFlags.Minor;
                    onlyMask |= EditFlags.Minor;
                }
                if (onlyUnreviewedEditsToolStripMenuItem.Checked)
                {
                    mask |= EditFlags.Unreviewed;
                    onlyMask |= EditFlags.Unreviewed;
                }
                recentChangesListView.BeginUpdate();
                recentChangesListView.Items.Clear();
                recentChangesListView.Groups.Clear();

                using (SQLiteCommand command = new SQLiteCommand(_connection))
                {
                    SQLiteParameter maskValue = new SQLiteParameter("@mask");
                    maskValue.Value = (int)mask;
                    command.Parameters.Add(maskValue);
                    SQLiteParameter onlyMaskValue = new SQLiteParameter("@onlyMask");
                    onlyMaskValue.Value = (int)onlyMask;
                    command.Parameters.Add(onlyMaskValue);
                    command.CommandText = @"SELECT max(timestamp),
                                                   sum(flags & 4),
                                                   page,
                                                   count(timestamp),
                                                   sum(size),
                                                   group_concat(user),
                                                   max(id),
                                                   min(oldid),
                                                   namespace,
                                                   (namespace || ':' || page) AS name
                                            FROM [edits]
                                            WHERE (flags & @onlyMask) == @onlyMask AND
                                                  (flags | @mask) == @mask
                                            GROUP BY name
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
                            int nm;
                            int.TryParse(reader[8].ToString(), out nm);
                            PageListViewItem item = new PageListViewItem(t,
                                WikiEdit.FlagsToString((EditFlags)flags),
                                size,
                                authors,
                                changes,
                                diff,
                                oldId,
                                reader[2].ToString(),
                                nm);
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
                    command.CommandText = @"SELECT max(timestamp), sum(flags & 4), edits.page, count(timestamp), sum(size), group_concat(user), max(edits.id), min(oldid), edits.namespace, (edits.namespace || ':' || edits.page) AS name
                                            FROM [edits], [watched_pages]
                                            WHERE edits.page = watched_pages.page AND
                                                  (edits.namespace=watched_pages.namespace OR
                                                   edits.namespace=watched_pages.namespace + 1)
                                            GROUP BY name
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
                            int nm;
                            int.TryParse(reader[8].ToString(), out nm);
                            PageListViewItem item = new PageListViewItem(t,
                                WikiEdit.FlagsToString((EditFlags)flags),
                                size,
                                authors,
                                changes,
                                diff,
                                oldId,
                                reader[2].ToString(),
                                nm);
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
                        (timestamp, user, page, flags, id, oldid, size, summary, namespace)
                        VALUES (datetime('now'), @user, @page, @flags, @id, @oldid, @size, @summary, @namespace)";
                    SQLiteParameter author = new SQLiteParameter("@user");
                    author.Value = edit.Author;
                    SQLiteParameter page = new SQLiteParameter("@page");
                    page.Value = edit.Article;
                    SQLiteParameter flags = new SQLiteParameter("@flags");
                    flags.Value = (int)edit.Flags;
                    SQLiteParameter diff = new SQLiteParameter("@id");
                    diff.Value = edit.Id;
                    SQLiteParameter size = new SQLiteParameter("@size");
                    size.Value = edit.Size;
                    SQLiteParameter summary = new SQLiteParameter("@summary");
                    summary.Value = edit.Summary;
                    SQLiteParameter oldid = new SQLiteParameter("@oldid");
                    oldid.Value = edit.OldId;
                    SQLiteParameter nm = new SQLiteParameter("@namespace");
                    nm.Value = 0;

                    var namespaces = WikiEdit.GetNamespaces();
                    string key = namespaces.Keys.FirstOrDefault(n => edit.Article.StartsWith(n + ":"));
                    if (!string.IsNullOrEmpty(key))
                    {
                        page.Value = edit.Article.Replace(key + ":", "");
                        nm.Value = namespaces[key];
                    }
                    
                    command.Parameters.Add(author);
                    command.Parameters.Add(page);
                    command.Parameters.Add(flags);
                    command.Parameters.Add(diff);
                    command.Parameters.Add(size);
                    command.Parameters.Add(summary);
                    command.Parameters.Add(oldid);
                    command.Parameters.Add(nm);

                    command.CommandType = CommandType.Text;
                    command.ExecuteNonQuery();
                }
            }
        }

        private void connectButton_Click(object sender, EventArgs e)
        {
            if (!_connection.ConnectionString.Contains("\\" + Settings.Default.Language + "\\"))
            {
                SwitchToLanguage(Settings.Default.Language);
                UpdateViews();
            }
            timer.Start();
            new Thread(new ThreadStart(Listen)).Start();
            disconnectToolStripMenuItem.Enabled = true;
            connectToolStripMenuItem.Enabled = false;
        }

        void Listen()
        {
            int port = 6667;
            string channel = "#" + Settings.Default.Language + ".wikipedia";
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
                _irc.Login(Settings.Default.IrcUser, Settings.Default.IrcDescription);
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
            _autoEvent.Set();
        }

        private void recentChangesListView_SelectedIndexChanged(object sender, EventArgs e)
        {
            PageListViewItem selectedItem = recentChangesListView.SelectedItems.Count > 0 ? (PageListViewItem)recentChangesListView.SelectedItems[0] : null;
            if (selectedItem != null)
            {
                string page = selectedItem.SubItems[2].Text;
                detailsListView.BeginUpdate();
                detailsListView.SuspendLayout();
                detailsListView.Items.Clear();

                using (SQLiteCommand command = new SQLiteCommand(_connection))
                {
                    SQLiteParameter pageValue = new SQLiteParameter("@page");
                    pageValue.Value = selectedItem.Page;
                    command.Parameters.Add(pageValue);
                    SQLiteParameter namespaceValue = new SQLiteParameter("@namespace");
                    namespaceValue.Value = selectedItem.Namespace;
                    command.Parameters.Add(namespaceValue);
                    command.CommandText = @"SELECT timestamp, flags, size, user, summary, id, oldid
                                            FROM [edits]
                                            WHERE page=@page AND namespace=@namespace
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
                process.StartInfo.FileName = GetUri(Settings.Default.Language,
                    Settings.Default.HttpsLinks,
                    "title=" + Uri.EscapeDataString(page));
                process.Start();
            }
        }

        public static string GetUri(string language, bool https, string parameters)
        {
            if (https)
            {
                return string.Format("https://secure.wikimedia.org/wikipedia/{0}/w/index.php?{1}", language, parameters);
            }
            return string.Format("http://{0}.wikipedia.org/w/index.php?{1}", language, parameters);
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
                command.CommandText = @"INSERT INTO [watched_pages] (page, namespace) VALUES (@page, @namespace)";
                SQLiteParameter page = new SQLiteParameter("@page");
                command.Parameters.Add(page);
                SQLiteParameter nm = new SQLiteParameter("@namespace");
                command.Parameters.Add(nm);
                string line;
                while ((line = sr.ReadLine()) != null)
                {
                    nm.Value = 0;
                    var namespaces = WikiEdit.GetNamespaces();
                    string key = namespaces.Keys.FirstOrDefault(n => line.StartsWith(n + ":"));
                    if (!string.IsNullOrEmpty(key))
                    {
                        page.Value = line.Replace(key + ":", "");
                        nm.Value = namespaces[key];
                    }
                    else
                    {
                        page.Value = line;
                    }
                    command.ExecuteNonQuery();
                }
                transaction.Commit();
            }
        }

        private void watchListView_SelectedIndexChanged(object sender, EventArgs e)
        {
            PageListViewItem selectedItem = watchListView.SelectedItems.Count > 0 ? (PageListViewItem)watchListView.SelectedItems[0] : null;
            if (selectedItem != null)
            {
                string page = selectedItem.SubItems[2].Text;
                watchListEditsView.BeginUpdate();
                watchListEditsView.Items.Clear();

                using (SQLiteCommand command = new SQLiteCommand(_connection))
                {
                    SQLiteParameter pageValue = new SQLiteParameter("@page");
                    pageValue.Value = selectedItem.Page;
                    command.Parameters.Add(pageValue);
                    SQLiteParameter namespaceValue = new SQLiteParameter("@namespace");
                    namespaceValue.Value = selectedItem.Namespace;
                    command.Parameters.Add(namespaceValue);
                    command.CommandText = @"SELECT timestamp, flags, size, user, summary, id, oldid
                                            FROM [edits]
                                            WHERE page=@page AND namespace=@namespace
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
                process.StartInfo.FileName = GetUri(Settings.Default.Language,
                    Settings.Default.HttpsLinks,
                    "title=" + Uri.EscapeDataString(page) + "&action=history");
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

        private void onlyNewToolStripMenuItem_Click(object sender, EventArgs e)
        {
            newArticlesToolStripMenuItem.Enabled = !newArticlesToolStripMenuItem.Enabled;
            UpdateViews();
        }

        private void onlyBotEditToolStripMenuItem_Click(object sender, EventArgs e)
        {
            botEditsToolStripMenuItem.Enabled = !botEditsToolStripMenuItem.Enabled;
            UpdateViews();
        }

        private void onlyMinToolStripMenuItem_Click(object sender, EventArgs e)
        {
            minorEditsToolStripMenuItem.Enabled = !minorEditsToolStripMenuItem.Enabled;
            UpdateViews();
        }

        private void onlyUnreviewedEditsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            reviewedEditsToolStripMenuItem.Enabled = !reviewedEditsToolStripMenuItem.Enabled;
            UpdateViews();
        }

        private void toolStripMenuItem4_Click(object sender, EventArgs e)
        {
            if (watchListEditsView.SelectedItems.Count != 0)
            {
                EditListViewItem selectedItem = watchListEditsView.SelectedItems[0] as EditListViewItem;
                System.Diagnostics.Process process = new System.Diagnostics.Process();
                process.StartInfo.UseShellExecute = true;
                process.StartInfo.FileName = selectedItem.Diff;
                process.Start();
            }
        }

        private void optionsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            OptionsForm dlg = new OptionsForm();
            if (dlg.ShowDialog() == DialogResult.OK)
            {
                Settings.Default.IrcUser = dlg.User;
                Settings.Default.IrcDescription = dlg.Description;
                Settings.Default.Language = dlg.Language;
                Settings.Default.HttpsLinks = dlg.HttpsLinks;
                Settings.Default.Save();
                
                disconnectButton_Click(this, EventArgs.Empty);
                if (!_connection.ConnectionString.Contains("\\" + Settings.Default.Language + "\\"))
                {
                    SwitchToLanguage(Settings.Default.Language);
                    WikiEdit.SwitchToLanguage(Settings.Default.Language);
                    UpdateViews();
                }
            }
        }

        private void toolStripMenuItem1_Click(object sender, EventArgs e)
        {
            if (watchListView.SelectedItems.Count != 0)
            {
                ListViewItem selectedItem = watchListView.SelectedItems[0];
                string page = selectedItem.SubItems[2].Text;
                System.Diagnostics.Process process = new System.Diagnostics.Process();
                process.StartInfo.UseShellExecute = true;
                process.StartInfo.FileName = GetUri(Settings.Default.Language,
                    Settings.Default.HttpsLinks,
                    "title=" + Uri.EscapeDataString(page));
                process.Start();
            }
        }

        private void toolStripMenuItem2_Click(object sender, EventArgs e)
        {
            if (watchListView.SelectedItems.Count != 0)
            {
                ListViewItem selectedItem = watchListView.SelectedItems[0];
                string page = selectedItem.SubItems[2].Text;
                System.Diagnostics.Process process = new System.Diagnostics.Process();
                process.StartInfo.UseShellExecute = true;
                process.StartInfo.FileName = GetUri(Settings.Default.Language,
                    Settings.Default.HttpsLinks,
                    "title=" + Uri.EscapeDataString(page) + "&action=history");
                process.Start();
            }
        }

        private void toolStripMenuItem3_Click(object sender, EventArgs e)
        {
            if (watchListView.SelectedItems.Count != 0)
            {
                PageListViewItem selectedItem = watchListView.SelectedItems[0] as PageListViewItem;
                System.Diagnostics.Process process = new System.Diagnostics.Process();
                process.StartInfo.UseShellExecute = true;
                process.StartInfo.FileName = selectedItem.Diff;
                process.Start();
            }
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
        int _namespace;

        public string Diff
        {
            get
            {
                return MainForm.GetUri(Settings.Default.Language,
                    Settings.Default.HttpsLinks,
                    _diffNum != 0
                        ? string.Format("diff={0}&oldid={1}", _diffNum, _oldId)
                        : string.Format("&oldid={0}", _oldId));
            }
        }

        public string Page
        {
            get { return _page; }
        }

        public int Namespace
        {
            get { return _namespace; }
        }

        public PageListViewItem(string time,
            string flags,
            int size,
            string authors,
            int changes,
            long diff,
            long oldId,
            string page,
            int nm)
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
            _namespace = nm;

            DateTime timeStamp = DateTime.Parse(_time, null,
                System.Globalization.DateTimeStyles.AssumeUniversal);
            string t = timeStamp.ToShortTimeString();
            string strSize = size >= 0 ? "+" + size.ToString() : size.ToString();

            SubItems[0].Text = t;
            SubItems.Add(_flags);
            if (_namespace != 0)
            {
                SubItems.Add(string.Format("{0}:{1}", WikiEdit.NumberToNamespace(_namespace), _page));
            }
            else
            {
                SubItems.Add(_page);
            }
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
        private static Regex _diffRE = new Regex(@"http://(.+?)\.wikipedia\.org/w/index\.php\?(diff=(\d+)&)?oldid=(\d+)");
        private static Dictionary<string, int> _namespaces;
        private static Dictionary<int, string> _reversedNamespaces;

        static WikiEdit()
        {
            _namespaces = new Dictionary<string, int>();
            _reversedNamespaces = new Dictionary<int, string>();
            SwitchToLanguage(Settings.Default.Language);
        }

        public static void SwitchToLanguage(string language)
        {
            _namespaces.Clear();
            _reversedNamespaces.Clear();

            string path = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            path += @"\WikiLive\" + language;
            Deserializer deserializer = new Deserializer(File.ReadAllBytes(path + @"\namespaces.dat"));
            int count = deserializer.GetInt();
            for (int i = 0; i < count; ++i)
            {
                int number;
                int.TryParse(deserializer.GetString(), out number);
                _namespaces.Add(deserializer.GetString(), number);
            }

            foreach (KeyValuePair<string, int> pair in _namespaces)
            {
                _reversedNamespaces.Add(pair.Value, pair.Key);
            }
        }

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

        public long Id
        {
            get { return _diff == 0 ? _oldId : _diff; }
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

                m = _diffRE.Match(m.Groups[3].Value);
                if (m.Success)
                {
                    long.TryParse(m.Groups[4].Value, out edit._oldId);
                    if (!string.IsNullOrEmpty(m.Groups[3].Value))
                    {
                        long.TryParse(m.Groups[3].Value, out edit._diff);
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

        public static string NumberToNamespace(int nameSpace)
        {
            return _reversedNamespaces[nameSpace];
        }

        public static int NamespaceToNumber(string nameSpace)
        {
            return _namespaces[nameSpace];
        }

        public static IDictionary<string, int> GetNamespaces()
        {
            return _namespaces;
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
