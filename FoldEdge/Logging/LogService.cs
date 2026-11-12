using System.Text;
using System.Text.Json;

namespace FoldEdge;

public sealed record FoldLogEntry(long Id, DateTime TsUtc, string Level, string Zh, string En);

/// <summary>
/// Unified log center:
///   - in-memory ring buffer for the live dashboard panel
///   - one JSONL file per day under Data/Logs
///   - files older than seven days are pruned automatically
///   - every entry carries both Chinese and English text so the UI can switch
///     languages without re-querying the server
/// </summary>
public sealed class LogService
{
    private const int MaxRecent = 2000;
    private const int RetentionDays = 7;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly object _gate = new();
    private readonly string _directory;
    private readonly Queue<FoldLogEntry> _recent = new();
    private long _nextId = 1;
    private DateTime _lastPruneUtc = DateTime.MinValue;

    public LogService(string dataDir)
    {
        _directory = Path.Combine(dataDir, "Logs");
        Directory.CreateDirectory(_directory);
        PruneLocked();
    }

    public void Write(string level, string zh, string en)
    {
        lock (_gate)
        {
            var entry = new FoldLogEntry(_nextId++, DateTime.UtcNow, level, zh, en);
            _recent.Enqueue(entry);
            while (_recent.Count > MaxRecent)
                _recent.Dequeue();

            try
            {
                string file = Path.Combine(_directory, $"foldedge-{entry.TsUtc:yyyy-MM-dd}.log");
                File.AppendAllText(file, JsonSerializer.Serialize(entry, JsonOptions) + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
                // Logging must never break a request.
            }

            if ((DateTime.UtcNow - _lastPruneUtc).TotalHours >= 1)
                PruneLocked();
        }
    }

    public void Write(string level, string message) => Write(level, message, message);

    public IReadOnlyList<FoldLogEntry> ReadAfter(long after, int limit)
    {
        lock (_gate)
        {
            return _recent
                .Where(entry => entry.Id > after)
                .Take(limit)
                .ToList();
        }
    }

    private void PruneLocked()
    {
        _lastPruneUtc = DateTime.UtcNow;
        try
        {
            DateTime cutoff = DateTime.UtcNow.AddDays(-RetentionDays);
            foreach (string file in Directory.GetFiles(_directory, "foldedge-*.log"))
            {
                if (new FileInfo(file).LastWriteTimeUtc < cutoff)
                    File.Delete(file);
            }
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }
}
