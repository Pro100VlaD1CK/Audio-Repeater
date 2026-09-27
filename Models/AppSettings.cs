namespace EchoBridge.Models;

public sealed class AppSettings
{
    public string? InputEndpointId { get; set; }
    public string? OutputEndpointId { get; set; }
    public int BufferMilliseconds { get; set; } = 50;
    public int GainPercent { get; set; } = 100;
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }

    public void Normalize()
    {
        if (BufferMilliseconds is not (10 or 25 or 50 or 100 or 200)) BufferMilliseconds = 50;
        GainPercent = Math.Clamp(GainPercent, 0, 150);
        if (WindowLeft is not null && !double.IsFinite(WindowLeft.Value)) WindowLeft = null;
        if (WindowTop is not null && !double.IsFinite(WindowTop.Value)) WindowTop = null;
    }
}
