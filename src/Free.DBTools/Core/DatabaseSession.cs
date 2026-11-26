using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Linq;
using SqlSugar;

namespace Free.DBTools.Core
{
    public sealed class DatabaseObjectInfo
    {
        public string Name { get; set; }
        public bool IsView { get; set; }
        public string Description { get; set; }
    }

    public sealed class QueryResultSet
    {
        public string Title { get; set; }
        public DataTable Table { get; set; }
    }

    public sealed class QueryBatchResult
    {
        public QueryBatchResult()
        {
            ResultSets = new List<QueryResultSet>();
        }

        public List<QueryResultSet> ResultSets { get; private set; }
        public long AffectedRows { get; set; }
        public long ElapsedMilliseconds { get; set; }
    }

    public sealed class DatabaseSession : IDisposable
    {
        private readonly SqlSugarClient _client;
        private bool _disposed;

        public DatabaseSession(DatabaseKind kind, string connectionString)
        {
            if (kind == null)
            {
                throw new ArgumentNullException("kind");
            }
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new ArgumentException("连接字符串不能为空。", "connectionString");
            }

            Kind = kind;
            ConnectionString = connectionString;
            _client = new SqlSugarClient(new ConnectionConfig
            {
                ConnectionString = connectionString,
                DbType = kind.SugarDbType,
                IsAutoCloseConnection = true,
                InitKeyType = InitKeyType.Attribute
            });
            _client.Ado.CommandTimeOut = 300;
        }

        public DatabaseKind Kind { get; private set; }
        public string ConnectionString { get; private set; }
        public string ServerVersion { get; private set; }
        public SqlSugarClient Client { get { return _client; } }

        public void Open()
        {
            ThrowIfDisposed();
            try
            {
                _client.Ado.CheckConnection();
                ServerVersion = ReadServerVersion();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "连接 " + Kind.DisplayName + " 失败：" + DescribeException(ex), ex);
            }
        }

        public static string TestConnection(DatabaseKind kind, string connectionString)
        {
            using (var session = new DatabaseSession(kind, connectionString))
            {
                session.Open();
                return session.ServerVersion ?? string.Empty;
            }
        }

        public List<DatabaseObjectInfo> GetTables()
        {
            ThrowIfDisposed();
            return _client.DbMaintenance.GetTableInfoList(false)
                .Select(t => new DatabaseObjectInfo { Name = t.Name, IsView = false, Description = t.Description })
                .Where(t => !string.IsNullOrEmpty(t.Name))
                .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public List<DatabaseObjectInfo> GetViews()
        {
            ThrowIfDisposed();
            return _client.DbMaintenance.GetViewInfoList(false)
                .Select(t => new DatabaseObjectInfo { Name = t.Name, IsView = true, Description = t.Description })
                .Where(t => !string.IsNullOrEmpty(t.Name))
                .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public List<string> GetDatabaseNames()
        {
            ThrowIfDisposed();
            if (!Kind.SupportsDatabaseSwitch)
            {
                return new List<string>();
            }

            string sql;
            switch (Kind.Key)
            {
                case "SqlServer":
                    sql = "SELECT name FROM sys.databases WHERE state = 0 ORDER BY name";
                    break;
                case "MySql":
                    sql = "SHOW DATABASES";
                    break;
                case "PostgreSQL":
                case "OpenGauss":
                case "Kdbndp":
                    sql = "SELECT datname FROM pg_database WHERE datallowconn = TRUE ORDER BY datname";
                    break;
                default:
                    return new List<string>();
            }

            var table = _client.Ado.GetDataTable(sql);
            var names = new List<string>();
            foreach (DataRow row in table.Rows)
            {
                if (row.ItemArray.Length == 0)
                {
                    continue;
                }

                var name = Convert.ToString(row[0]);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    names.Add(name.Trim());
                }
            }

            return Kind.Key == "MySql" ? FilterMySqlSystemSchemas(names) : names;
        }

        private List<string> FilterMySqlSystemSchemas(List<string> names)
        {
            var systemSchemas = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "information_schema", "performance_schema", "mysql", "sys"
            };

            string current = null;
            try
            {
                current = _client.Ado.Connection == null ? null : _client.Ado.Connection.Database;
            }
            catch
            {
            }

            var filtered = names
                .Where(n => !systemSchemas.Contains(n) ||
                            string.Equals(n, current, StringComparison.OrdinalIgnoreCase))
                .ToList();
            return filtered.Count > 0 ? filtered : names;
        }

        public List<DbColumnInfo> GetColumns(string tableName)
        {
            ThrowIfDisposed();
            if (string.IsNullOrWhiteSpace(tableName))
            {
                return new List<DbColumnInfo>();
            }

            var columns = SafeGetColumns(tableName);
            if (columns.Count == 0 && tableName.IndexOf('.') >= 0)
            {
                columns = SafeGetColumns(tableName.Substring(tableName.LastIndexOf('.') + 1));
            }
            return columns;
        }

        private List<DbColumnInfo> SafeGetColumns(string tableName)
        {
            try
            {
                return _client.DbMaintenance.GetColumnInfosByTableName(tableName, false) ?? new List<DbColumnInfo>();
            }
            catch
            {
                return new List<DbColumnInfo>();
            }
        }

        public List<string> GetColumnNames(string tableName)
        {
            return GetColumns(tableName)
                .Select(c => c == null ? null : c.DbColumnName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public DataTable GetTablePage(string tableName, int pageIndex, int pageSize)
        {
            ThrowIfDisposed();
            if (string.IsNullOrWhiteSpace(tableName))
            {
                throw new ArgumentException("请先选择表。", "tableName");
            }

            pageIndex = Math.Max(0, pageIndex);
            pageSize = Math.Max(1, Math.Min(pageSize, 10000));
            var quoted = Kind.QuoteQualifiedName(tableName);
            var offset = pageIndex * pageSize;
            string sql;

            switch (Kind.PagingMode)
            {
                case DataPagingMode.SqlServerOffsetFetch:
                    sql = "SELECT * FROM " + quoted +
                          " ORDER BY (SELECT NULL) OFFSET " + offset + " ROWS FETCH NEXT " + pageSize + " ROWS ONLY";
                    break;
                case DataPagingMode.OracleOffsetFetch:
                    sql = "SELECT * FROM " + quoted +
                          " OFFSET " + offset + " ROWS FETCH NEXT " + pageSize + " ROWS ONLY";
                    break;
                case DataPagingMode.LimitOffset:
                    sql = "SELECT * FROM " + quoted + " LIMIT " + pageSize + " OFFSET " + offset;
                    break;
                case DataPagingMode.TopOnly:
                    if (pageIndex > 0)
                    {
                        throw new InvalidOperationException(
                            Kind.DisplayName + " 不支持分页偏移，仅可浏览第一页；需要翻页请使用 SQL 查询。");
                    }
                    sql = "SELECT TOP " + pageSize + " * FROM " + quoted;
                    break;
                default:
                    sql = "SELECT * FROM " + quoted;
                    break;
            }

            return _client.Ado.GetDataTable(sql);
        }

        public long GetRowCount(string tableName)
        {
            ThrowIfDisposed();
            var quoted = Kind.QuoteQualifiedName(tableName);
            var oldTimeout = _client.Ado.CommandTimeOut;
            try
            {
                _client.Ado.CommandTimeOut = 30;
                var value = _client.Ado.GetScalar("SELECT COUNT(*) FROM " + quoted);
                return value == null || value == DBNull.Value ? -1 : Convert.ToInt64(value);
            }
            finally
            {
                _client.Ado.CommandTimeOut = oldTimeout;
            }
        }

        public string GeneratePreviewSql(string tableName, int limit = 200)
        {
            var quoted = Kind.QuoteQualifiedName(tableName);
            switch (Kind.PagingMode)
            {
                case DataPagingMode.SqlServerOffsetFetch:
                case DataPagingMode.TopOnly:
                    return "SELECT TOP " + limit + " * FROM " + quoted + ";";
                case DataPagingMode.OracleOffsetFetch:
                    return "SELECT * FROM " + quoted + " FETCH FIRST " + limit + " ROWS ONLY;";
                case DataPagingMode.LimitOffset:
                    return "SELECT * FROM " + quoted + " LIMIT " + limit + ";";
                default:
                    return "SELECT * FROM " + quoted + ";";
            }
        }

        public QueryBatchResult ExecuteBatch(string sql, int commandTimeoutSeconds = 300)
        {
            ThrowIfDisposed();
            if (string.IsNullOrWhiteSpace(sql))
            {
                throw new ArgumentException("SQL 不能为空。", "sql");
            }

            var result = new QueryBatchResult();
            var watch = Stopwatch.StartNew();
            var connection = _client.Ado.Connection;
            var openedHere = false;

            try
            {
                if (connection.State != ConnectionState.Open)
                {
                    connection.Open();
                    openedHere = true;
                }

                using (var command = connection.CreateCommand())
                {
                    command.CommandText = sql;
                    command.CommandTimeout = Math.Max(0, commandTimeoutSeconds);

                    using (var reader = command.ExecuteReader())
                    {
                        var index = 0;
                        do
                        {
                            if (reader.FieldCount > 0)
                            {
                                index++;
                                var table = new DataTable("结果" + index);
                                table.Load(reader);
                                result.ResultSets.Add(new QueryResultSet
                                {
                                    Title = "结果 " + index,
                                    Table = table
                                });
                            }
                            else if (reader.RecordsAffected > 0)
                            {
                                result.AffectedRows += reader.RecordsAffected;
                            }
                        }
                        while (!reader.IsClosed && reader.NextResult());
                    }
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(DescribeException(ex), ex);
            }
            finally
            {
                if (openedHere)
                {
                    try { connection.Close(); }
                    catch { }
                }
                watch.Stop();
                result.ElapsedMilliseconds = watch.ElapsedMilliseconds;
            }

            return result;
        }

        private string ReadServerVersion()
        {
            try
            {
                var connection = _client.Ado.Connection;
                var openedHere = false;
                if (connection.State != ConnectionState.Open)
                {
                    connection.Open();
                    openedHere = true;
                }

                try
                {
                    var dbConnection = connection as DbConnection;
                    return dbConnection == null ? null : dbConnection.ServerVersion;
                }
                finally
                {
                    if (openedHere)
                    {
                        connection.Close();
                    }
                }
            }
            catch
            {
                return null;
            }
        }

        public static string DescribeException(Exception ex)
        {
            if (ex == null)
            {
                return string.Empty;
            }

            var messages = new List<string>();
            for (var current = ex; current != null; current = current.InnerException)
            {
                if (!string.IsNullOrWhiteSpace(current.Message) && !messages.Contains(current.Message))
                {
                    messages.Add(current.Message.Trim());
                }
            }
            return string.Join(" -> ", messages);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(GetType().Name);
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;

            try
            {
                var connection = _client.Ado.Connection;
                if (connection != null && connection.State != ConnectionState.Closed)
                {
                    connection.Close();
                }
            }
            catch
            {
            }

            try { _client.Dispose(); }
            catch { }
        }
    }
}
