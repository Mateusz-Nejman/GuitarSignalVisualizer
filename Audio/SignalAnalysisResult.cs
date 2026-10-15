namespace GuitarSignalVisualiser.Audio;

public class SignalAnalysisResult
{
    // Amplitude & Dynamics
    public float RmsDbfs { get; set; } = -96f;
    public float PeakDbfs { get; set; } = -96f;
    public float PeakLinear { get; set; } = 0f;
    public bool IsClipping { get; set; }
    public bool IsGateOpen { get; set; }

    // Pitch & Tuning
    public bool HasPitch { get; set; }
    public float PitchHz { get; set; } = 0f;
    public string NoteName { get; set; } = "--";
    public int MidiNote { get; set; } = 0;
    public float CentDeviation { get; set; } = 0f;
    public bool IsInTune { get; set; }
    public string TuningHint { get; set; } = string.Empty;

    // Timbre & Harmonics
    public float SpectralCentroidHz { get; set; } = 0f;
    public string TimbreClassification { get; set; } = "Cisza";
    public float HarmonicRatio { get; set; } = 0f; // 0.0 to 1.0 (Harmonics energy relative to fundamental)

    // Visualisation buffers
    public float[] WaveformBuffer { get; set; } = [];
    public float[] SpectrumBuffer { get; set; } = [];
    public int SampleRate { get; set; } = 48000;
}
