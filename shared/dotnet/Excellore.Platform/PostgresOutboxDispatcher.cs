using System.Data;
using System.Data.Common;
using System.Text;

namespace Excellore.Platform;

/// <summary>Publishes one pending outbox event and marks it sent after broker confirmation.</summary>
public sealed class PostgresOutboxDispatcher
{
    public async Task<bool> DispatchOnceAsync(DbConnection connection, IEventPublisher publisher,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(publisher);
        if (connection.State != ConnectionState.Open)
        {
            throw new InvalidOperationException("An open database connection is required.");
        }

        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted,
            cancellationToken);
        var eventId = Guid.Empty;
        var eventType = string.Empty;
        Guid? tenantId = null;
        byte[] body = [];
        bool found;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT id, type, tenant_id, payload::text
                FROM outbox
                WHERE published_at IS NULL
                ORDER BY created_at, id
                LIMIT 1
                FOR UPDATE SKIP LOCKED
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            found = await reader.ReadAsync(cancellationToken);
            if (found)
            {
                eventId = reader.GetGuid(0);
                eventType = reader.GetString(1);
                tenantId = reader.IsDBNull(2) ? null : reader.GetGuid(2);
                body = Encoding.UTF8.GetBytes(reader.GetString(3));
            }
        }
        if (!found)
        {
            await transaction.CommitAsync(cancellationToken);
            return false;
        }

        PostgresEventOutbox.ValidateEnvelope(eventId, eventType, tenantId, body);
        await publisher.PublishAsync(eventId, eventType, body, cancellationToken);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "UPDATE outbox SET published_at = now() WHERE id = @id AND published_at IS NULL";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "id";
            parameter.DbType = DbType.Guid;
            parameter.Value = eventId;
            command.Parameters.Add(parameter);
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidOperationException("The outbox row could not be marked published.");
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }
}
