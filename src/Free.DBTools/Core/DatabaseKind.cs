using System;
using System.Collections.Generic;
using System.Linq;
using SqlSugar;

namespace Free.DBTools.Core
{
    public enum DataPagingMode
    {
        SqlServerOffsetFetch,
        OracleOffsetFetch,
        LimitOffset,
        TopOnly,
        None
    }

    public sealed class ConnectionInput
    {
        public string Host { get; set; }
        public int Port { get; set; }
        public string Database { get; set; }
        public string User { get; set; }
        public string Password { get; set; }
        public string FilePath { get; set; }
        public bool UseCustomString { get; set; }
        public string CustomConnectionString { get; set; }
    }

    public sealed class DatabaseKind
    {
        public string Key { get; private set; }
        public string DisplayName { get; private set; }
        public int DefaultPort { get; private set; }
        public bool IsFileBased { get; private set; }
        public string FileFilter { get; private set; }
        public string DriverNote { get; private set; }
        public DataPagingMode PagingMode { get; private set; }
        public string QuoteOpen { get; private set; }
        public string QuoteClose { get; private set; }
        public string DefaultDatabase { get; private set; }
        public bool SupportsDatabaseSwitch { get; private set; }

        public DbType SugarDbType
        {
            get { return (DbType)Enum.Parse(typeof(DbType), Key, true); }
        }

        internal static DatabaseKind Create(
            string key,
            string displayName,
            int defaultPort,
            string driverNote,
            DataPagingMode pagingMode,
            string quoteOpen,
            string quoteClose,
            bool isFileBased = false,
            string fileFilter = null,
            string defaultDatabase = null,
            bool supportsDatabaseSwitch = false)
        {
            return new DatabaseKind
            {
                Key = key,
                DisplayName = displayName,
                DefaultPort = defaultPort,
                DriverNote = driverNote,
                PagingMode = pagingMode,
                QuoteOpen = quoteOpen,
                QuoteClose = quoteClose,
                IsFileBased = isFileBased,
                FileFilter = fileFilter,
                DefaultDatabase = defaultDatabase,
                SupportsDatabaseSwitch = supportsDatabaseSwitch
            };
        }

        public string BuildConnectionString(ConnectionInput input)
        {
            if (input == null)
            {
                return string.Empty;
            }

            if (input.UseCustomString)
            {
                return (input.CustomConnectionString ?? string.Empty).Trim();
            }

            var host = (input.Host ?? string.Empty).Trim();
            var database = (input.Database ?? string.Empty).Trim();
            var user = (input.User ?? string.Empty).Trim();
            var password = input.Password ?? string.Empty;
            var file = (input.FilePath ?? string.Empty).Trim();
            var port = input.Port;

            switch (Key)
            {
                case "SqlServer":
                {
                    var server = host;
                    if (port > 0 && server.IndexOf(',') < 0 && server.IndexOf('\\') < 0)
                    {
                        server += "," + port;
                    }

                    var databasePart = string.IsNullOrEmpty(database)
                        ? string.Empty
                        : ";Database=" + Quote(database);

                    if (string.IsNullOrEmpty(user))
                    {
                        return "Server=" + Quote(server) + databasePart +
                               ";Integrated Security=SSPI;Encrypt=False;TrustServerCertificate=True;Connect Timeout=15;";
                    }

                    return "Server=" + Quote(server) + databasePart +
                           ";User Id=" + Quote(user) + ";Password=" + Quote(password) +
                           ";Encrypt=False;TrustServerCertificate=True;Connect Timeout=15;";
                }
                case "MySql":
                case "MySqlConnector":
                {
                    var databasePart = string.IsNullOrEmpty(database)
                        ? string.Empty
                        : "Database=" + Quote(database) + ";";
                    return "Server=" + Quote(host) + ";Port=" + port + ";" + databasePart +
                           "Uid=" + Quote(user) + ";Pwd=" + Quote(password) +
                           ";CharSet=utf8mb4;SslMode=None;AllowPublicKeyRetrieval=True;Connection Timeout=15;";
                }
                case "PostgreSQL":
                case "OpenGauss":
                {
                    var databasePart = string.IsNullOrEmpty(database)
                        ? string.Empty
                        : "Database=" + Quote(database) + ";";
                    return "Host=" + Quote(host) + ";Port=" + port + ";" + databasePart +
                           "Username=" + Quote(user) + ";Password=" + Quote(password) +
                           ";Timeout=15;Pooling=false;";
                }
                case "Oracle":
                {
                    var dataSource = host;
                    if (port > 0)
                    {
                        dataSource += ":" + port;
                    }
                    if (!string.IsNullOrEmpty(database))
                    {
                        dataSource += "/" + database;
                    }
                    return "Data Source=" + Quote(dataSource) + ";User Id=" + Quote(user) +
                           ";Password=" + Quote(password) + ";Connection Timeout=15;";
                }
                case "Sqlite":
                    return "Data Source=" + Quote(file) + ";Version=3;Pooling=False;";
                case "Access":
                {
                    var cs = "Provider=Microsoft.ACE.OLEDB.12.0;Data Source=" + Quote(file) + ";";
                    if (!string.IsNullOrEmpty(password))
                    {
                        cs += "Jet OLEDB:Database Password=" + Quote(password) + ";";
                    }
                    return cs;
                }
                case "Dm":
                    return "Server=" + Quote(host) + ";Port=" + port + ";User Id=" + Quote(user) +
                           ";Password=" + Quote(password) + ";";
                case "Kdbndp":
                {
                    var databasePart = string.IsNullOrEmpty(database)
                        ? string.Empty
                        : "Database=" + Quote(database) + ";";
                    return "Server=" + Quote(host) + ";Port=" + port + ";" + databasePart +
                           "UID=" + Quote(user) + ";PWD=" + Quote(password) + ";";
                }
                case "Oscar":
                    return "Server=" + Quote(host) + ";Port=" + port + ";Database=" + Quote(database) +
                           ";User Id=" + Quote(user) + ";Password=" + Quote(password) + ";";
                case "ClickHouse":
                    return "Host=" + Quote(host) + ";Port=" + port + ";Database=" + Quote(database) +
                           ";Username=" + Quote(user) + ";Password=" + Quote(password) + ";";
                case "TDengine":
                    return "host=" + Quote(host) + ";port=" + port + ";username=" + Quote(user) +
                           ";password=" + Quote(password) + ";database=" + Quote(database) + ";";
                case "Odbc":
                    return "Dsn=" + Quote(database) + ";Uid=" + Quote(user) + ";Pwd=" + Quote(password) + ";";
                default:
                    return string.Empty;
            }
        }

        public string QuoteIdentifier(string identifier)
        {
            if (string.IsNullOrEmpty(identifier))
            {
                return identifier;
            }

            var value = identifier.Trim();
            if (value.Length >= 2)
            {
                var first = value[0];
                var last = value[value.Length - 1];
                if ((first == '[' && last == ']') || (first == '"' && last == '"') || (first == '`' && last == '`'))
                {
                    value = value.Substring(1, value.Length - 2);
                }
            }

            var close = string.IsNullOrEmpty(QuoteClose) ? "\"" : QuoteClose;
            return QuoteOpen + value.Replace(close, close + close) + close;
        }

        public string QuoteQualifiedName(string tableName)
        {
            if (string.IsNullOrEmpty(tableName))
            {
                return tableName;
            }

            return string.Join(".", tableName.Split('.').Select(QuoteIdentifier));
        }

        public override string ToString()
        {
            return DisplayName;
        }

        private static string Quote(string value)
        {
            value = value ?? string.Empty;
            if (value.Length == 0)
            {
                return "\"\"";
            }

            if (value.IndexOfAny(new[] { ';', '"', '\'', '=' }) >= 0 || value.Any(char.IsWhiteSpace))
            {
                return "\"" + value.Replace("\"", "\"\"") + "\"";
            }

            return value;
        }
    }

    public static class DatabaseCatalog
    {
        public static readonly IReadOnlyList<DatabaseKind> All = new List<DatabaseKind>
        {
            DatabaseKind.Create(
                "SqlServer", "SQL Server", 1433,
                "使用 .NET Framework 内置的 System.Data.SqlClient，无需额外驱动。连接后可在左侧选择数据库。",
                DataPagingMode.SqlServerOffsetFetch, "[", "]", false, null, null, true),
            DatabaseKind.Create(
                "MySql", "MySQL", 3306,
                "内置 MySql.Data 驱动。默认关闭 SSL 并允许公钥获取，适配常见自建/内网实例。连接后可在左侧选择数据库。",
                DataPagingMode.LimitOffset, "`", "`", false, null, null, true),
            DatabaseKind.Create(
                "PostgreSQL", "PostgreSQL", 5432,
                "内置 Npgsql 驱动。连接后可在左侧选择数据库。",
                DataPagingMode.LimitOffset, "\"", "\"", false, null, "postgres", true),
            DatabaseKind.Create(
                "Oracle", "Oracle", 1521,
                "内置 Oracle.ManagedDataAccess 驱动，无需安装 Oracle 客户端。服务名填写在下方“服务名”框中。",
                DataPagingMode.OracleOffsetFetch, "\"", "\""),
            DatabaseKind.Create(
                "Sqlite", "SQLite", 0,
                "内置 System.Data.SQLite（含 x86/x64 原生库）。可直接输入不存在的文件路径来新建数据库。",
                DataPagingMode.LimitOffset, "\"", "\"", true, "SQLite 数据库|*.db;*.sqlite;*.sqlite3;*.db3|所有文件|*.*"),
            DatabaseKind.Create(
                "Dm", "达梦 DM", 5236,
                "内置 DM.DmProvider 驱动（通过程序集重定向加载）。达梦通常无需填写数据库名，模式与用户名相关联。",
                DataPagingMode.LimitOffset, "\"", "\""),
            DatabaseKind.Create(
                "Kdbndp", "人大金仓 KingbaseES", 54321,
                "内置 Kdbndp 8.0.3 驱动（netstandard2.0）。连接后可在左侧选择数据库；如连接异常，请用 KingbaseES 安装目录 Interface\\dotnet 下的 Kdbndp.dll 覆盖程序目录中的同名文件。",
                DataPagingMode.LimitOffset, "\"", "\"", false, null, "test", true),
            DatabaseKind.Create(
                "OpenGauss", "OpenGauss", 5432,
                "通过 Npgsql 驱动连接（OpenGauss 兼容 PostgreSQL 协议）。连接后可在左侧选择数据库。",
                DataPagingMode.LimitOffset, "\"", "\"", false, null, "postgres", true),
            DatabaseKind.Create(
                "Access", "Access (OLEDB)", 0,
                "使用系统 OLEDB，需要安装 Microsoft Access Database Engine（ACE）；.mdb/.accdb 均可尝试。",
                DataPagingMode.TopOnly, "[", "]", true, "Access 数据库|*.accdb;*.mdb|所有文件|*.*")
        };

        public static DatabaseKind Find(string key)
        {
            if (string.IsNullOrEmpty(key))
            {
                return null;
            }

            return All.FirstOrDefault(k => string.Equals(k.Key, key, StringComparison.OrdinalIgnoreCase));
        }
    }
}
