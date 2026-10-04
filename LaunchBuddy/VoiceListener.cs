using NAudio.Wave;
using System.Threading.Channels;
using Whisper.net;
using Whisper.net.Ggml;

namespace LaunchBuddy;

// Listens to the default microphone, cuts speech out with a simple energy VAD and transcribes it locally with Whisper.
// Transcribed is raised on a background thread.
internal sealed class VoiceListener : IDisposable
{
    private const int SampleRate = 16000;
    private const int FrameMilliseconds = 30;
    private const int PreRollFrames = 10;            // keep 300 ms before speech starts
    private const int StartFrames = 2;               // 60 ms above threshold starts a segment
    private const int EndSilenceFrames = 27;         // 800 ms of quiet ends it
    private const int MinimumSpeechFrames = 10;      // ignore clicks shorter than 300 ms
    private const int MaximumSegmentFrames = 15000 / FrameMilliseconds;
    private const float MinimumThreshold = 0.01f;

    public static readonly string ModelPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LaunchBuddy", "models", "ggml-small-q5_1.bin");

    private readonly Channel<(float[] Samples, DateTime StartedAt)> _segments =
        Channel.CreateBounded<(float[], DateTime)>(new BoundedChannelOptions(3) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly CancellationTokenSource _stop = new();
    private readonly Queue<float[]> _preRoll = new();
    private readonly List<float> _speech = [];
    private WaveIn? _microphone;
    private WhisperFactory? _factory;
    private WhisperProcessor? _processor;
    private Task? _worker;
    private float _noiseFloor = 0.005f;
    private bool _inSpeech;
    private int _loudFrames;
    private int _quietFrames;
    private int _voicedFrames;
    private int _segmentFrames;
    private DateTime _speechStarted;

    public event Action<string, DateTime>? Transcribed;
    public event Action<Exception>? Failed;

    public static bool IsModelDownloaded => File.Exists(ModelPath);

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

    public void Start()
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

        // Device -1 is WAVE_MAPPER, i.e. the microphone chosen in Windows sound settings.
        _microphone = new WaveIn
        {
            DeviceNumber = -1,
            WaveFormat = new WaveFormat(SampleRate, 16, 1),
            BufferMilliseconds = FrameMilliseconds
        };
        _microphone.DataAvailable += OnAudio;
        _microphone.RecordingStopped += (_, eventArgs) =>
        {
            if (eventArgs.Exception is not null && !_stop.IsCancellationRequested)
                Failed?.Invoke(eventArgs.Exception);
        };
        _microphone.StartRecording();
        _worker = Task.Run(() => TranscribeLoopAsync(_stop.Token));
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
        var threshold = Math.Max(MinimumThreshold, _noiseFloor * 3f);
        var loud = rms > threshold;

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

        _inSpeech = false;
        _loudFrames = 0;
        if (_voicedFrames >= MinimumSpeechFrames)
        {
            // whisper.cpp refuses clips under one second, so pad short commands such as "approve" with silence.
            var samples = new float[Math.Max(_speech.Count, SampleRate * 6 / 5)];
            _speech.CopyTo(samples);
            _segments.Writer.TryWrite((samples, _speechStarted));
        }
        _speech.Clear();
    }

    private async Task TranscribeLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var (samples, startedAt) in _segments.Reader.ReadAllAsync(cancellationToken))
            {
                var parts = new List<string>();
                await foreach (var segment in _processor!.ProcessAsync(samples, cancellationToken))
                    parts.Add(segment.Text);
                var text = VoicePhrases.Clean(string.Join(" ", parts));
                if (text.Length > 0)
                    Transcribed?.Invoke(text, startedAt);
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
        if (_microphone is not null)
        {
            _microphone.DataAvailable -= OnAudio;
            try { _microphone.StopRecording(); } catch (Exception exception) { AppLog.Error("Voice", exception); }
            _microphone.Dispose();
        }
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
