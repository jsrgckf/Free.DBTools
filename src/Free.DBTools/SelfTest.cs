using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Free.DBTools.Core;

namespace Free.DBTools
{
    public static class SelfTest
    {
        private const int AttachParentProcess = -1;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(int dwProcessId);

        public static int Run()
        {
            AssemblyRedirects.Install();
            AttachConsole(AttachParentProcess);

            var log = new StringBuilder();
            var failures = new List<string>();

            void Write(string line)
            {
                log.AppendLine(line);
                try
                {
                    Console.WriteLine(line);
                }
                catch
                {
                }
            }

            Write("Free.DBTools self-test started at " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            Write("Runtime: " + Environment.Version + " / " + RuntimeInformation.FrameworkDescription);
            Write("Base directory: " + AppDomain.CurrentDomain.BaseDirectory);
            Write(string.Empty);

            Write("[1] Provider initialization");
            foreach (var kind in DatabaseCatalog.All)
            {
                var input = new ConnectionInput
                {
                    Host = "127.0.0.1",
                    Port = kind.DefaultPort > 0 ? 1 : 0,
                    Database = "test",
                    User = "user",
                    Password = "pwd",
                    FilePath = Path.Combine(Path.GetTempPath(), "dbtools_provider_test.db")
                };

                var connectionString = kind.BuildConnectionString(input);
                DatabaseSession session = null;
                try
                {
                    session = new DatabaseSession(kind, connectionString);
                    var connection = session.Client.Ado.Connection;
                    var typeName = connection == null ? "<null>" : connection.GetType().FullName;
                    Write("  OK    " + kind.Key.PadRight(12) + " -> " + typeName);
                }
                catch (Exception ex)
                {
                    var message = DatabaseSession.DescribeException(ex);
                    Write("  FAIL  " + kind.Key.PadRight(12) + " -> " + message);
                    failures.Add("驱动初始化失败：" + kind.Key + " - " + message);
                }
                finally
                {
                    if (session != null)
                    {
                        try { session.Dispose(); }
                        catch { }
                    }
                }
            }

            Write(string.Empty);
            Write("[2] SQLite end-to-end");
            RunSqliteTests(Write, failures);

            Write(string.Empty);
            Write("[3] Secret protection");
            try
            {
                var cipher = SecretProtector.Protect("p@ss;word'\"中文");
                var plain = SecretProtector.Unprotect(cipher);
                if (plain != "p@ss;word'\"中文")
                {
                    failures.Add("DPAPI 加解密结果不一致");
                    Write("  FAIL  DPAPI roundtrip");
                }
                else
                {
                    Write("  OK    DPAPI roundtrip");
                }
            }
            catch (Exception ex)
            {
                failures.Add("DPAPI 测试异常：" + ex.Message);
                Write("  FAIL  DPAPI: " + ex.Message);
            }

            Write(string.Empty);
            Write("[4] Database switching metadata");
            RunDatabaseSwitchChecks(Write, failures);

            Write(string.Empty);
            Write(failures.Count == 0
                ? "RESULT: PASS"
                : "RESULT: FAIL (" + failures.Count + ")");
            foreach (var failure in failures)
            {
                Write("  - " + failure);
            }

            var logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "selftest.log");
            try
            {
                File.WriteAllText(logPath, log.ToString(), new UTF8Encoding(false));
            }
            catch
            {
            }

            return failures.Count == 0 ? 0 : 1;
        }

        private static void RunSqliteTests(Action<string> write, List<string> failures)
        {
            var dbFile = Path.Combine(Path.GetTempPath(), "dbtools_selftest_" + Guid.NewGuid().ToString("N") + ".db");
            var kind = DatabaseCatalog.Find("Sqlite");

            try
            {
                var input = new ConnectionInput { FilePath = dbFile };
                using (var session = new DatabaseSession(kind, kind.BuildConnectionString(input)))
                {
                    session.Open();
                    write("  OK    已打开 SQLite：" + dbFile + " (server " + (session.ServerVersion ?? "?") + ")");

                    session.ExecuteBatch(
                        "CREATE TABLE t_person (" +
                        "id INTEGER PRIMARY KEY AUTOINCREMENT, " +
                        "name TEXT NOT NULL, " +
                        "age INTEGER, " +
                        "salary REAL, " +
                        "created_at TEXT);");
                    session.ExecuteBatch(
                        "INSERT INTO t_person (name, age, salary, created_at) VALUES " +
                        "('Alice', 30, 12000.5, '2024-01-02 03:04:05')," +
                        "('Bob', 24, 8000, '2024-02-03 04:05:06')," +
                        "('Cathy', 41, 21000.75, '2024-03-04 05:06:07');");

                    var tables = session.GetTables();
                    Check(write, failures, tables.Any(t => string.Equals(t.Name, "t_person", StringComparison.OrdinalIgnoreCase)),
                        "表列表包含 t_person");

                    var databaseNames = session.GetDatabaseNames();
                    Check(write, failures, databaseNames.Count == 0, "SQLite 不提供数据库切换列表");

                    var columns = session.GetColumns("t_person");
                    Check(write, failures, columns.Count == 5, "列数量为 5（实际 " + columns.Count + "）");
                    Check(write, failures, columns.Any(c => c.DbColumnName == "name" && c.IsPrimarykey == false),
                        "列信息可读取");

                    var page0 = session.GetTablePage("t_person", 0, 2);
                    var page1 = session.GetTablePage("t_person", 1, 2);
                    Check(write, failures, page0.Rows.Count == 2, "第一页返回 2 行");
                    Check(write, failures, page1.Rows.Count == 1, "第二页返回 1 行");

                    var count = session.GetRowCount("t_person");
                    Check(write, failures, count == 3, "COUNT(*) = 3（实际 " + count + "）");

                    var batch = session.ExecuteBatch("SELECT id, name FROM t_person ORDER BY id;");
                    Check(write, failures, batch.ResultSets.Count >= 1, "SQL 查询返回结果集");
                    Check(write, failures, batch.ResultSets[0].Table.Rows.Count == 3, "结果集为 3 行");
                    Check(write, failures,
                        Convert.ToString(batch.ResultSets[0].Table.Rows[0]["name"]) == "Alice",
                        "首行数据为 Alice");

                    var multi = session.ExecuteBatch("SELECT 1 AS a; SELECT 2 AS b;");
                    write("  INFO  多语句返回结果集数量：" + multi.ResultSets.Count);

                    var csvPath = Path.Combine(Path.GetTempPath(), "dbtools_selftest_" + Guid.NewGuid().ToString("N") + ".csv");
                    CsvExporter.Export(page0, csvPath);
                    var lines = File.ReadAllLines(csvPath);
                    Check(write, failures, lines.Length == 3, "CSV 导出 2 行 + 表头");
                    File.Delete(csvPath);

                    var preview = session.GeneratePreviewSql("t_person");
                    Check(write, failures, preview.IndexOf("LIMIT", StringComparison.OrdinalIgnoreCase) >= 0,
                        "预览 SQL 包含 LIMIT");
                }
            }
            catch (Exception ex)
            {
                failures.Add("SQLite 端到端测试异常：" + DatabaseSession.DescribeException(ex));
                write("  FAIL  SQLite 端到端：" + DatabaseSession.DescribeException(ex));
                write(ex.ToString());
            }
            finally
            {
                try
                {
                    if (File.Exists(dbFile))
                    {
                        File.Delete(dbFile);
                    }
                }
                catch
                {
                }
            }
        }

        private static void RunDatabaseSwitchChecks(Action<string> write, List<string> failures)
        {
            var switchable = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "SqlServer", "MySql", "PostgreSQL", "OpenGauss", "Kdbndp"
            };

            foreach (var kind in DatabaseCatalog.All)
            {
                var expected = switchable.Contains(kind.Key);
                Check(write, failures, kind.SupportsDatabaseSwitch == expected,
                    kind.Key + " 支持连接后切换数据库 = " + expected);
            }

            var sqlServer = DatabaseCatalog.Find("SqlServer");
            var withoutDatabase = sqlServer.BuildConnectionString(new ConnectionInput
            {
                Host = "localhost",
                Port = 1433,
                User = "sa",
                Password = "p"
            });
            Check(write, failures,
                withoutDatabase.IndexOf("Database=", StringComparison.OrdinalIgnoreCase) < 0,
                "SQL Server 未提供库名时连接串省略 Database");

            var withDatabase = sqlServer.BuildConnectionString(new ConnectionInput
            {
                Host = "localhost",
                Port = 1433,
                Database = "Demo",
                User = "sa",
                Password = "p"
            });
            Check(write, failures,
                withDatabase.IndexOf("Database=Demo", StringComparison.OrdinalIgnoreCase) >= 0,
                "SQL Server 指定库名时写入 Database=Demo");

            Check(write, failures,
                string.Equals(DatabaseCatalog.Find("PostgreSQL").DefaultDatabase, "postgres", StringComparison.Ordinal),
                "PostgreSQL 默认库为 postgres");
            Check(write, failures,
                DatabaseCatalog.Find("MySql").DefaultDatabase == null,
                "MySQL 连接时不指定默认库");

            using (var session = new DatabaseSession(
                DatabaseCatalog.Find("Sqlite"), "Data Source=:memory:;Version=3;Pooling=False;"))
            {
                Check(write, failures, session.GetDatabaseNames().Count == 0,
                    "不支持切换时返回空数据库列表");
            }
        }

        private static void Check(Action<string> write, List<string> failures, bool condition, string description)
        {
            if (condition)
            {
                write("  OK    " + description);
            }
            else
            {
                failures.Add("断言失败：" + description);
                write("  FAIL  " + description);
            }
        }
    }
}
