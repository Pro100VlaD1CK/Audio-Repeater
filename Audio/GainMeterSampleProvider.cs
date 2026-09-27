using NAudio.Wave;

namespace EchoBridge.Audio;

internal sealed class GainMeterSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly Func<int> _availableSamples;
    private readonly Action _onUnderrun;
    private float _gain = 1f;
    private float _peak;

    public GainMeterSampleProvider(ISampleProvider source, Func<int> availableSamples, Action onUnderrun)
    {
        _source = source;
        _availableSamples = availableSamples;
        _onUnderrun = onUnderrun;
    }
    public WaveFormat WaveFormat => _source.WaveFormat;
    public float Peak => Volatile.Read(ref _peak);
    public void SetGain(float value) => Volatile.Write(ref _gain, Math.Clamp(value, 0f, 1.5f));

    public int Read(Span<float> buffer)
    {
        if (_availableSamples() < buffer.Length) _onUnderrun();
        var read = _source.Read(buffer);
        var gain = Volatile.Read(ref _gain);
        var peak = 0f;
        for (var i = 0; i < read; i++)
        {
            buffer[i] *= gain;
            peak = Math.Max(peak, Math.Abs(buffer[i]));
        }
        Volatile.Write(ref _peak, Math.Min(1f, peak));
        return read;
    }
}
