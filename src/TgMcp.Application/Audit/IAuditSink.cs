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

/// <summary>Append-only audit trail.</summary>
public interface IAuditSink
{
    /// <summary>Appends one record. Implementations must not throw.</summary>
    void Write(AuditEntry entry);

    /// <summary>Appends a startup/lifecycle event with free-form fields (no secrets).</summary>
    void WriteEvent(string name, IReadOnlyDictionary<string, object?> fields);
}
