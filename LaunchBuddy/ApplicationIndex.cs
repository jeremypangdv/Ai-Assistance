using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace LaunchBuddy;

internal sealed class ApplicationIndex : IDisposable
{
    private sealed record IndexedItem(ComputerItem Item, string NormalizedName);
    private readonly object _gate = new();
    private readonly List<IndexedItem> _items = [];
    private readonly HashSet<string> _knownItems = new(StringComparer.OrdinalIgnoreCase);
    // Keys seen by the build in progress; used to drop items that no longer exist once it finishes.
    private HashSet<string>? _seen;
    private bool _scanIncomplete;
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _indexTask;
    private int _count;

    public bool IsExtendedIndexing => _indexTask is { IsCompleted: false };
    public int ExtendedItemCount => Volatile.Read(ref _count);

    public void StartExtendedIndexing()
    {
        lock (_gate)
        {
            if (_indexTask is { IsCompleted: false } || _shutdown.IsCancellationRequested)
                return;
            _indexTask = Task.Run(() => BuildIndex(_shutdown.Token));
        }
    }

    public void Refresh() => StartExtendedIndexing();

    public ComputerItem? Find(string query)
    {
        var direct = ResolveDirectPath(query);
        if (direct is not null)
            return direct;

        // These targets are available immediately, even while the disk index is still building.
        var normalized = Normalize(query);
        var known = KnownItems().FirstOrDefault(item => Normalize(item.Name) == normalized);
        if (known is not null)
            return known;

        return FindInFolder(query) ?? Search(query, 1).FirstOrDefault();
    }

    // "Downloads 裡的 report.pdf" / "report.pdf in Downloads": match the name only inside that folder.
    private static readonly Regex InFolderPattern = new(
        @"^(?<folder>.+?)\s*(?:裡面的|裡的|里的|中的|內的)\s*(?<name>.+)$|^(?<name>.+?)\s+in\s+(?<folder>.+)$",
        RegexOptions.IgnoreCase);

    private ComputerItem? FindInFolder(string query)
    {
        var match = InFolderPattern.Match(query.Trim());
        if (!match.Success)
            return null;

        var folder = FindFolder(match.Groups["folder"].Value);
        var name = match.Groups["name"].Value.Trim();
        if (folder is null || name.Length == 0)
            return null;

        var direct = ResolveDirectPath(Path.Combine(folder.Path, name));
        if (direct is not null)
            return direct;

        var prefix = Path.TrimEndingDirectorySeparator(folder.Path) + Path.DirectorySeparatorChar;
        var normalized = Normalize(name);
        IndexedItem[] snapshot;
        lock (_gate)
        {
            snapshot = _items.ToArray();
        }

        return snapshot
            .Where(item => item.Item.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(item => (item.Item, Score: Score(item.NormalizedName, normalized)))
            .Where(candidate => candidate.Score > 0)
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Item.Path.Length)
            .Select(candidate => candidate.Item)
            .FirstOrDefault();
    }

    private ComputerItem? FindFolder(string query)
    {
        var direct = ResolveDirectPath(query);
        if (direct is not null)
            return direct.Kind == ComputerItemKind.Folder ? direct : null;

        var normalized = Normalize(query);
        return KnownItems().FirstOrDefault(item => item.Kind == ComputerItemKind.Folder && Normalize(item.Name) == normalized)
            ?? Search(query, 50).FirstOrDefault(item => item.Kind == ComputerItemKind.Folder);
    }

    public IReadOnlyList<ComputerItem> Search(string query, int maximum = 8)
    {
        var normalized = Normalize(query);
        if (normalized.Length == 0)
            return [];

        IndexedItem[] snapshot;
        lock (_gate)
        {
            // Take a snapshot under a short lock, then score outside it.
            snapshot = _items.ToArray();
        }

        return snapshot
            .Select(item => (item.Item, Score: Score(item.NormalizedName, normalized)))
            .Where(candidate => candidate.Score > 0)
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Item.Kind == ComputerItemKind.Application ? 0 : 1)
            .ThenBy(candidate => candidate.Item.Name.Length)
            .Take(maximum)
            .Select(candidate => candidate.Item)
            .ToList();
    }

    private void BuildIndex(CancellationToken token)
    {
        lock (_gate)
        {
            _seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
        _scanIncomplete = false;

        try
        {
            Merge(KnownItems());
            AddStartMenu(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu));
            AddStartMenu(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu));
            AddAppPaths(Registry.CurrentUser);
            AddAppPaths(Registry.LocalMachine);
            AddStoreApps();
            foreach (var root in KnownItems().Where(item => item.Kind == ComputerItemKind.Folder)
                         .Select(item => item.Path).Distinct(StringComparer.OrdinalIgnoreCase))
                ScanFolder(root, "個人資料夾", token);

            foreach (var drive in DriveInfo.GetDrives())
            {
                token.ThrowIfCancellationRequested();
                if (drive.IsReady && drive.DriveType == DriveType.Fixed)
                    ScanFolder(drive.RootDirectory.FullName, "本機磁碟", token);
            }

            // Only prune after a complete scan, so a failed scan never drops items it did not reach.
            if (!_scanIncomplete)
                RemoveItemsNotSeen();
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            AppLog.Error("Index", exception);
        }
        finally
        {
            lock (_gate)
            {
                _seen = null;
            }
        }
    }

    private void RemoveItemsNotSeen()
    {
        lock (_gate)
        {
            if (_seen is null)
                return;
            _items.RemoveAll(item => !_seen.Contains(Key(item.Item)));
            _knownItems.IntersectWith(_seen);
            Volatile.Write(ref _count, _items.Count);
        }
    }

    private static string Key(ComputerItem item) => item.Path + "\0" + item.Name;

    private void Merge(IEnumerable<ComputerItem> additions)
    {
        // Normalize only the new batch, rather than rebuilding a HashSet of the whole disk
        // for every 500 entries (which previously blocked the window thread).
        var batch = additions.Select(item => new IndexedItem(item, Normalize(item.Name))).ToArray();
        lock (_gate)
        {
            foreach (var item in batch)
            {
                var key = Key(item.Item);
                _seen?.Add(key);
                if (_knownItems.Add(key))
                    _items.Add(item);
            }
            Volatile.Write(ref _count, _items.Count);
        }
    }

    private void ScanFolder(string root, string source, CancellationToken token)
    {
        if (!Directory.Exists(root))
            return;
        var batch = new List<ComputerItem>(500);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.System | FileAttributes.ReparsePoint
        };

        try
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", options))
            {
                token.ThrowIfCancellationRequested();
                var isDirectory = Directory.Exists(path);
                batch.Add(new ComputerItem(Path.GetFileName(path), path, source,
                    isDirectory ? ComputerItemKind.Folder : IsApplication(path) ? ComputerItemKind.Application : ComputerItemKind.File));
                if (batch.Count >= 500)
                {
                    Merge(batch);
                    batch.Clear();
                    Thread.Sleep(4); // Yield while indexing, so the desktop remains responsive.
                }
            }
        }
        catch (UnauthorizedAccessException) { _scanIncomplete = true; }
        catch (IOException) { _scanIncomplete = true; }
        finally
        {
            Merge(batch);
        }
    }

    private void AddStartMenu(string directory)
    {
        if (!Directory.Exists(directory))
            return;
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        try
        {
            Merge(Directory.EnumerateFiles(directory, "*.lnk", options)
                .Select(path => new ComputerItem(Path.GetFileNameWithoutExtension(path), path, "開始功能表", ComputerItemKind.Application)));
        }
        catch (IOException) { _scanIncomplete = true; }
    }

    private void AddAppPaths(RegistryKey root)
    {
        try
        {
            using var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths");
            if (key is null)
                return;
            var found = new List<ComputerItem>();
            foreach (var name in key.GetSubKeyNames())
            {
                using var appKey = key.OpenSubKey(name);
                if (appKey?.GetValue(null) is not string path)
                    continue;
                path = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
                if (File.Exists(path))
                    found.Add(new ComputerItem(Path.GetFileNameWithoutExtension(name), path, "Windows app 登錄", ComputerItemKind.Application));
            }
            Merge(found);
        }
        catch (UnauthorizedAccessException) { _scanIncomplete = true; }
        catch (System.Security.SecurityException) { _scanIncomplete = true; }
    }

    private void AddStoreApps()
    {
        object? shellObject = null;
        try
        {
            var type = Type.GetTypeFromProgID("Shell.Application");
            if (type is null)
                return;
            shellObject = Activator.CreateInstance(type);
            dynamic shell = shellObject!;
            dynamic folder = shell.NameSpace("shell:AppsFolder");
            if (folder is null)
                return;
            dynamic apps = folder.Items();
            var found = new List<ComputerItem>();
            for (var index = 0; index < apps.Count; index++)
            {
                // One broken entry must not prevent the remaining Store apps from being indexed.
                try
                {
                    dynamic app = apps.Item(index);
                    string? name = app.Name;
                    string? appId = app.Path;
                    if (!string.IsNullOrWhiteSpace(name) && appId is not null && appId.Contains('!'))
                        found.Add(new ComputerItem(name, "shell:AppsFolder\\" + appId, "Microsoft Store app", ComputerItemKind.Application));
                }
                catch (Exception exception) { AppLog.Error("StoreIndex", exception); }
            }
            Merge(found);
        }
        catch (Exception exception)
        {
            _scanIncomplete = true;
            AppLog.Error("StoreIndex", exception);
        }
        finally
        {
            if (shellObject is not null && Marshal.IsComObject(shellObject))
                Marshal.FinalReleaseComObject(shellObject);
        }
    }

    private static IEnumerable<ComputerItem> KnownItems()
    {
        var system = Environment.SystemDirectory;
        var folders = new (string Name, string Path)[]
        {
            ("Notepad", Path.Combine(system, "notepad.exe")),
            ("記事本", Path.Combine(system, "notepad.exe")),
            ("Calculator", Path.Combine(system, "calc.exe")),
            ("計算機", Path.Combine(system, "calc.exe")),
            ("Paint", Path.Combine(system, "mspaint.exe")),
            ("Command Prompt", Path.Combine(system, "cmd.exe")),
            ("Desktop", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)),
            ("桌面", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)),
            ("Documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)),
            ("我的文件", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)),
            ("我的檔案", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)),
            ("檔案", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)),
            ("Downloads", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")),
            ("下載資料夾", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")),
            ("Pictures", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)),
            ("圖片", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)),
            ("Music", Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)),
            ("音樂", Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)),
            ("Videos", Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)),
            ("影片", Environment.GetFolderPath(Environment.SpecialFolder.MyVideos))
        };
        foreach (var (name, path) in folders)
        {
            if (File.Exists(path))
                yield return new ComputerItem(name, path, "Windows", ComputerItemKind.Application);
            else if (Directory.Exists(path))
                yield return new ComputerItem(name, path, "Windows 資料夾", ComputerItemKind.Folder);
        }
    }

    private static ComputerItem? ResolveDirectPath(string query)
    {
        var value = Environment.ExpandEnvironmentVariables(query.Trim().Trim('「', '」', '“', '”', '"', '\''));
        if (File.Exists(value))
            return new ComputerItem(Path.GetFileName(value), Path.GetFullPath(value), "指定路徑",
                IsApplication(value) ? ComputerItemKind.Application : ComputerItemKind.File);
        if (Directory.Exists(value))
            return new ComputerItem(new DirectoryInfo(value).Name, Path.GetFullPath(value), "指定路徑", ComputerItemKind.Folder);
        return null;
    }

    private static bool IsApplication(string path) => Path.GetExtension(path).ToLowerInvariant() is ".exe" or ".lnk" or ".appref-ms";

    private static int Score(string name, string query)
    {
        if (name == query) return 100;
        if (name.StartsWith(query, StringComparison.Ordinal)) return 80;
        if (name.Contains(query, StringComparison.Ordinal)) return 60;
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length > 0 && words.All(word => name.Contains(word, StringComparison.Ordinal)) ? 40 : 0;
    }

    private static string Normalize(string value) => value.Trim().ToLowerInvariant();

    public void Dispose() => _shutdown.Cancel();
}
