namespace LaunchBuddy;

internal sealed class WebsiteRecord
{
    public string Alias { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
}

internal sealed class WebsiteDatabase
{
    public List<WebsiteRecord> Websites { get; set; } = [];
}

internal enum ComputerItemKind
{
    Application,
    File,
    Folder
}

internal sealed record ComputerItem(string Name, string Path, string Source, ComputerItemKind Kind);

internal sealed class Intent
{
    public string Action { get; init; } = "chat";
    public string Alias { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
    public string Query { get; init; } = string.Empty;
    public bool AsAdministrator { get; init; }
    public string Reply { get; init; } = string.Empty;
}

internal sealed class PendingAction
{
    public required PendingActionKind Kind { get; init; }
    public required string Title { get; init; }
    public required string Details { get; init; }
    public WebsiteRecord? Website { get; init; }
    public ComputerItem? Item { get; init; }
    public bool AsAdministrator { get; init; }
    public ChatAppInfo? ChatApp { get; init; }
}

internal enum PendingActionKind
{
    SaveWebsite,
    OpenWebsite,
    RemoveWebsite,
    OpenApplication,
    ControlChat
}
