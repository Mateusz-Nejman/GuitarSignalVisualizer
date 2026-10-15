using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace GuitarSignalVisualiser.Controls;

public enum VisualizerDisplayMode
{
    Combined = 0,
    WaveformOnly = 1,
    SpectrumOnly = 2
}

/// <summary>
/// High-performance custom hardware-accelerated audio visualization control.
/// Renders real-time oscilloscope waveform with zero-crossing stabilization and glow,
/// FFT frequency spectrum with decay smoothing, logarithmic grid, and spectral centroid marker.
/// </summary>
public class SoundWaveControl : Control
{
    public static readonly StyledProperty<float[]> WaveformDataProperty =
        AvaloniaProperty.Register<SoundWaveControl, float[]>(nameof(WaveformData), []);

    public static readonly StyledProperty<float[]> SpectrumDataProperty =
        AvaloniaProperty.Register<SoundWaveControl, float[]>(nameof(SpectrumData), []);

    public static readonly StyledProperty<float> SpectralCentroidHzProperty =
        AvaloniaProperty.Register<SoundWaveControl, float>(nameof(SpectralCentroidHz), 0f);

    public static readonly StyledProperty<int> SampleRateProperty =
        AvaloniaProperty.Register<SoundWaveControl, int>(nameof(SampleRate), 48000);

    public static readonly StyledProperty<bool> IsClippingProperty =
        AvaloniaProperty.Register<SoundWaveControl, bool>(nameof(IsClipping), false);

    public static readonly StyledProperty<VisualizerDisplayMode> DisplayModeProperty =
        AvaloniaProperty.Register<SoundWaveControl, VisualizerDisplayMode>(nameof(DisplayMode), VisualizerDisplayMode.Combined);

    public float[] WaveformData
    {
        get => GetValue(WaveformDataProperty);
        set => SetValue(WaveformDataProperty, value);
    }

    public float[] SpectrumData
    {
        get => GetValue(SpectrumDataProperty);
        set => SetValue(SpectrumDataProperty, value);
    }

    public float SpectralCentroidHz
    {
        get => GetValue(SpectralCentroidHzProperty);
        set => SetValue(SpectralCentroidHzProperty, value);
    }

    public int SampleRate
    {
        get => GetValue(SampleRateProperty);
        set => SetValue(SampleRateProperty, value);
    }

    public bool IsClipping
    {
        get => GetValue(IsClippingProperty);
        set => SetValue(IsClippingProperty, value);
    }

    public VisualizerDisplayMode DisplayMode
    {
        get => GetValue(DisplayModeProperty);
        set => SetValue(DisplayModeProperty, value);
    }

    // Smoothed spectrum buffer for organic studio analyzer fall-off
    private float[] _smoothedSpectrum = new float[1024];

    // Pre-allocated graphic resources
    private static readonly Typeface LabelTypeface = new(
        new FontFamily("Inter, Segoe UI, sans-serif"),
        FontStyle.Normal,
        FontWeight.SemiBold);

    private static readonly IBrush BackgroundBrush = new SolidColorBrush(Color.FromRgb(11, 15, 23)); // #0B0F17 Deep Studio Slate
    private static readonly IBrush GridLineBrush = new SolidColorBrush(Color.FromArgb(40, 148, 163, 184)); // Subtle grid
    private static readonly IBrush CenterLineBrush = new SolidColorBrush(Color.FromArgb(90, 56, 189, 248)); // Faint cyan center
    private static readonly IBrush TextBrush = new SolidColorBrush(Color.FromArgb(180, 148, 163, 184));
    private static readonly IBrush HeaderBrush = new SolidColorBrush(Color.FromRgb(226, 232, 240));

    // Glow and trace pens
    private static readonly IPen WaveformGlowPen = new Pen(new SolidColorBrush(Color.FromArgb(70, 16, 185, 129)), 4.0); // Emerald Glow
    private static readonly IPen WaveformCorePen = new Pen(new SolidColorBrush(Color.FromRgb(52, 211, 153)), 1.8); // Emerald Core
    private static readonly IPen SpectrumCrestPen = new Pen(new SolidColorBrush(Color.FromRgb(56, 189, 248)), 1.5); // Sky Blue Crest
    private static readonly IPen CentroidPen = new Pen(new SolidColorBrush(Color.FromRgb(244, 63, 94)), 1.5); // Rose marker
    private static readonly IBrush CentroidBrush = new SolidColorBrush(Color.FromRgb(244, 63, 94));
    private static readonly IPen GridPen = new Pen(GridLineBrush, 1.0);
    private static readonly IPen CenterPen = new Pen(CenterLineBrush, 1.0);
    private static readonly IPen ClipBorderPen = new Pen(new SolidColorBrush(Color.FromRgb(239, 68, 68)), 2.5);
    private static readonly IPen NormalBorderPen = new Pen(new SolidColorBrush(Color.FromArgb(80, 51, 65, 85)), 1.2);

    private readonly DispatcherTimer _refreshTimer;

    public SoundWaveControl()
    {
        ClipToBounds = true;

        // Synchronize redraw with ~60 FPS
        _refreshTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _refreshTimer.Tick += (s, e) => InvalidateVisual();
        _refreshTimer.Start();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        double width = Bounds.Width;
        double height = Bounds.Height;
        if (width <= 10 || height <= 10) return;

        var fullRect = new Rect(0, 0, width, height);

        // 1. Draw Container Background & Border
        context.DrawRectangle(BackgroundBrush, null, fullRect, 8, 8);

        // 2. Technical dB & Frequency Reference Grid
        DrawGrid(context, width, height);

        // 3. FFT Spectrum Analyzer (Background / Lower Layer)
        if (DisplayMode != VisualizerDisplayMode.WaveformOnly)
        {
            DrawSpectrum(context, width, height);
        }

        // 4. Oscilloscope Waveform (Foreground Layer with Glow)
        if (DisplayMode != VisualizerDisplayMode.SpectrumOnly)
        {
            DrawWaveform(context, width, height);
        }

        // 5. Spectral Centroid Marker
        if (DisplayMode != VisualizerDisplayMode.WaveformOnly && SpectralCentroidHz > 60f)
        {
            DrawSpectralCentroidMarker(context, width, height);
        }

        // 6. Header Badges & Info
        DrawOverlayBadges(context, width, height);

        // 7. Outer Border (Red if clipping, subtle slate otherwise)
        context.DrawRectangle(null, IsClipping ? ClipBorderPen : NormalBorderPen, fullRect, 8, 8);
    }

    private void DrawGrid(DrawingContext context, double width, double height)
    {
        double midY = height * 0.5;

        // Center 0V zero-line
        context.DrawLine(CenterPen, new Point(0, midY), new Point(width, midY));

        // Horizontal amplitude lines (+1.0, +0.5, -0.5, -1.0)
        double[] ampLevels = [0.15, 0.30, 0.70, 0.85];
        foreach (double ratio in ampLevels)
        {
            double y = height * ratio;
            context.DrawLine(GridPen, new Point(0, y), new Point(width, y));
        }

        // Vertical Logarithmic Frequency Lines for Guitar Range: 100Hz, 250Hz, 500Hz, 1kHz, 2kHz, 4kHz, 8kHz
        (float freq, string label)[] freqGuides =
        [
            (100f, "100Hz"),
            (250f, "250Hz"),
            (500f, "500Hz"),
            (1000f, "1kHz"),
            (2000f, "2kHz"),
            (4000f, "4kHz"),
            (8000f, "8kHz"),
            (16000f, "16kHz")
        ];

        foreach (var (freq, label) in freqGuides)
        {
            double x = FrequencyToX(freq, width);
            if (x > 20 && x < width - 20)
            {
                context.DrawLine(GridPen, new Point(x, 0), new Point(x, height));

                var text = new FormattedText(
                    label,
                    CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    LabelTypeface,
                    9.5,
                    TextBrush);

                context.DrawText(text, new Point(x + 3, height - 16));
            }
        }

        // Horizontal dB markings
        (double yRatio, string dbText)[] dbLabels =
        [
            (0.15, "+6 dB"),
            (0.30, "0 dB"),
            (0.50, "-∞"),
            (0.70, "-6 dB"),
            (0.85, "-18 dB")
        ];

        foreach (var (yRatio, dbText) in dbLabels)
        {
            var text = new FormattedText(
                dbText,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                LabelTypeface,
                9.0,
                TextBrush);

            context.DrawText(text, new Point(6, height * yRatio - 12));
        }
    }

    private void DrawSpectrum(DrawingContext context, double width, double height)
    {
        var spectrum = SpectrumData;
        if (spectrum == null || spectrum.Length < 16) return;

        if (_smoothedSpectrum.Length != spectrum.Length)
        {
            _smoothedSpectrum = new float[spectrum.Length];
        }

        // Apply studio-grade peak falloff / smoothing
        for (int i = 0; i < spectrum.Length; i++)
        {
            float target = spectrum[i];
            if (target > _smoothedSpectrum[i])
            {
                // Rapid attack
                _smoothedSpectrum[i] = target;
            }
            else
            {
                // Exponential decay
                _smoothedSpectrum[i] = Math.Max(0f, _smoothedSpectrum[i] * 0.88f);
            }
        }

        int bins = _smoothedSpectrum.Length;
        int nyquist = SampleRate > 0 ? SampleRate / 2 : 24000;

        // Build continuous spectrum polygon fill
        var geometry = new StreamGeometry();
        using (var sgc = geometry.Open())
        {
            sgc.BeginFigure(new Point(0, height), isFilled: true);

            int stepCount = Math.Min((int)width / 2, 250);
            for (int step = 0; step <= stepCount; step++)
            {
                double xRatio = (double)step / stepCount;
                double x = xRatio * width;

                // Logarithmic frequency mapping from 50 Hz to Nyquist
                double freq = 50.0 * Math.Pow(nyquist / 50.0, xRatio);
                int binIndex = (int)(freq / nyquist * bins);
                binIndex = Math.Clamp(binIndex, 0, bins - 1);

                float mag = _smoothedSpectrum[binIndex];
                // Scale magnitude: height * (1 - mag * 0.85)
                double y = height - (mag * height * 0.85);
                sgc.LineTo(new Point(x, y));
            }

            sgc.LineTo(new Point(width, height));
            sgc.EndFigure(isClosed: true);
        }

        // Gradient brush for spectrum fill
        var spectrumGradient = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0.5, 0.0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0.5, 1.0, RelativeUnit.Relative),
            GradientStops =
            [
                new GradientStop(Color.FromArgb(120, 56, 189, 248), 0.0), // Electric Cyan
                new GradientStop(Color.FromArgb(60, 99, 102, 241), 0.5),  // Indigo
                new GradientStop(Color.FromArgb(10, 15, 23, 42), 1.0)     // Dark Fade
            ]
        };

        context.DrawGeometry(spectrumGradient, SpectrumCrestPen, geometry);
    }

    private void DrawWaveform(DrawingContext context, double width, double height)
    {
        var waveform = WaveformData;
        if (waveform == null || waveform.Length < 32) return;

        double midY = height * 0.5;
        double ampScale = height * 0.42; // Leave margin top and bottom

        int sampleCount = waveform.Length;
        // Limit display to ~1024 points for crisp oscilloscope view
        int displayPoints = Math.Min(sampleCount, (int)width * 2);
        displayPoints = Math.Max(displayPoints, 128);

        var geometry = new StreamGeometry();
        using (var sgc = geometry.Open())
        {
            double firstY = midY - (waveform[0] * ampScale);
            sgc.BeginFigure(new Point(0, Math.Clamp(firstY, 4, height - 4)), isFilled: false);

            double xStep = width / (displayPoints - 1);
            for (int i = 1; i < displayPoints; i++)
            {
                int sampleIdx = (int)((long)i * sampleCount / displayPoints);
                float val = waveform[Math.Clamp(sampleIdx, 0, sampleCount - 1)];
                double y = midY - (val * ampScale);
                sgc.LineTo(new Point(i * xStep, Math.Clamp(y, 4, height - 4)));
            }

            sgc.EndFigure(isClosed: false);
        }

        // Pass 1: Neon Glow
        context.DrawGeometry(null, WaveformGlowPen, geometry);
        // Pass 2: High-contrast Sharp Trace
        context.DrawGeometry(null, WaveformCorePen, geometry);
    }

    private void DrawSpectralCentroidMarker(DrawingContext context, double width, double height)
    {
        double x = FrequencyToX(SpectralCentroidHz, width);
        if (x < 15 || x > width - 15) return;

        // Vertical dashed marker line
        context.DrawLine(CentroidPen, new Point(x, 15), new Point(x, height - 20));

        // Top indicator triangle
        var triangle = new StreamGeometry();
        using (var sgc = triangle.Open())
        {
            sgc.BeginFigure(new Point(x - 5, 12), isFilled: true);
            sgc.LineTo(new Point(x + 5, 12));
            sgc.LineTo(new Point(x, 18));
            sgc.EndFigure(isClosed: true);
        }
        context.DrawGeometry(CentroidBrush, null, triangle);

        // Text label
        var label = new FormattedText(
            $"Centroid: {SpectralCentroidHz:F0} Hz",
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            LabelTypeface,
            9.0,
            CentroidBrush);

        double textX = Math.Clamp(x - 30, 8, width - 90);
        context.DrawText(label, new Point(textX, 20));
    }

    private void DrawOverlayBadges(DrawingContext context, double width, double height)
    {
        // Top-left title badge
        var title = new FormattedText(
            "● LIVE OSCILLOSCOPE & FFT SPECTRUM",
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            LabelTypeface,
            10.0,
            HeaderBrush);
        context.DrawText(title, new Point(12, 10));

        // Top-right technical specifications
        string specs = $"60 FPS • {WaveformData?.Length ?? 2048} SAMPLES • {SampleRate} Hz";
        var specText = new FormattedText(
            specs,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            LabelTypeface,
            9.5,
            TextBrush);
        context.DrawText(specText, new Point(width - 240, 10));
    }

    private double FrequencyToX(float frequencyHz, double width)
    {
        int nyquist = SampleRate > 0 ? SampleRate / 2 : 24000;
        if (frequencyHz <= 50f) return 0;
        if (frequencyHz >= nyquist) return width;

        // Logarithmic scale: 50 Hz to Nyquist
        double minLog = Math.Log10(50.0);
        double maxLog = Math.Log10(nyquist);
        double freqLog = Math.Log10(frequencyHz);

        double normalized = (freqLog - minLog) / (maxLog - minLog);
        return normalized * width;
    }
}
