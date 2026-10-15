using System;

namespace GuitarSignalVisualiser.Audio;

/// <summary>
/// High-performance Digital Signal Processing (DSP) engine for guitar signal analysis.
/// Implements zero-allocation FFT with Hanning window, YIN pitch detection,
/// RMS/Peak dBFS measurement, Spectral Centroid, and Harmonic Ratio analysis.
/// </summary>
public sealed class GuitarDspAnalyzer
{
    private readonly int _bufferSize;
    private readonly float[] _hannWindow;
    private readonly float[] _fftReal;
    private readonly float[] _fftImag;
    private readonly float[] _magnitudes;
    private readonly float[] _yinDiff;
    private readonly float[] _yinCmndf;
    private readonly int[] _bitReversal;
    private readonly float[] _cosTable;
    private readonly float[] _sinTable;

    // Snapshot buffers returned to UI
    private readonly float[] _waveformSnapshot;
    private readonly float[] _spectrumSnapshot;
    private readonly int _spectrumBins;

    private static readonly string[] NoteNames =
    [
        "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"
    ];

    public int BufferSize => _bufferSize;
    public int SpectrumBins => _spectrumBins;

    public GuitarDspAnalyzer(int bufferSize = 2048)
    {
        if ((bufferSize & (bufferSize - 1)) != 0)
        {
            throw new ArgumentException("Buffer size must be a power of 2.", nameof(bufferSize));
        }

        _bufferSize = bufferSize;
        _spectrumBins = bufferSize / 2;

        _hannWindow = new float[bufferSize];
        _fftReal = new float[bufferSize];
        _fftImag = new float[bufferSize];
        _magnitudes = new float[_spectrumBins];
        _waveformSnapshot = new float[bufferSize];
        _spectrumSnapshot = new float[_spectrumBins];

        int yinMaxTau = bufferSize / 2;
        _yinDiff = new float[yinMaxTau];
        _yinCmndf = new float[yinMaxTau];

        // Precompute Hann Window: 0.5 * (1 - cos(2*pi*n / (N-1)))
        for (int i = 0; i < bufferSize; i++)
        {
            _hannWindow[i] = 0.5f * (1.0f - MathF.Cos(2.0f * MathF.PI * i / (bufferSize - 1)));
        }

        // Precompute FFT Bit-reversal and Twiddle tables
        _bitReversal = new int[bufferSize];
        int bits = (int)MathF.Round(MathF.Log2(bufferSize));
        for (int i = 0; i < bufferSize; i++)
        {
            int reversed = 0;
            for (int b = 0; b < bits; b++)
            {
                if ((i & (1 << b)) != 0)
                {
                    reversed |= (1 << (bits - 1 - b));
                }
            }
            _bitReversal[i] = reversed;
        }

        int halfSize = bufferSize / 2;
        _cosTable = new float[halfSize];
        _sinTable = new float[halfSize];
        for (int i = 0; i < halfSize; i++)
        {
            double angle = -2.0 * Math.PI * i / bufferSize;
            _cosTable[i] = (float)Math.Cos(angle);
            _sinTable[i] = (float)Math.Sin(angle);
        }
    }

    /// <summary>
    /// Processes an input audio buffer and fills a SignalAnalysisResult telemetry object.
    /// Thread-safe if called on dedicated processing thread.
    /// </summary>
    public void Process(
        ReadOnlySpan<float> inputBuffer,
        int sampleRate,
        float inputGainLinear,
        float noiseGateThresholdDbfs,
        SignalAnalysisResult result)
    {
        int count = Math.Min(inputBuffer.Length, _bufferSize);
        if (count < _bufferSize)
        {
            return;
        }

        result.SampleRate = sampleRate;

        // 1. Calculate Peak and RMS (applying gain)
        float sumSquares = 0f;
        float peakLinear = 0f;

        for (int i = 0; i < _bufferSize; i++)
        {
            float s = inputBuffer[i] * inputGainLinear;
            float abs = MathF.Abs(s);
            if (abs > peakLinear) peakLinear = abs;
            sumSquares += s * s;
        }

        float rms = MathF.Sqrt(sumSquares / _bufferSize);
        float rmsDbfs = rms > 1e-6f ? 20.0f * MathF.Log10(rms) : -96.0f;
        float peakDbfs = peakLinear > 1e-6f ? 20.0f * MathF.Log10(peakLinear) : -96.0f;

        result.RmsDbfs = MathF.Max(-96.0f, MathF.Min(6.0f, rmsDbfs));
        result.PeakDbfs = MathF.Max(-96.0f, MathF.Min(6.0f, peakDbfs));
        result.PeakLinear = peakLinear;
        result.IsClipping = peakDbfs >= -0.5f;

        // 2. Noise Gate check
        bool gateOpen = rmsDbfs >= noiseGateThresholdDbfs;
        result.IsGateOpen = gateOpen;

        // 3. Prepare Oscilloscope waveform with Triggering (Zero-Crossing stabilization)
        ExtractTriggeredWaveform(inputBuffer, inputGainLinear, _waveformSnapshot);
        result.WaveformBuffer = _waveformSnapshot;

        if (!gateOpen)
        {
            // Signal below noise gate: clear pitch and spectrum to avoid jitter
            result.HasPitch = false;
            result.PitchHz = 0f;
            result.NoteName = "--";
            result.MidiNote = 0;
            result.CentDeviation = 0f;
            result.IsInTune = false;
            result.TuningHint = "Bramka szumów wyciszona";
            result.SpectralCentroidHz = 0f;
            result.TimbreClassification = "Cisza";
            result.HarmonicRatio = 0f;

            Array.Clear(_spectrumSnapshot, 0, _spectrumSnapshot.Length);
            result.SpectrumBuffer = _spectrumSnapshot;
            return;
        }

        // 4. Compute FFT for Spectrum, Centroid and Harmonics
        for (int i = 0; i < _bufferSize; i++)
        {
            float val = inputBuffer[i] * inputGainLinear * _hannWindow[i];
            _fftReal[i] = val;
            _fftImag[i] = 0f;
        }

        ComputeFft(_fftReal, _fftImag);

        // Calculate Magnitudes (in linear and dBFS)
        float binWidth = (float)sampleRate / _bufferSize;
        float normFactor = 2.0f / _bufferSize;

        for (int k = 0; k < _spectrumBins; k++)
        {
            float r = _fftReal[k];
            float im = _fftImag[k];
            float mag = MathF.Sqrt(r * r + im * im) * normFactor;
            _magnitudes[k] = mag;

            // Normalized dBFS scale for visualization (-96 dB to 0 dB mapped to 0.0 .. 1.0)
            float magDbfs = mag > 1e-5f ? 20.0f * MathF.Log10(mag) : -100.0f;
            float normalized = (magDbfs + 90.0f) / 90.0f; // -90 dB = 0.0, 0 dB = 1.0
            _spectrumSnapshot[k] = Math.Clamp(normalized, 0.0f, 1.0f);
        }
        result.SpectrumBuffer = _spectrumSnapshot;

        // 5. Pitch Detection via YIN Algorithm (Guitar band: 60 Hz to 1300 Hz)
        float detectedPitch = DetectPitchYin(inputBuffer, sampleRate, inputGainLinear);
        if (detectedPitch >= 60.0f && detectedPitch <= 1300.0f)
        {
            result.HasPitch = true;
            result.PitchHz = detectedPitch;
            CalculateNoteAndTuning(detectedPitch, result);
        }
        else
        {
            result.HasPitch = false;
            result.PitchHz = 0f;
            result.NoteName = "--";
            result.MidiNote = 0;
            result.CentDeviation = 0f;
            result.IsInTune = false;
            result.TuningHint = "Szum / Brak dźwięku";
        }

        // 6. Spectral Centroid Calculation
        float centroid = CalculateSpectralCentroid(_magnitudes, binWidth, _spectrumBins);
        result.SpectralCentroidHz = centroid;
        result.TimbreClassification = ClassifyTimbre(centroid);

        // 7. Harmonic Ratio Analysis
        if (result.HasPitch)
        {
            result.HarmonicRatio = CalculateHarmonicRatio(_magnitudes, binWidth, result.PitchHz, _spectrumBins);
        }
        else
        {
            result.HarmonicRatio = 0f;
        }
    }

    /// <summary>
    /// Radix-2 Cooley-Tukey In-Place FFT using precomputed bit-reversal and twiddle factors.
    /// </summary>
    private void ComputeFft(Span<float> real, Span<float> imag)
    {
        int n = _bufferSize;

        // Bit-reversal permutation
        for (int i = 0; i < n; i++)
        {
            int j = _bitReversal[i];
            if (j > i)
            {
                (real[i], real[j]) = (real[j], real[i]);
                (imag[i], imag[j]) = (imag[j], imag[i]);
            }
        }

        // Cooley-Tukey butterflies
        for (int len = 2; len <= n; len <<= 1)
        {
            int halfLen = len >> 1;
            int step = n / len;

            for (int i = 0; i < n; i += len)
            {
                int k = 0;
                for (int j = 0; j < halfLen; j++)
                {
                    float c = _cosTable[k];
                    float s = _sinTable[k];
                    k += step;

                    int uIdx = i + j;
                    int vIdx = i + j + halfLen;

                    float tr = real[vIdx] * c - imag[vIdx] * s;
                    float ti = real[vIdx] * s + imag[vIdx] * c;

                    real[vIdx] = real[uIdx] - tr;
                    imag[vIdx] = imag[uIdx] - ti;
                    real[uIdx] += tr;
                    imag[uIdx] += ti;
                }
            }
        }
    }

    /// <summary>
    /// Highly robust YIN pitch detection algorithm optimized for electric guitar (60 Hz to 1300 Hz).
    /// </summary>
    private float DetectPitchYin(ReadOnlySpan<float> input, int sampleRate, float gain)
    {
        int minTau = Math.Max(2, (int)(sampleRate / 1300.0f)); // ~1300 Hz max
        int maxTau = Math.Min(_bufferSize / 2 - 1, (int)(sampleRate / 60.0f)); // ~60 Hz min (Drop C / Low B)
        int window = _bufferSize / 2;

        if (maxTau >= window) maxTau = window - 1;

        // Step 1: Difference function d(tau)
        _yinDiff[0] = 0f;
        for (int tau = 1; tau <= maxTau; tau++)
        {
            float sum = 0f;
            for (int j = 0; j < window; j++)
            {
                float delta = (input[j] - input[j + tau]) * gain;
                sum += delta * delta;
            }
            _yinDiff[tau] = sum;
        }

        // Step 2: Cumulative Mean Normalized Difference Function (CMNDF)
        _yinCmndf[0] = 1.0f;
        float runningSum = 0f;
        for (int tau = 1; tau <= maxTau; tau++)
        {
            runningSum += _yinDiff[tau];
            _yinCmndf[tau] = runningSum > 1e-8f
                ? _yinDiff[tau] * tau / runningSum
                : 1.0f;
        }

        // Step 3: Absolute Thresholding & Local Minimum Search
        const float threshold = 0.15f;
        int bestTau = -1;

        for (int tau = minTau; tau <= maxTau; tau++)
        {
            if (_yinCmndf[tau] < threshold)
            {
                while (tau + 1 <= maxTau && _yinCmndf[tau + 1] < _yinCmndf[tau])
                {
                    tau++;
                }
                bestTau = tau;
                break;
            }
        }

        // Fallback: If no candidate below threshold, pick global minimum if distinct enough
        if (bestTau == -1)
        {
            float minVal = float.MaxValue;
            for (int tau = minTau; tau <= maxTau; tau++)
            {
                if (_yinCmndf[tau] < minVal)
                {
                    minVal = _yinCmndf[tau];
                    bestTau = tau;
                }
            }

            if (minVal > 0.35f)
            {
                return 0f; // No clear periodicity
            }
        }

        // Step 4: Parabolic Interpolation for sub-sample accuracy
        float refinedTau = bestTau;
        if (bestTau > minTau && bestTau < maxTau)
        {
            float s0 = _yinCmndf[bestTau - 1];
            float s1 = _yinCmndf[bestTau];
            float s2 = _yinCmndf[bestTau + 1];

            float denom = 2.0f * (2.0f * s1 - s0 - s2);
            if (MathF.Abs(denom) > 1e-6f)
            {
                float delta = (s2 - s0) / denom;
                refinedTau = bestTau + delta;
            }
        }

        if (refinedTau <= 0f) return 0f;
        return (float)sampleRate / refinedTau;
    }

    /// <summary>
    /// Calculates musical note, octave, and tuning deviation in cents from frequency.
    /// </summary>
    private static void CalculateNoteAndTuning(float frequencyHz, SignalAnalysisResult result)
    {
        // MIDI note number: 69 + 12 * log2(f / 440)
        double midiFloat = 69.0 + 12.0 * Math.Log2(frequencyHz / 440.0);
        int midiInt = (int)Math.Round(midiFloat);

        int noteIndex = (midiInt % 12 + 12) % 12;
        int octave = (midiInt / 12) - 1;

        string noteName = $"{NoteNames[noteIndex]}{octave}";
        double targetFreq = 440.0 * Math.Pow(2.0, (midiInt - 69.0) / 12.0);
        float cents = (float)(1200.0 * Math.Log2(frequencyHz / targetFreq));

        result.NoteName = noteName;
        result.MidiNote = midiInt;
        result.CentDeviation = Math.Clamp(cents, -50.0f, 50.0f);
        result.IsInTune = MathF.Abs(cents) <= 3.0f;

        // Guitar string hint
        result.TuningHint = midiInt switch
        {
            38 => "Drop D (D2 ~73.4 Hz)",
            40 => "Struna 6: E2 (82.4 Hz)",
            45 => "Struna 5: A2 (110.0 Hz)",
            50 => "Struna 4: D3 (146.8 Hz)",
            55 => "Struna 3: G3 (196.0 Hz)",
            59 => "Struna 2: B3 (246.9 Hz)",
            64 => "Struna 1: E4 (329.6 Hz)",
            _ => $"{frequencyHz:F1} Hz"
        };
    }

    /// <summary>
    /// Calculates Spectral Centroid (Center of Spectral Mass) in Hz.
    /// </summary>
    private static float CalculateSpectralCentroid(ReadOnlySpan<float> magnitudes, float binWidth, int numBins)
    {
        float weightedSum = 0f;
        float totalMagnitude = 0f;

        // Skip DC bin (k=0)
        for (int k = 1; k < numBins; k++)
        {
            float freq = k * binWidth;
            float mag = magnitudes[k];
            weightedSum += freq * mag;
            totalMagnitude += mag;
        }

        if (totalMagnitude <= 1e-7f) return 0f;
        return weightedSum / totalMagnitude;
    }

    /// <summary>
    /// Classifies guitar timbre based on spectral centroid frequency.
    /// </summary>
    private static string ClassifyTimbre(float centroidHz)
    {
        return centroidHz switch
        {
            <= 0f => "Cisza",
            < 1200.0f => "Ciemna / Ciepła",
            <= 2800.0f => "Zrównoważona",
            _ => "Jasna / Ostra"
        };
    }

    /// <summary>
    /// Calculates the Harmonic Ratio: ratio of harmonic energy (2f0, 3f0, ...) to total harmonic + fundamental energy.
    /// Indicates saturation / distortion / richness of the guitar pickup tone.
    /// </summary>
    private static float CalculateHarmonicRatio(
        ReadOnlySpan<float> magnitudes,
        float binWidth,
        float fundamentalHz,
        int numBins)
    {
        if (fundamentalHz <= 0f || binWidth <= 0f) return 0f;

        float f0Bin = fundamentalHz / binWidth;
        int centerBin = (int)MathF.Round(f0Bin);
        if (centerBin <= 0 || centerBin >= numBins) return 0f;

        // Energy of fundamental (taking 3 bins around peak)
        float fundamentalEnergy = 0f;
        for (int b = Math.Max(0, centerBin - 1); b <= Math.Min(numBins - 1, centerBin + 1); b++)
        {
            fundamentalEnergy += magnitudes[b] * magnitudes[b];
        }

        // Energy of harmonics (2f0, 3f0, 4f0, ... up to 12 harmonics)
        float harmonicEnergy = 0f;
        for (int h = 2; h <= 12; h++)
        {
            float harmFreq = fundamentalHz * h;
            int harmBin = (int)MathF.Round(harmFreq / binWidth);
            if (harmBin >= numBins - 1) break;

            for (int b = Math.Max(0, harmBin - 1); b <= Math.Min(numBins - 1, harmBin + 1); b++)
            {
                harmonicEnergy += magnitudes[b] * magnitudes[b];
            }
        }

        float totalEnergy = fundamentalEnergy + harmonicEnergy;
        if (totalEnergy <= 1e-8f) return 0f;

        // Ratio from 0.0 (pure tone) to 1.0 (heavily saturated)
        return Math.Clamp(harmonicEnergy / totalEnergy, 0.0f, 1.0f);
    }

    /// <summary>
    /// Performs zero-crossing search to stabilize the oscilloscope view.
    /// </summary>
    private static void ExtractTriggeredWaveform(
        ReadOnlySpan<float> input,
        float gain,
        Span<float> destination)
    {
        int length = destination.Length;
        int searchLimit = Math.Min(input.Length - length, 300);

        int triggerIndex = 0;
        // Search for positive slope zero-crossing
        for (int i = 1; i < searchLimit; i++)
        {
            if (input[i - 1] <= 0f && input[i] > 0f)
            {
                triggerIndex = i;
                break;
            }
        }

        int copyCount = Math.Min(length, input.Length - triggerIndex);
        for (int i = 0; i < copyCount; i++)
        {
            destination[i] = input[triggerIndex + i] * gain;
        }

        // Fill remainder if any
        for (int i = copyCount; i < length; i++)
        {
            destination[i] = 0f;
        }
    }
}
