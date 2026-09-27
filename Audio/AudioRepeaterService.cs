using EchoBridge.Services;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace EchoBridge.Audio;

public sealed class AudioRepeaterService : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private MMDeviceEnumerator? _enumerator;
    private MMDeviceNotificationClient? _notifications;
    private MMDevice? _input;
    private MMDevice? _output;
    private WasapiRecorder? _recorder;
    private WasapiPlayer? _player;
    private BufferedWaveProvider? _buffer;
    private GainMeterSampleProvider? _gainMeter;
    private string? _inputId;
    private string? _outputId;
    private int _running;
    private int _faultQueued;
    private int _session;
    private int _overflowCount;
    private int _underrunCount;
    private long _lastCaptureTick;

    public event Action<string>? StatusChanged;
    public bool IsRunning => Volatile.Read(ref _running) != 0;
    public float Level => _gainMeter?.Peak ?? 0f;

    public async Task StartAsync(string inputId, string outputId, int bufferMilliseconds, int gainPercent)
    {
        if (string.IsNullOrWhiteSpace(inputId) || string.IsNullOrWhiteSpace(outputId))
            throw new ArgumentException("Select both devices.");
        if (string.Equals(inputId, outputId, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Input and Output must be different devices.");
        if (bufferMilliseconds is not (10 or 25 or 50 or 100 or 200))
            throw new ArgumentOutOfRangeException(nameof(bufferMilliseconds));

        await _gate.WaitAsync();
        try
        {
            if (IsRunning) return;
            await Task.Run(() => StartCore(inputId, outputId, bufferMilliseconds, gainPercent));
            StatusChanged?.Invoke("Running");
        }
        finally { _gate.Release(); }
    }

    private void StartCore(string inputId, string outputId, int bufferMilliseconds, int gainPercent)
    {
        var stage = "input";
        try
        {
            _inputId = inputId;
            _outputId = outputId;
            _enumerator = new MMDeviceEnumerator();
            _input = _enumerator.GetDevice(inputId);
            if (_input.State != DeviceState.Active) throw new InvalidOperationException("Input device disconnected");
            stage = "output";
            _output = _enumerator.GetDevice(outputId);
            if (_output.State != DeviceState.Active) throw new InvalidOperationException("Output device disconnected");

            LoggingService.Write($"Start input={inputId} output={outputId} buffer={bufferMilliseconds}ms gain={gainPercent}%");
            stage = "input";
            _recorder = new WasapiRecorderBuilder()
                .WithDevice(_input).WithLoopbackCapture().WithSharedMode()
                .WithBufferLength(bufferMilliseconds).Build();
            _recorder.DataAvailable += OnDataAvailable;
            _recorder.RecordingStopped += OnRecordingStopped;

            var captureFormat = _recorder.WaveFormat;
            var capacityMs = Math.Max(500, bufferMilliseconds * 4);
            _buffer = new BufferedWaveProvider(captureFormat, TimeSpan.FromMilliseconds(capacityMs))
            {
                DiscardOnBufferOverflow = false,
                ReadFully = true
            };
            var buffer = _buffer;
            _gainMeter = new GainMeterSampleProvider(buffer.ToSampleProvider(),
                () => buffer.BufferedBytes / Math.Max(1, captureFormat.BitsPerSample / 8), OnPossibleUnderrun);
            _gainMeter.SetGain(gainPercent / 100f);

            stage = "output";
            _player = new WasapiPlayerBuilder()
                .WithDevice(_output).WithSharedMode().WithEventSync()
                .WithLatency(bufferMilliseconds).Build();
            _player.PlaybackStopped += OnPlaybackStopped;
            _player.Init(_gainMeter);

            _notifications = _enumerator.CreateNotificationClient(useSynchronizationContext: false);
            _notifications.DeviceRemoved += OnDeviceRemoved;
            _notifications.DeviceStateChanged += OnDeviceStateChanged;

            LoggingService.Write($"Formats capture={captureFormat}; output mix={_player.DeviceMixFormat}; rendered={_player.OutputWaveFormat}; output latency={_player.LatencyMilliseconds}ms");
            Interlocked.Increment(ref _session);
            Volatile.Write(ref _running, 1);
            _player.Play();
            stage = "input";
            _recorder.StartRecording();
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _running, 0);
            LoggingService.Write("Start failed", ex);
            StopCore();
            throw new AudioStartException(ex switch
            {
                NotSupportedException => "Unsupported audio format",
                InvalidOperationException when ex.Message.Contains("disconnected", StringComparison.OrdinalIgnoreCase) => ex.Message,
                _ when stage == "output" => "Failed to open output device",
                _ => "Failed to open input device"
            }, ex);
        }
    }

    public async Task StopAsync(string status = "Ready")
    {
        await _gate.WaitAsync();
        try
        {
            await Task.Run(StopCore);
            StatusChanged?.Invoke(status);
        }
        finally { _gate.Release(); }
    }

    private void StopCore()
    {
        Volatile.Write(ref _running, 0);
        Interlocked.Increment(ref _session);
        // Stop capture first so no new samples enter the buffer while playback is released.
        try { _notifications?.Dispose(); } catch (Exception ex) { LoggingService.Write("Notification disposal failed", ex); }
        _notifications = null;
        try { _recorder?.StopRecording(); } catch (Exception ex) { LoggingService.Write("Capture stop failed", ex); }
        try { _player?.Stop(); } catch (Exception ex) { LoggingService.Write("Playback stop failed", ex); }
        if (_recorder is not null)
        {
            _recorder.DataAvailable -= OnDataAvailable;
            _recorder.RecordingStopped -= OnRecordingStopped;
            try { _recorder.Dispose(); } catch (Exception ex) { LoggingService.Write("Capture disposal failed", ex); }
            _recorder = null;
        }
        if (_player is not null)
        {
            _player.PlaybackStopped -= OnPlaybackStopped;
            try { _player.Dispose(); } catch (Exception ex) { LoggingService.Write("Playback disposal failed", ex); }
            _player = null;
        }
        try { _input?.Dispose(); } catch (Exception ex) { LoggingService.Write("Input disposal failed", ex); }
        _input = null;
        try { _output?.Dispose(); } catch (Exception ex) { LoggingService.Write("Output disposal failed", ex); }
        _output = null;
        try { _enumerator?.Dispose(); } catch (Exception ex) { LoggingService.Write("Enumerator disposal failed", ex); }
        _enumerator = null;
        _buffer = null;
        _gainMeter = null;
        _inputId = null;
        _outputId = null;
        Interlocked.Exchange(ref _faultQueued, 0);
        LoggingService.Write($"Stop; overflows={_overflowCount}; underruns={_underrunCount}");
        _overflowCount = 0;
        _underrunCount = 0;
    }

    public void SetGainPercent(int value) => _gainMeter?.SetGain(value / 100f);

    private void OnDataAvailable(ReadOnlySpan<byte> data, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
    {
        if (!IsRunning || _buffer is null) return;
        try
        {
            Volatile.Write(ref _lastCaptureTick, Environment.TickCount64);
            if (data.Length > _buffer.BufferLength)
            {
                var alignedLength = _buffer.BufferLength / _buffer.WaveFormat.BlockAlign * _buffer.WaveFormat.BlockAlign;
                data = data[^alignedLength..];
            }
            // Keep latency bounded. Drop stale audio rather than letting Bluetooth jitter grow the queue forever.
            if (_buffer.BufferedBytes + data.Length > _buffer.BufferLength)
            {
                _buffer.ClearBuffer();
                if (Interlocked.Increment(ref _overflowCount) % 20 == 1)
                    _ = Task.Run(() => LoggingService.Write("Capture buffer overflow: dropped stale audio"));
            }
            _buffer.AddSamples(data);
        }
        catch (Exception ex) { SignalFault("Audio stream stopped unexpectedly", ex); }
    }

    private void OnPossibleUnderrun()
    {
        if (!IsRunning || Environment.TickCount64 - Volatile.Read(ref _lastCaptureTick) > 250) return;
        if (Interlocked.Increment(ref _underrunCount) % 100 == 1)
            _ = Task.Run(() => LoggingService.Write("Playback buffer underrun: silence supplied"));
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (IsRunning) SignalFault(e.Exception is null ? "Input device disconnected" : "Audio stream stopped unexpectedly", e.Exception);
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (IsRunning) SignalFault(e.Exception is null ? "Output device disconnected" : "Audio stream stopped unexpectedly", e.Exception);
    }

    private void OnDeviceRemoved(object? sender, DeviceNotificationEventArgs e) => CheckDeviceLost(e.DeviceId);
    private void OnDeviceStateChanged(object? sender, DeviceStateChangedEventArgs e)
    {
        if (e.NewState != DeviceState.Active) CheckDeviceLost(e.DeviceId);
    }

    private void CheckDeviceLost(string id)
    {
        if (string.Equals(id, _inputId, StringComparison.OrdinalIgnoreCase)) SignalFault("Input device disconnected");
        else if (string.Equals(id, _outputId, StringComparison.OrdinalIgnoreCase)) SignalFault("Output device disconnected");
    }

    private void SignalFault(string status, Exception? exception = null)
    {
        if (!IsRunning || Interlocked.Exchange(ref _faultQueued, 1) != 0) return;
        var session = Volatile.Read(ref _session);
        _ = Task.Run(async () =>
        {
            LoggingService.Write(status, exception);
            try { await StopIfSessionAsync(session, status); }
            catch (Exception ex) { LoggingService.Write("Fault cleanup failed", ex); StatusChanged?.Invoke(status); }
        });
    }

    private async Task StopIfSessionAsync(int session, string status)
    {
        await _gate.WaitAsync();
        try
        {
            if (session != Volatile.Read(ref _session) || !IsRunning) return;
            await Task.Run(StopCore);
            StatusChanged?.Invoke(status);
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _gate.Dispose();
    }
}

public sealed class AudioStartException(string message, Exception innerException) : Exception(message, innerException);
