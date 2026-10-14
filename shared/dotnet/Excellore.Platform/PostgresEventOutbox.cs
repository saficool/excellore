using System.Data;
using System.Data.Common;
using System.Text;
using System.Text.Json;

namespace Excellore.Platform;

/// <summary>Stores an event in the caller's PostgreSQL transaction for later publication.</summary>
public sealed class PostgresEventOutbox
{
    private const int MaxPayloadBytes = 256 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public async Task EnqueueAsync(DbTransaction transaction, Guid eventId, string eventType,
        Guid? tenantId, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        var json = ValidateEnvelope(eventId, eventType, tenantId, payload);

        var connection = transaction.Connection ??
                         throw new InvalidOperationException("An active database transaction is required.");
        if (connection.State != ConnectionState.Open)
        {
            throw new InvalidOperationException("An open database connection is required.");
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO outbox (id, type, tenant_id, payload)
            VALUES (@id, @type, @tenant_id, CAST(@payload AS jsonb))
            """;
        AddParameter(command, "id", DbType.Guid, eventId);
        AddParameter(command, "type", DbType.String, eventType);
        AddParameter(command, "tenant_id", DbType.Guid, tenantId ?? (object)DBNull.Value);
        AddParameter(command, "payload", DbType.String, json);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    internal static string ValidateEnvelope(Guid eventId, string eventType, Guid? tenantId,
        ReadOnlyMemory<byte> payload)
    {
        if (eventId == Guid.Empty || tenantId == Guid.Empty)
        {
            throw new ArgumentException("A valid event ID and tenant context are required.");
        }
        if (string.IsNullOrWhiteSpace(eventType) || eventType.Length > 128)
        {
            throw new ArgumentException("A valid event type is required.", nameof(eventType));
        }
        if (payload.IsEmpty || payload.Length > MaxPayloadBytes)
        {
            throw new ArgumentException("The event payload size is invalid.", nameof(payload));
        }

        string json;
        try
        {
            json = StrictUtf8.GetString(payload.Span);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("specversion", out var version) ||
                version.ValueKind != JsonValueKind.String || version.GetString() != "1.0" ||
                !root.TryGetProperty("id", out var id) ||
                id.ValueKind != JsonValueKind.String ||
                !Guid.TryParse(id.GetString(), out var envelopeId) || envelopeId != eventId ||
                !root.TryGetProperty("type", out var type) ||
                type.ValueKind != JsonValueKind.String || type.GetString() != eventType ||
                !MatchesTenant(root, tenantId))
            {
                throw new ArgumentException("Event envelope metadata does not match the outbox row.",
                    nameof(payload));
            }
        }
        catch (DecoderFallbackException)
        {
            throw new ArgumentException("The event payload must be UTF-8 JSON.", nameof(payload));
        }
        catch (JsonException)
        {
            throw new ArgumentException("The event payload must be UTF-8 JSON.", nameof(payload));
        }

        return json;
    }

    private static bool MatchesTenant(JsonElement root, Guid? tenantId)
    {
        if (!root.TryGetProperty("tenantid", out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return tenantId is null;
        }
        return tenantId is not null && value.ValueKind == JsonValueKind.String &&
               Guid.TryParse(value.GetString(), out var envelopeTenant) &&
               envelopeTenant == tenantId;
    }

    private static void AddParameter(DbCommand command, string name, DbType dbType, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = dbType;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
