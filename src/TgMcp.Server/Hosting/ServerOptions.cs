namespace TgMcp.Server.Hosting;

/// <summary>Process settings bound from TGMCP_AuditPath and TGMCP_DataDir.</summary>
public sealed class ServerOptions
{
    /// <summary>Data root (default /data).</summary>
    public string DataDir { get; set; } = "/data";

    /// <summary>Audit JSONL file (default {DataDir}/audit.jsonl).</summary>
    public string? AuditPath { get; set; }

    /// <summary>The effective audit path.</summary>
    public string EffectiveAuditPath => string.IsNullOrWhiteSpace(AuditPath) ? Path.Combine(DataDir, "audit.jsonl") : AuditPath;
}
