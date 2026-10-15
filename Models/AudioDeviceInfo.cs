namespace GuitarSignalVisualiser.Models;

public enum AudioChannelSelection
{
    MonoInput1 = 0, // Left / Input 1
    MonoInput2 = 1, // Right / Input 2
    StereoMix = 2   // (L + R) / 2
}

public class AudioDeviceInfo
{
    public int DeviceNumber { get; init; }
    public string Name { get; init; } = string.Empty;
    public int Channels { get; init; }
    public bool IsDefault { get; init; }

    public override string ToString() => Name;
}
