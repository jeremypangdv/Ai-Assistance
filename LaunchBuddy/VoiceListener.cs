using NAudio.Wave;
using System.Threading.Channels;
using Whisper.net;
using Whisper.net.Ggml;

namespace LaunchBuddy;

// Transcribes speech from the default microphone locally with Whisper, in two modes that can run together:
// continuous listening (a simple energy VAD cuts out each utterance) and push-to-talk (everything between Begin and End).
// The microphone is open only while one of them is active. Transcribed is raised on a background thread.
internal sealed class VoiceListener : IDisposable
{
    private const int SampleRate = 16000;
    private const int FrameMilliseconds = 30;
    private const int PreRollFrames = 10;            // keep 300 ms before speech starts
    private const int StartFrames = 2;               // 60 ms above threshold starts a segment
    private const int EndSilenceFrames = 27;         // 800 ms of quiet ends it
    private const int MinimumSpeechFrames = 10;      // ignore clicks shorter than 300 ms
    private const int MinimumPushToTalkFrames = 6;   // a held key with less speech than this was silence
    private const int MaximumSegmentFrames = 15000 / FrameMilliseconds;
    private const float MinimumThreshold = 0.01f;
    // Audio still in flight when the key is released.
    private static readonly TimeSpan PushToTalkTail = TimeSpan.FromMilliseconds(150);

    public static readonly string ModelPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LaunchBuddy", "models", "ggml-small-q5_1.bin");

    private readonly Channel<(float[] Samples, DateTime StartedAt, bool PushToTalk)> _segments =
        Channel.CreateBounded<(float[], DateTime, bool)>(new BoundedChannelOptions(3) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private readonly Queue<float[]> _preRoll = new();
    private readonly List<float> _speech = [];
    private readonly List<float> _pushToTalkAudio = [];
    private WaveIn? _microphone;
    private WhisperFactory? _factory;
    private WhisperProcessor? _processor;
    private Task? _worker;
    private bool _continuous;
    private bool _pushToTalk;
    private int _pushToTalkGeneration;
    private int _pushToTalkVoicedFrames;
    private DateTime _pushToTalkStarted;
    private float _noiseFloor = 0.005f;
    private bool _inSpeech;
    private int _loudFrames;
    private int _quietFrames;
    private int _voicedFrames;
    private int _segmentFrames;
    private DateTime _speechStarted;

    public event Action<string, DateTime, bool>? Transcribed;
    public event Action<Exception>? Failed;

    public static bool IsModelDownloaded => File.Exists(ModelPath);
    public bool IsContinuous => _continuous;

    public static async Task DownloadModelAsync(IProgress<long>? progress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ModelPath)!);
        var partial = ModelPath + ".part";
        await using (var source = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(GgmlType.Small, QuantizationType.Q5_1, cancellationToken))
        await using (var target = File.Create(partial))
        {
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                total += read;
                progress?.Report(total);
            }
        }
        File.Move(partial, ModelPath, overwrite: true);
    }

    // Slow (about a second); call it off the UI thread.
    public void LoadModel()
    {
        _factory = WhisperFactory.FromPath(ModelPath);
        // Fixing the language to "zh" still transcribes English as English, and skips auto-detection (about 4 s per clip on CPU).
        // An audio context of 768 covers the 15 s segment cap and cuts the encoder cost by more than half.
        _processor = _factory.CreateBuilder()
            .WithLanguage("zh")
            .WithAudioContextSize(768)
            .WithPrompt(VoicePhrases.WhisperPrompt)
            .WithThreads(Math.Clamp(Environment.ProcessorCount / 2, 2, 8))
            .Build();
        _worker = Task.Run(() => TranscribeLoopAsync(_stop.Token));
    }

    public void SetContinuous(bool enabled)
    {
        lock (_gate)
        {
            _continuous = enabled;
            ResetVad();
        }
        UpdateMicrophone();
    }

    public void BeginPushToTalk()
    {
        lock (_gate)
        {
            _pushToTalk = true;
            _pushToTalkGeneration++;
            _pushToTalkVoicedFrames = 0;
            _pushToTalkAudio.Clear();
            // With continuous listening on, the last 300 ms are already buffered; keep them in case speech began with the key.
            foreach (var buffered in _preRoll)
                _pushToTalkAudio.AddRange(buffered);
            _pushToTalkStarted = DateTime.Now.AddMilliseconds(-FrameMilliseconds * _preRoll.Count);
            ResetVad();
        }
        UpdateMicrophone();
    }

    public void CancelPushToTalk()
    {
        lock (_gate)
        {
            _pushToTalk = false;
            _pushToTalkAudio.Clear();
        }
        UpdateMicrophone();
    }

    // False when nothing was sent for transcription: no speech was heard, or a newer press took over.
    public async Task<bool> EndPushToTalkAsync()
    {
        int generation;
        var queued = false;
        lock (_gate)
            generation = _pushToTalkGeneration;
        await Task.Delay(PushToTalkTail);
        lock (_gate)
        {
            // A new press during the tail owns the recording now.
            if (!_pushToTalk || generation != _pushToTalkGeneration)
                return false;
            _pushToTalk = false;
            if (_pushToTalkVoicedFrames >= MinimumPushToTalkFrames)
                queued = _segments.Writer.TryWrite((PadForWhisper(_pushToTalkAudio), _pushToTalkStarted, true));
            _pushToTalkAudio.Clear();
        }
        UpdateMicrophone();
        return queued;
    }

    private void UpdateMicrophone()
    {
        bool wanted;
        lock (_gate)
            wanted = _continuous || _pushToTalk;
        if (wanted && _microphone is null)
        {
            // Device -1 is WAVE_MAPPER, i.e. the microphone chosen in Windows sound settings.
            var microphone = new WaveIn
            {
                DeviceNumber = -1,
                WaveFormat = new WaveFormat(SampleRate, 16, 1),
                BufferMilliseconds = FrameMilliseconds
            };
            microphone.DataAvailable += OnAudio;
            microphone.RecordingStopped += (_, eventArgs) =>
            {
                if (eventArgs.Exception is not null && !_stop.IsCancellationRequested)
                    Failed?.Invoke(eventArgs.Exception);
            };
            try
            {
                microphone.StartRecording();
            }
            catch
            {
                microphone.Dispose();
                lock (_gate)
                {
                    _continuous = false;
                    _pushToTalk = false;
                }
                throw;
            }
            _microphone = microphone;
        }
        else if (!wanted && _microphone is not null)
        {
            // A fresh device per session: a stopping WaveIn cannot be restarted until its thread has finished.
            var microphone = _microphone;
            _microphone = null;
            microphone.DataAvailable -= OnAudio;
            try { microphone.StopRecording(); } catch (Exception exception) { AppLog.Error("Voice", exception); }
            microphone.Dispose();
            lock (_gate)
            {
                _preRoll.Clear();
                ResetVad();
            }
        }
    }

    private void OnAudio(object? sender, WaveInEventArgs eventArgs)
    {
        var frame = new float[eventArgs.BytesRecorded / 2];
        double sum = 0;
        for (var index = 0; index < frame.Length; index++)
        {
            frame[index] = BitConverter.ToInt16(eventArgs.Buffer, index * 2) / 32768f;
            sum += frame[index] * frame[index];
        }
        var rms = frame.Length == 0 ? 0f : (float)Math.Sqrt(sum / frame.Length);

        lock (_gate)
        {
            var threshold = Math.Max(MinimumThreshold, _noiseFloor * 3f);
            var loud = rms > threshold;
            if (_pushToTalk)
            {
                if (_pushToTalkAudio.Count < SampleRate * 15)
                    _pushToTalkAudio.AddRange(frame);
                if (loud)
                    _pushToTalkVoicedFrames++;
                return;
            }
            if (_continuous)
                DetectSpeech(frame, rms, loud);
        }
    }

    private void DetectSpeech(float[] frame, float rms, bool loud)
    {
        if (!_inSpeech)
        {
            if (!loud)
                _noiseFloor = _noiseFloor * 0.95f + rms * 0.05f;
            _preRoll.Enqueue(frame);
            if (_preRoll.Count > PreRollFrames)
                _preRoll.Dequeue();
            _loudFrames = loud ? _loudFrames + 1 : 0;
            if (_loudFrames < StartFrames)
                return;

            _inSpeech = true;
            _speechStarted = DateTime.Now.AddMilliseconds(-FrameMilliseconds * _preRoll.Count);
            _speech.Clear();
            foreach (var buffered in _preRoll)
                _speech.AddRange(buffered);
            _preRoll.Clear();
            _voicedFrames = _loudFrames;
            _segmentFrames = _loudFrames;
            _quietFrames = 0;
            return;
        }

        _speech.AddRange(frame);
        _segmentFrames++;
        if (loud)
        {
            _voicedFrames++;
            _quietFrames = 0;
        }
        else
        {
            _quietFrames++;
        }

        if (_quietFrames < EndSilenceFrames && _segmentFrames < MaximumSegmentFrames)
            return;

        if (_voicedFrames >= MinimumSpeechFrames)
            _segments.Writer.TryWrite((PadForWhisper(_speech), _speechStarted, false));
        ResetVad();
    }

    private void ResetVad()
    {
        _inSpeech = false;
        _loudFrames = 0;
        _speech.Clear();
    }

    // whisper.cpp refuses clips under one second, so pad short commands such as "approve" with silence.
    private static float[] PadForWhisper(List<float> audio)
    {
        var samples = new float[Math.Max(audio.Count, SampleRate * 6 / 5)];
        audio.CopyTo(samples);
        return samples;
    }

    private async Task TranscribeLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var (samples, startedAt, pushToTalk) in _segments.Reader.ReadAllAsync(cancellationToken))
            {
                var parts = new List<string>();
                await foreach (var segment in _processor!.ProcessAsync(samples, cancellationToken))
                    parts.Add(segment.Text);
                var text = VoicePhrases.Clean(string.Join(" ", parts));
                // A push-to-talk recording always gets an answer, even an empty one, so the popup can stop waiting.
                if (text.Length > 0 || pushToTalk)
                    Transcribed?.Invoke(text, startedAt, pushToTalk);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            AppLog.Error("Voice", exception);
            Failed?.Invoke(exception);
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        lock (_gate)
        {
            _continuous = false;
            _pushToTalk = false;
        }
        UpdateMicrophone();
        _segments.Writer.TryComplete();
        var finished = true;
        try { finished = _worker?.Wait(TimeSpan.FromSeconds(5)) ?? true; } catch (AggregateException) { }
        // Freeing the native model while a transcription is still running would crash, so leave it to process exit.
        if (finished)
        {
            _processor?.Dispose();
            _factory?.Dispose();
        }
        _stop.Dispose();
    }
}
