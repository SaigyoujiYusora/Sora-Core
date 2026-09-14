using Sora.Core;

/// <summary>One-entry process cache, enabled only for a persistent sequential task session.</summary>
internal static class SessionDatabase
{
    public static bool Enabled { get; set; }
    private sealed record Entry(string Path, long Length, DateTime Modified, string Header, ValidatedDatabase Container);
    private static Entry? cached;
    public static DatabaseDocument Read(string path) => ReadValidated(path).Database;
    public static ValidatedDatabase ReadValidated(string path)
    {
        if (!Enabled) return DatabaseFile.ReadValidated(path);
        path = Path.GetFullPath(path);
        var file = new FileInfo(path);
        using var probe = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var header = new byte[52]; probe.ReadExactly(header); string identity = Convert.ToHexString(header);
        if (cached is {} item && string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase)
            && file.Exists && item.Length == file.Length && item.Modified == file.LastWriteTimeUtc && item.Header == identity) {
            OperationProgress.Report("database-cache-hit"); return item.Container;
        }
        cached = null;
        OperationProgress.Report("read-database");
        long length = file.Length; DateTime modified = file.LastWriteTimeUtc;
        var database = DatabaseFile.ReadValidated(path);
        file.Refresh();
        Validation.Require(file.Exists && file.Length == length && file.LastWriteTimeUtc == modified, "Database changed while reading; retry the request");
        cached = new(path, length, modified, identity, database);
        return database;
    }
}
