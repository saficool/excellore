using System.Data;
using System.Data.Common;

namespace Excellore.Platform;

/// <summary>Runs a database event handler once, atomically with its inbox marker.</summary>
public sealed class PostgresEventInbox
{
    public async Task<bool> RunOnceAsync(DbConnection connection, Guid eventId, Guid? tenantId,
        Func<DbTransaction, CancellationToken, Task> work,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(work);
        if (eventId == Guid.Empty || tenantId == Guid.Empty)
        {
            throw new ArgumentException("A valid event ID and tenant context are required.");
        }
        if (connection.State != ConnectionState.Open)
        {
            throw new InvalidOperationException("An open database connection is required.");
        }

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await SetTenantContextAsync(connection, transaction, tenantId, cancellationToken);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO inbox (event_id) VALUES (@event_id) ON CONFLICT (event_id) DO NOTHING";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "event_id";
            parameter.DbType = DbType.Guid;
            parameter.Value = eventId;
            command.Parameters.Add(parameter);
            if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
            {
                return false;
            }
        }

        await work(transaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private static async Task SetTenantContextAsync(DbConnection connection, DbTransaction transaction,
        Guid? tenantId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT set_config('app.tenant_id', @tenant_id, true)";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "tenant_id";
        parameter.DbType = DbType.String;
        parameter.Value = tenantId?.ToString("D") ?? string.Empty;
        command.Parameters.Add(parameter);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
