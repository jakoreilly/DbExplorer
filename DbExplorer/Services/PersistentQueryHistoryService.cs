using DbExplorer.Core.Interfaces;
using DbExplorer.Core.Models;
using DbExplorer.Options;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DbExplorer.Services;

/// <summary>
/// Per-user query history persisted as JSON-Lines under the configured
/// <see cref="ProfilerOptions.HistoryPath"/>. One file per user keyed on a
/// SHA-256 hash of the username so the on-disk path never reveals identities.
/// Append-only with periodic trim when the file grows past
/// <c>HistoryMaxEntries * 1.5</c>.
/// </summary>
public sealed class PersistentQueryHistoryService(
    IOptions<ProfilerOptions> options,
    ILogger<PersistentQueryHistoryService> logger) : IPersistentQueryHistoryService
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _gate = new(1, 1);

    private const int TrimMargin = 50;

    private string Root => string.IsNullOrWhiteSpace(options.Value.HistoryPath)
        ? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DbExplorer", "history")
        : options.Value.HistoryPath;

    public async Task AppendAsync(string username, ProfiledQuery entry, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(username))
            return;

        try
        {
            Directory.CreateDirectory(Root);
            var path = PathFor(username);

            await _gate.WaitAsync(ct);
            try
            {
                var toStore = entry with { Sql = entry.Sql.ReplaceLineEndings(" ") };
                var line = JsonSerializer.Serialize(toStore, JsonOpts) + Environment.NewLine;
                await using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.None);
                var bytes = Encoding.UTF8.GetBytes(line);
                await fs.WriteAsync(bytes, ct);

                await TrimIfNeededAsync(path, options.Value.HistoryMaxEntries, ct);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to persist query history for user {User}", username);
        }
    }

    public async Task<IReadOnlyList<ProfiledQuery>> GetForUserAsync(string username, int maxEntries = 200, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(username))
            return [];

        var path = PathFor(username);
        if (!File.Exists(path))
            return [];

        await _gate.WaitAsync(ct);
        try
        {
            var lines = await File.ReadAllLinesAsync(path, ct);
            return lines
                .Reverse()
                .Take(maxEntries)
                .Select(TryDeserialize)
                .Where(e => e is not null)
                .Cast<ProfiledQuery>()
                .ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ClearAsync(string username, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(username))
            return;

        var path = PathFor(username);
        await _gate.WaitAsync(ct);
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        finally
        {
            _gate.Release();
        }
    }

    private string PathFor(string username)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(username.Trim().ToUpperInvariant()));
        var hash = Convert.ToHexString(bytes)[..16].ToUpperInvariant();
        return Path.Combine(Root, $"{hash}.jsonl");
    }

    private async Task TrimIfNeededAsync(string path, int maxEntries, CancellationToken ct)
    {
        if (maxEntries <= 0)
            return;

        var lineCount = await CountLinesAsync(path, ct);
        if (lineCount <= maxEntries + TrimMargin)
            return;

        var lines = await File.ReadAllLinesAsync(path, ct);
        var trimmed = lines.Skip(lines.Length - maxEntries);
        var tmp = path + ".tmp";
        await File.WriteAllLinesAsync(tmp, trimmed, ct);
        File.Move(tmp, path, overwrite: true);
    }

    private static async Task<int> CountLinesAsync(string path, CancellationToken ct)
    {
        var count = 0;
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 8192, useAsync: true);
        var buffer = new byte[8192];
        int read;
        while ((read = await fs.ReadAsync(buffer, ct)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                if (buffer[i] == (byte)'\n') count++;
            }
        }
        return count;
    }

    private static ProfiledQuery? TryDeserialize(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;
        try
        {
            return JsonSerializer.Deserialize<ProfiledQuery>(line, JsonOpts);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
