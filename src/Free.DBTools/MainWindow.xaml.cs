using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Free.DBTools.Core;

namespace Free.DBTools
{
    public sealed class SqlSuggestion
    {
        public SqlSuggestion(string text, string category)
        {
            Text = text;
            Category = category;
        }

        public string Text { get; private set; }
        public string Category { get; private set; }

        public override string ToString()
        {
            return Text;
        }
    }

    internal sealed class TableReference
    {
        public TableReference(string name, string alias)
        {
            Name = name;
            Alias = alias;
        }

        public string Name { get; private set; }
        public string Alias { get; private set; }

        public string ShortName
        {
            get
            {
                var index = Name.LastIndexOf('.');
                return index >= 0 ? Name.Substring(index + 1) : Name;
            }
        }
    }

    internal sealed class ColumnCandidate
    {
        public ColumnCandidate(string display, string column)
        {
            Display = display;
            Column = column;
        }

        public string Display { get; private set; }
        public string Column { get; private set; }
    }

    internal sealed class SqlCompletionContext
    {
        public SqlCompletionContext()
        {
            Tables = new List<TableReference>();
        }

        public List<TableReference> Tables { get; private set; }
        public string Qualifier { get; set; }
        public bool PreferColumns { get; set; }
        public bool PreferObjects { get; set; }
        public bool AtSelectStart { get; set; }
        public bool AutoShow { get; set; }
    }

    public partial class MainWindow : Window
    {
        private static readonly int[] PageSizes = { 50, 100, 200, 500, 1000 };

        private static readonly string[] SqlKeywords =
        {
            "SELECT", "FROM", "WHERE", "JOIN", "LEFT JOIN", "RIGHT JOIN", "INNER JOIN",
            "OUTER JOIN", "FULL JOIN", "CROSS JOIN", "ON", "GROUP BY", "ORDER BY",
            "HAVING", "LIMIT", "OFFSET", "FETCH", "TOP", "DISTINCT", "UNION", "UNION ALL",
            "INSERT INTO", "VALUES", "UPDATE", "SET", "DELETE FROM", "CREATE TABLE",
            "ALTER TABLE", "DROP TABLE", "TRUNCATE TABLE", "CREATE VIEW", "CREATE INDEX",
            "AND", "OR", "NOT", "NULL", "IS NULL", "IS NOT NULL", "IN", "NOT IN", "LIKE",
            "NOT LIKE", "BETWEEN", "EXISTS", "AS", "ASC", "DESC", "CASE", "WHEN", "THEN",
            "ELSE", "END", "COUNT", "SUM", "AVG", "MIN", "MAX", "COALESCE", "IFNULL",
            "NULLIF", "CAST", "CONVERT", "WITH", "INTO", "PRIMARY KEY", "FOREIGN KEY",
            "DEFAULT", "CONSTRAINT", "IF EXISTS", "BEGIN", "COMMIT", "ROLLBACK"
        };

        private static readonly HashSet<string> ReservedWords = BuildReservedWords();

        private static readonly HashSet<string> ObjectContextKeywords = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase)
        {
            "FROM", "JOIN", "INTO", "UPDATE", "LEFT", "RIGHT", "INNER", "OUTER",
            "FULL", "CROSS", "TABLE", "VIEW"
        };

        private static readonly HashSet<string> ColumnContextKeywords = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase)
        {
            "SELECT", "WHERE", "ON", "HAVING", "SET", "ORDER", "GROUP", "BY",
            "AND", "OR", "USING", "WHEN", "THEN", "ELSE"
        };

        private static readonly HashSet<string> ClauseBoundaryKeywords = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase)
        {
            "WHERE", "GROUP", "ORDER", "HAVING", "LIMIT", "OFFSET", "FETCH", "UNION",
            "EXCEPT", "INTERSECT", "MINUS", "JOIN", "LEFT", "RIGHT", "INNER", "OUTER",
            "FULL", "CROSS", "NATURAL", "ON", "SET", "VALUES", "USING", "WINDOW",
            "QUALIFY", "RETURNING", "FROM", "SELECT", "DELETE", "INSERT", "UPDATE"
        };

        private static readonly Regex SqlTokenRegex = new Regex(
            @"(?<id>(?:\[[^\]]*\]|`[^`]*`|""[^""]*""|[\w$#@]+)(?:\s*\.\s*(?:\[[^\]]*\]|`[^`]*`|""[^""]*""|[\w$#@]+))*)|(?<punct>[(),;.=<>!+*/%-])",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static HashSet<string> BuildReservedWords()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var keyword in SqlKeywords)
            {
                foreach (var part in keyword.Split(' '))
                {
                    if (part.Length > 0)
                    {
                        set.Add(part);
                    }
                }
            }

            foreach (var word in new[]
            {
                "AS", "ASC", "DESC", "ALL", "ANY", "USING", "WINDOW", "QUALIFY",
                "RETURNING", "EXCEPT", "INTERSECT", "MINUS", "OVER", "PARTITION",
                "ROWS", "RANGE", "PRECEDING", "FOLLOWING", "CURRENT", "ROW", "LATERAL",
                "NATURAL", "EXPLAIN"
            })
            {
                set.Add(word);
            }
            return set;
        }

        private readonly Dictionary<string, List<string>> _columnCache =
            new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _columnLoading =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private readonly ConnectionStore _store = new ConnectionStore();
        private readonly ObservableCollection<ConnectionProfile> _profiles =
            new ObservableCollection<ConnectionProfile>();

        private DatabaseSession _session;
        private List<DatabaseObjectInfo> _tables = new List<DatabaseObjectInfo>();
        private List<DatabaseObjectInfo> _views = new List<DatabaseObjectInfo>();
        private DatabaseObjectInfo _currentObject;
        private ConnectionProfile _activeProfile;
        private DataTable _currentTable;
        private int _pageIndex;
        private int _pageSize = 200;
        private long _rowCount = -1;
        private bool _busy;
        private bool _suppressKindChange;
        private bool _suppressProfileSelection;
        private bool _suppressDatabaseSelection;
        private bool _databaseSelectionRequired;

        private ConnectionInput _currentInput;
        private string _currentDatabase;
        private string _preferredDatabase;
        private List<string> _databases = new List<string>();

        private bool _autoCompleteSuppressed;
        private bool _autoCompleteRefreshBlocked;
        private int _autoCompleteStart;
        private int _autoCompleteLength;

        public MainWindow()
        {
            InitializeComponent();

            UpdateThemeButtons();

            var workArea = SystemParameters.WorkArea;
            Width = Math.Min(1320, Math.Max(MinWidth, workArea.Width - 32));
            Height = Math.Min(840, Math.Max(MinHeight, workArea.Height - 32));
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = workArea.Left + Math.Max(0, (workArea.Width - Width) / 2);
            Top = workArea.Top + Math.Max(0, (workArea.Height - Height) / 2);

            CmbPageSize.ItemsSource = PageSizes;
            CmbKind.ItemsSource = DatabaseCatalog.All;
            CmbProfiles.ItemsSource = _profiles;
            CmbPageSize.SelectedItem = _pageSize;
            CmbKind.SelectedIndex = 0;
            ReloadProfiles();

            TxtHost.Text = "localhost";
            UpdateUiState();
            UpdatePagerUi();
            AppendLog("欢迎使用 Free.DBTools。选择数据库类型并填写连接信息，然后点击“连接数据库”。");
        }

        private bool _syncingThemeButtons;

        private void BtnTheme_Changed(object sender, RoutedEventArgs e)
        {
            if (_syncingThemeButtons)
            {
                return;
            }

            var button = sender as System.Windows.Controls.Primitives.ToggleButton;
            if (button == BtnThemeDark && button.IsChecked == true)
            {
                SetTheme(ThemeManager.Dark);
            }
            else if (button == BtnThemeLight && button.IsChecked == true)
            {
                SetTheme(ThemeManager.Light);
            }

            UpdateThemeButtons();
        }

        private static void SetTheme(string theme)
        {
            if (string.Equals(theme, ThemeManager.Current, StringComparison.Ordinal))
            {
                return;
            }

            ThemeManager.Apply(theme);
            ThemeManager.Save(theme);
        }

        private void UpdateThemeButtons()
        {
            _syncingThemeButtons = true;
            BtnThemeLight.IsChecked = ThemeManager.Current == ThemeManager.Light;
            BtnThemeDark.IsChecked = ThemeManager.Current == ThemeManager.Dark;
            _syncingThemeButtons = false;
        }

        // ==================== 连接配置 ====================

        private ConnectionInput BuildInput(DatabaseKind kind, bool forceAuto = false)
        {
            var useCustom = !forceAuto && ChkUseCustom.IsChecked == true;
            string database;
            if (useCustom)
            {
                database = string.Empty;
            }
            else if (kind != null && kind.Key == "Oracle")
            {
                database = (TxtDatabase.Text ?? string.Empty).Trim();
            }
            else
            {
                database = _preferredDatabase ?? string.Empty;
            }

            return new ConnectionInput
            {
                Host = (TxtHost.Text ?? string.Empty).Trim(),
                Port = ParsePort(TxtPort.Text, kind == null ? 0 : kind.DefaultPort),
                Database = database,
                User = (TxtUser.Text ?? string.Empty).Trim(),
                Password = GetPassword(),
                FilePath = (TxtFile.Text ?? string.Empty).Trim(),
                UseCustomString = useCustom,
                CustomConnectionString = TxtCustomConn.Text
            };
        }

        private static int ParsePort(string text, int fallback)
        {
            int port;
            return int.TryParse((text ?? string.Empty).Trim(), out port) && port > 0 ? port : fallback;
        }

        private string GetPassword()
        {
            return ChkShowPassword.IsChecked == true ? TxtPasswordPlain.Text : PwdBox.Password;
        }

        private void SetPassword(string value)
        {
            value = value ?? string.Empty;
            PwdBox.Password = value;
            TxtPasswordPlain.Text = value;
            if (ChkShowPassword.IsChecked == true)
            {
                ChkShowPassword.IsChecked = false;
            }
        }

        private void ApplyKindUi(DatabaseKind kind)
        {
            var custom = ChkUseCustom.IsChecked == true;
            var fileBased = kind != null && kind.IsFileBased;
            var isOracle = kind != null && kind.Key == "Oracle";

            ServerPanel.Visibility = (!custom && !fileBased) ? Visibility.Visible : Visibility.Collapsed;
            OraclePanel.Visibility = (!custom && isOracle) ? Visibility.Visible : Visibility.Collapsed;
            FilePanel.Visibility = (!custom && fileBased) ? Visibility.Visible : Visibility.Collapsed;
            CustomPanel.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;

            var canSelectDatabase = !custom && kind != null && kind.SupportsDatabaseSwitch &&
                                    _session != null && ReferenceEquals(_session.Kind, kind) &&
                                    (_currentInput == null || !_currentInput.UseCustomString);
            DatabasePanel.Visibility = canSelectDatabase ? Visibility.Visible : Visibility.Collapsed;

            if (kind == null)
            {
                return;
            }

            TxtDriverNote.Text = kind.DriverNote;
        }

        private void CmbKind_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressKindChange)
            {
                return;
            }

            var kind = CmbKind.SelectedItem as DatabaseKind;
            if (kind == null)
            {
                return;
            }

            if (_session != null && !ReferenceEquals(_session.Kind, kind))
            {
                CloseSession();
                AppendLog("数据库类型已更改，当前连接已断开。");
            }

            if (_activeProfile != null &&
                !string.Equals(_activeProfile.KindKey, kind.Key, StringComparison.OrdinalIgnoreCase))
            {
                _suppressProfileSelection = true;
                CmbProfiles.SelectedItem = null;
                _suppressProfileSelection = false;
                _activeProfile = null;
            }

            _preferredDatabase = null;
            TxtPort.Text = kind.DefaultPort > 0 ? kind.DefaultPort.ToString(CultureInfo.InvariantCulture) : string.Empty;
            ApplyKindUi(kind);
            UpdateUiState();
        }

        private void ChkUseCustom_Changed(object sender, RoutedEventArgs e)
        {
            var kind = CmbKind.SelectedItem as DatabaseKind;
            if (ChkUseCustom.IsChecked == true && kind != null && string.IsNullOrWhiteSpace(TxtCustomConn.Text))
            {
                TxtCustomConn.Text = kind.BuildConnectionString(BuildInput(kind, true));
            }

            ApplyKindUi(kind);
        }

        private void ChkShowPassword_Changed(object sender, RoutedEventArgs e)
        {
            if (ChkShowPassword.IsChecked == true)
            {
                TxtPasswordPlain.Text = PwdBox.Password;
                TxtPasswordPlain.Visibility = Visibility.Visible;
                PwdBox.Visibility = Visibility.Collapsed;
            }
            else
            {
                PwdBox.Password = TxtPasswordPlain.Text;
                TxtPasswordPlain.Visibility = Visibility.Collapsed;
                PwdBox.Visibility = Visibility.Visible;
            }
        }

        private void BtnBrowse_Click(object sender, RoutedEventArgs e)
        {
            var kind = CmbKind.SelectedItem as DatabaseKind;
            var dialog = new OpenFileDialog
            {
                Title = "选择数据库文件",
                Filter = kind != null && !string.IsNullOrEmpty(kind.FileFilter)
                    ? kind.FileFilter
                    : "所有文件|*.*",
                CheckFileExists = false
            };

            var current = (TxtFile.Text ?? string.Empty).Trim();
            if (Directory.Exists(current))
            {
                dialog.InitialDirectory = current;
            }

            if (dialog.ShowDialog(this) == true)
            {
                TxtFile.Text = dialog.FileName;
            }
        }

        private bool ValidateInput(DatabaseKind kind, ConnectionInput input)
        {
            if (input.UseCustomString)
            {
                if (string.IsNullOrWhiteSpace(input.CustomConnectionString))
                {
                    Warn("请输入自定义连接字符串。");
                    TxtCustomConn.Focus();
                    return false;
                }
                return true;
            }

            if (kind.IsFileBased)
            {
                if (string.IsNullOrWhiteSpace(input.FilePath))
                {
                    Warn("请选择或输入数据库文件路径。");
                    TxtFile.Focus();
                    return false;
                }
                if (kind.Key == "Access" && !File.Exists(input.FilePath))
                {
                    Warn("Access 数据库文件不存在：\n" + input.FilePath);
                    return false;
                }
                return true;
            }

            if (string.IsNullOrWhiteSpace(input.Host))
            {
                Warn("请输入主机 / 服务器地址。");
                TxtHost.Focus();
                return false;
            }

            return true;
        }

        private async void BtnConnect_Click(object sender, RoutedEventArgs e)
        {
            var kind = CmbKind.SelectedItem as DatabaseKind;
            if (kind == null)
            {
                return;
            }

            var input = BuildInput(kind);
            if (!ValidateInput(kind, input))
            {
                return;
            }

            var connectionString = kind.BuildConnectionString(input);
            SetBusy(true, "正在连接 " + kind.DisplayName + "…");

            DatabaseSession session = null;
            try
            {
                session = await Task.Run(() =>
                {
                    var created = new DatabaseSession(kind, connectionString);
                    try
                    {
                        created.Open();
                        return created;
                    }
                    catch
                    {
                        created.Dispose();
                        throw;
                    }
                });
            }
            catch (Exception ex)
            {
                SetBusy(false);
                SetStatus(false, "连接失败");
                TxtServerVersion.Text = string.Empty;
                AppendLog("连接失败：" + ex.Message);
                ShowError("连接失败", ex.Message);
                return;
            }

            SetBusy(false);
            _currentInput = input;
            _currentDatabase = kind.SupportsDatabaseSwitch && !string.IsNullOrWhiteSpace(input.Database)
                ? input.Database.Trim()
                : null;
            ReplaceSession(session);
            TxtServerVersion.Text = string.IsNullOrWhiteSpace(session.ServerVersion)
                ? "版本未知"
                : session.ServerVersion;
            SetStatus(true, kind.DisplayName + " 已连接");
            AppendLog("已连接 " + kind.DisplayName + "（" + TxtServerVersion.Text + "）。");

            if (_activeProfile != null && _profiles.Contains(_activeProfile))
            {
                _activeProfile.LastUsedAt = DateTime.Now;
                try { _store.Save(_profiles); }
                catch { }
            }

            await InitializeDatabaseSelectorAsync(session);
        }

        private async Task InitializeDatabaseSelectorAsync(DatabaseSession session)
        {
            ApplyKindUi(session.Kind);

            var canSwitch = session.Kind.SupportsDatabaseSwitch &&
                            _currentInput != null && !_currentInput.UseCustomString;
            if (!canSwitch)
            {
                await LoadObjectsAsync();
                return;
            }

            SetBusy(true, "正在读取数据库列表…");
            List<string> databases;
            try
            {
                databases = await Task.Run(() => session.GetDatabaseNames());
            }
            catch (Exception ex)
            {
                databases = new List<string>();
                AppendLog("读取数据库列表失败：" + ex.Message);
            }
            finally
            {
                SetBusy(false);
            }

            _databases = databases ?? new List<string>();
            if (_databases.Count == 0)
            {
                AppendLog("未能获取数据库列表，将在当前默认数据库上浏览。");
                PopulateDatabaseSelector();
                await LoadObjectsAsync();
                return;
            }

            var target = string.IsNullOrWhiteSpace(_currentDatabase)
                ? session.Kind.DefaultDatabase
                : _currentDatabase;
            var actual = _databases.FirstOrDefault(d =>
                string.Equals(d, target, StringComparison.OrdinalIgnoreCase));
            _currentDatabase = actual;
            if (actual != null)
            {
                _preferredDatabase = actual;
            }
            PopulateDatabaseSelector();

            if (actual == null)
            {
                _databaseSelectionRequired = true;
                _tables = new List<DatabaseObjectInfo>();
                _views = new List<DatabaseObjectInfo>();
                RebuildTree();
                UpdateUiState();
                AppendLog("已连接，请在左侧选择要浏览的数据库。");
                return;
            }

            await LoadObjectsAsync();
        }

        private void PopulateDatabaseSelector()
        {
            _suppressDatabaseSelection = true;
            try
            {
                CmbDatabase.ItemsSource = null;
                CmbDatabase.ItemsSource = _databases;
                CmbDatabase.SelectedItem = string.IsNullOrEmpty(_currentDatabase)
                    ? null
                    : _databases.FirstOrDefault(d =>
                        string.Equals(d, _currentDatabase, StringComparison.OrdinalIgnoreCase));
            }
            finally
            {
                _suppressDatabaseSelection = false;
            }

            ApplyKindUi(_session == null ? null : _session.Kind);
        }

        private void ClearDatabaseSelector()
        {
            _suppressDatabaseSelection = true;
            try
            {
                CmbDatabase.ItemsSource = null;
                CmbDatabase.SelectedItem = null;
            }
            finally
            {
                _suppressDatabaseSelection = false;
            }

            DatabasePanel.Visibility = Visibility.Collapsed;
        }

        private async void CmbDatabase_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressDatabaseSelection || _busy || _session == null)
            {
                return;
            }

            var database = CmbDatabase.SelectedItem as string;
            if (string.IsNullOrWhiteSpace(database) ||
                string.Equals(database, _currentDatabase, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var kind = _session.Kind;
            if (!kind.SupportsDatabaseSwitch || _currentInput == null)
            {
                return;
            }

            var previous = _currentDatabase;
            var input = new ConnectionInput
            {
                Host = _currentInput.Host,
                Port = _currentInput.Port,
                Database = database,
                User = _currentInput.User,
                Password = _currentInput.Password,
                FilePath = _currentInput.FilePath,
                UseCustomString = false,
                CustomConnectionString = _currentInput.CustomConnectionString
            };

            SetBusy(true, "正在切换到数据库 " + database + "…");
            DatabaseSession session;
            try
            {
                var connectionString = kind.BuildConnectionString(input);
                session = await Task.Run(() =>
                {
                    var created = new DatabaseSession(kind, connectionString);
                    try
                    {
                        created.Open();
                        return created;
                    }
                    catch
                    {
                        created.Dispose();
                        throw;
                    }
                });
            }
            catch (Exception ex)
            {
                SetBusy(false);
                RestoreDatabaseSelection(previous);
                SetStatus(true, string.IsNullOrEmpty(previous)
                    ? kind.DisplayName + " 已连接"
                    : kind.DisplayName + " · " + previous);
                AppendLog("切换数据库失败：" + ex.Message);
                ShowError("切换数据库失败", ex.Message);
                return;
            }

            SetBusy(false);
            _currentInput = input;
            _currentDatabase = database;
            _preferredDatabase = database;
            ReplaceSession(session);
            TxtServerVersion.Text = string.IsNullOrWhiteSpace(session.ServerVersion)
                ? "版本未知"
                : session.ServerVersion;
            SetStatus(true, kind.DisplayName + " · " + database);
            AppendLog("已切换数据库：" + database + "。");
            PopulateDatabaseSelector();
            PersistActiveProfileDatabase(database);
            await LoadObjectsAsync();
        }

        private void RestoreDatabaseSelection(string database)
        {
            _suppressDatabaseSelection = true;
            try
            {
                CmbDatabase.ItemsSource = _databases;
                CmbDatabase.SelectedItem = string.IsNullOrEmpty(database)
                    ? null
                    : _databases.FirstOrDefault(d =>
                        string.Equals(d, database, StringComparison.OrdinalIgnoreCase));
            }
            finally
            {
                _suppressDatabaseSelection = false;
            }
        }

        private void PersistActiveProfileDatabase(string database)
        {
            if (_activeProfile == null || !_profiles.Contains(_activeProfile))
            {
                return;
            }

            _activeProfile.Database = database;
            _activeProfile.LastUsedAt = DateTime.Now;
            try { _store.Save(_profiles); }
            catch { }
        }

        private async void BtnTest_Click(object sender, RoutedEventArgs e)
        {
            var kind = CmbKind.SelectedItem as DatabaseKind;
            if (kind == null)
            {
                return;
            }

            var input = BuildInput(kind);
            if (!ValidateInput(kind, input))
            {
                return;
            }

            var connectionString = kind.BuildConnectionString(input);
            SetBusy(true, "正在测试 " + kind.DisplayName + " 连接…");
            try
            {
                var version = await Task.Run(() => DatabaseSession.TestConnection(kind, connectionString));
                var versionText = string.IsNullOrWhiteSpace(version) ? "未知" : version;
                AppendLog("测试成功：" + kind.DisplayName + "（" + versionText + "）。");
                MessageBox.Show(
                    this,
                    "连接成功！\n\n服务器版本：" + versionText,
                    "测试连接",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                AppendLog("测试连接失败：" + ex.Message);
                ShowError("测试连接失败", ex.Message);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private void ReplaceSession(DatabaseSession session)
        {
            var old = _session;
            _session = session;

            _currentObject = null;
            _currentTable = null;
            _rowCount = -1;
            _pageIndex = 0;
            _databaseSelectionRequired = false;
            _tables = new List<DatabaseObjectInfo>();
            _views = new List<DatabaseObjectInfo>();
            _columnCache.Clear();
            _columnLoading.Clear();

            GridData.ItemsSource = null;
            TxtCurrentObject.Text = "未选择";
            TxtRowCount.Text = "0 行";
            TxtPageInfo.Text = "第 1 页";
            TxtPagerHint.Text = string.Empty;
            SqlResultTabs.Items.Clear();
            TxtSqlMessage.Text = "就绪。";
            TxtObjectFilter.Text = string.Empty;

            if (session == null)
            {
                _databases = new List<string>();
                _currentDatabase = null;
                ClearDatabaseSelector();
            }

            RebuildTree();
            UpdatePagerUi();
            UpdateUiState();
            ApplyKindUi(session == null ? (CmbKind.SelectedItem as DatabaseKind) : session.Kind);

            if (old != null)
            {
                try { old.Dispose(); }
                catch { }
            }
        }

        private void CloseSession()
        {
            ReplaceSession(null);
            _currentInput = null;
            _currentDatabase = null;
            _preferredDatabase = null;
            _databases = new List<string>();
            _databaseSelectionRequired = false;
            TxtServerVersion.Text = string.Empty;
            SetStatus(false, "未连接");
            UpdateUiState();
        }

        private async Task LoadObjectsAsync()
        {
            if (_session == null)
            {
                return;
            }

            SetBusy(true, "正在读取数据库对象…");
            try
            {
                var session = _session;
                var tables = await Task.Run(() => session.GetTables());
                var views = await Task.Run(() => session.GetViews());
                _tables = tables;
                _views = views;
                _databaseSelectionRequired = false;
                RebuildTree();
                AppendLog("对象加载完成：表 " + _tables.Count + " 个，视图 " + _views.Count + " 个。");
            }
            catch (Exception ex)
            {
                AppendLog("读取数据库对象失败：" + ex.Message);
                ShowError("读取数据库对象失败", ex.Message);
            }
            finally
            {
                SetBusy(false);
            }
        }

        // ==================== 保存的连接配置 ====================

        private void ReloadProfiles()
        {
            _suppressProfileSelection = true;
            _profiles.Clear();
            foreach (var profile in _store.Load().OrderByDescending(p => p.LastUsedAt))
            {
                _profiles.Add(profile);
            }
            CmbProfiles.SelectedItem = null;
            _suppressProfileSelection = false;
        }

        private void CmbProfiles_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressProfileSelection)
            {
                return;
            }

            var profile = CmbProfiles.SelectedItem as ConnectionProfile;
            if (profile != null)
            {
                FillForm(profile);
            }
        }

        private void FillForm(ConnectionProfile profile)
        {
            var kind = DatabaseCatalog.Find(profile.KindKey);
            if (kind == null)
            {
                AppendLog("该配置引用了不支持的数据库类型：" + profile.KindKey);
                return;
            }

            if (_session != null)
            {
                CloseSession();
                AppendLog("已断开当前连接。");
            }

            _suppressKindChange = true;
            CmbKind.SelectedItem = kind;
            _suppressKindChange = false;

            TxtHost.Text = profile.Host ?? string.Empty;
            TxtPort.Text = profile.Port > 0
                ? profile.Port.ToString(CultureInfo.InvariantCulture)
                : (kind.DefaultPort > 0 ? kind.DefaultPort.ToString(CultureInfo.InvariantCulture) : string.Empty);
            TxtDatabase.Text = kind.Key == "Oracle" ? (profile.Database ?? string.Empty) : string.Empty;
            TxtUser.Text = profile.User ?? string.Empty;
            TxtFile.Text = profile.FilePath ?? string.Empty;

            ChkUseCustom.IsChecked = profile.UseCustomString;
            TxtCustomConn.Text = profile.CustomConnectionString ?? string.Empty;
            SetPassword(profile.Password);

            _preferredDatabase = kind.SupportsDatabaseSwitch && !string.IsNullOrWhiteSpace(profile.Database)
                ? profile.Database.Trim()
                : null;

            ApplyKindUi(kind);
            _activeProfile = profile;
            AppendLog("已载入配置：" + profile.DisplayName + "。点击“连接数据库”开始连接。");
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            var kind = CmbKind.SelectedItem as DatabaseKind;
            if (kind == null)
            {
                return;
            }

            var profile = CmbProfiles.SelectedItem as ConnectionProfile;
            var isNew = profile == null;
            if (isNew)
            {
                profile = new ConnectionProfile { Id = Guid.NewGuid().ToString("N") };
            }

            var input = BuildInput(kind);
            profile.KindKey = kind.Key;
            profile.Host = input.Host;
            profile.Port = input.Port;
            profile.Database = input.Database;
            profile.User = input.User;
            profile.FilePath = input.FilePath;
            profile.UseCustomString = input.UseCustomString;
            profile.Password = input.Password;
            profile.CustomConnectionString = input.CustomConnectionString;
            profile.LastUsedAt = DateTime.Now;
            if (string.IsNullOrWhiteSpace(profile.Name))
            {
                profile.Name = profile.BuildAutoName();
            }

            _profiles.Remove(profile);
            _profiles.Insert(0, profile);

            try
            {
                _store.Save(_profiles);
            }
            catch (Exception ex)
            {
                ShowError("保存配置失败", ex.Message);
                return;
            }

            _suppressProfileSelection = true;
            CmbProfiles.SelectedItem = profile;
            _suppressProfileSelection = false;
            _activeProfile = profile;
            AppendLog((isNew ? "已保存新配置：" : "已更新配置：") + profile.DisplayName);
        }

        private void BtnDeleteProfile_Click(object sender, RoutedEventArgs e)
        {
            var profile = CmbProfiles.SelectedItem as ConnectionProfile;
            if (profile == null)
            {
                Warn("请先选择要删除的配置。");
                return;
            }

            var confirm = MessageBox.Show(
                this,
                "确定要删除配置“" + profile.DisplayName + "”吗？",
                "删除配置",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes)
            {
                return;
            }

            _profiles.Remove(profile);
            try
            {
                _store.Save(_profiles);
            }
            catch (Exception ex)
            {
                ShowError("删除配置失败", ex.Message);
                return;
            }

            _suppressProfileSelection = true;
            CmbProfiles.SelectedItem = null;
            _suppressProfileSelection = false;

            if (ReferenceEquals(_activeProfile, profile))
            {
                _activeProfile = null;
            }
            AppendLog("已删除配置：" + profile.DisplayName);
        }

        // ==================== 数据库对象树 ====================

        private async void BtnRefreshObjects_Click(object sender, RoutedEventArgs e)
        {
            if (_session == null)
            {
                Warn("请先连接数据库。");
                return;
            }
            if (_databaseSelectionRequired)
            {
                Warn("请先选择数据库。");
                return;
            }
            await LoadObjectsAsync();
        }

        private void TxtObjectFilter_TextChanged(object sender, TextChangedEventArgs e)
        {
            RebuildTree();
        }

        private void RebuildTree()
        {
            TreeObjects.Items.Clear();

            if (_session == null)
            {
                TxtTreeEmpty.Text = "连接数据库后，将在此列出表与视图。";
                TxtTreeEmpty.Visibility = Visibility.Visible;
                return;
            }

            if (_databaseSelectionRequired)
            {
                TxtTreeEmpty.Text = "已连接，请在上方选择数据库以加载表与视图。";
                TxtTreeEmpty.Visibility = Visibility.Visible;
                return;
            }

            var filter = (TxtObjectFilter.Text ?? string.Empty).Trim();
            Func<DatabaseObjectInfo, bool> match = info =>
                filter.Length == 0 || info.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;

            var tables = _tables.Where(match).ToList();
            var views = _views.Where(match).ToList();

            if (tables.Count > 0)
            {
                TreeObjects.Items.Add(BuildGroup("表", tables));
            }
            if (views.Count > 0)
            {
                TreeObjects.Items.Add(BuildGroup("视图", views));
            }

            var empty = tables.Count == 0 && views.Count == 0;
            TxtTreeEmpty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            TxtTreeEmpty.Text = empty
                ? (filter.Length == 0 ? "数据库中没有可显示的表或视图。" : "没有匹配“" + filter + "”的对象。")
                : string.Empty;
        }

        private TreeViewItem BuildGroup(string title, List<DatabaseObjectInfo> items)
        {
            var header = new TextBlock
            {
                Text = title + " (" + items.Count + ")",
                FontWeight = FontWeights.SemiBold
            };
            header.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");

            var group = new TreeViewItem
            {
                IsExpanded = true,
                Header = header
            };

            foreach (var info in items)
            {
                group.Items.Add(BuildObjectNode(info));
            }
            return group;
        }

        private TreeViewItem BuildObjectNode(DatabaseObjectInfo info)
        {
            var header = new StackPanel { Orientation = Orientation.Horizontal };
            var icon = new TextBlock
            {
                Text = info.IsView ? "◫" : "▦",
                FontFamily = new FontFamily("Consolas"),
                Margin = new Thickness(0, 0, 6, 0)
            };
            icon.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
            header.Children.Add(icon);
            header.Children.Add(new TextBlock
            {
                Text = info.Name,
                TextTrimming = TextTrimming.CharacterEllipsis
            });

            var node = new TreeViewItem
            {
                Header = header,
                Tag = info
            };
            if (!string.IsNullOrWhiteSpace(info.Description))
            {
                node.ToolTip = info.Description;
            }
            AutomationProperties.SetName(node, info.Name);

            var menu = new ContextMenu();
            var openItem = new MenuItem { Header = "浏览数据" };
            openItem.Click += (s, e) => OpenObject(info);
            var queryItem = new MenuItem { Header = "生成查询 SQL" };
            queryItem.Click += (s, e) => OpenQueryFor(info);
            var copyItem = new MenuItem { Header = "复制名称" };
            copyItem.Click += (s, e) =>
            {
                try
                {
                    Clipboard.SetText(info.Name);
                    AppendLog("已复制表名：" + info.Name);
                }
                catch (Exception ex)
                {
                    AppendLog("复制失败：" + ex.Message);
                }
            };
            menu.Items.Add(openItem);
            menu.Items.Add(queryItem);
            menu.Items.Add(copyItem);
            node.ContextMenu = menu;

            node.MouseDoubleClick += (s, e) =>
            {
                e.Handled = true;
                OpenObject(info);
            };

            return node;
        }

        private async void TreeObjects_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            var node = e.NewValue as TreeViewItem;
            var info = node == null ? null : node.Tag as DatabaseObjectInfo;
            if (info != null)
            {
                await OpenObjectAsync(info);
            }
        }

        private async void OpenObject(DatabaseObjectInfo info)
        {
            await OpenObjectAsync(info);
        }

        private async Task OpenObjectAsync(DatabaseObjectInfo info)
        {
            if (info == null || _session == null)
            {
                return;
            }

            _currentObject = info;
            _pageIndex = 0;
            _rowCount = -1;
            _currentTable = null;
            GridData.ItemsSource = null;
            TxtCurrentObject.Text = info.Name + (info.IsView ? "（视图）" : string.Empty);
            MainTabs.SelectedIndex = 0;
            EnsureColumnsLoaded(new[] { info.Name });

            await LoadTablePageAsync(true);
        }

        private void OpenQueryFor(DatabaseObjectInfo info)
        {
            if (_session == null || info == null)
            {
                return;
            }

            CloseAutoComplete();
            EnsureColumnsLoaded(new[] { info.Name });
            _autoCompleteSuppressed = true;
            try
            {
                TxtSql.Text = _session.GeneratePreviewSql(info.Name);
            }
            finally
            {
                _autoCompleteSuppressed = false;
            }
            MainTabs.SelectedIndex = 1;
            TxtSql.Focus();
            TxtSql.CaretIndex = TxtSql.Text.Length;
            AppendLog("已生成查询 SQL：" + info.Name);
        }

        // ==================== 数据分页浏览 ====================

        private async Task LoadTablePageAsync(bool resetPage)
        {
            if (_session == null || _currentObject == null || _busy)
            {
                return;
            }

            if (resetPage)
            {
                _pageIndex = 0;
            }

            var session = _session;
            var obj = _currentObject;
            var page = _pageIndex;
            var size = _pageSize;
            var needCount = _rowCount < 0;

            SetBusy(true, "正在读取数据…");
            try
            {
                var table = await Task.Run(() => session.GetTablePage(obj.Name, page, size));
                _currentTable = table;
                GridData.ItemsSource = table.DefaultView;

                if (needCount)
                {
                    try
                    {
                        _rowCount = await Task.Run(() => session.GetRowCount(obj.Name));
                    }
                    catch
                    {
                        _rowCount = -1;
                    }
                }

                UpdatePagerUi();
                SetStatus(true, obj.Name + " · 第 " + (page + 1) + " 页");
                AppendLog("读取 " + obj.Name + " 第 " + (page + 1) + " 页，返回 " + table.Rows.Count + " 行。");
            }
            catch (Exception ex)
            {
                SetStatus(false, "读取数据失败");
                AppendLog("读取数据失败：" + ex.Message);
                ShowError("读取数据失败", ex.Message);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private int TotalPages
        {
            get { return _pageSize <= 0 || _rowCount <= 0 ? 0 : (int)((_rowCount + _pageSize - 1) / _pageSize); }
        }

        private int LastPageIndex
        {
            get { return Math.Max(0, TotalPages - 1); }
        }

        private bool CanGoNext()
        {
            if (_session == null || _currentObject == null || _session.Kind.PagingMode == DataPagingMode.TopOnly)
            {
                return false;
            }
            if (_rowCount >= 0)
            {
                return (long)(_pageIndex + 1) * _pageSize < _rowCount;
            }
            return _currentTable != null && _currentTable.Rows.Count >= _pageSize;
        }

        private void UpdatePagerUi()
        {
            var hasTable = _session != null && _currentObject != null;
            BtnFirst.IsEnabled = hasTable && _pageIndex > 0;
            BtnPrev.IsEnabled = hasTable && _pageIndex > 0;
            BtnNext.IsEnabled = hasTable && CanGoNext();
            BtnLast.IsEnabled = hasTable && _rowCount > 0 && _pageIndex < LastPageIndex;

            TxtPageInfo.Text = hasTable
                ? "第 " + (_pageIndex + 1) + " 页" + (_rowCount > 0 ? " / 共 " + TotalPages + " 页" : string.Empty)
                : "第 1 页";

            if (_rowCount >= 0)
            {
                TxtRowCount.Text = _rowCount.ToString("N0", CultureInfo.InvariantCulture) + " 行";
            }
            else if (_currentTable != null)
            {
                TxtRowCount.Text = "当前 " + _currentTable.Rows.Count + " 行";
            }
            else
            {
                TxtRowCount.Text = "0 行";
            }

            TxtPagerHint.Text = _session != null && _session.Kind.PagingMode == DataPagingMode.TopOnly
                ? "该数据库仅支持首页预览，翻页请使用 SQL 查询"
                : string.Empty;
        }

        private async void BtnFirst_Click(object sender, RoutedEventArgs e)
        {
            if (_pageIndex == 0)
            {
                return;
            }
            _pageIndex = 0;
            await LoadTablePageAsync(false);
        }

        private async void BtnPrev_Click(object sender, RoutedEventArgs e)
        {
            if (_pageIndex == 0)
            {
                return;
            }
            _pageIndex--;
            await LoadTablePageAsync(false);
        }

        private async void BtnNext_Click(object sender, RoutedEventArgs e)
        {
            if (!CanGoNext())
            {
                return;
            }
            _pageIndex++;
            await LoadTablePageAsync(false);
        }

        private async void BtnLast_Click(object sender, RoutedEventArgs e)
        {
            if (_rowCount <= 0)
            {
                return;
            }
            _pageIndex = LastPageIndex;
            await LoadTablePageAsync(false);
        }

        private async void CmbPageSize_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!(CmbPageSize.SelectedItem is int))
            {
                return;
            }

            _pageSize = (int)CmbPageSize.SelectedItem;
            if (_session != null && _currentObject != null && !_busy)
            {
                await LoadTablePageAsync(true);
            }
        }

        private async void BtnRefreshData_Click(object sender, RoutedEventArgs e)
        {
            if (_session == null || _currentObject == null)
            {
                return;
            }
            _rowCount = -1;
            await LoadTablePageAsync(false);
        }

        // ==================== 导出 ====================

        private void BtnExportCsv_Click(object sender, RoutedEventArgs e)
        {
            ExportCurrentTable();
        }

        private void ExportCurrentTable()
        {
            if (_currentTable == null)
            {
                Warn("当前没有可导出的数据。");
                return;
            }
            ExportView(_currentTable.DefaultView, _currentObject == null ? "data" : _currentObject.Name);
        }

        private void ExportView(DataView view, string name)
        {
            var dialog = new SaveFileDialog
            {
                Title = "导出为 CSV",
                Filter = "CSV 文件|*.csv|所有文件|*.*",
                FileName = SafeFileName(name) + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".csv"
            };

            if (dialog.ShowDialog(this) != true)
            {
                return;
            }

            try
            {
                CsvExporter.Export(view, dialog.FileName);
                AppendLog("已导出：" + dialog.FileName);
            }
            catch (Exception ex)
            {
                ShowError("导出失败", ex.Message);
            }
        }

        private static string SafeFileName(string name)
        {
            name = string.IsNullOrWhiteSpace(name) ? "data" : name.Trim();
            foreach (var ch in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(ch, '_');
            }
            return name;
        }

        private void MenuCopyCells_Click(object sender, RoutedEventArgs e)
        {
            CopySelectedCells(GridData);
        }

        private void CopySelectedCells(DataGrid grid)
        {
            if (grid == null || grid.SelectedCells.Count == 0)
            {
                AppendLog("没有选中的单元格。");
                return;
            }

            var lines = grid.SelectedCells
                .GroupBy(cell => grid.Items.IndexOf(cell.Item))
                .OrderBy(group => group.Key)
                .Select(group => string.Join("\t", group
                    .OrderBy(cell => cell.Column.DisplayIndex)
                    .Select(cell => FormatCell(cell))));

            try
            {
                Clipboard.SetText(string.Join(Environment.NewLine, lines));
                AppendLog("已复制 " + grid.SelectedCells.Count + " 个单元格。");
            }
            catch (Exception ex)
            {
                ShowError("复制失败", ex.Message);
            }
        }

        private static string FormatCell(DataGridCellInfo cell)
        {
            var rowView = cell.Item as DataRowView;
            var column = cell.Column;
            if (rowView == null || column == null)
            {
                return string.Empty;
            }

            var name = column.Header as string;
            if (string.IsNullOrEmpty(name) || !rowView.Row.Table.Columns.Contains(name))
            {
                return string.Empty;
            }

            var value = rowView.Row[name];
            return value == null || value == DBNull.Value
                ? string.Empty
                : Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        // ==================== SQL 查询 ====================

        private async void BtnExecute_Click(object sender, RoutedEventArgs e)
        {
            await ExecuteSqlAsync();
        }

        private void BtnClearSql_Click(object sender, RoutedEventArgs e)
        {
            TxtSql.Clear();
            TxtSql.Focus();
        }

        private async Task ExecuteSqlAsync()
        {
            CloseAutoComplete();

            if (_session == null)
            {
                Warn("请先连接数据库。");
                return;
            }
            if (_busy)
            {
                return;
            }

            var sql = TxtSql.SelectionLength > 0 ? TxtSql.SelectedText : TxtSql.Text;
            if (string.IsNullOrWhiteSpace(sql))
            {
                Warn("请输入要执行的 SQL。");
                TxtSql.Focus();
                return;
            }

            var session = _session;
            SetBusy(true, "正在执行 SQL…");
            try
            {
                var result = await Task.Run(() => session.ExecuteBatch(sql));
                RenderSqlResults(result);

                var message = "执行完成 · 用时 " + result.ElapsedMilliseconds + " ms";
                if (result.ResultSets.Count > 0)
                {
                    message += " · " + result.ResultSets.Count + " 个结果集";
                }
                if (result.AffectedRows > 0)
                {
                    message += " · 影响 " + result.AffectedRows + " 行";
                }
                TxtSqlMessage.Text = message;
                AppendLog(message);
                SetStatus(true, "SQL 执行完成");
            }
            catch (Exception ex)
            {
                TxtSqlMessage.Text = "执行失败：" + ex.Message;
                AppendLog("SQL 执行失败：" + ex.Message);
                SetStatus(false, "SQL 执行失败");
            }
            finally
            {
                SetBusy(false);
            }
        }

        private void RenderSqlResults(QueryBatchResult result)
        {
            SqlResultTabs.Items.Clear();

            if (result.ResultSets.Count == 0)
            {
                var message = new TextBlock
                {
                    Text = "执行完成。\n影响行数：" + result.AffectedRows + "\n用时：" + result.ElapsedMilliseconds + " ms",
                    Margin = new Thickness(14),
                    TextWrapping = TextWrapping.Wrap
                };
                message.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                SqlResultTabs.Items.Add(new TabItem { Header = "消息", Content = message });
            }
            else
            {
                foreach (var resultSet in result.ResultSets)
                {
                    var view = resultSet.Table.DefaultView;
                    var title = resultSet.Title;
                    var grid = new DataGrid
                    {
                        ItemsSource = view,
                        Margin = new Thickness(6)
                    };

                    var menu = new ContextMenu();
                    var copyItem = new MenuItem { Header = "复制所选单元格" };
                    copyItem.Click += (s, e) => CopySelectedCells(grid);
                    var exportItem = new MenuItem { Header = "导出为 CSV…" };
                    exportItem.Click += (s, e) => ExportView(view, title);
                    menu.Items.Add(copyItem);
                    menu.Items.Add(exportItem);
                    grid.ContextMenu = menu;

                    SqlResultTabs.Items.Add(new TabItem
                    {
                        Header = title + " (" + resultSet.Table.Rows.Count + " 行)",
                        Content = grid
                    });
                }
            }

            SqlResultTabs.SelectedIndex = 0;
        }

        // ==================== SQL 自动补全 ====================

        private void TxtSql_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_autoCompleteSuppressed)
            {
                return;
            }

            _autoCompleteRefreshBlocked = false;
            Dispatcher.BeginInvoke(new Action(() => UpdateAutoComplete(false)), DispatcherPriority.Background);
        }

        private void TxtSql_SelectionChanged(object sender, RoutedEventArgs e)
        {
            if (_autoCompleteSuppressed)
            {
                return;
            }

            if (AutoCompletePopup.IsOpen)
            {
                UpdateAutoComplete(true);
            }
        }

        private void TxtSql_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
            var shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;

            if (AutoCompletePopup.IsOpen && AutoCompleteList.Items.Count > 0)
            {
                if (e.Key == Key.Down)
                {
                    MoveAutoCompleteSelection(1);
                    e.Handled = true;
                    return;
                }
                if (e.Key == Key.Up)
                {
                    MoveAutoCompleteSelection(-1);
                    e.Handled = true;
                    return;
                }
                if (e.Key == Key.Enter && !ctrl && !shift)
                {
                    CommitAutoCompleteSelection();
                    e.Handled = true;
                    return;
                }
                if (e.Key == Key.Tab && !ctrl && !shift)
                {
                    CommitAutoCompleteSelection();
                    e.Handled = true;
                    return;
                }
                if (e.Key == Key.Escape)
                {
                    _autoCompleteRefreshBlocked = true;
                    CloseAutoComplete();
                    e.Handled = true;
                    return;
                }
            }

            if (ctrl && e.Key == Key.Space)
            {
                UpdateAutoComplete(false, true);
                e.Handled = true;
            }
        }

        private void UpdateAutoComplete(bool keepSelection, bool force = false)
        {
            if (_autoCompleteSuppressed)
            {
                return;
            }

            if (!TxtSql.IsKeyboardFocusWithin)
            {
                CloseAutoComplete();
                return;
            }

            var text = TxtSql.Text ?? string.Empty;
            var caret = TxtSql.CaretIndex;
            if (caret < 0 || caret > text.Length || IsCaretInLiteralOrComment(text, caret))
            {
                CloseAutoComplete();
                return;
            }

            var start = caret;
            while (start > 0 && IsSqlTokenChar(text[start - 1]))
            {
                start--;
            }

            if (start > 0 && text[start - 1] == '@')
            {
                CloseAutoComplete();
                return;
            }

            var token = text.Substring(start, caret - start);
            var lastDot = token.LastIndexOf('.');
            var partialStart = start + lastDot + 1;
            var partial = text.Substring(partialStart, caret - partialStart);
            var qualifier = lastDot > 0 ? token.Substring(0, lastDot) : null;

            var context = BuildCompletionContext(text, caret, start, qualifier);

            if (!force)
            {
                if (partial.Length == 0)
                {
                    if (!context.AutoShow)
                    {
                        CloseAutoComplete();
                        return;
                    }
                }
                else if (partial.Length < 2 && partial[0] < 128 &&
                         !context.PreferColumns && !context.PreferObjects)
                {
                    CloseAutoComplete();
                    return;
                }
            }

            PrefetchColumnsFor(context);

            var suggestions = BuildSuggestions(partial, context);
            if (suggestions.Count == 0)
            {
                CloseAutoComplete();
                return;
            }

            var previous = keepSelection
                ? (AutoCompleteList.SelectedItem as SqlSuggestion)
                : null;

            _autoCompleteStart = partialStart;
            _autoCompleteLength = partial.Length;

            AutoCompleteList.ItemsSource = null;
            AutoCompleteList.ItemsSource = suggestions;

            var restore = previous == null
                ? null
                : suggestions.FirstOrDefault(s =>
                    string.Equals(s.Text, previous.Text, StringComparison.Ordinal));
            AutoCompleteList.SelectedItem = restore ?? suggestions[0];
            AutoCompleteList.ScrollIntoView(AutoCompleteList.SelectedItem);

            PositionAutoComplete(caret);
            if (!AutoCompletePopup.IsOpen)
            {
                AutoCompletePopup.IsOpen = true;
            }
        }

        private SqlCompletionContext BuildCompletionContext(
            string text, int caret, int tokenStart, string qualifier)
        {
            var context = new SqlCompletionContext { Qualifier = qualifier };

            var statementStart = text.LastIndexOf(';', Math.Max(0, caret - 1));
            statementStart = statementStart < 0 ? 0 : statementStart + 1;

            var contextEnd = Math.Max(statementStart, tokenStart);
            var prefix = StripSqlNoise(text.Substring(statementStart, contextEnd - statementStart));
            var tokens = TokenizeSql(prefix);
            context.Tables.AddRange(ParseTableReferences(tokens));

            if (context.Qualifier != null)
            {
                context.PreferColumns = true;
                context.AutoShow = true;
                return context;
            }

            string lastKeyword = null;
            string lastSignificant = null;
            foreach (var token in tokens)
            {
                if (token.Length == 0)
                {
                    continue;
                }

                lastSignificant = token;
                var upper = token.ToUpperInvariant();
                if (ObjectContextKeywords.Contains(upper) || ColumnContextKeywords.Contains(upper))
                {
                    lastKeyword = upper;
                }
            }

            if (lastKeyword != null)
            {
                context.PreferObjects = ObjectContextKeywords.Contains(lastKeyword);
                context.PreferColumns = ColumnContextKeywords.Contains(lastKeyword);
            }

            context.AtSelectStart =
                string.Equals(lastSignificant, "SELECT", StringComparison.OrdinalIgnoreCase);

            if (lastSignificant == null)
            {
                return context;
            }

            var atKeyword = lastKeyword != null &&
                string.Equals(lastSignificant, lastKeyword, StringComparison.OrdinalIgnoreCase);
            var atCommaOrParen = lastSignificant == "," || lastSignificant == "(";
            var atOperator = lastSignificant == "=" || lastSignificant == "!" ||
                             lastSignificant == "<" || lastSignificant == ">";
            if ((atKeyword || atCommaOrParen || atOperator) &&
                (context.PreferColumns || context.PreferObjects))
            {
                context.AutoShow = true;
            }

            return context;
        }

        private static string StripSqlNoise(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return text ?? string.Empty;
            }

            var builder = new StringBuilder(text.Length);
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (c == '-' && i + 1 < text.Length && text[i + 1] == '-')
                {
                    while (i < text.Length && text[i] != '\n')
                    {
                        i++;
                    }
                    builder.Append(' ');
                    continue;
                }

                if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
                {
                    i += 2;
                    while (i + 1 < text.Length && !(text[i] == '*' && text[i + 1] == '/'))
                    {
                        i++;
                    }
                    i++;
                    builder.Append(' ');
                    continue;
                }

                if (c == '\'')
                {
                    i++;
                    while (i < text.Length)
                    {
                        if (text[i] == '\'')
                        {
                            if (i + 1 < text.Length && text[i + 1] == '\'')
                            {
                                i += 2;
                                continue;
                            }
                            break;
                        }
                        i++;
                    }
                    builder.Append(' ');
                    continue;
                }

                builder.Append(c);
            }

            return builder.ToString();
        }

        private static List<string> TokenizeSql(string text)
        {
            var tokens = new List<string>();
            if (string.IsNullOrEmpty(text))
            {
                return tokens;
            }

            foreach (Match match in SqlTokenRegex.Matches(text))
            {
                if (match.Groups["id"].Success)
                {
                    var identifier = NormalizeIdentifier(match.Groups["id"].Value);
                    if (identifier.Length > 0)
                    {
                        tokens.Add(identifier);
                    }
                }
                else if (match.Groups["punct"].Success)
                {
                    tokens.Add(match.Groups["punct"].Value);
                }
            }

            return tokens;
        }

        private static string NormalizeIdentifier(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            var builder = new StringBuilder(value.Length);
            foreach (var c in value)
            {
                if (c == '[' || c == ']' || c == '`' || c == '"' || char.IsWhiteSpace(c))
                {
                    continue;
                }
                builder.Append(c);
            }
            return builder.ToString();
        }

        private static bool IsPunctuation(string token)
        {
            return token != null && token.Length == 1 && "(),;.=<>!+*/%-".IndexOf(token[0]) >= 0;
        }

        private static bool IsAliasCandidate(string token)
        {
            return !string.IsNullOrEmpty(token) &&
                   !IsPunctuation(token) &&
                   !ReservedWords.Contains(token);
        }

        private static List<TableReference> ParseTableReferences(List<string> tokens)
        {
            var refs = new List<TableReference>();
            for (var i = 0; i < tokens.Count; i++)
            {
                var token = tokens[i];
                if (string.Equals(token, "FROM", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(token, "INTO", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(token, "UPDATE", StringComparison.OrdinalIgnoreCase))
                {
                    i = ParseTableList(tokens, i + 1, refs, true);
                }
                else if (string.Equals(token, "JOIN", StringComparison.OrdinalIgnoreCase))
                {
                    i = ParseTableList(tokens, i + 1, refs, false);
                }
            }
            return refs;
        }

        private static int ParseTableList(
            List<string> tokens, int index, List<TableReference> refs, bool allowCommaList)
        {
            var consumed = index - 1;
            while (index < tokens.Count)
            {
                var tableToken = tokens[index];
                if (IsPunctuation(tableToken) || ClauseBoundaryKeywords.Contains(tableToken))
                {
                    return consumed;
                }

                index++;
                consumed = index - 1;

                string alias = null;
                if (index < tokens.Count &&
                    string.Equals(tokens[index], "AS", StringComparison.OrdinalIgnoreCase))
                {
                    index++;
                    consumed = index - 1;
                    if (index < tokens.Count && IsAliasCandidate(tokens[index]))
                    {
                        alias = tokens[index];
                        index++;
                        consumed = index - 1;
                    }
                }
                else if (index < tokens.Count && IsAliasCandidate(tokens[index]))
                {
                    alias = tokens[index];
                    index++;
                    consumed = index - 1;
                }

                refs.Add(new TableReference(tableToken, alias));

                if (!allowCommaList || index >= tokens.Count || tokens[index] != ",")
                {
                    return consumed;
                }

                index++;
                consumed = index - 1;
            }

            return consumed;
        }

        private void PrefetchColumnsFor(SqlCompletionContext context)
        {
            if (_session == null)
            {
                return;
            }

            var names = new List<string>();
            if (!string.IsNullOrEmpty(context.Qualifier))
            {
                names.Add(ResolveTableName(context.Tables, context.Qualifier) ?? context.Qualifier);
            }
            else if (context.PreferColumns)
            {
                foreach (var reference in context.Tables)
                {
                    names.Add(reference.Name);
                }
                if (names.Count == 0 && _currentObject != null)
                {
                    names.Add(_currentObject.Name);
                }
            }

            EnsureColumnsLoaded(names);
        }

        private static string ResolveTableName(List<TableReference> tables, string qualifier)
        {
            foreach (var reference in tables)
            {
                if (string.Equals(reference.Alias, qualifier, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(reference.Name, qualifier, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(reference.ShortName, qualifier, StringComparison.OrdinalIgnoreCase))
                {
                    return reference.Name;
                }
            }
            return null;
        }

        private void EnsureColumnsLoaded(IEnumerable<string> tableNames)
        {
            var session = _session;
            if (session == null || tableNames == null)
            {
                return;
            }

            foreach (var raw in tableNames)
            {
                var name = NormalizeIdentifier(raw ?? string.Empty);
                if (name.Length == 0 ||
                    _columnCache.ContainsKey(name) ||
                    _columnLoading.Contains(name))
                {
                    continue;
                }

                _columnLoading.Add(name);
                var cacheKey = name;
                Task.Run(() =>
                {
                    using (var probe = new DatabaseSession(session.Kind, session.ConnectionString))
                    {
                        return probe.GetColumnNames(name);
                    }
                }).ContinueWith(task =>
                {
                    var columns = task.IsFaulted || task.IsCanceled || task.Result == null
                        ? new List<string>()
                        : task.Result;

                    var dispatcher = Dispatcher;
                    if (dispatcher == null || dispatcher.HasShutdownStarted)
                    {
                        return;
                    }

                    dispatcher.BeginInvoke(new Action(() =>
                    {
                        _columnLoading.Remove(cacheKey);
                        if (!ReferenceEquals(_session, session))
                        {
                            return;
                        }
                        _columnCache[cacheKey] = columns;
                        if (!_autoCompleteRefreshBlocked &&
                            TxtSql.IsKeyboardFocusWithin &&
                            MainTabs.SelectedIndex == 1)
                        {
                            UpdateAutoComplete(true);
                        }
                    }));
                }, TaskScheduler.Default);
            }
        }

        private List<string> GetCachedColumns(string tableName)
        {
            var name = NormalizeIdentifier(tableName ?? string.Empty);
            List<string> columns;
            return name.Length > 0 && _columnCache.TryGetValue(name, out columns)
                ? columns
                : new List<string>();
        }

        private List<ColumnCandidate> BuildColumnCandidates(SqlCompletionContext context)
        {
            var candidates = new List<ColumnCandidate>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrEmpty(context.Qualifier))
            {
                var table = ResolveTableName(context.Tables, context.Qualifier) ?? context.Qualifier;
                foreach (var column in GetCachedColumns(table))
                {
                    if (seen.Add(column))
                    {
                        candidates.Add(new ColumnCandidate(column, column));
                    }
                }
                return candidates;
            }

            var refs = context.Tables;
            if (refs.Count == 0 && _currentObject != null && !string.IsNullOrWhiteSpace(_currentObject.Name))
            {
                refs = new List<TableReference> { new TableReference(_currentObject.Name, null) };
            }

            var multiple = refs.Count > 1;
            foreach (var reference in refs)
            {
                var prefix = string.IsNullOrEmpty(reference.Alias)
                    ? reference.ShortName
                    : reference.Alias;
                foreach (var column in GetCachedColumns(reference.Name))
                {
                    var display = multiple ? prefix + "." + column : column;
                    if (seen.Add(display))
                    {
                        candidates.Add(new ColumnCandidate(display, column));
                    }
                }
            }

            return candidates;
        }

        private List<SqlSuggestion> BuildSuggestions(string partial, SqlCompletionContext context)
        {
            const int max = 14;
            var result = new List<SqlSuggestion>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (context.PreferColumns)
            {
                foreach (var candidate in BuildColumnCandidates(context))
                {
                    if (result.Count >= max)
                    {
                        break;
                    }
                    if (!candidate.Display.StartsWith(partial, StringComparison.OrdinalIgnoreCase) &&
                        !candidate.Column.StartsWith(partial, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    if (seen.Add(candidate.Display))
                    {
                        result.Add(new SqlSuggestion(candidate.Display, "字段"));
                    }
                }

                if (context.AtSelectStart && partial.Length == 0 && result.Count < max)
                {
                    result.Add(new SqlSuggestion("*", "全部字段"));
                }

                if (partial.Length > 0)
                {
                    AddKeywordSuggestions(result, seen, partial,
                        result.Count >= max - 2 ? 2 : 4);
                    if (result.Count < max)
                    {
                        AddObjectSuggestions(result, seen, _tables, partial, "表", max);
                    }
                    if (result.Count < max)
                    {
                        AddObjectSuggestions(result, seen, _views, partial, "视图", max);
                    }
                }
            }
            else
            {
                AddObjectSuggestions(result, seen, _tables, partial, "表", max);
                AddObjectSuggestions(result, seen, _views, partial, "视图", max);
                AddKeywordSuggestions(result, seen, partial,
                    result.Count == 0 ? max : 6);
            }

            return result;
        }

        private static void AddKeywordSuggestions(
            List<SqlSuggestion> result,
            HashSet<string> seen,
            string partial,
            int limit)
        {
            var added = 0;
            foreach (var keyword in SqlKeywords)
            {
                if (added >= limit || result.Count >= 14)
                {
                    break;
                }
                if (!keyword.StartsWith(partial, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (seen.Add(keyword))
                {
                    result.Add(new SqlSuggestion(keyword, "关键字"));
                    added++;
                }
            }
        }

        private static void AddObjectSuggestions(
            List<SqlSuggestion> result,
            HashSet<string> seen,
            List<DatabaseObjectInfo> items,
            string partial,
            string category,
            int max)
        {
            foreach (var item in items)
            {
                if (result.Count >= max)
                {
                    break;
                }
                if (item == null || string.IsNullOrWhiteSpace(item.Name))
                {
                    continue;
                }

                var name = item.Name;
                var lastDot = name.LastIndexOf('.');
                var shortName = lastDot >= 0 ? name.Substring(lastDot + 1) : name;
                if (!name.StartsWith(partial, StringComparison.OrdinalIgnoreCase) &&
                    !shortName.StartsWith(partial, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (seen.Add(name))
                {
                    result.Add(new SqlSuggestion(name, category));
                }
            }
        }

        private static bool IsSqlTokenChar(char c)
        {
            return char.IsLetterOrDigit(c) || c == '_' || c == '$' || c == '#' || c == '.';
        }

        private static bool IsCaretInLiteralOrComment(string text, int caret)
        {
            var lineStart = caret <= 0 ? 0 : text.LastIndexOf('\n', caret - 1);
            lineStart = lineStart < 0 ? 0 : lineStart + 1;

            var inSingle = false;
            var inDouble = false;
            for (var i = lineStart; i < caret; i++)
            {
                var c = text[i];
                if (!inSingle && !inDouble && c == '-' && i + 1 < caret && text[i + 1] == '-')
                {
                    return true;
                }

                if (c == '\'' && !inDouble)
                {
                    if (inSingle && i + 1 < caret && text[i + 1] == '\'')
                    {
                        i++;
                        continue;
                    }
                    inSingle = !inSingle;
                }
                else if (c == '"' && !inSingle)
                {
                    if (inDouble && i + 1 < caret && text[i + 1] == '"')
                    {
                        i++;
                        continue;
                    }
                    inDouble = !inDouble;
                }
            }

            return inSingle || inDouble;
        }

        private void PositionAutoComplete(int caret)
        {
            AutoCompletePopup.PlacementTarget = TxtSql;
            AutoCompletePopup.Placement = System.Windows.Controls.Primitives.PlacementMode.Relative;

            try
            {
                var text = TxtSql.Text ?? string.Empty;
                if (text.Length > 0)
                {
                    var index = Math.Min(Math.Max(caret - 1, 0), text.Length - 1);
                    var rect = TxtSql.GetRectFromCharacterIndex(index, true);
                    if (!rect.IsEmpty && !double.IsInfinity(rect.Left) && !double.IsNaN(rect.Left))
                    {
                        AutoCompletePopup.HorizontalOffset = Math.Max(0, rect.Left) + 2;
                        AutoCompletePopup.VerticalOffset = rect.Bottom + 4;
                        return;
                    }
                }
            }
            catch
            {
            }

            AutoCompletePopup.HorizontalOffset = 24;
            AutoCompletePopup.VerticalOffset = 40;
        }

        private void MoveAutoCompleteSelection(int delta)
        {
            var count = AutoCompleteList.Items.Count;
            if (count == 0)
            {
                return;
            }

            var index = AutoCompleteList.SelectedIndex;
            index = ((index + delta) % count + count) % count;
            AutoCompleteList.SelectedIndex = index;
            AutoCompleteList.ScrollIntoView(AutoCompleteList.Items[index]);
        }

        private void CommitAutoCompleteSelection()
        {
            var suggestion = AutoCompleteList.SelectedItem as SqlSuggestion;
            if (suggestion == null)
            {
                CloseAutoComplete();
                return;
            }

            CommitAutoCompleteSuggestion(suggestion.Text);
        }

        private void CommitAutoCompleteSuggestion(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                CloseAutoComplete();
                return;
            }

            var text = TxtSql.Text ?? string.Empty;
            var start = Math.Max(0, Math.Min(_autoCompleteStart, text.Length));
            var length = Math.Max(0, Math.Min(_autoCompleteLength, text.Length - start));
            var replacement = value + " ";

            _autoCompleteSuppressed = true;
            try
            {
                TxtSql.Text = text.Substring(0, start) + replacement + text.Substring(start + length);
                TxtSql.CaretIndex = start + replacement.Length;
            }
            finally
            {
                _autoCompleteSuppressed = false;
            }

            CloseAutoComplete();
            TxtSql.Focus();
        }

        private void AutoCompleteList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var container = ItemsControl.ContainerFromElement(
                AutoCompleteList, e.OriginalSource as DependencyObject) as ListBoxItem;
            var suggestion = container == null ? null : container.DataContext as SqlSuggestion;
            if (suggestion == null)
            {
                return;
            }

            e.Handled = true;
            CommitAutoCompleteSuggestion(suggestion.Text);
        }

        private void CloseAutoComplete()
        {
            if (AutoCompletePopup.IsOpen)
            {
                AutoCompletePopup.IsOpen = false;
            }
            AutoCompleteList.ItemsSource = null;
            AutoCompleteList.SelectedItem = null;
        }

        // ==================== 通用 UI 辅助 ====================

        private void SetBusy(bool busy, string message = null)
        {
            _busy = busy;
            if (!string.IsNullOrEmpty(message))
            {
                TxtBusy.Text = message;
            }

            BusyLayer.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            BtnConnect.IsEnabled = !busy;
            BtnTest.IsEnabled = !busy;
            BtnSave.IsEnabled = !busy;
            UpdateUiState();
        }

        private void UpdateUiState()
        {
            var hasSession = _session != null;
            BtnRefreshObjects.IsEnabled = hasSession && !_busy && !_databaseSelectionRequired;
            BtnExecute.IsEnabled = hasSession && !_busy;
            BtnRefreshData.IsEnabled = hasSession && _currentObject != null && !_busy;
            BtnExportCsv.IsEnabled = hasSession && _currentTable != null && !_busy;
            CmbDatabase.IsEnabled = hasSession && !_busy;
        }

        private void SetStatus(bool ok, string text)
        {
            TxtConnStatus.Text = text;
            StatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, ok ? "SuccessBrush" : "DangerBrush");
        }

        private void AppendLog(string message)
        {
            var line = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  " + message;
            TxtLog.Text = line;
            TxtLog.ToolTip = line;
        }

        private void Warn(string message)
        {
            MessageBox.Show(this, message, "Free.DBTools", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private void ShowError(string title, string message)
        {
            MessageBox.Show(this, message, title, MessageBoxButton.OK, MessageBoxImage.Error);
        }

        private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.F5)
            {
                e.Handled = true;
                await ExecuteSqlAsync();
            }
            else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                await ExecuteSqlAsync();
            }
            else if (e.Key == Key.E && Keyboard.Modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                ExportCurrentTable();
            }
        }

        private void Window_Deactivated(object sender, EventArgs e)
        {
            CloseAutoComplete();
        }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            CloseAutoComplete();
            if (_session != null)
            {
                try { _session.Dispose(); }
                catch { }
                _session = null;
            }
        }
    }
}
