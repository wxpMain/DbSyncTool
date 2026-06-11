using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using MySqlConnector;
using DbSyncTool.Models;
using DbSyncTool.Helpers;

namespace DbSyncTool.DataAccess
{
    /// <summary>
    /// SQL Server 数据库访问助手
    /// </summary>
    public class SqlServerHelper
    {
        private readonly string _connectionString;

        public SqlServerHelper(DbConfig config)
        {
            var pwd = ConfigStore.Decrypt(config.EncryptedPassword);
            _connectionString = $"Server={config.ServerAddress},{config.Port};" +
                $"Database={config.DatabaseName};" +
                $"User Id={config.Account};Password={pwd};" +
                $"Connect Timeout={config.ConnectTimeout};" +
                $"TrustServerCertificate=True;Encrypt=False;" +
                $"Pooling=False;";
        }

        public SqlServerHelper(string connectionString)
        {
            _connectionString = connectionString;
        }

        public static async Task<bool> TestConnectionAsync(DbConfig config)
        {
            try
            {
                var helper = new SqlServerHelper(config);
                await using var conn = new SqlConnection(helper._connectionString);
                await conn.OpenAsync();
                return true;
            }
            catch { return false; }
        }

        public async Task<List<TableInfo>> GetTablesAsync()
        {
            var tables = new List<TableInfo>();
            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();

            // 获取表列表
            var tableCmd = conn.CreateCommand();
            tableCmd.CommandText = @"
                SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES 
                WHERE TABLE_TYPE='BASE TABLE' AND TABLE_SCHEMA='dbo'
                ORDER BY TABLE_NAME";
            await using var reader = await tableCmd.ExecuteReaderAsync();
            var tableNames = new List<string>();
            while (await reader.ReadAsync())
                tableNames.Add(reader.GetString(0));
            await reader.CloseAsync();

            foreach (var tbl in tableNames)
            {
                var ti = new TableInfo { TableName = tbl };
                var colCmd = conn.CreateCommand();
                colCmd.CommandText = @"
                    SELECT c.COLUMN_NAME, c.DATA_TYPE, c.CHARACTER_MAXIMUM_LENGTH, c.IS_NULLABLE,
                           CASE WHEN pk.COLUMN_NAME IS NOT NULL THEN 1 ELSE 0 END AS IsPK
                    FROM INFORMATION_SCHEMA.COLUMNS c
                    LEFT JOIN (
                        SELECT ku.COLUMN_NAME FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
                        JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE ku ON tc.CONSTRAINT_NAME=ku.CONSTRAINT_NAME
                        WHERE tc.CONSTRAINT_TYPE='PRIMARY KEY' AND tc.TABLE_NAME=@tbl
                    ) pk ON c.COLUMN_NAME=pk.COLUMN_NAME
                    WHERE c.TABLE_NAME=@tbl ORDER BY c.ORDINAL_POSITION";
                colCmd.Parameters.AddWithValue("@tbl", tbl);
                await using var colReader = await colCmd.ExecuteReaderAsync();
                while (await colReader.ReadAsync())
                {
                    ti.Columns.Add(new ColumnInfo
                    {
                        ColumnName = colReader.GetString(0),
                        DataType = colReader.GetString(1).ToUpper(),
                        MaxLength = colReader.IsDBNull(2) ? null : (int?)colReader.GetInt32(2),
                        IsNullable = colReader.GetString(3) == "YES",
                        IsPrimaryKey = colReader.GetInt32(4) == 1
                    });
                }
                tables.Add(ti);
            }
            return tables;
        }

        public async Task<List<Dictionary<string, object?>>> QueryPagedAsync(string sql, int page, int pageSize, string? where = null)
        {
            var result = new List<Dictionary<string, object?>>();
            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();
            var cmd = conn.CreateCommand();
            var offset = page * pageSize;
            var fullSql = $"SELECT * FROM ({sql}) AS _t {(string.IsNullOrWhiteSpace(where) ? "" : $"WHERE {where}")} ORDER BY (SELECT NULL) OFFSET {offset} ROWS FETCH NEXT {pageSize} ROWS ONLY";
            cmd.CommandText = fullSql;
            cmd.CommandTimeout = 120;
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var row = new Dictionary<string, object?>();
                for (int i = 0; i < reader.FieldCount; i++)
                    row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                result.Add(row);
            }
            return result;
        }

        public async Task<int> GetCountAsync(string sql, string? where = null)
        {
            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();
            var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM ({sql}) AS _t {(string.IsNullOrWhiteSpace(where) ? "" : $"WHERE {where}")}";
            var result = await cmd.ExecuteScalarAsync();
            return Convert.ToInt32(result);
        }

        public async Task TruncateTableAsync(string tableName)
        {
            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();
            var cmd = conn.CreateCommand();
            cmd.CommandText = $"TRUNCATE TABLE [{tableName}]";
            await cmd.ExecuteNonQueryAsync();
        }

        /// <summary>执行任意非查询SQL（UPDATE/DELETE/CALL等）</summary>
        public async Task ExecuteNonQueryAsync(string sql)
        {
            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();
            var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = 300;
            await cmd.ExecuteNonQueryAsync();
        }

        /// <summary>用完整SQL验证语法（SELECT TOP 0）</summary>
        public async Task<int> GetCountByTableQueryAsync(string sql)
        {
            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();
            var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = 30;
            await cmd.ExecuteNonQueryAsync();
            return 0;
        }

        /// <summary>按表名+WHERE子句统计行数</summary>
        public async Task<int> GetCountByTableAsync(string tableName, string whereClause)
        {
            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();
            var cmd = conn.CreateCommand();
            var where = string.IsNullOrWhiteSpace(whereClause) ? "" : $" WHERE {whereClause}";
            cmd.CommandText = $"SELECT COUNT(*) FROM [{tableName}]{where}";
            cmd.CommandTimeout = 120;
            var r = await cmd.ExecuteScalarAsync();
            return Convert.ToInt32(r);
        }

        /// <summary>按表名+列列表分页查询（ROW_NUMBER兼容SQL Server 2008+）</summary>
        public async Task<List<Dictionary<string, object?>>> QueryPageByTableAsync(
            string tableName, List<string> columns, string whereClause, int page, int pageSize)
        {
            var result = new List<Dictionary<string, object?>>();
            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();
            var cmd = conn.CreateCommand();
            var colList = string.Join(",", columns.Select(c => $"[{c}]"));
            var where = string.IsNullOrWhiteSpace(whereClause) ? "" : $" WHERE {whereClause}";
            var rowStart = page * pageSize + 1;
            var rowEnd   = (page + 1) * pageSize;
            cmd.CommandText = $@"
                SELECT {colList} FROM (
                    SELECT {colList}, ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS _rn
                    FROM [{tableName}]{where}
                ) AS _paged
                WHERE _rn BETWEEN {rowStart} AND {rowEnd}";
            cmd.CommandTimeout = 120;
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var row = new Dictionary<string, object?>();
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    var name = reader.GetName(i);
                    if (name == "_rn") continue;  // 跳过行号列
                    row[name] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                }
                result.Add(row);
            }
            return result;
        }

        /// <summary>用自定义SQL（子查询）统计总行数</summary>
        public async Task<int> GetCountByQueryAsync(string sql, string whereClause)
        {
            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();
            var cmd = conn.CreateCommand();
            var where = string.IsNullOrWhiteSpace(whereClause) ? "" : $" WHERE {whereClause}";
            cmd.CommandText = $"SELECT COUNT(*) FROM ({sql}) AS _src{where}";
            cmd.CommandTimeout = 120;
            return Convert.ToInt32(await cmd.ExecuteScalarAsync());
        }

        /// <summary>用自定义SQL（子查询）分页查询（ROW_NUMBER兼容SQL Server 2008+）</summary>
        public async Task<List<Dictionary<string, object?>>> QueryPageByQueryAsync(
            string sql, List<string> columns, string whereClause, int page, int pageSize)
        {
            var result = new List<Dictionary<string, object?>>();
            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();
            var cmd = conn.CreateCommand();
            // 自定义SQL已含别名，外层直接 SELECT * 取所有列
            var where = string.IsNullOrWhiteSpace(whereClause) ? "" : $" WHERE {whereClause}";
            var rowStart = page * pageSize + 1;
            var rowEnd   = (page + 1) * pageSize;
            cmd.CommandText = $@"
                SELECT * FROM (
                    SELECT *, ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS _rn
                    FROM ({sql}) AS _src{where}
                ) AS _paged
                WHERE _rn BETWEEN {rowStart} AND {rowEnd}";
            cmd.CommandTimeout = 120;
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var row = new Dictionary<string, object?>();
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    var name = reader.GetName(i);
                    if (name == "_rn") continue;
                    row[name] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                }
                result.Add(row);
            }
            return result;
        }

        /// <summary>查找目标表中匹配唯一键的一行，不存在返回 null</summary>
        public async Task<Dictionary<string, object?>?> FindRowAsync(
            string tableName, List<string> keyFields, Dictionary<string, object?> row)
        {
            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();
            var cmd = conn.CreateCommand();
            var whereParts = keyFields.Select((k, i) => $"[{k}] = @k{i}").ToList();
            cmd.CommandText = $"SELECT TOP 1 * FROM [{tableName}] WHERE {string.Join(" AND ", whereParts)}";
            for (int i = 0; i < keyFields.Count; i++)
                cmd.Parameters.AddWithValue($"@k{i}", row.TryGetValue(keyFields[i], out var v) ? (v ?? DBNull.Value) : DBNull.Value);
            cmd.CommandTimeout = 30;
            await using var reader = await cmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) return null;
            var result = new Dictionary<string, object?>();
            for (int i = 0; i < reader.FieldCount; i++)
                result[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            return result;
        }

        /// <summary>按唯一键 UPDATE 一行</summary>
        public async Task UpdateRowAsync(
            string tableName, List<string> keyFields, List<string> allCols, Dictionary<string, object?> row)
        {
            var updateCols = allCols.Except(keyFields).ToList();
            if (updateCols.Count == 0) return;
            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();
            var cmd = conn.CreateCommand();
            var setClauses  = updateCols.Select((c, i) => $"[{c}] = @s{i}").ToList();
            var whereClauses = keyFields.Select((k, i) => $"[{k}] = @w{i}").ToList();
            cmd.CommandText = $"UPDATE [{tableName}] SET {string.Join(",", setClauses)} WHERE {string.Join(" AND ", whereClauses)}";
            for (int i = 0; i < updateCols.Count; i++)
                cmd.Parameters.AddWithValue($"@s{i}", row.TryGetValue(updateCols[i], out var v) ? (v ?? DBNull.Value) : DBNull.Value);
            for (int i = 0; i < keyFields.Count; i++)
                cmd.Parameters.AddWithValue($"@w{i}", row.TryGetValue(keyFields[i], out var v) ? (v ?? DBNull.Value) : DBNull.Value);
            cmd.CommandTimeout = 30;
            await cmd.ExecuteNonQueryAsync();
        }

        /// <summary>
        /// 批量写入 SQL Server —— 用多行 INSERT 替代 SqlBulkCopy。
        /// SqlBulkCopy 在旧版 SQL Server（2016及以下）上会发起第二次 TLS 握手，
        /// 与 Microsoft.Data.SqlClient 5.x 的 TLS 1.2 不兼容，导致"内部连接致命错误"。
        /// 改用参数化 INSERT 完全走同一条连接，彻底规避该问题。
        /// </summary>
        public async Task BulkInsertAsync(string targetTable, List<string> columns, List<Dictionary<string, object?>> rows)
        {
            if (rows.Count == 0) return;

            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();
            await using var tran = conn.BeginTransaction();
            try
            {
                // 每次最多 500 行拼一条 INSERT，避免参数超限（SQL Server 上限 2100 个参数）
                int batchSize = Math.Max(1, 2000 / columns.Count);
                var colList = string.Join(",", columns.Select(c => $"[{c}]"));

                for (int i = 0; i < rows.Count; i += batchSize)
                {
                    var batch = rows.Skip(i).Take(batchSize).ToList();
                    var valueClauses = new List<string>();
                    var cmd = conn.CreateCommand();
                    cmd.Transaction = tran;
                    cmd.CommandTimeout = 300;

                    for (int r = 0; r < batch.Count; r++)
                    {
                        var paramNames = columns.Select(c => $"@p{r}_{c}").ToList();
                        valueClauses.Add($"({string.Join(",", paramNames)})");
                        for (int c = 0; c < columns.Count; c++)
                        {
                            var val = batch[r].TryGetValue(columns[c], out var v) ? v : null;
                            cmd.Parameters.AddWithValue($"@p{r}_{columns[c]}", val ?? DBNull.Value);
                        }
                    }

                    cmd.CommandText = $"INSERT INTO [{targetTable}] ({colList}) VALUES {string.Join(",", valueClauses)}";
                    await cmd.ExecuteNonQueryAsync();
                }

                await tran.CommitAsync();
            }
            catch
            {
                await tran.RollbackAsync();
                throw;
            }
        }
    }

    /// <summary>
    /// MySQL 数据库访问助手
    /// </summary>
    public class MySqlHelper
    {
        private readonly string _connectionString;

        public MySqlHelper(DbConfig config)
        {
            var pwd = ConfigStore.Decrypt(config.EncryptedPassword);
            _connectionString = $"Server={config.ServerAddress};Port={config.Port};" +
                $"Database={config.DatabaseName};" +
                $"User={config.Account};Password={pwd};" +
                $"CharSet={config.Charset ?? "utf8mb4"};" +
                $"ConnectionTimeout={config.ConnectTimeout};";
        }

        public static async Task<bool> TestConnectionAsync(DbConfig config)
        {
            try
            {
                var helper = new MySqlHelper(config);
                await using var conn = new MySqlConnection(helper._connectionString);
                await conn.OpenAsync();
                return true;
            }
            catch { return false; }
        }

        public async Task<List<TableInfo>> GetTablesAsync()
        {
            var tables = new List<TableInfo>();
            await using var conn = new MySqlConnection(_connectionString);
            await conn.OpenAsync();

            // conn.Database 有时返回空，直接从连接字符串解析库名更可靠
            var builder = new MySqlConnector.MySqlConnectionStringBuilder(_connectionString);
            var dbName = !string.IsNullOrWhiteSpace(conn.Database)
                ? conn.Database
                : builder.Database;

            var tableCmd = conn.CreateCommand();
            tableCmd.CommandText = $"SELECT TABLE_NAME FROM information_schema.TABLES WHERE TABLE_SCHEMA='{dbName}' AND TABLE_TYPE='BASE TABLE' ORDER BY TABLE_NAME";
            await using var reader = await tableCmd.ExecuteReaderAsync();
            var tableNames = new List<string>();
            while (await reader.ReadAsync())
                tableNames.Add(reader.GetString(0));
            await reader.CloseAsync();

            foreach (var tbl in tableNames)
            {
                var ti = new TableInfo { TableName = tbl };
                var colCmd = conn.CreateCommand();
                colCmd.CommandText = $@"
                    SELECT COLUMN_NAME, DATA_TYPE, CHARACTER_MAXIMUM_LENGTH, IS_NULLABLE, COLUMN_KEY
                    FROM information_schema.COLUMNS
                    WHERE TABLE_SCHEMA='{dbName}' AND TABLE_NAME='{tbl}'
                    ORDER BY ORDINAL_POSITION";
                await using var colReader = await colCmd.ExecuteReaderAsync();
                while (await colReader.ReadAsync())
                {
                    ti.Columns.Add(new ColumnInfo
                    {
                        ColumnName = colReader.GetString(0),
                        DataType = colReader.GetString(1).ToUpper(),
                        MaxLength = colReader.IsDBNull(2) ? null : (int?)colReader.GetInt64(2),
                        IsNullable = colReader.GetString(3) == "YES",
                        IsPrimaryKey = colReader.GetString(4) == "PRI"
                    });
                }
                tables.Add(ti);
            }
            return tables;
        }

        public async Task<List<Dictionary<string, object?>>> QueryPagedAsync(string sql, int page, int pageSize, string? where = null)
        {
            var result = new List<Dictionary<string, object?>>();
            await using var conn = new MySqlConnection(_connectionString);
            await conn.OpenAsync();
            var cmd = conn.CreateCommand();
            var offset = page * pageSize;
            cmd.CommandText = $"SELECT * FROM ({sql}) AS _t {(string.IsNullOrWhiteSpace(where) ? "" : $"WHERE {where}")} LIMIT {pageSize} OFFSET {offset}";
            cmd.CommandTimeout = 120;
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var row = new Dictionary<string, object?>();
                for (int i = 0; i < reader.FieldCount; i++)
                    row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                result.Add(row);
            }
            return result;
        }

        /// <summary>查找目标表中匹配唯一键的一行，不存在返回 null</summary>
        public async Task<Dictionary<string, object?>?> FindRowAsync(
            string tableName, List<string> keyFields, Dictionary<string, object?> row)
        {
            await using var conn = new MySqlConnection(_connectionString);
            await conn.OpenAsync();
            var cmd = conn.CreateCommand();
            var whereParts = keyFields.Select((k, i) => $"`{k}` = @k{i}").ToList();
            cmd.CommandText = $"SELECT * FROM `{tableName}` WHERE {string.Join(" AND ", whereParts)} LIMIT 1";
            for (int i = 0; i < keyFields.Count; i++)
                cmd.Parameters.AddWithValue($"@k{i}", row.TryGetValue(keyFields[i], out var v) ? (v ?? DBNull.Value) : DBNull.Value);
            cmd.CommandTimeout = 30;
            await using var reader = await cmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) return null;
            var result = new Dictionary<string, object?>();
            for (int i = 0; i < reader.FieldCount; i++)
                result[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            return result;
        }

        /// <summary>按唯一键 UPDATE 一行</summary>
        public async Task UpdateRowAsync(
            string tableName, List<string> keyFields, List<string> allCols, Dictionary<string, object?> row)
        {
            var updateCols = allCols.Except(keyFields).ToList();
            if (updateCols.Count == 0) return;
            await using var conn = new MySqlConnection(_connectionString);
            await conn.OpenAsync();
            var cmd = conn.CreateCommand();
            var setClauses   = updateCols.Select((c, i) => $"`{c}` = @s{i}").ToList();
            var whereClauses = keyFields.Select((k, i) => $"`{k}` = @w{i}").ToList();
            cmd.CommandText = $"UPDATE `{tableName}` SET {string.Join(",", setClauses)} WHERE {string.Join(" AND ", whereClauses)}";
            for (int i = 0; i < updateCols.Count; i++)
                cmd.Parameters.AddWithValue($"@s{i}", row.TryGetValue(updateCols[i], out var v) ? (v ?? DBNull.Value) : DBNull.Value);
            for (int i = 0; i < keyFields.Count; i++)
                cmd.Parameters.AddWithValue($"@w{i}", row.TryGetValue(keyFields[i], out var v) ? (v ?? DBNull.Value) : DBNull.Value);
            cmd.CommandTimeout = 30;
            await cmd.ExecuteNonQueryAsync();
        }

        public async Task BatchInsertAsync(string targetTable, List<string> columns, List<Dictionary<string, object?>> rows, int batchSize = 500)
        {
            if (rows.Count == 0) return;
            await using var conn = new MySqlConnection(_connectionString);
            await conn.OpenAsync();

            var colList = string.Join(",", columns.Select(c => $"`{c}`"));

            for (int i = 0; i < rows.Count; i += batchSize)
            {
                var batch = rows.Skip(i).Take(batchSize).ToList();
                var valueParts = new List<string>();
                var cmd = conn.CreateCommand();
                int paramIdx = 0;

                foreach (var row in batch)
                {
                    var paramNames = columns.Select(c => { var p = $"@p{paramIdx++}"; return p; }).ToList();
                    valueParts.Add($"({string.Join(",", paramNames)})");
                    int pi = paramIdx - columns.Count;
                    foreach (var col in columns)
                    {
                        cmd.Parameters.AddWithValue($"@p{pi++}", row.ContainsKey(col) ? (row[col] ?? DBNull.Value) : DBNull.Value);
                    }
                }

                cmd.CommandText = $"INSERT INTO `{targetTable}` ({colList}) VALUES {string.Join(",", valueParts)}";
                cmd.CommandTimeout = 300;
                await cmd.ExecuteNonQueryAsync();
            }
        }

        public async Task TruncateTableAsync(string tableName)
        {
            await using var conn = new MySqlConnection(_connectionString);
            await conn.OpenAsync();
            var cmd = conn.CreateCommand();
            cmd.CommandText = $"TRUNCATE TABLE `{tableName}`";
            await cmd.ExecuteNonQueryAsync();
        }

        /// <summary>执行任意非查询SQL（UPDATE/DELETE/CALL等）</summary>
        public async Task ExecuteNonQueryAsync(string sql)
        {
            await using var conn = new MySqlConnection(_connectionString);
            await conn.OpenAsync();
            var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = 300;
            await cmd.ExecuteNonQueryAsync();
        }

        /// <summary>用完整SQL验证语法（SELECT * LIMIT 0）</summary>
        public async Task<int> GetCountByTableQueryAsync(string sql)
        {
            await using var conn = new MySqlConnection(_connectionString);
            await conn.OpenAsync();
            var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = 30;
            await cmd.ExecuteNonQueryAsync();
            return 0;
        }

        /// <summary>按表名+WHERE子句统计行数</summary>
        public async Task<int> GetCountByTableAsync(string tableName, string whereClause)
        {
            await using var conn = new MySqlConnection(_connectionString);
            await conn.OpenAsync();
            var cmd = conn.CreateCommand();
            var where = string.IsNullOrWhiteSpace(whereClause) ? "" : $" WHERE {whereClause}";
            cmd.CommandText = $"SELECT COUNT(*) FROM `{tableName}`{where}";
            cmd.CommandTimeout = 120;
            var r = await cmd.ExecuteScalarAsync();
            return Convert.ToInt32(r);
        }

        /// <summary>用自定义SQL（子查询）统计总行数</summary>
        public async Task<int> GetCountByQueryAsync(string sql, string whereClause)
        {
            await using var conn = new MySqlConnection(_connectionString);
            await conn.OpenAsync();
            var cmd = conn.CreateCommand();
            var where = string.IsNullOrWhiteSpace(whereClause) ? "" : $" WHERE {whereClause}";
            cmd.CommandText = $"SELECT COUNT(*) FROM ({sql}) AS _src{where}";
            cmd.CommandTimeout = 120;
            return Convert.ToInt32(await cmd.ExecuteScalarAsync());
        }

        /// <summary>用自定义SQL（子查询）分页查询</summary>
        public async Task<List<Dictionary<string, object?>>> QueryPageByQueryAsync(
            string sql, List<string> columns, string whereClause, int page, int pageSize)
        {
            var result = new List<Dictionary<string, object?>>();
            await using var conn = new MySqlConnection(_connectionString);
            await conn.OpenAsync();
            var cmd = conn.CreateCommand();
            // 自定义SQL已含别名，外层直接 SELECT * 取所有列
            var where = string.IsNullOrWhiteSpace(whereClause) ? "" : $" WHERE {whereClause}";
            var offset = page * pageSize;
            cmd.CommandText = $"SELECT * FROM ({sql}) AS _src{where} LIMIT {pageSize} OFFSET {offset}";
            cmd.CommandTimeout = 120;
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var row = new Dictionary<string, object?>();
                for (int i = 0; i < reader.FieldCount; i++)
                    row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                result.Add(row);
            }
            return result;
        }

        /// <summary>按表名+列列表分页查询</summary>
        public async Task<List<Dictionary<string, object?>>> QueryPageByTableAsync(
            string tableName, List<string> columns, string whereClause, int page, int pageSize)
        {
            var result = new List<Dictionary<string, object?>>();
            await using var conn = new MySqlConnection(_connectionString);
            await conn.OpenAsync();
            var cmd = conn.CreateCommand();
            var colList = string.Join(",", columns.Select(c => $"`{c}`"));
            var where = string.IsNullOrWhiteSpace(whereClause) ? "" : $" WHERE {whereClause}";
            var offset = page * pageSize;
            cmd.CommandText = $"SELECT {colList} FROM `{tableName}`{where} LIMIT {pageSize} OFFSET {offset}";
            cmd.CommandTimeout = 120;
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var row = new Dictionary<string, object?>();
                for (int i = 0; i < reader.FieldCount; i++)
                    row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                result.Add(row);
            }
            return result;
        }
    }
}
