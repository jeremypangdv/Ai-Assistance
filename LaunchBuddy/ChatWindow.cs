using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Automation;

namespace LaunchBuddy;

internal sealed record ChatMessage(ulong Id, string Author, string Text, bool FromMe);

// One chat app window: types into its message box through the keyboard, and (Discord) reads its recent
// messages through UI Automation. Call from a background thread; UI Automation calls block.
internal sealed class ChatWindow
{
    private const int SW_RESTORE = 9;
    private static readonly Regex TimeLike = new(@"^\s*(?:\d{1,2}/\d{1,2}/\d{2,4}\s*)?(?:today at|yesterday at|今天|昨天)?\s*\d{1,2}:\d{2}(?:\s?[AP]M)?\s*$|^[A-Z][a-z]+day, \d{1,2} [A-Z][a-z]+,? \d{4}", RegexOptions.IgnoreCase);
    // The author line is read with whatever follows it: "Name Server Tag: X" or "Name 2/10/2026 8:32 PM".
    private static readonly Regex AuthorSuffix = new(@"\s*(?:Server Tag:|(?:\d{1,2}/\d{1,2}/\d{2,4}\s*)?(?:Today at |Yesterday at )?\d{1,2}:\d{2}\s?[AP]M).*$", RegexOptions.Singleline | RegexOptions.IgnoreCase);
    private static readonly CacheRequest MessageCache = BuildCache();

    public ChatWindow(ChatAppInfo app, IntPtr handle)
    {
        App = app;
        Handle = handle;
    }

    public ChatAppInfo App { get; }
    public IntPtr Handle { get; }
    public bool IsOpen => IsWindow(Handle) && IsWindowVisible(Handle);

    // "#chat-room-1 | Server - Discord" → "#chat-room-1 | Server".
    public string Conversation
    {
        get
        {
            var buffer = new StringBuilder(512);
            GetWindowText(Handle, buffer, buffer.Capacity);
            var title = buffer.ToString();
            var suffix = " - " + App.Name;
            return title.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ? title[..^suffix.Length] : title;
        }
    }

    // Brings the window forward, puts the caret in its message box and types; Enter sends when asked.
    // Returns the window that was in front before, so an automatic reply can hand the focus back.
    public IntPtr Type(string text, bool send)
    {
        var previous = GetForegroundWindow();
        Activate(Handle);
        var input = FindInput();
        try
        {
            input?.SetFocus();
        }
        catch (Exception exception) when (exception is InvalidOperationException or ElementNotAvailableException)
        {
            // Activating the window already focuses the message box in most chats.
        }
        Thread.Sleep(120);
        // Keep what is already in the box readable as a separate sentence.
        if (input is not null && ReadInput(input).Length > 0)
            text = " " + text;
        Keyboard.TypeText(text.ReplaceLineEndings(" "));
        if (send)
        {
            Thread.Sleep(80);
            Keyboard.Press(Keys.Enter);
        }
        return previous;
    }

    public void Send()
    {
        Activate(Handle);
        try { FindInput()?.SetFocus(); } catch (Exception exception) when (exception is InvalidOperationException or ElementNotAvailableException) { }
        Thread.Sleep(80);
        Keyboard.Press(Keys.Enter);
    }

    // Text sitting unsent in the message box; an automatic reply must not send it along.
    public string PendingInput() => FindInput() is { } input ? ReadInput(input) : string.Empty;

    public static void ReturnFocus(IntPtr window)
    {
        if (window != IntPtr.Zero && IsWindow(window))
            Activate(window);
    }

    private AutomationElement? FindInput()
    {
        var root = AutomationElement.FromHandle(Handle);
        var edits = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
        AutomationElement? fallback = null;
        foreach (AutomationElement edit in edits)
        {
            var name = edit.Current.Name;
            // Discord: "Message #channel" / "Message @friend"; WhatsApp: "Type a message".
            if (name.StartsWith("Message", StringComparison.OrdinalIgnoreCase) || name.Contains("message", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("訊息") || name.Contains("消息") || name.Contains("輸入"))
                return edit;
            if (!name.Contains("search", StringComparison.OrdinalIgnoreCase) && !name.Contains("搜尋"))
                fallback ??= edit;
        }
        return fallback;
    }

    private static string ReadInput(AutomationElement input)
    {
        if (!input.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern))
            return string.Empty;
        // Empty editors can hold a zero-width placeholder character.
        return ((ValuePattern)pattern).Current.Value.Replace("​", string.Empty).Replace("﻿", string.Empty).Trim();
    }

    // The account name shown in Discord's bottom-left panel (display name, then username).
    public IReadOnlyList<string> ReadOwnNames()
    {
        if (App != ChatApps.Discord)
            return [];
        var root = AutomationElement.FromHandle(Handle);
        var panel = root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, "User status and settings"));
        if (panel is null)
            return [];
        var texts = panel.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text));
        return texts.Cast<AutomationElement>()
            .Select(text => text.Current.Name.Trim())
            .Where(name => name.Length > 0)
            .Take(2)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // The newest messages of the open Discord channel, oldest first.
    public IReadOnlyList<ChatMessage> ReadMessages(IReadOnlyList<string> ownNames, int count = 15)
    {
        if (App != ChatApps.Discord)
            throw new NotSupportedException($"還不支援讀取 {App.Name} 的訊息。");

        var root = AutomationElement.FromHandle(Handle);
        var list = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.List))
            .Cast<AutomationElement>()
            .FirstOrDefault(element => element.Current.Name.StartsWith("Messages in ", StringComparison.Ordinal));
        if (list is null)
            return [];

        var items = list.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem))
            .Cast<AutomationElement>()
            .Where(item => item.Current.AutomationId.StartsWith("chat-messages-", StringComparison.Ordinal))
            .ToList();

        var messages = new List<ChatMessage>();
        // A message without an author line continues the one before it, so start a few earlier to know who wrote it.
        var author = string.Empty;
        foreach (var item in items.Skip(Math.Max(0, items.Count - count - 10)))
        {
            var id = ParseId(item.Current.AutomationId);
            if (id == 0)
                continue;
            var (itemAuthor, text) = ParseDiscordMessage(item);
            if (itemAuthor.Length > 0)
                author = itemAuthor;
            var fromMe = ownNames.Any(name => author.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                author.StartsWith(name + " ", StringComparison.OrdinalIgnoreCase));
            messages.Add(new ChatMessage(id, author, text, fromMe));
        }
        return messages.TakeLast(count).ToList();
    }

    // "chat-messages-<channel id>-<message id>"; Discord ids grow over time.
    private static ulong ParseId(string automationId)
    {
        var dash = automationId.LastIndexOf('-');
        return dash > 0 && ulong.TryParse(automationId[(dash + 1)..], out var id) ? id : 0;
    }

    // Inside a message, in order: [reply preview], author, timestamp, the text pieces, then "Message Actions" and reactions.
    private static (string Author, string Text) ParseDiscordMessage(AutomationElement item)
    {
        AutomationElementCollection nodes;
        using (MessageCache.Activate())
            nodes = item.FindAll(TreeScope.Descendants, Condition.TrueCondition);

        var replyPreview = new HashSet<string>();
        foreach (AutomationElement node in nodes)
        {
            if (node.Cached.AutomationId.StartsWith("message-reply-context-", StringComparison.Ordinal))
            {
                using (MessageCache.Activate())
                    foreach (AutomationElement inner in node.FindAll(TreeScope.Descendants, Condition.TrueCondition))
                        replyPreview.Add(Key(inner));
                replyPreview.Add(Key(node));
            }
        }

        var author = string.Empty;
        var afterTimestamp = false;
        var text = new StringBuilder();
        foreach (AutomationElement node in nodes)
        {
            if (replyPreview.Contains(Key(node)))
                continue;
            var name = node.Cached.Name.Trim();
            if (node.Cached.Name == "Message Actions" || name == "Click to react")
                break;
            if (node.Cached.AutomationId.StartsWith("message-timestamp-", StringComparison.Ordinal))
            {
                afterTimestamp = true;
                continue;
            }
            if (node.Cached.ControlType != ControlType.Text || name.Length == 0)
                continue;
            if (!afterTimestamp)
            {
                if (author.Length == 0)
                    author = AuthorSuffix.Replace(name, string.Empty).Trim();
                continue;
            }
            // The time under the timestamp is read out as text before the message itself.
            if (text.Length == 0 && TimeLike.IsMatch(name))
                continue;
            text.Append(text.Length == 0 ? "" : " ").Append(name);
        }
        var body = Regex.Replace(text.ToString(), @"\s+", " ").Trim();
        return (author, body.Length > 0 ? body : "[圖片、貼圖或附件]");
    }

    private static string Key(AutomationElement element) => string.Join(".", element.GetCachedPropertyValue(AutomationElement.RuntimeIdProperty) as int[] ?? []);

    private static CacheRequest BuildCache()
    {
        var request = new CacheRequest();
        request.Add(AutomationElement.NameProperty);
        request.Add(AutomationElement.AutomationIdProperty);
        request.Add(AutomationElement.ControlTypeProperty);
        request.Add(AutomationElement.RuntimeIdProperty);
        return request;
    }

    // Windows only lets the foreground app hand the focus over, so borrow its input queue for the switch.
    private static void Activate(IntPtr window)
    {
        if (IsIconic(window))
            ShowWindow(window, SW_RESTORE);
        var foreground = GetForegroundWindow();
        if (foreground == window)
            return;
        var foregroundThread = GetWindowThreadProcessId(foreground, out _);
        var thisThread = GetCurrentThreadId();
        var attached = foregroundThread != thisThread && AttachThreadInput(thisThread, foregroundThread, true);
        try
        {
            BringWindowToTop(window);
            SetForegroundWindow(window);
        }
        finally
        {
            if (attached)
                AttachThreadInput(thisThread, foregroundThread, false);
        }
        Thread.Sleep(150);
    }

    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint attach, uint attachTo, bool doAttach);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int maxCount);
}

// Synthesised keystrokes. Text goes in as Unicode characters, so Chinese and emoji type the same as letters
// and no keyboard layout or input method gets in the way.
internal static class Keyboard
{
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x2;
    private const uint KEYEVENTF_UNICODE = 0x4;

    public static void TypeText(string text)
    {
        var inputs = new List<Input>(text.Length * 2);
        foreach (var unit in text)
        {
            inputs.Add(Key(0, unit, KEYEVENTF_UNICODE));
            inputs.Add(Key(0, unit, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP));
        }
        Send(inputs);
    }

    public static void Press(Keys key) => Send([Key((ushort)key, '\0', 0), Key((ushort)key, '\0', KEYEVENTF_KEYUP)]);

    private static Input Key(ushort virtualKey, ushort scan, uint flags) => new()
    {
        Type = INPUT_KEYBOARD,
        Keyboard = new KeyboardInput { VirtualKey = virtualKey, Scan = scan, Flags = flags }
    };

    private static void Send(List<Input> inputs)
    {
        // Large batches can be cut short by the input queue; send in chunks.
        foreach (var chunk in inputs.Chunk(64))
        {
            if (SendInput((uint)chunk.Length, chunk, Marshal.SizeOf<Input>()) != chunk.Length)
                throw new InvalidOperationException("Windows 拒絕了模擬鍵盤輸入（可能有以系統管理員身分執行的視窗在最上層）。");
            Thread.Sleep(5);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public KeyboardInput Keyboard;
        // The native INPUT is a union whose largest member (MOUSEINPUT) is 8 bytes bigger than KEYBDINPUT.
        private readonly long _padding;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        // ushort, not char: a char field would be marshalled as ANSI and turn every letter into "?".
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);
}
