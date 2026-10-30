namespace TgMcp.Application.Audit;

/// <summary>One audit record. Never contains message text, queries or secrets.</summary>
/// <param name="TimeUtc">When the call finished.</param>
/// <param name="Tool">Tool or event name.</param>
/// <param name="ChatId">Chat the call targeted, if any.</param>
/// <param name="Count">Number of items returned.</param>
/// <param name="Allowed">True if the policy allowed the call.</param>
/// <param name="Rule">Matched rule label.</param>
/// <param name="Reason">Short reason or outcome.</param>
public sealed record AuditEntry(
    DateTimeOffset TimeUtc,
    string Tool,
    long? ChatId,
    int Count,
    bool Allowed,
    string Rule,
    string Reason);

/// <summary>Append-only audit trail. Tool calls fail closed when it cannot be written.</summary>
public interface IAuditSink
{
    /// <summary>True if a record can be appended right now (checked before any Telegram call). Must not throw.</summary>
    bool IsAvailable();

    /// <summary>Appends one record; false if it could not be persisted. Must not throw.</summary>
    bool Write(AuditEntry entry);

    /// <summary>Appends a startup/lifecycle event with free-form fields (no secrets); false on failure. Must not throw.</summary>
    bool WriteEvent(string name, IReadOnlyDictionary<string, object?> fields);
}
