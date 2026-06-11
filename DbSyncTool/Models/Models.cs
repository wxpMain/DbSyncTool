using System;
using System.Collections.Generic;

namespace DbSyncTool.Models
{
    // ========================
    // 数据库配置
    // ========================
    public enum DbType { SqlServer = 1, MySQL = 2 }
    public enum SyncDirection { SqlServerToMySQL = 1, MySQLToSqlServer = 2, SqlServerToSqlServer = 3, MySQLToMySQL = 4 }
    public enum SyncMode { Full = 1, Incremental = 2 }
    public enum CallType { Direct = 1, Api = 2, Script = 3 }
    public enum TaskStatus { Pending, Running, Success, Failed, Disabled }
    public enum TriggerType { Once, Interval, Cron }
    public enum LogLevel { DEBUG, INFO, WARNING, ERROR, FATAL }
    public enum HttpMethod { GET, POST, PUT, DELETE }

    public class DbConfig
    {
        public int Id { get; set; }
        public string ConfigName { get; set; } = "";
        public DbType DbType { get; set; }
        public string ServerAddress { get; set; } = "";
        public int Port { get; set; }
        public string DatabaseName { get; set; } = "";
        public string Account { get; set; } = "";
        public string EncryptedPassword { get; set; } = "";
        public int ConnectTimeout { get; set; } = 30;
        public string? Charset { get; set; }
        public DateTime CreateTime { get; set; } = DateTime.Now;
        public DateTime UpdateTime { get; set; } = DateTime.Now;

        public string DbTypeDisplay => DbType == DbType.SqlServer ? "SQL Server" : "MySQL";
        public string DisplayName => $"{ConfigName} ({ServerAddress}:{Port}/{DatabaseName})";
    }

    // ========================
    // 字典匹配
    // ========================
    public class DictMatchConfig
    {
        public int Id { get; set; }
        public string ConfigName { get; set; } = "";
        public string? ConfigDesc { get; set; }
        public int SourceDbConfigId { get; set; }
        public int TargetDbConfigId { get; set; }
        public string SourceDbName { get; set; } = "";
        public string TargetDbName { get; set; } = "";
        public DateTime CreateTime { get; set; } = DateTime.Now;
        public DateTime UpdateTime { get; set; } = DateTime.Now;
        public List<DictMatchDetail> Details { get; set; } = new();

        /// <summary>
        /// 可选：自定义查询SQL（支持多表JOIN）。
        /// 不为空时，SyncExecutor 用此SQL替代单表查询。
        /// 示例：SELECT h.BillNo, d.ItemCode FROM BOM_Header h JOIN BOM_Detail d ON h.BillNo=d.BillNo
        /// </summary>
        public string? CustomSql { get; set; }

        // Navigation
        public DbConfig? SourceDbConfig { get; set; }
        public DbConfig? TargetDbConfig { get; set; }

        public string SyncDirectionDisplay
        {
            get
            {
                var srcType = SourceDbConfig?.DbType;
                var dstType = TargetDbConfig?.DbType;
                return (srcType, dstType) switch
                {
                    (DbType.SqlServer, DbType.MySQL)     => "SQL Server → MySQL",
                    (DbType.MySQL,     DbType.SqlServer) => "MySQL → SQL Server",
                    (DbType.SqlServer, DbType.SqlServer) => "SQL Server → SQL Server",
                    (DbType.MySQL,     DbType.MySQL)     => "MySQL → MySQL",
                    _                                    => $"配置#{SourceDbConfigId} → 配置#{TargetDbConfigId}"
                };
            }
        }
    }

    public class DictMatchDetail
    {
        public int Id { get; set; }
        public int MatchConfigId { get; set; }
        public string SourceTableName { get; set; } = "";
        public string SourceFieldName { get; set; } = "";
        public string SourceFieldType { get; set; } = "";
        public string TargetTableName { get; set; } = "";
        public string TargetFieldName { get; set; } = "";
        public string TargetFieldType { get; set; } = "";
        public bool TypeWarning { get; set; }
        public int SortOrder { get; set; }
    }

    // ========================
    // API 配置
    // ========================
    public class ApiConfig
    {
        public int Id { get; set; }
        public string ApiName { get; set; } = "";
        public string Url { get; set; } = "";
        public HttpMethod Method { get; set; } = HttpMethod.POST;
        public int Timeout { get; set; } = 30;
        public List<ApiHeader> Headers { get; set; } = new();
        public string? BodyTemplate { get; set; }
        public string? JsonPath { get; set; }
        public string SuccessStatusCodes { get; set; } = "200";
        public bool IsVerified { get; set; }

        // ── 自动登录认证 ──
        /// <summary>是否启用自动登录获取Token</summary>
        public bool EnableAutoLogin { get; set; } = false;
        /// <summary>登录接口URL</summary>
        public string? LoginUrl { get; set; }
        /// <summary>登录请求体JSON</summary>
        public string? LoginBody { get; set; }
        /// <summary>从登录响应中取Token的路径，如 response.xtoken</summary>
        public string? TokenJsonPath { get; set; } = "response.xtoken";
        /// <summary>Token放入请求头的Key名</summary>
        public string TokenHeaderKey { get; set; } = "X-token";
        public DateTime CreateTime { get; set; } = DateTime.Now;
        public DateTime UpdateTime { get; set; } = DateTime.Now;
    }

    public class ApiHeader
    {
        public string Key { get; set; } = "";
        public string Value { get; set; } = "";
    }

    // ========================
    // 数据调用配置
    // ========================
    public class DataCallConfig
    {
        public int Id { get; set; }
        public string ConfigName { get; set; } = "";
        public SyncDirection SyncDirection { get; set; }
        public CallType CallType { get; set; } = CallType.Direct;
        public int DictMatchConfigId { get; set; }
        public int? ApiConfigId { get; set; }
        public SyncMode SyncMode { get; set; } = SyncMode.Full;
        public string? IncrementalField { get; set; }
        public string? WhereCondition { get; set; }
        public int BatchSize { get; set; } = 500;

        /// <summary>
        /// Upsert 唯一键字段（目标表）。不为空时启用 Upsert 模式：
        /// 目标库有该键值 → UPDATE，没有 → INSERT，数据一致 → 跳过并写日志。
        /// 多个字段用英文逗号分隔，如：BillNo,ItemCode
        /// </summary>
        public string? UpsertKeyFields { get; set; }

        /// <summary>
        /// 同步完成后在目标库执行的SQL（如存储过程调用、修正语句等）。
        /// 多条语句用分号分隔。
        /// 示例：CALL sp_fix_bom_parentid();
        /// </summary>
        public string? PostSyncSql { get; set; }

        /// <summary>
        /// 脚本模式：直接在目标库执行的SQL脚本。
        /// 支持多条语句（用分号分隔），存储过程调用（CALL/EXEC），INSERT SELECT 等。
        /// CallType = Script 时生效，不走字段映射逻辑。
        /// </summary>
        public string? ScriptSql { get; set; }
        public DateTime CreateTime { get; set; } = DateTime.Now;
        public DateTime UpdateTime { get; set; } = DateTime.Now;

        // Navigation
        public DictMatchConfig? DictMatchConfig { get; set; }
        public ApiConfig? ApiConfig { get; set; }

        public string SyncDirectionDisplay => SyncDirection switch
        {
            SyncDirection.SqlServerToMySQL     => "SQL Server → MySQL",
            SyncDirection.MySQLToSqlServer     => "MySQL → SQL Server",
            SyncDirection.SqlServerToSqlServer => "SQL Server → SQL Server",
            SyncDirection.MySQLToMySQL         => "MySQL → MySQL",
            _                                  => "未知"
        };
        public string CallTypeDisplay => CallType == CallType.Direct ? "直接插入" : "API处理";
    }

    // ========================
    // 任务配置
    // ========================
    public class TaskConfig
    {
        public int Id { get; set; }
        public string TaskName { get; set; } = "";
        public string? TaskDesc { get; set; }
        public int DataCallConfigId { get; set; }
        /// <summary>存储过程配置ID（与DataCallConfigId二选一）</summary>
        public int? ProcConfigId { get; set; }
        public TriggerType TriggerType { get; set; }
        public DateTime? ScheduledTime { get; set; }
        public int? IntervalValue { get; set; }
        public string? IntervalUnit { get; set; }
        public string? CronExpression { get; set; }
        public DateTime? StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public int MaxRows { get; set; } = 10000;
        public int RetryCount { get; set; } = 3;
        public TaskStatus Status { get; set; } = TaskStatus.Pending;
        public DateTime? LastRunTime { get; set; }
        public string? LastRunStatus { get; set; }
        public DateTime CreateTime { get; set; } = DateTime.Now;
        public DateTime UpdateTime { get; set; } = DateTime.Now;

        // Navigation
        public DataCallConfig? DataCallConfig { get; set; }

        public string TriggerTypeDisplay => TriggerType switch
        {
            TriggerType.Once => "一次性",
            TriggerType.Interval => "循环",
            TriggerType.Cron => "定时",
            _ => ""
        };
    }

    // ========================
    // 存储过程配置
    // ========================
    public class ProcConfig
    {
        public string? WriteBackSql { get; set; }
        public int WriteBackDbConfigId { get; set; }
        public int Id { get; set; }
        public string ConfigName { get; set; } = "";
        public string? ConfigDesc { get; set; }

        // 源库
        public int SourceDbConfigId { get; set; }
        public string SourceSql { get; set; } = "";  // SELECT语句，从源库读数据

        // 目标库
        public int TargetDbConfigId { get; set; }
        public string TargetTable { get; set; } = "";

        // 字段映射（SQL别名 → 目标字段，支持固定值/表达式）
        public List<ProcFieldMapping> FieldMappings { get; set; } = new();

        // 同步控制
        public SyncMode SyncMode { get; set; } = SyncMode.Full;
        public string? UpsertKeyFields { get; set; }
        public int BatchSize { get; set; } = 500;
        public string? WhereCondition { get; set; }

        // 同步后执行
        public string? PostSyncSql { get; set; }

        /// <summary>输出方式：Direct=直接写入目标表，Api=调用API写入</summary>
        public string OutputMode { get; set; } = "Direct";

        /// <summary>API模式下使用的API配置ID</summary>
        public int? ApiConfigId { get; set; }

        /// <summary>API批次模式：Single=每条单独调用，Batch=整批打包为数组调用</summary>
        public string ApiBodyMode { get; set; } = "Single";

        /// <summary>API并发数（同时发送的请求数，默认1，建议不超过10）</summary>
        public int ApiConcurrency { get; set; } = 5;
        /// <summary>BOM分组键字段名（如RootCode），按此字段分组后每组打包成数组一次POST，空则不分组</summary>
        public string? BomGroupKey { get; set; }
        /// <summary>嵌套子数组模板（用于主表+明细结构），主模板中用[[details]]占位</summary>
        public string? DetailsTemplate { get; set; }
        /// <summary>API模式下用于查重和映射的目标数据库ID</summary>
        public int ApiTargetDbConfigId { get; set; } = 0;

        /// <summary>API模式下是否启用查重（已存在则跳过不调API）</summary>
        public bool ApiEnableUpsert { get; set; } = false;
        /// <summary>API查重目标表（MySQL表名，如 product_info）</summary>
        public string? ApiUpsertTable { get; set; }
        /// <summary>API查重唯一键字段（目标表字段名，多个逗号分隔，如 Code）</summary>
        public string? ApiUpsertKeyFields { get; set; }

        public DateTime CreateTime { get; set; } = DateTime.Now;
        public DateTime UpdateTime { get; set; } = DateTime.Now;

        // Navigation
        public DbConfig? SourceDbConfig { get; set; }
        public DbConfig? TargetDbConfig { get; set; }
        public ApiConfig? ApiConfig { get; set; }
        public string SyncModeDisplay => SyncMode == SyncMode.Full ? "全量" : "增量";
        public string OutputModeDisplay => OutputMode == "Api" ? "调用API" : "直接写入";
    }

    public class ProcFieldMapping
    {
        public int SortOrder { get; set; }
        /// <summary>源字段：SQL别名 或 留空表示使用固定值/表达式</summary>
        public string SourceAlias { get; set; } = "";
        /// <summary>目标字段名</summary>
        public string TargetField { get; set; } = "";
        /// <summary>目标字段类型</summary>
        public string TargetFieldType { get; set; } = "";
        /// <summary>固定值或表达式（SourceAlias为空时使用）</summary>
        public string? DefaultValue { get; set; }
        /// <summary>映射类型：Field=字段映射, Const=固定值</summary>
        public string MappingType { get; set; } = "Field";
    }

    // ========================
    // 日志
    // ========================
    public class LogEntry
    {
        public DateTime Timestamp { get; set; }
        public LogLevel Level { get; set; }
        public string Module { get; set; } = "";
        public string Message { get; set; } = "";
        public string? ExceptionType { get; set; }
        public string? StackTrace { get; set; }
        public string? TaskId { get; set; }
    }

    // ========================
    // 表结构元数据
    // ========================
    public class TableInfo
    {
        public string TableName { get; set; } = "";
        public List<ColumnInfo> Columns { get; set; } = new();
    }

    public class ColumnInfo
    {
        public string ColumnName { get; set; } = "";
        public string DataType { get; set; } = "";
        public int? MaxLength { get; set; }
        public bool IsNullable { get; set; }
        public bool IsPrimaryKey { get; set; }
        public string DisplayInfo => $"{ColumnName} ({DataType}{(MaxLength.HasValue ? $"({MaxLength})" : "")}, {(IsNullable ? "NULL" : "NOT NULL")})";
    }

    // ========================
    // 菜单项
    // ========================
    public class MenuItem
    {
        public string Title { get; set; } = "";
        public string Icon { get; set; } = "";
        public string? TargetPage { get; set; }
        public List<MenuItem> Children { get; set; } = new();
        public bool HasChildren => Children.Count > 0;
    }

    // ========================
    // 同步进度
    // ========================
    public class SyncProgress
    {
        public int Total { get; set; }
        public int Processed { get; set; }
        public int Success { get; set; }
        public int Failed { get; set; }
        public TimeSpan Elapsed { get; set; }
        public string Status { get; set; } = "";
    }
}
