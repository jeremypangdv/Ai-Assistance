using System.Text.Json;

namespace LaunchBuddy;

internal sealed class WebsiteStore
{
    private readonly string _databasePath;
    private readonly object _gate = new();
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };
    private WebsiteDatabase _database;

    public WebsiteStore()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LaunchBuddy");
        Directory.CreateDirectory(directory);
        _databasePath = Path.Combine(directory, "saved-websites.json");
        _database = Load();
    }

    public IReadOnlyList<WebsiteRecord> All()
    {
        lock (_gate)
        {
            return _database.Websites
                .OrderBy(w => w.Alias, StringComparer.OrdinalIgnoreCase)
                .Select(Clone)
                .ToList();
        }
    }

    public WebsiteRecord? Find(string alias)
    {
        lock (_gate)
        {
            var result = _database.Websites.FirstOrDefault(w =>
                string.Equals(w.Alias.Trim(), alias.Trim(), StringComparison.OrdinalIgnoreCase));
            return result is null ? null : Clone(result);
        }
    }

    // Same page saved under any name; a trailing slash or letter case in the host doesn't make it different.
    public WebsiteRecord? FindByUrl(string url)
    {
        lock (_gate)
        {
            var result = _database.Websites.FirstOrDefault(w => SameUrl(w.Url, url));
            return result is null ? null : Clone(result);
        }
    }

    private static bool SameUrl(string first, string second) =>
        Uri.TryCreate(first.Trim(), UriKind.Absolute, out var a) && Uri.TryCreate(second.Trim(), UriKind.Absolute, out var b)
            ? a.AbsoluteUri.TrimEnd('/') == b.AbsoluteUri.TrimEnd('/')
            : string.Equals(first.Trim(), second.Trim(), StringComparison.OrdinalIgnoreCase);

    // Replaces the whole list with the one edited in settings, keeping each kept site's creation time.
    public void ReplaceAll(IEnumerable<WebsiteRecord> websites)
    {
        lock (_gate)
        {
            var now = DateTimeOffset.Now;
            var previous = _database.Websites;
            var updated = new WebsiteDatabase
            {
                Websites = websites.Select(website =>
                {
                    var record = Clone(website);
                    record.Alias = record.Alias.Trim();
                    record.Url = record.Url.Trim();
                    var before = previous.FirstOrDefault(w => w.CreatedAt == website.CreatedAt);
                    record.UpdatedAt = before is not null && before.Alias == record.Alias && before.Url == record.Url ? before.UpdatedAt : now;
                    return record;
                }).ToList()
            };
            Save(updated);
            _database = updated;
        }
    }

    public WebsiteRecord Upsert(string alias, string url)
    {
        lock (_gate)
        {
            // Apply the change to a copy and only publish it once it is on disk,
            // so a failed save never leaves memory and the file out of sync.
            var updated = Copy(_database);
            var now = DateTimeOffset.Now;
            var existing = updated.Websites.FirstOrDefault(w =>
                string.Equals(w.Alias.Trim(), alias.Trim(), StringComparison.OrdinalIgnoreCase));

            if (existing is null)
            {
                existing = new WebsiteRecord { Alias = alias.Trim(), Url = url.Trim(), CreatedAt = now, UpdatedAt = now };
                updated.Websites.Add(existing);
            }
            else
            {
                existing.Alias = alias.Trim();
                existing.Url = url.Trim();
                existing.UpdatedAt = now;
            }

            Save(updated);
            _database = updated;
            return Clone(existing);
        }
    }

    public bool Remove(string alias)
    {
        lock (_gate)
        {
            var updated = Copy(_database);
            var existing = updated.Websites.FirstOrDefault(w =>
                string.Equals(w.Alias.Trim(), alias.Trim(), StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                return false;
            }

            updated.Websites.Remove(existing);
            Save(updated);
            _database = updated;
            return true;
        }
    }

    private WebsiteDatabase Load()
    {
        try
        {
            if (!File.Exists(_databasePath))
            {
                return new WebsiteDatabase();
            }

            return JsonSerializer.Deserialize<WebsiteDatabase>(File.ReadAllText(_databasePath), _jsonOptions)
                ?? new WebsiteDatabase();
        }
        catch (Exception exception)
        {
            // A malformed file must not prevent the tray assistant from starting, but keep a copy
            // so the next save cannot silently overwrite the user's only list of websites.
            AppLog.Error("WebsiteStore", exception);
            BackUpUnreadableFile();
            return new WebsiteDatabase();
        }
    }

    private void BackUpUnreadableFile()
    {
        try
        {
            var backupPath = Path.Combine(
                Path.GetDirectoryName(_databasePath)!,
                $"saved-websites.unreadable-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.json");
            File.Copy(_databasePath, backupPath, false);
        }
        catch (Exception exception)
        {
            AppLog.Error("WebsiteStore", exception);
        }
    }

    private void Save(WebsiteDatabase database)
    {
        var temporaryPath = _databasePath + ".new";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(database, _jsonOptions));
        File.Move(temporaryPath, _databasePath, true);
    }

    private static WebsiteDatabase Copy(WebsiteDatabase database) => new()
    {
        Websites = database.Websites.Select(Clone).ToList()
    };

    private static WebsiteRecord Clone(WebsiteRecord website) => new()
    {
        Alias = website.Alias,
        Url = website.Url,
        CreatedAt = website.CreatedAt,
        UpdatedAt = website.UpdatedAt
    };
}
