namespace LaunchBuddy;

// One "Minibot is controlling Discord" session, from Approve until Cancel (or the app closing).
// Push-to-talk mode types what was said into the chat's message box and leaves sending to the user;
// AI reply mode watches for new messages from others and sends a model-written text reply. Runs on the UI thread;
// UI Automation and keystrokes go to the thread pool.
internal sealed class ChatControlSession : IDisposable
{
    // Watching for new messages, and noticing when the app closes or the channel changes.
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2.5);
    // Several messages in a row get one reply: wait for the burst to settle before answering.
    private static readonly TimeSpan BurstWait = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MinimumReplyGap = TimeSpan.FromSeconds(6);

    private readonly ChatWindow _chat;
    private readonly IntentInterpreter _interpreter;
    private readonly string _talkKeyName;
    private readonly ChatControlBar _bar;
    private readonly System.Windows.Forms.Timer _poll;
    private readonly CancellationTokenSource _stop = new();
    // Replies Minibot sent, so they are recognised as ours even if the account name was not read.
    private readonly Queue<string> _sentReplies = new();
    private IReadOnlyList<string> _ownNames = [];
    private string _channel = string.Empty;
    private ulong _lastSeen;
    private DateTime _lastReplyAt = DateTime.MinValue;
    private bool _busy;
    private bool _ended;

    public ChatControlSession(ChatWindow chat, IntentInterpreter interpreter, string talkKeyName)
    {
        _chat = chat;
        _interpreter = interpreter;
        _talkKeyName = talkKeyName;
        _bar = new ChatControlBar(chat.App.Name, chat.App.CanReadMessages);
        _bar.ModeSelected += SetMode;
        _bar.SendClicked += () => _ = SendAsync();
        _bar.CancelClicked += () => Stop("已按取消。");
        _poll = new System.Windows.Forms.Timer { Interval = (int)PollInterval.TotalMilliseconds };
        _poll.Tick += async (_, _) => await PollAsync();
    }

    public ChatAppInfo App => _chat.App;
    public ChatMode Mode { get; private set; } = ChatMode.PushToTalk;

    // Reason the session ended, for the tray to show.
    public event Action<string>? Ended;

    public void Start()
    {
        _bar.SetConversation(_chat.Conversation);
        ShowPushToTalkHint();
        _bar.ShowAbove(_chat.Handle);
        _poll.Start();
    }

    // Push-to-talk mode: what was said goes into the message box, unsent.
    public async Task DictateAsync(string text)
    {
        if (_ended)
            return;
        _bar.SetStatus($"正在輸入：「{text}」");
        try
        {
            await Task.Run(() => _chat.Type(text, send: false));
            _bar.SetStatus($"已輸入：「{text}」— 按 Enter 或「發送」送出。");
        }
        catch (Exception exception)
        {
            AppLog.Error("ChatType", exception);
            _bar.SetStatus($"無法輸入：{exception.Message}");
        }
    }

    public void Stop(string reason)
    {
        if (_ended)
            return;
        _ended = true;
        _poll.Stop();
        _stop.Cancel();
        _bar.Close();
        Ended?.Invoke(reason);
    }

    private async Task SendAsync()
    {
        try
        {
            await Task.Run(_chat.Send);
            _bar.SetStatus("已發送。");
        }
        catch (Exception exception)
        {
            AppLog.Error("ChatSend", exception);
            _bar.SetStatus($"無法發送：{exception.Message}");
        }
    }

    private void SetMode(ChatMode mode)
    {
        Mode = mode;
        _bar.ShowMode(mode);
        if (mode == ChatMode.PushToTalk)
        {
            ShowPushToTalkHint();
            return;
        }
        // Only messages that arrive from now on are answered.
        _lastSeen = 0;
        _bar.SetStatus("AI 回覆已開啟：正在讀取聊天…");
        _ = PollAsync();
    }

    private void ShowPushToTalkHint() =>
        _bar.SetStatus($"按住 {_talkKeyName} 說話，放開後文字會打進 {App.Name} 的輸入框（不會自動發送）。");

    private async Task PollAsync()
    {
        if (_ended || _busy)
            return;
        if (!_chat.IsOpen)
        {
            Stop($"{App.Name} 已關閉或縮到系統列。");
            return;
        }
        var channel = _chat.Conversation;
        _bar.SetConversation(channel);
        if (Mode != ChatMode.AiReply)
            return;

        _busy = true;
        try
        {
            await AnswerNewMessagesAsync(channel);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            AppLog.Error("ChatAiReply", exception);
            if (!_ended)
                _bar.SetStatus($"AI 回覆出錯：{exception.Message}");
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task AnswerNewMessagesAsync(string channel)
    {
        if (_ownNames.Count == 0)
            _ownNames = await Task.Run(_chat.ReadOwnNames);
        var messages = await ReadAsync();
        if (messages.Count == 0)
            return;

        // First look at this channel (mode just turned on, or the user switched channel): remember where it stands.
        if (_lastSeen == 0 || channel != _channel)
        {
            _channel = channel;
            _lastSeen = messages[^1].Id;
            _bar.SetStatus($"AI 回覆中：等待 {channel} 的新訊息…");
            return;
        }

        if (!HasNewFromOthers(messages))
        {
            _lastSeen = Math.Max(_lastSeen, messages[^1].Id);
            return;
        }

        await Task.Delay(BurstWait, _stop.Token);
        messages = await ReadAsync();
        if (Mode != ChatMode.AiReply || _chat.Conversation != channel || messages.Count == 0)
            return;
        var newest = messages[^1];
        // The user (or Minibot) already answered the burst.
        if (IsOurs(newest))
        {
            _lastSeen = newest.Id;
            return;
        }
        // Leave _lastSeen alone in these cases, so the message is answered on a later poll.
        if (DateTime.Now - _lastReplyAt < MinimumReplyGap)
            return;
        if (await Task.Run(_chat.PendingInput) is { Length: > 0 })
        {
            _bar.SetStatus("輸入框裡有未送出的文字，AI 先不回覆；送出或清空後會繼續。");
            return;
        }

        _bar.SetStatus($"AI 正在回覆 {newest.Author}：「{Shorten(newest.Text, 40)}」…");
        var reply = await _interpreter.ComposeChatReplyAsync(App.Name, channel, _ownNames, messages, _stop.Token);
        if (reply is null)
        {
            _lastSeen = newest.Id;
            _bar.SetStatus("AI 無法產生回覆（Ollama 沒有回應？），這則訊息略過。");
            return;
        }
        if (_ended || Mode != ChatMode.AiReply || _chat.Conversation != channel)
            return;

        await Task.Run(() =>
        {
            var previous = _chat.Type(reply, send: true);
            // Hand the keyboard back to whatever the user was doing.
            if (previous != _chat.Handle)
                ChatWindow.ReturnFocus(previous);
        });
        _sentReplies.Enqueue(reply);
        if (_sentReplies.Count > 20)
            _sentReplies.Dequeue();
        _lastReplyAt = DateTime.Now;
        _lastSeen = newest.Id;
        _bar.SetStatus($"已回覆 {newest.Author}：「{Shorten(reply, 60)}」");
    }

    private Task<IReadOnlyList<ChatMessage>> ReadAsync() => Task.Run(() => _chat.ReadMessages(_ownNames));

    private bool HasNewFromOthers(IReadOnlyList<ChatMessage> messages) =>
        messages.Any(message => message.Id > _lastSeen && !IsOurs(message));

    private bool IsOurs(ChatMessage message) => message.FromMe || _sentReplies.Contains(message.Text);

    private static string Shorten(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";

    public void Dispose()
    {
        Stop("LaunchBuddy 已結束。");
        _poll.Dispose();
        _stop.Dispose();
        _bar.Dispose();
    }
}
