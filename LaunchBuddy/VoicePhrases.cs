using System.Text.RegularExpressions;

namespace LaunchBuddy;

internal enum VoiceConfirmation
{
    None,
    Approve,
    Reject
}

// Turns Whisper transcripts into wake-phrase commands and spoken Approve/Reject answers.
internal static class VoicePhrases
{
    // Biases Whisper towards the words the assistant listens for.
    public const string WhisperPrompt = "Hey Minibot, open Google Chrome. Approve. Reject. 批准。取消。";

    // Whisper spells the wake phrase many ways: "Hey Minibot", "Hey, mini bot.", "Hi Minibots", "嘿 Minibot".
    private static readonly Regex WakePattern = new(
        @"^[\s\p{P}]*(?:(?:hey|hi|hay|hei|嘿|嗨|喂|黑)[\s\p{P}]*)?(?:mini|many|meany|迷你)[\s\-]*(?:bots?|but|bought|波|博)(?![a-z])[\s\p{P}]*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Whisper marks silence or noise with tags such as "[BLANK_AUDIO]" or "(music)".
    private static readonly Regex NoiseTags = new(@"\[[^\]]*\]|\([^)]*\)|（[^）]*）", RegexOptions.CultureInvariant);

    private static readonly HashSet<string> ApproveWords = new(StringComparer.Ordinal)
    {
        "approve", "approved", "approveit", "approval", "iapprove", "yesapprove", "批准", "確認", "确认", "核准", "同意"
    };

    private static readonly HashSet<string> RejectWords = new(StringComparer.Ordinal)
    {
        "reject", "rejected", "rejectit", "cancel", "noreject", "取消", "拒絕", "拒绝", "唔要", "不要"
    };

    public static string Clean(string transcript) => NoiseTags.Replace(transcript, " ").Trim();

    public static bool TryStripWakePhrase(string transcript, out string command)
    {
        var text = Clean(transcript);
        var match = WakePattern.Match(text);
        command = match.Success ? text[match.Length..].Trim() : string.Empty;
        return match.Success;
    }

    // Only a short, unambiguous answer counts, so a passing sentence that merely contains "approve" does not.
    public static VoiceConfirmation ParseConfirmation(string transcript)
    {
        var text = TryStripWakePhrase(transcript, out var rest) ? rest : Clean(transcript);
        var normalized = new string(text.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        if (ApproveWords.Contains(normalized))
            return VoiceConfirmation.Approve;
        if (RejectWords.Contains(normalized))
            return VoiceConfirmation.Reject;
        return VoiceConfirmation.None;
    }
}
