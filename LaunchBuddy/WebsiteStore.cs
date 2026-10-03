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

    public WebsiteRecord Upsert(string alias, string url)
    {
        lock (_gate)
        {
            var now = DateTimeOffset.Now;
            var existing = _database.Websites.FirstOrDefault(w =>
                string.Equals(w.Alias.Trim(), alias.Trim(), StringComparison.OrdinalIgnoreCase));

            if (existing is null)
            {
                existing = new WebsiteRecord { Alias = alias.Trim(), Url = url.Trim(), CreatedAt = now, UpdatedAt = now };
                _database.Websites.Add(existing);
            }
            else
            {
                existing.Alias = alias.Trim();
                existing.Url = url.Trim();
                existing.UpdatedAt = now;
            }

            Save();
            return Clone(existing);
        }
    }

    public bool Remove(string alias)
    {
        lock (_gate)
        {
            var existing = _database.Websites.FirstOrDefault(w =>
                string.Equals(w.Alias.Trim(), alias.Trim(), StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                return false;
            }

            _database.Websites.Remove(existing);
            Save();
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
        catch (Exception)
        {
            // A malformed file must not prevent the tray assistant from starting.
            return new WebsiteDatabase();
        }
    }

    private void Save()
    {
        var temporaryPath = _databasePath + ".new";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(_database, _jsonOptions));
        File.Move(temporaryPath, _databasePath, true);
    }

    private static WebsiteRecord Clone(WebsiteRecord website) => new()
    {
        Alias = website.Alias,
        Url = website.Url,
        CreatedAt = website.CreatedAt,
        UpdatedAt = website.UpdatedAt
    };
}
