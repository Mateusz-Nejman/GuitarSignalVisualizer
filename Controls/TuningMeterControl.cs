using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace GuitarSignalVisualiser.Controls;

/// <summary>
/// High-precision guitar tuner meter control with sub-cent needle and ±3ct in-tune zone.
/// </summary>
public class TuningMeterControl : Control
{
    public static readonly StyledProperty<float> CentsProperty =
        AvaloniaProperty.Register<TuningMeterControl, float>(nameof(Cents), 0f);

    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<TuningMeterControl, bool>(nameof(IsActive), false);

    public float Cents
    {
        get => GetValue(CentsProperty);
        set => SetValue(CentsProperty, value);
    }

    public bool IsActive
    {
        get => GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    private static readonly IBrush BgBrush = new SolidColorBrush(Color.FromRgb(11, 15, 23));
    private static readonly IBrush BorderBrush = new SolidColorBrush(Color.FromRgb(37, 45, 61));
    private static readonly IPen BorderPen = new Pen(BorderBrush, 1.0);

    private static readonly IBrush InTuneZoneBrush = new SolidColorBrush(Color.FromArgb(45, 16, 185, 129));
    private static readonly IPen TickPen = new Pen(new SolidColorBrush(Color.FromArgb(90, 148, 163, 184)), 1.0);
    private static readonly IPen CenterTickPen = new Pen(new SolidColorBrush(Color.FromRgb(16, 185, 129)), 2.0);

    private static readonly IPen NeedleInTunePen = new Pen(new SolidColorBrush(Color.FromRgb(52, 211, 153)), 2.5);
    private static readonly IPen NeedleFlatPen = new Pen(new SolidColorBrush(Color.FromRgb(56, 189, 248)), 2.5);
    private static readonly IPen NeedleSharpPen = new Pen(new SolidColorBrush(Color.FromRgb(245, 158, 11)), 2.5);
    private static readonly IPen NeedleInactivePen = new Pen(new SolidColorBrush(Color.FromArgb(80, 100, 116, 139)), 2.0);

    private static readonly IBrush InTuneFill = new SolidColorBrush(Color.FromRgb(52, 211, 153));
    private static readonly IBrush FlatFill = new SolidColorBrush(Color.FromRgb(56, 189, 248));
    private static readonly IBrush SharpFill = new SolidColorBrush(Color.FromRgb(245, 158, 11));

    static TuningMeterControl()
    {
        AffectsRender<TuningMeterControl>(CentsProperty, IsActiveProperty);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        double width = Bounds.Width;
        double height = Bounds.Height;
        if (width <= 10 || height <= 5) return;

        var fullRect = new Rect(0, 0, width, height);

        // Background
        context.DrawRectangle(BgBrush, BorderPen, fullRect, 6, 6);

        double midX = width * 0.5;

        // Draw ±3 cent In-Tune green sweet-spot
        double inTuneWidth = (6.0 / 100.0) * width;
        var inTuneRect = new Rect(midX - inTuneWidth * 0.5, 2, inTuneWidth, height - 4);
        context.DrawRectangle(InTuneZoneBrush, null, inTuneRect, 3, 3);

        // Draw Tick Marks (-50, -40, -30, -20, -10, 0, +10, +20, +30, +40, +50)
        for (int c = -50; c <= 50; c += 10)
        {
            double x = midX + (c / 50.0) * (midX - 10);
            bool isCenter = c == 0;
            double tickHeight = isCenter ? height - 6 : (c % 25 == 0 ? height * 0.55 : height * 0.35);
            double top = (height - tickHeight) * 0.5;

            context.DrawLine(isCenter ? CenterTickPen : TickPen, new Point(x, top), new Point(x, top + tickHeight));
        }

        // Draw Needle
        if (IsActive)
        {
            float clampedCents = Math.Clamp(Cents, -50.0f, 50.0f);
            double needleX = midX + (clampedCents / 50.0) * (midX - 10);
            bool inTune = MathF.Abs(clampedCents) <= 3.0f;
            bool flat = clampedCents < -3.0f;

            IPen needlePen = inTune ? NeedleInTunePen : (flat ? NeedleFlatPen : NeedleSharpPen);
            IBrush needleBrush = inTune ? InTuneFill : (flat ? FlatFill : SharpFill);

            // Needle line
            context.DrawLine(needlePen, new Point(needleX, 2), new Point(needleX, height - 2));

            // Needle indicator triangle
            var tri = new StreamGeometry();
            using (var sgc = tri.Open())
            {
                sgc.BeginFigure(new Point(needleX - 4, 1), isFilled: true);
                sgc.LineTo(new Point(needleX + 4, 1));
                sgc.LineTo(new Point(needleX, 6));
                sgc.EndFigure(isClosed: true);
            }
            context.DrawGeometry(needleBrush, null, tri);
        }
        else
        {
            // Center dormant needle
            context.DrawLine(NeedleInactivePen, new Point(midX, 4), new Point(midX, height - 4));
        }
    }
}
