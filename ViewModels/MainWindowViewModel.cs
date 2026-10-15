using System;
using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GuitarSignalVisualiser.Audio;
using GuitarSignalVisualiser.Controls;
using GuitarSignalVisualiser.Models;

namespace GuitarSignalVisualiser.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly AudioCaptureService _audioService;
    private readonly DispatcherTimer _uiUpdateTimer;
    private readonly SignalAnalysisResult _snapshotBuffer = new();

    // Clip hold timer & peak decay
    private int _clipHoldCounter = 0;
    private double _peakHoldPercent = 0.0;

    // Collections
    public ObservableCollection<AudioDeviceInfo> Devices { get; } = [];
    public ObservableCollection<string> Channels { get; } =
    [
        "Kanał 1 / Lewy (Hi-Z Gitara)",
        "Kanał 2 / Prawy",
        "Miks Stereo (L + R)"
    ];

    [ObservableProperty]
    private AudioDeviceInfo? _selectedDevice;

    [ObservableProperty]
    private int _selectedChannelIndex = 0;

    [ObservableProperty]
    private bool _isCapturing;

    [ObservableProperty]
    private string _statusText = "Gotowy. Wybierz wejście i kliknij START.";

    [ObservableProperty]
    private string _startStopButtonText = "▶ START ANALIZY";

    // Controls
    [ObservableProperty]
    private float _inputGainDb = 0f;

    [ObservableProperty]
    private string _inputGainLabel = "+0.0 dB (1.0x)";

    [ObservableProperty]
    private float _noiseGateThresholdDbfs = -50f;

    [ObservableProperty]
    private string _noiseGateLabel = "-50.0 dBFS";

    [ObservableProperty]
    private VisualizerDisplayMode _displayMode = VisualizerDisplayMode.Combined;

    // Visualizer Buffers
    [ObservableProperty]
    private float[] _waveformData = [];

    [ObservableProperty]
    private float[] _spectrumData = [];

    [ObservableProperty]
    private int _sampleRate = 48000;

    [ObservableProperty]
    private bool _isClipping;

    // Card 1: Pitch & Tuning
    [ObservableProperty]
    private string _noteName = "--";

    [ObservableProperty]
    private string _frequencyText = "-- Hz";

    [ObservableProperty]
    private float _centDeviation = 0f;

    [ObservableProperty]
    private string _centDeviationText = "0 ct";

    [ObservableProperty]
    private double _centMeterPercent = 50.0; // 0% = -50ct, 50% = 0ct, 100% = +50ct

    [ObservableProperty]
    private bool _isInTune;

    [ObservableProperty]
    private bool _isFlat;

    [ObservableProperty]
    private bool _isSharp;

    [ObservableProperty]
    private string _tuningStatusText = "Oczekiwanie na sygnał";

    [ObservableProperty]
    private bool _hasPitch;

    [ObservableProperty]
    private string _tuningHint = "Oczekiwanie na sygnał...";

    // Card 2: Amplitude & Dynamics
    [ObservableProperty]
    private float _rmsDbfs = -96f;

    [ObservableProperty]
    private string _rmsText = "-96.0 dBFS";

    [ObservableProperty]
    private float _peakDbfs = -96f;

    [ObservableProperty]
    private string _peakText = "-96.0 dBFS";

    [ObservableProperty]
    private double _vuMeterPercent = 0.0;

    [ObservableProperty]
    private double _peakHoldBarPercent = 0.0;

    [ObservableProperty]
    private bool _isClippingIndicatorActive;

    [ObservableProperty]
    private bool _isGateOpen;

    // Card 3: Timbre & Harmonics
    [ObservableProperty]
    private float _spectralCentroidHz = 0f;

    [ObservableProperty]
    private string _centroidText = "-- Hz";

    [ObservableProperty]
    private string _timbreClassification = "Cisza";

    [ObservableProperty]
    private double _toneBrightnessPercent = 0.0; // 0 to 100%

    [ObservableProperty]
    private float _harmonicRatio = 0f;

    [ObservableProperty]
    private string _harmonicRatioText = "0.0%";

    [ObservableProperty]
    private double _harmonicPercent = 0.0; // 0 to 100%

    public MainWindowViewModel()
    {
        _audioService = new AudioCaptureService();

        // Refresh devices list
        RefreshDevices();

        // 60 FPS Telemetry Polling Timer to synchronize UI without Dispatcher flooding
        _uiUpdateTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _uiUpdateTimer.Tick += OnUiUpdateTick;
        _uiUpdateTimer.Start();
    }

    [RelayCommand]
    public void RefreshDevices()
    {
        Devices.Clear();
        var list = AudioCaptureService.GetInputDevices();
        foreach (var dev in list)
        {
            Devices.Add(dev);
        }

        if (Devices.Count > 0)
        {
            // Prefer the demo generator or first real device
            SelectedDevice = Devices[0];
        }
    }

    [RelayCommand]
    public void ToggleCapture()
    {
        if (IsCapturing)
        {
            StopCapture();
        }
        else
        {
            StartCapture();
        }
    }

    private void StartCapture()
    {
        if (SelectedDevice == null) return;

        try
        {
            var channel = SelectedChannelIndex switch
            {
                1 => AudioChannelSelection.MonoInput2,
                2 => AudioChannelSelection.StereoMix,
                _ => AudioChannelSelection.MonoInput1
            };

            _audioService.SetGain(InputGainDb);
            _audioService.SetNoiseGateThreshold(NoiseGateThresholdDbfs);
            _audioService.Start(SelectedDevice.DeviceNumber, channel, 48000);

            IsCapturing = true;
            StartStopButtonText = "⏹ STOP ANALIZY";
            StatusText = SelectedDevice.DeviceNumber == -1
                ? "🎸 Wirtualna gitara aktywna (Syntetyczny plucking E-A-D-G-B-E)"
                : $"🔴 Przechwytywanie na żywo: {SelectedDevice.Name}";
        }
        catch (Exception ex)
        {
            StatusText = $"Błąd uruchomienia audio: {ex.Message}";
            IsCapturing = false;
            StartStopButtonText = "▶ START ANALIZY";
        }
    }

    private void StopCapture()
    {
        _audioService.Stop();
        IsCapturing = false;
        StartStopButtonText = "▶ START ANALIZY";
        StatusText = "Zatrzymano analizę sygnału.";

        // Clear live visual readings
        NoteName = "--";
        FrequencyText = "-- Hz";
        CentDeviation = 0f;
        CentDeviationText = "0 ct";
        IsInTune = false;
        HasPitch = false;
        TuningHint = "Zatrzymano.";
        RmsDbfs = -96f;
        RmsText = "-96.0 dBFS";
        PeakDbfs = -96f;
        PeakText = "-96.0 dBFS";
        VuMeterPercent = 0.0;
        PeakHoldBarPercent = 0.0;
        IsClipping = false;
        IsClippingIndicatorActive = false;
        SpectralCentroidHz = 0f;
        CentroidText = "-- Hz";
        TimbreClassification = "Cisza";
        ToneBrightnessPercent = 0.0;
        HarmonicRatio = 0f;
        HarmonicRatioText = "0.0%";
        HarmonicPercent = 0.0;
    }

    partial void OnInputGainDbChanged(float value)
    {
        float linear = MathF.Pow(10.0f, value / 20.0f);
        InputGainLabel = $"{value:+0.0;-0.0;0.0} dB ({linear:F1}x)";
        _audioService.SetGain(value);
    }

    partial void OnNoiseGateThresholdDbfsChanged(float value)
    {
        NoiseGateLabel = $"{value:F1} dBFS";
        _audioService.SetNoiseGateThreshold(value);
    }

    partial void OnSelectedChannelIndexChanged(int value)
    {
        var channel = value switch
        {
            1 => AudioChannelSelection.MonoInput2,
            2 => AudioChannelSelection.StereoMix,
            _ => AudioChannelSelection.MonoInput1
        };
        _audioService.SetChannelSelection(channel);
    }

    [RelayCommand]
    public void SetDisplayMode(string mode)
    {
        DisplayMode = mode switch
        {
            "Waveform" => VisualizerDisplayMode.WaveformOnly,
            "Spectrum" => VisualizerDisplayMode.SpectrumOnly,
            _ => VisualizerDisplayMode.Combined
        };
    }

    private void OnUiUpdateTick(object? sender, EventArgs e)
    {
        if (!IsCapturing) return;

        // Copy telemetry from audio service thread-safely
        _audioService.CopyCurrentSnapshot(_snapshotBuffer);

        SampleRate = _snapshotBuffer.SampleRate;
        WaveformData = _snapshotBuffer.WaveformBuffer;
        SpectrumData = _snapshotBuffer.SpectrumBuffer;
        IsClipping = _snapshotBuffer.IsClipping;
        IsGateOpen = _snapshotBuffer.IsGateOpen;

        // Clip indicator LED with hold
        if (_snapshotBuffer.IsClipping)
        {
            _clipHoldCounter = 30; // ~0.5s hold
            IsClippingIndicatorActive = true;
        }
        else if (_clipHoldCounter > 0)
        {
            _clipHoldCounter--;
            IsClippingIndicatorActive = true;
        }
        else
        {
            IsClippingIndicatorActive = false;
        }

        // 1. Pitch & Tuning
        HasPitch = _snapshotBuffer.HasPitch;
        if (_snapshotBuffer.HasPitch)
        {
            NoteName = _snapshotBuffer.NoteName;
            FrequencyText = $"{_snapshotBuffer.PitchHz:F1} Hz";
            CentDeviation = _snapshotBuffer.CentDeviation;
            CentDeviationText = $"{_snapshotBuffer.CentDeviation:+0.0;-0.0;0.0} ct";
            CentMeterPercent = Math.Clamp((_snapshotBuffer.CentDeviation + 50.0) / 100.0 * 100.0, 0.0, 100.0);
            IsInTune = _snapshotBuffer.IsInTune;
            IsFlat = _snapshotBuffer.CentDeviation < -3.0f;
            IsSharp = _snapshotBuffer.CentDeviation > 3.0f;
            TuningStatusText = IsInTune ? "● W STROJU (IN TUNE)" : (IsFlat ? "▼ ZA NISKO (FLAT)" : "▲ ZA WYSOKO (SHARP)");
            TuningHint = _snapshotBuffer.TuningHint;
        }
        else
        {
            NoteName = "--";
            FrequencyText = "-- Hz";
            CentDeviation = 0f;
            CentDeviationText = "0 ct";
            CentMeterPercent = 50.0;
            IsInTune = false;
            IsFlat = false;
            IsSharp = false;
            TuningStatusText = _snapshotBuffer.IsGateOpen ? "Szum / Brak tonu" : "Bramka szumów wyciszona";
            TuningHint = _snapshotBuffer.IsGateOpen ? "Graj strunę gitary" : "Sygnał poniżej progu bramki";
        }

        // 2. Amplitude & Dynamics
        RmsDbfs = _snapshotBuffer.RmsDbfs;
        RmsText = $"{_snapshotBuffer.RmsDbfs:F1} dBFS";
        PeakDbfs = _snapshotBuffer.PeakDbfs;
        PeakText = $"{_snapshotBuffer.PeakDbfs:F1} dBFS";

        // Map RMS (-60 dBFS to 0 dBFS) to (0% to 100%)
        double vu = Math.Clamp((_snapshotBuffer.RmsDbfs + 60.0) / 60.0 * 100.0, 0.0, 100.0);
        VuMeterPercent = vu;

        // Peak Hold needle with smooth gravity falloff
        if (vu > _peakHoldPercent)
        {
            _peakHoldPercent = vu;
        }
        else
        {
            _peakHoldPercent = Math.Max(0.0, _peakHoldPercent - 0.7);
        }
        PeakHoldBarPercent = _peakHoldPercent;

        // 3. Timbre & Harmonics
        SpectralCentroidHz = _snapshotBuffer.SpectralCentroidHz;
        if (_snapshotBuffer.SpectralCentroidHz > 50f)
        {
            CentroidText = $"{_snapshotBuffer.SpectralCentroidHz:F0} Hz";
            TimbreClassification = _snapshotBuffer.TimbreClassification;

            // Map centroid 400 Hz .. 4000 Hz to 0% .. 100%
            double bright = Math.Clamp((_snapshotBuffer.SpectralCentroidHz - 400.0) / 3600.0 * 100.0, 0.0, 100.0);
            ToneBrightnessPercent = bright;
        }
        else
        {
            CentroidText = "-- Hz";
            TimbreClassification = "Cisza";
            ToneBrightnessPercent = 0.0;
        }

        HarmonicRatio = _snapshotBuffer.HarmonicRatio;
        HarmonicPercent = Math.Clamp(_snapshotBuffer.HarmonicRatio * 100.0, 0.0, 100.0);
        HarmonicRatioText = $"{HarmonicPercent:F1}%";
    }
}
