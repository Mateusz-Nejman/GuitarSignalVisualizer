using System;
using Xunit;
using GuitarSignalVisualiser.Audio;

namespace GuitarSignalVisualiser.Tests;

public class DspTests
{
    [Fact]
    public void RingBuffer_WriteReadPeek_WorksCorrectly()
    {
        var rb = new RingBuffer(64);
        Assert.Equal(64, rb.Capacity);
        Assert.Equal(0, rb.AvailableToRead);

        float[] writeData = [1f, 2f, 3f, 4f, 5f];
        int written = rb.Write(writeData);
        Assert.Equal(5, written);
        Assert.Equal(5, rb.AvailableToRead);

        // Peek
        float[] peekData = new float[5];
        int peeked = rb.Peek(peekData);
        Assert.Equal(5, peeked);
        Assert.Equal(writeData, peekData);
        Assert.Equal(5, rb.AvailableToRead); // Read position unchanged

        // Read
        float[] readData = new float[5];
        int read = rb.Read(readData);
        Assert.Equal(5, read);
        Assert.Equal(writeData, readData);
        Assert.Equal(0, rb.AvailableToRead);
    }

    [Fact]
    public void DspAnalyzer_RmsAndPeak_CalculatedAccurately()
    {
        var analyzer = new GuitarDspAnalyzer(2048);
        var result = new SignalAnalysisResult();

        // 1.0 amplitude sine wave: Theoretical RMS is 1 / sqrt(2) ≈ 0.7071 => 20*log10(0.7071) ≈ -3.01 dBFS
        float[] buffer = new float[2048];
        for (int i = 0; i < 2048; i++)
        {
            buffer[i] = MathF.Sin(2.0f * MathF.PI * 440.0f * i / 48000.0f);
        }

        analyzer.Process(buffer, 48000, 1.0f, -80.0f, result);

        Assert.True(MathF.Abs(result.PeakLinear - 1.0f) < 0.05f, $"PeakLinear was {result.PeakLinear}");
        Assert.True(MathF.Abs(result.RmsDbfs - (-3.01f)) < 0.5f, $"RmsDbfs was {result.RmsDbfs}");
        Assert.True(result.IsGateOpen);
    }

    [Theory]
    [InlineData(65.41f, "C2")]   // Drop C
    [InlineData(82.41f, "E2")]   // 6th String
    [InlineData(110.0f, "A2")]   // 5th String
    [InlineData(146.83f, "D3")]  // 4th String
    [InlineData(196.0f, "G3")]   // 3rd String
    [InlineData(246.94f, "B3")]  // 2nd String
    [InlineData(329.63f, "E4")]  // 1st String
    public void YinPitchDetection_AccurateAcrossGuitarStrings(float freq, string expectedNote)
    {
        var analyzer = new GuitarDspAnalyzer(2048);
        var result = new SignalAnalysisResult();

        float[] buffer = new float[2048];
        for (int i = 0; i < 2048; i++)
        {
            // Add fundamental and 2nd harmonic
            buffer[i] = 0.8f * MathF.Sin(2.0f * MathF.PI * freq * i / 48000.0f)
                      + 0.2f * MathF.Sin(4.0f * MathF.PI * freq * i / 48000.0f);
        }

        analyzer.Process(buffer, 48000, 1.0f, -80.0f, result);

        Assert.True(result.HasPitch, $"Failed to detect pitch for {freq} Hz");
        Assert.Equal(expectedNote, result.NoteName);
        Assert.True(MathF.Abs(result.PitchHz - freq) < 2.0f, $"Detected pitch {result.PitchHz} differed from {freq}");
        Assert.True(result.IsInTune, $"Tuning deviation {result.CentDeviation} was not in tune for {freq} Hz");
    }

    [Fact]
    public void SpectralCentroid_DistinguishesDarkVsBrightTone()
    {
        var analyzer = new GuitarDspAnalyzer(2048);

        // Low frequency tone (200 Hz - neck pickup warm)
        var darkResult = new SignalAnalysisResult();
        float[] darkBuffer = new float[2048];
        for (int i = 0; i < 2048; i++)
        {
            darkBuffer[i] = MathF.Sin(2.0f * MathF.PI * 200.0f * i / 48000.0f);
        }
        analyzer.Process(darkBuffer, 48000, 1.0f, -80.0f, darkResult);

        // High frequency tone (3500 Hz - bridge pickup biting)
        var brightResult = new SignalAnalysisResult();
        float[] brightBuffer = new float[2048];
        for (int i = 0; i < 2048; i++)
        {
            brightBuffer[i] = MathF.Sin(2.0f * MathF.PI * 3500.0f * i / 48000.0f);
        }
        analyzer.Process(brightBuffer, 48000, 1.0f, -80.0f, brightResult);

        Assert.True(darkResult.SpectralCentroidHz < 1200.0f, $"Dark centroid was {darkResult.SpectralCentroidHz}");
        Assert.Equal("Ciemna / Ciepła", darkResult.TimbreClassification);

        Assert.True(brightResult.SpectralCentroidHz > 2800.0f, $"Bright centroid was {brightResult.SpectralCentroidHz}");
        Assert.Equal("Jasna / Ostra", brightResult.TimbreClassification);
    }

    [Fact]
    public void NoiseGate_MutesAnalysis_WhenSignalBelowThreshold()
    {
        var analyzer = new GuitarDspAnalyzer(2048);
        var result = new SignalAnalysisResult();

        // Very faint signal: -70 dBFS
        float[] buffer = new float[2048];
        for (int i = 0; i < 2048; i++)
        {
            buffer[i] = 0.0003f * MathF.Sin(2.0f * MathF.PI * 440.0f * i / 48000.0f);
        }

        // Noise gate set to -40 dBFS
        analyzer.Process(buffer, 48000, 1.0f, -40.0f, result);

        Assert.False(result.IsGateOpen);
        Assert.False(result.HasPitch);
        Assert.Equal("--", result.NoteName);
        Assert.Equal("Cisza", result.TimbreClassification);
    }

    [Fact]
    public void HarmonicRatio_HigherForDistortedSignalThanPureSine()
    {
        var analyzer = new GuitarDspAnalyzer(2048);

        // Pure sine (110 Hz)
        var pureResult = new SignalAnalysisResult();
        float[] pureBuffer = new float[2048];
        for (int i = 0; i < 2048; i++)
        {
            pureBuffer[i] = 0.8f * MathF.Sin(2.0f * MathF.PI * 110.0f * i / 48000.0f);
        }
        analyzer.Process(pureBuffer, 48000, 1.0f, -80.0f, pureResult);

        // Overdriven / saturated signal with rich harmonics
        var distResult = new SignalAnalysisResult();
        float[] distBuffer = new float[2048];
        for (int i = 0; i < 2048; i++)
        {
            float s = 0.8f * MathF.Sin(2.0f * MathF.PI * 110.0f * i / 48000.0f)
                    + 0.5f * MathF.Sin(4.0f * MathF.PI * 110.0f * i / 48000.0f)
                    + 0.4f * MathF.Sin(6.0f * MathF.PI * 110.0f * i / 48000.0f)
                    + 0.3f * MathF.Sin(8.0f * MathF.PI * 110.0f * i / 48000.0f);
            distBuffer[i] = MathF.Tanh(s * 1.5f);
        }
        analyzer.Process(distBuffer, 48000, 1.0f, -80.0f, distResult);

        Assert.True(distResult.HarmonicRatio > pureResult.HarmonicRatio,
            $"Distorted ratio {distResult.HarmonicRatio} should exceed pure ratio {pureResult.HarmonicRatio}");
    }
}
