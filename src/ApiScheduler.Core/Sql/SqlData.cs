using System.Data;
using ApiScheduler.Core.Models;
using ApiScheduler.Core.Security;
using Microsoft.Data.SqlClient;

namespace ApiScheduler.Core.Sql;

public sealed class DataRowValues : Dictionary<string, object?>
{
    public DataRowValues() : base(StringComparer.OrdinalIgnoreCase) { }
}

public sealed record QueryResult(IReadOnlyList<string> Columns, IReadOnlyList<DataRowValues> Rows);

public static class SqlData
{
    public static string BuildConnectionString(DbConnectionProfile p)
    {
        var b = new SqlConnectionStringBuilder
        {
            DataSource = p.Server,
            InitialCatalog = p.Database,
            Encrypt = p.Encrypt ? SqlConnectionEncryptOption.Mandatory : SqlConnectionEncryptOption.Optional,
            TrustServerCertificate = p.TrustServerCertificate,
            ApplicationName = "ApiScheduler",
            ConnectTimeout = 30,
            PersistSecurityInfo = false,
        };
        if (p.AuthMode == DbAuthMode.Windows)
        {
            b.IntegratedSecurity = true;
        }
        else
        {
            b.IntegratedSecurity = false;
            b.UserID = p.SqlUser;
            b.Password = SecretProtector.Unprotect(p.EncryptedSqlPassword ?? "");
        }
        return b.ConnectionString;
    }

    public static async Task<SqlConnection> OpenAsync(DbConnectionProfile p, CancellationToken ct)
    {
        var conn = new SqlConnection(BuildConnectionString(p));
        try
        {
            await conn.OpenAsync(ct);
            return conn;
        }
        catch
        {
            await conn.DisposeAsync();
            throw;
        }
    }

    /// <summary>Runs a validated SELECT and returns at most <paramref name="maxRows"/> rows.</summary>
    public static async Task<QueryResult> QueryAsync(SqlConnection conn, string selectSql, int maxRows, CancellationToken ct)
    {
        var errors = SqlSafety.ValidateSelect(selectSql);
        if (errors.Count > 0) throw new InvalidOperationException("Query rejected: " + string.Join(" ", errors));

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = selectSql;
        cmd.CommandType = CommandType.Text;
        cmd.CommandTimeout = 120;

        var rows = new List<DataRowValues>();
        await using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
        var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToList();
        while (rows.Count < maxRows && await reader.ReadAsync(ct))
        {
            var row = new DataRowValues();
            for (int i = 0; i < reader.FieldCount; i++)
            {
                var v = await reader.IsDBNullAsync(i, ct) ? null : reader.GetValue(i);
                row[columns[i]] = v;   // last one wins on duplicate column names
            }
            rows.Add(row);
        }
        if (rows.Count >= maxRows) cmd.Cancel();   // stop the server streaming the rest
        return new QueryResult(columns, rows);
    }

    public static async Task<int> MarkRowAsync(SqlConnection conn, WriteBackSettings wb, object keyValue, bool success, string sentCallValue, CancellationToken ct)
    {
        var table = SqlSafety.QuoteIdentifier(wb.TableName);
        var key = SqlSafety.QuoteIdentifier(wb.KeyColumn);
        var call = SqlSafety.QuoteIdentifier(wb.SentCallColumn);
        var date = SqlSafety.QuoteIdentifier(wb.SentDateColumn);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = success
            ? $"UPDATE {table} SET {call} = @call, {date} = GETDATE() WHERE {key} = @key AND {date} IS NULL;"
            : $"UPDATE {table} SET {call} = @call WHERE {key} = @key AND {date} IS NULL;";
        cmd.Parameters.Add(new SqlParameter("@call", SqlDbType.VarChar, 50) { Value = Truncate(sentCallValue, 50) });
        cmd.Parameters.AddWithValue("@key", keyValue);
        cmd.CommandTimeout = 30;
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n];
}
