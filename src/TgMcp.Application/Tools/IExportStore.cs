namespace TgMcp.Application.Tools;

/// <summary>Where an export file was written.</summary>
/// <param name="Path">Full path inside the server (container).</param>
/// <param name="FileName">File name only.</param>
public sealed record StoredExport(string Path, string FileName);

/// <summary>Owner-only export files in <c>{DataDir}/exports</c> (used by read_new with <c>export: true</c>).</summary>
public interface IExportStore
{
    /// <summary>
    /// Atomically writes <paramref name="json"/> to <c>{prefix}-{UTC yyyyMMddTHHmmssZ}.json</c> (directory 0700, file 0600)
    /// and deletes this store's files older than two days.
    /// </summary>
    Task<StoredExport> WriteAsync(string prefix, string json, CancellationToken ct);
}
