using System;
using System.Collections.Generic;
using System.Threading;
using NAudio.Wave;
using GuitarSignalVisualiser.Models;

namespace GuitarSignalVisualiser.Audio;

/// <summary>
/// Audio capture and streaming service utilizing NAudio and a lock-free RingBuffer.
/// Executes DSP processing on a dedicated background thread to guarantee zero UI stutter.
/// </summary>
public sealed class AudioCaptureService : IDisposable
{
    private readonly GuitarDspAnalyzer _dspAnalyzer;
    private readonly RingBuffer _ringBuffer;
    private readonly float[] _dspInputBuffer;
    private readonly SignalAnalysisResult _workingResult;
    private readonly SignalAnalysisResult _publishedResult;
    private readonly object _publishLock = new();

    private WaveInEvent? _waveIn;
    private Thread? _dspThread;
    private CancellationTokenSource? _cts;
    private bool _isDisposed;

    // Configuration
    private int _sampleRate = 48000;
    private AudioChannelSelection _channelSelection = AudioChannelSelection.MonoInput1;
    private float _gainLinear = 1.0f;
    private float _noiseGateThresholdDbfs = -50.0f;
    private bool _isDemoMode = false;
    private bool _isRunning = false;

    // Demo guitar string synthesizer state
    private double _demoPhase = 0;
    private double _demoFreq = 82.4; // E2 default
    private double _demoTimer = 0;
    private double _demoDecay = 1.0;
    private readonly double[] _demoNotes = [82.41, 110.0, 146.83, 196.0, 246.94, 329.63]; // Standard E A D G B E
    private int _demoNoteIndex = 0;

    public const int BufferSize = 2048;

    public bool IsRunning => _isRunning;
    public bool IsDemoMode => _isDemoMode;
    public int SampleRate => _sampleRate;

    public event Action<SignalAnalysisResult>? AnalysisUpdated;

    public AudioCaptureService()
    {
        _dspAnalyzer = new GuitarDspAnalyzer(BufferSize);
        _ringBuffer = new RingBuffer(32768);
        _dspInputBuffer = new float[BufferSize];
        _workingResult = new SignalAnalysisResult();
        _publishedResult = new SignalAnalysisResult();
    }

    /// <summary>
    /// Enumerates all available audio input recording devices on the system.
    /// Also appends the synthetic Guitar Demo generator.
    /// </summary>
    public static List<AudioDeviceInfo> GetInputDevices()
    {
        var devices = new List<AudioDeviceInfo>();

        // Synthetic guitar generator for immediate testing without external hardware
        devices.Add(new AudioDeviceInfo
        {
            DeviceNumber = -1,
            Name = "🎸 Demo Generator (Wirtualna Gitara E-A-D-G-B-E)",
            Channels = 1,
            IsDefault = false
        });

        int deviceCount = WaveInEvent.DeviceCount;
        for (int i = 0; i < deviceCount; i++)
        {
            try
            {
                var caps = WaveInEvent.GetCapabilities(i);
                devices.Add(new AudioDeviceInfo
                {
                    DeviceNumber = i,
                    Name = string.IsNullOrWhiteSpace(caps.ProductName) ? $"Wejście Audio {i + 1}" : caps.ProductName,
                    Channels = caps.Channels,
                    IsDefault = i == 0
                });
            }
            catch
            {
                // Ignore device enumeration errors for disconnected drivers
            }
        }

        return devices;
    }

    public void SetGain(float gainDb)
    {
        _gainLinear = MathF.Pow(10.0f, gainDb / 20.0f);
    }

    public void SetNoiseGateThreshold(float thresholdDbfs)
    {
        _noiseGateThresholdDbfs = thresholdDbfs;
    }

    public void SetChannelSelection(AudioChannelSelection channel)
    {
        _channelSelection = channel;
    }

    public void Start(int deviceNumber, AudioChannelSelection channel, int sampleRate = 48000)
    {
        Stop();

        _sampleRate = sampleRate;
        _channelSelection = channel;
        _ringBuffer.Clear();

        _cts = new CancellationTokenSource();

        if (deviceNumber == -1)
        {
            // Synthetic Demo Mode
            _isDemoMode = true;
            _isRunning = true;
            _dspThread = new Thread(DemoSynthesizerLoop)
            {
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal,
                Name = "GuitarDemoSynthThread"
            };
            _dspThread.Start();
        }
        else
        {
            _isDemoMode = false;
            try
            {
                _waveIn = new WaveInEvent
                {
                    DeviceNumber = deviceNumber,
                    WaveFormat = new WaveFormat(_sampleRate, 16, 2), // Default 16-bit stereo for USB interfaces
                    BufferMilliseconds = 20,
                    NumberOfBuffers = 3
                };

                _waveIn.DataAvailable += OnAudioDataAvailable;
                _waveIn.RecordingStopped += (s, e) =>
                {
                    _isRunning = false;
                };

                _waveIn.StartRecording();
                _isRunning = true;

                _dspThread = new Thread(DspProcessingLoop)
                {
                    IsBackground = true,
                    Priority = ThreadPriority.AboveNormal,
                    Name = "GuitarDspProcessingThread"
                };
                _dspThread.Start();
            }
            catch (Exception)
            {
                // Fallback attempt to 44.1kHz mono if 48kHz stereo was rejected by driver
                try
                {
                    _waveIn?.Dispose();
                    _sampleRate = 44100;
                    _waveIn = new WaveInEvent
                    {
                        DeviceNumber = deviceNumber,
                        WaveFormat = new WaveFormat(44100, 16, 1),
                        BufferMilliseconds = 25,
                        NumberOfBuffers = 3
                    };
                    _waveIn.DataAvailable += OnAudioDataAvailable;
                    _waveIn.StartRecording();
                    _isRunning = true;

                    _dspThread = new Thread(DspProcessingLoop)
                    {
                        IsBackground = true,
                        Priority = ThreadPriority.AboveNormal,
                        Name = "GuitarDspProcessingThread"
                    };
                    _dspThread.Start();
                }
                catch
                {
                    _isRunning = false;
                    throw;
                }
            }
        }
    }

    public void Stop()
    {
        _isRunning = false;
        _cts?.Cancel();

        if (_waveIn != null)
        {
            try
            {
                _waveIn.StopRecording();
                _waveIn.DataAvailable -= OnAudioDataAvailable;
                _waveIn.Dispose();
            }
            catch
            {
                // Suppress driver shutdown quirks
            }
            _waveIn = null;
        }

        if (_dspThread != null && _dspThread.IsAlive)
        {
            _dspThread.Join(200);
            _dspThread = null;
        }

        _cts?.Dispose();
        _cts = null;
    }

    private void OnAudioDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (!_isRunning || e.BytesRecorded <= 0) return;

        int channels = _waveIn?.WaveFormat.Channels ?? 1;
        int bitsPerSample = _waveIn?.WaveFormat.BitsPerSample ?? 16;
        int bytesPerSample = bitsPerSample / 8;
        int frameBytes = channels * bytesPerSample;
        int frameCount = e.BytesRecorded / frameBytes;

        Span<float> tempSamples = stackalloc float[Math.Min(frameCount, 1024)];
        int sampleIdx = 0;

        ReadOnlySpan<byte> rawData = e.Buffer.AsSpan(0, e.BytesRecorded);

        for (int i = 0; i < frameCount; i++)
        {
            int baseByte = i * frameBytes;
            float leftSample = 0f;
            float rightSample = 0f;

            if (bitsPerSample == 16)
            {
                short lShort = (short)(rawData[baseByte] | (rawData[baseByte + 1] << 8));
                leftSample = lShort / 32768.0f;

                if (channels >= 2)
                {
                    short rShort = (short)(rawData[baseByte + 2] | (rawData[baseByte + 3] << 8));
                    rightSample = rShort / 32768.0f;
                }
                else
                {
                    rightSample = leftSample;
                }
            }
            else if (bitsPerSample == 32)
            {
                leftSample = BitConverter.ToSingle(rawData.Slice(baseByte, 4));
                rightSample = channels >= 2
                    ? BitConverter.ToSingle(rawData.Slice(baseByte + 4, 4))
                    : leftSample;
            }

            float finalSample = _channelSelection switch
            {
                AudioChannelSelection.MonoInput2 => rightSample,
                AudioChannelSelection.StereoMix => (leftSample + rightSample) * 0.5f,
                _ => leftSample
            };

            tempSamples[sampleIdx++] = finalSample;

            if (sampleIdx >= tempSamples.Length)
            {
                _ringBuffer.Write(tempSamples[..sampleIdx]);
                sampleIdx = 0;
            }
        }

        if (sampleIdx > 0)
        {
            _ringBuffer.Write(tempSamples[..sampleIdx]);
        }
    }

    /// <summary>
    /// DSP loop that continuously reads from the ring buffer and processes chunks.
    /// </summary>
    private void DspProcessingLoop()
    {
        var token = _cts?.Token ?? CancellationToken.None;
        const int hopSize = 512; // Overlapping analysis for responsive ~90 Hz updates

        while (!token.IsCancellationRequested && _isRunning)
        {
            if (_ringBuffer.AvailableToRead >= BufferSize)
            {
                int read = _ringBuffer.Peek(_dspInputBuffer);
                if (read == BufferSize)
                {
                    _dspAnalyzer.Process(
                        _dspInputBuffer,
                        _sampleRate,
                        _gainLinear,
                        _noiseGateThresholdDbfs,
                        _workingResult);

                    PublishResult();
                    _ringBuffer.Advance(hopSize);
                }
            }
            else
            {
                Thread.Sleep(5);
            }
        }
    }

    /// <summary>
    /// Realistic synthetic guitar string model plucking standard guitar strings E2, A2, D3, G3, B3, E4.
    /// Provides an immediate out-of-the-box working visual demonstration.
    /// </summary>
    private void DemoSynthesizerLoop()
    {
        var token = _cts?.Token ?? CancellationToken.None;
        float[] demoBlock = new float[BufferSize];
        double dt = 1.0 / _sampleRate;

        while (!token.IsCancellationRequested && _isRunning)
        {
            _demoTimer += (BufferSize * dt);
            if (_demoTimer > 1.8) // Pluck next string every 1.8 seconds
            {
                _demoTimer = 0;
                _demoNoteIndex = (_demoNoteIndex + 1) % _demoNotes.Length;
                _demoFreq = _demoNotes[_demoNoteIndex];
                _demoDecay = 1.0;
            }

            // Generate physical-style plucked string waveform:
            // Fundamental + 2nd, 3rd, 4th, 5th harmonics with exponential decay and slight pick attack
            for (int i = 0; i < BufferSize; i++)
            {
                _demoDecay = Math.Max(0.005, _demoDecay * 0.99992);
                _demoPhase += 2.0 * Math.PI * _demoFreq * dt;
                if (_demoPhase > 2.0 * Math.PI) _demoPhase -= 2.0 * Math.PI;

                double fund = Math.Sin(_demoPhase);
                double h2 = 0.65 * Math.Sin(2.0 * _demoPhase);
                double h3 = 0.40 * Math.Sin(3.0 * _demoPhase);
                double h4 = 0.25 * Math.Sin(4.0 * _demoPhase);
                double h5 = 0.15 * Math.Sin(5.0 * _demoPhase);
                double h6 = 0.08 * Math.Sin(6.0 * _demoPhase);

                // Add slight soft-clipping tube warmth
                double raw = (fund + h2 + h3 + h4 + h5 + h6) * 0.5 * _demoDecay;
                double saturated = Math.Tanh(raw * 1.4);

                demoBlock[i] = (float)saturated;
            }

            _dspAnalyzer.Process(
                demoBlock,
                _sampleRate,
                _gainLinear,
                _noiseGateThresholdDbfs,
                _workingResult);

            PublishResult();
            Thread.Sleep(20); // ~50 fps update
        }
    }

    private void PublishResult()
    {
        lock (_publishLock)
        {
            _publishedResult.RmsDbfs = _workingResult.RmsDbfs;
            _publishedResult.PeakDbfs = _workingResult.PeakDbfs;
            _publishedResult.PeakLinear = _workingResult.PeakLinear;
            _publishedResult.IsClipping = _workingResult.IsClipping;
            _publishedResult.IsGateOpen = _workingResult.IsGateOpen;
            _publishedResult.HasPitch = _workingResult.HasPitch;
            _publishedResult.PitchHz = _workingResult.PitchHz;
            _publishedResult.NoteName = _workingResult.NoteName;
            _publishedResult.MidiNote = _workingResult.MidiNote;
            _publishedResult.CentDeviation = _workingResult.CentDeviation;
            _publishedResult.IsInTune = _workingResult.IsInTune;
            _publishedResult.TuningHint = _workingResult.TuningHint;
            _publishedResult.SpectralCentroidHz = _workingResult.SpectralCentroidHz;
            _publishedResult.TimbreClassification = _workingResult.TimbreClassification;
            _publishedResult.HarmonicRatio = _workingResult.HarmonicRatio;
            _publishedResult.SampleRate = _workingResult.SampleRate;

            if (_publishedResult.WaveformBuffer.Length != _workingResult.WaveformBuffer.Length)
            {
                _publishedResult.WaveformBuffer = new float[_workingResult.WaveformBuffer.Length];
            }
            Array.Copy(_workingResult.WaveformBuffer, _publishedResult.WaveformBuffer, _workingResult.WaveformBuffer.Length);

            if (_publishedResult.SpectrumBuffer.Length != _workingResult.SpectrumBuffer.Length)
            {
                _publishedResult.SpectrumBuffer = new float[_workingResult.SpectrumBuffer.Length];
            }
            Array.Copy(_workingResult.SpectrumBuffer, _publishedResult.SpectrumBuffer, _workingResult.SpectrumBuffer.Length);
        }

        AnalysisUpdated?.Invoke(_publishedResult);
    }

    /// <summary>
    /// Thread-safely copies current telemetry snapshot to a destination instance.
    /// </summary>
    public void CopyCurrentSnapshot(SignalAnalysisResult destination)
    {
        lock (_publishLock)
        {
            destination.RmsDbfs = _publishedResult.RmsDbfs;
            destination.PeakDbfs = _publishedResult.PeakDbfs;
            destination.PeakLinear = _publishedResult.PeakLinear;
            destination.IsClipping = _publishedResult.IsClipping;
            destination.IsGateOpen = _publishedResult.IsGateOpen;
            destination.HasPitch = _publishedResult.HasPitch;
            destination.PitchHz = _publishedResult.PitchHz;
            destination.NoteName = _publishedResult.NoteName;
            destination.MidiNote = _publishedResult.MidiNote;
            destination.CentDeviation = _publishedResult.CentDeviation;
            destination.IsInTune = _publishedResult.IsInTune;
            destination.TuningHint = _publishedResult.TuningHint;
            destination.SpectralCentroidHz = _publishedResult.SpectralCentroidHz;
            destination.TimbreClassification = _publishedResult.TimbreClassification;
            destination.HarmonicRatio = _publishedResult.HarmonicRatio;
            destination.SampleRate = _publishedResult.SampleRate;

            if (destination.WaveformBuffer.Length != _publishedResult.WaveformBuffer.Length)
            {
                destination.WaveformBuffer = new float[_publishedResult.WaveformBuffer.Length];
            }
            Array.Copy(_publishedResult.WaveformBuffer, destination.WaveformBuffer, _publishedResult.WaveformBuffer.Length);

            if (destination.SpectrumBuffer.Length != _publishedResult.SpectrumBuffer.Length)
            {
                destination.SpectrumBuffer = new float[_publishedResult.SpectrumBuffer.Length];
            }
            Array.Copy(_publishedResult.SpectrumBuffer, destination.SpectrumBuffer, _publishedResult.SpectrumBuffer.Length);
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        Stop();
    }
}
