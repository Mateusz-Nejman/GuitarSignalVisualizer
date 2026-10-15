using System;

namespace GuitarSignalVisualiser.Audio;

/// <summary>
/// Alias / Facade for GuitarDspAnalyzer matching deliverable name DspProcessor.cs.
/// </summary>
public sealed class DspProcessor
{
    private readonly GuitarDspAnalyzer _analyzer;

    public int BufferSize => _analyzer.BufferSize;
    public int SpectrumBins => _analyzer.SpectrumBins;

    public DspProcessor(int bufferSize = 2048)
    {
        _analyzer = new GuitarDspAnalyzer(bufferSize);
    }

    public void Process(
        ReadOnlySpan<float> inputBuffer,
        int sampleRate,
        float inputGainLinear,
        float noiseGateThresholdDbfs,
        SignalAnalysisResult result)
    {
        _analyzer.Process(inputBuffer, sampleRate, inputGainLinear, noiseGateThresholdDbfs, result);
    }
}
