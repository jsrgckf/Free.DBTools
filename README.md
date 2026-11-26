# Free.DBTools · 多数据库连接工具 主要用于无网环境
https://github.com/jsrgckf/Free.DBTools/blob/main/src/Free.DBTools/bin/Release/net48.zip 直接下载编译好的
# 需要安装.net framework 4.8
基于 **.NET Framework 4.8 + WPF + SqlSugar** 的轻量桌面数据库工具，一个 EXE 覆盖常见主流数据库与部分国产数据库。支持浅色/深色主题（默认浅色并记住选择）、连接配置保存、连接后选库、对象树浏览、表数据分页、SQL 执行与自动补全、CSV 导出。

## 功能

- **多数据库支持**：SQL Server、MySQL、PostgreSQL、Oracle、SQLite、达梦 DM、人大金仓 KingbaseES、OpenGauss、Access。
- **主题切换**：右上角“浅色 / 深色”一键切换，默认浅色；选择写入 `settings.json`，下次启动自动恢复。
- **连接管理**：主机/端口/账号/密码或自定义连接字符串；测试连接；保存多套配置。主流程无需预先填写数据库名。
- **连接后选库**：SQL Server、MySQL、PostgreSQL、OpenGauss、人大金仓连接成功后，在左侧对象树上方选择数据库，切换后自动重新加载表与视图。
- **密码保护**：保存的密码与自定义连接串使用 Windows DPAPI（当前用户）加密，落盘不存明文。
- **对象浏览**：表/视图分组、名称过滤、右键菜单（浏览数据 / 生成查询 SQL / 复制名称）。
- **数据浏览**：方言分页（OFFSET/FETCH、LIMIT/OFFSET、TOP）、页码/页大小、总行数、刷新。
- **SQL 查询**：多语句执行、多结果集标签页、选中部分执行、错误提示、耗时统计。
- **SQL 自动补全**：输入关键字、表名/视图名或字段名前缀时弹出候选；在 `SELECT`、`WHERE`、`ORDER BY`、`GROUP BY`、`JOIN ... ON`、`SET` 等位置按当前语句引用的表给出字段，支持 `别名.` 直接列出该表字段；多表时字段带别名前缀，`Ctrl+Space` 手动触发。
- **导出与复制**：当前结果或 SQL 结果集导出 CSV（UTF-8 BOM）、复制所选单元格。
- **自检**：`Free.DBTools.exe --selftest` 校验全部驱动加载、SQLite 全链路与选库元数据。

## 支持的数据库

| 类型 | SqlSugar DbType | 默认端口 | 驱动 / 说明 |
| --- | --- | --- | --- |
| SQL Server | `SqlServer` | 1433 | 系统内置 `System.Data.SqlClient`；登录信息留空时使用 Windows 集成认证；连接后选库 |
| MySQL | `MySql` | 3306 | `MySql.Data` 8.0.33；默认关闭 SSL 并允许公钥获取；连接后选库 |
| PostgreSQL | `PostgreSQL` | 5432 | `Npgsql` 4.1.13；默认先连接 `postgres` 库，再选择目标库 |
| Oracle | `Oracle` | 1521 | `Oracle.ManagedDataAccess` 19.27，无需安装客户端；连接时在“服务名”处填写服务名 |
| SQLite | `Sqlite` | - | `System.Data.SQLite`（含 x86/x64 原生库）；可直接填写不存在的文件路径来新建 |
| 达梦 DM | `Dm` | 5236 | `DM.DmProvider` 8.3，程序内部将 `DmProvider` 重定向到 `DM.DmProvider.dll` |
| 人大金仓 | `Kdbndp` | 54321 | `Kdbndp_V9` 8.0.3（netstandard2.0）；默认先连接 `test` 库，再选择目标库 |
| OpenGauss | `OpenGauss` | 5432 | 走 `Npgsql`（兼容 PostgreSQL 协议）；默认先连接 `postgres` 库 |
| Access | `Access` | - | OLEDB `Microsoft.ACE.OLEDB.12.0`，需安装 Access Database Engine（ACE） |

> **不支持**：Oscar、ClickHouse、TDengine、ODBC。这些类型在 **.NET Framework 版 SqlSugar** 中不可用（仅 .NET Core 版支持），因此未纳入目录。

### 驱动位数说明

程序以 AnyCPU、`Prefer32Bit=false` 构建，默认运行在 64 位进程：

- Access 需要安装 **64 位** Microsoft Access Database Engine；
- SQLite 原生库已同时包含 x86/x64，无需额外安装；
- 其余数据库均为纯托管驱动。

## 环境要求

- Windows 7 SP1 及以上（推荐 Windows 10/11 或 Windows Server）；
- .NET Framework 4.8 运行时（目标框架即 4.8）；
- 访问 Access 时另需 64 位 ACE 引擎。

## 构建与运行

使用 Visual Studio 2022 打开 `Free.DBTools.sln`，或使用 .NET SDK：

```bat
build.bat
```

等价命令：

```bat
dotnet build Free.DBTools.sln -c Release
```

生成结果：

```
src\Free.DBTools\bin\Release\net48\Free.DBTools.exe
```

运行：

```bat
src\Free.DBTools\bin\Release\net48\Free.DBTools.exe
```

> `bin\Release\net48` 目录内的 DLL（SqlSugar、各数据库驱动、SQLite 原生库等）必须与 EXE 一起分发，不能只拷贝 EXE。

### 自检

```bat
src\Free.DBTools\bin\Release\net48\Free.DBTools.exe --selftest
```

自检会：

1. 逐一初始化 9 种数据库驱动；
2. 用临时 SQLite 文件跑完整链路（建表、插入、表/列读取、分页、COUNT、SQL 查询、CSV 导出、预览 SQL）；
3. 校验 DPAPI 加解密往返；
4. 校验选库能力标记、连接串中的 Database 省略/写入规则。

结果写入程序目录 `selftest.log`，进程退出码 0 表示通过。

## 使用说明

1. **连接**：选择数据库类型，填写主机/端口/账号/密码即可，无需预先填写数据库名（Oracle 需填写“服务名”）。SQLite/Access 会切换为文件选择；“使用自定义连接字符串”可完全接管连接串。
2. **选择数据库**：SQL Server、MySQL、PostgreSQL、OpenGauss、人大金仓连接成功后，左侧“数据库对象”上方会出现数据库下拉框；选中后自动切换连接并加载表与视图。使用自定义连接字符串时不显示该下拉框。
3. **保存配置**：点击“保存配置”写入 `%AppData%\Free.DBTools\connections.json`；左上角下拉框可直接载入，右侧“删除”移除。最近选择的数据库会随配置保存。
4. **对象浏览**：连接后左侧自动列出表与视图；顶部输入框按名称过滤；单击表即加载数据，右键可生成查询 SQL。
5. **分页浏览**：底部可切换每页条数，首页/上一页/下一页/末页。Access 仅支持首页预览（TOP），翻页请使用 SQL。
6. **SQL 查询**：在“SQL 查询”页输入语句，`F5` 或 `Ctrl+Enter` 执行；选中文本时只执行选中部分；多条语句返回多个结果标签页。
7. **自动补全**：输入关键字、表名/视图名或字段名前缀后自动弹出候选（中文名一个字符即可，英文名两个字符；字段在 `ORDER BY`、`ON` 等位置可单个字符即提示）。`SELECT * FROM 表 ORDER BY ` 这类位置会直接列出该表字段；输入 `别名.` 列出对应表字段；多表查询时字段显示为 `别名.字段`。`↑`/`↓` 选择，`Enter`/`Tab` 插入，`Esc` 关闭；`Ctrl+Space` 可随时打开候选。
8. **导出**：数据页“导出 CSV”、右键菜单或 `Ctrl+E`；SQL 结果集右键同样可导出。
9. **主题**：右上角“浅色 / 深色”切换即时生效；首次启动默认浅色，之后每次切换都会被记住并在下次启动时自动恢复。

### 快捷键

| 快捷键 | 功能 |
| --- | --- |
| `F5` / `Ctrl+Enter` | 执行 SQL（有选中文本时执行选中部分） |
| `Ctrl+Space` | 打开/刷新 SQL 自动补全 |
| `↑` / `↓` | 在补全候选间移动 |
| `Enter` / `Tab` | 插入当前补全候选 |
| `Esc` | 关闭补全候选 |
| `Ctrl+E` | 导出当前表数据为 CSV |
| `Ctrl+C` | 在数据网格中复制所选单元格（网格自带行为） |
| `双击表节点` | 打开数据浏览 |

## 数据与安全

- 配置文件：`%AppData%\Free.DBTools\connections.json`；
- 主题偏好：`%AppData%\Free.DBTools\settings.json`（仅记录 `Light` / `Dark`）；
- 密码/自定义连接串：DPAPI（`CurrentUser` 范围）加密，换用户或换机器无法解密，**配置不可直接迁移**；
- 保存采用临时文件 + 原子替换，并保留 `.bak`；读取失败时保留 `.corrupt` 副本；
- 日志仅显示操作状态与服务器版本，不记录 SQL 与连接串明文。

## 项目结构

```
Free.DBTools.sln
build.bat
README.md
src/Free.DBTools/
  App.xaml / App.xaml.cs         应用入口、全局异常、--selftest 分支
  AssemblyRedirects.cs           DmProvider / Kdbndp 程序集名兼容
  ThemeManager.cs                主题切换与偏好持久化
  MainWindow.xaml / .xaml.cs     主界面与全部交互
  SelfTest.cs                    命令行自检
  Core/
    DatabaseKind.cs              数据库目录、连接串构建、标识符引用、分页模式
    DatabaseSession.cs           SqlSugar 会话、对象/列/分页/查询
    ConnectionStore.cs           配置持久化、DPAPI
    CsvExporter.cs               CSV 导出
  Themes/Light.xaml             浅色主题与控件模板
  Themes/Dark.xaml              深色主题与控件模板
```

## 已知限制

- **Oracle 分页**使用 `OFFSET ... FETCH`，需 Oracle 12c 及以上；11g 请直接写 SQL。
- **Access 分页**仅首页（TOP），不支持 OFFSET。
- **选库范围**：Oracle 的库即服务名，需在连接时填写；SQLite/Access 为单文件数据库；这些类型不显示数据库选择框。
- **自定义连接串**完全由用户接管，连接后不提供数据库切换。
- MySQL 默认 `SslMode=None`，如实例强制 SSL，请使用自定义连接字符串。
- SQL Server 默认 `Encrypt=False;TrustServerCertificate=True`，生产环境建议改自定义连接串。
- 达梦/人大金仓的驱动包版本较旧，如服务端要求更高版本，可替换为厂商提供的新驱动，并保持程序集名兼容（达梦需同时满足 `DM.DmProvider.dll` 与内部 `DmProvider` 引用，程序已做重定向）。
- 超大表的总行数统计（`COUNT(*)`）在首次打开时可能较慢，超时时间为 30 秒。
- SQL 自动补全为轻量前缀匹配，不解析完整语法树：会识别当前语句的 `FROM` / `JOIN` 表与别名，子查询、CTE 的别名不保证识别；字段列表按需从数据库元数据读取并缓存（切换连接或数据库后自动失效）。字符串与注释内不弹出候选。
