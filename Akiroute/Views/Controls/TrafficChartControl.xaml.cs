using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Akiroute.Views.Controls;

/// <summary>
/// A lightweight real-time throughput waveform. The view model feeds samples
/// into the <see cref="DataPoints"/> collection; every change repaints a
/// smooth Polyline over a subtle gridline background. Self-contained: it only
/// knows about the data collection, never about any view-model type.
/// </summary>
public sealed partial class TrafficChartControl : UserControl
{
    /// <summary>Maximum number of samples drawn; older samples are dropped.</summary>
    private const int MaxSamples = 120;

    private const double PaddingX = 2;
    private const double PaddingTop = 6;
    private const double PaddingBottom = 4;

    /// <summary>Backing store for <see cref="DataPoints"/>.</summary>
    public static readonly DependencyProperty DataPointsProperty = DependencyProperty.Register(
        nameof(DataPoints),
        typeof(ObservableCollection<double>),
        typeof(TrafficChartControl),
        new PropertyMetadata(null, OnDataPointsChanged));

    /// <summary>Backing store for <see cref="Title"/>.</summary>
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title),
        typeof(string),
        typeof(TrafficChartControl),
        new PropertyMetadata(null, OnTitleChanged));

    /// <summary>Initializes a new instance of the <see cref="TrafficChartControl"/> class.</summary>
    public TrafficChartControl()
    {
        InitializeComponent();
    }

    /// <summary>
    /// The throughput samples to chart (bytes/ms or similar). The control draws
    /// the last <see cref="MaxSamples"/> values and repaints on every change.
    /// </summary>
    public ObservableCollection<double>? DataPoints
    {
        get => (ObservableCollection<double>?)GetValue(DataPointsProperty);
        set => SetValue(DataPointsProperty, value);
    }

    /// <summary>Optional label shown in the top-left corner (e.g. "实时流量").</summary>
    public string? Title
    {
        get => (string?)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>Switches the observed collection and repaints.</summary>
    private static void OnDataPointsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((TrafficChartControl)d).SwapDataPoints(
            (ObservableCollection<double>?)e.OldValue,
            (ObservableCollection<double>?)e.NewValue);

    /// <summary>Refreshes the title overlay.</summary>
    private static void OnTitleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((TrafficChartControl)d).UpdateTitle();

    private void SwapDataPoints(ObservableCollection<double>? oldPoints, ObservableCollection<double>? newPoints)
    {
        if (oldPoints is not null)
        {
            oldPoints.CollectionChanged -= OnDataPointsCollectionChanged;
        }

        if (newPoints is not null)
        {
            newPoints.CollectionChanged += OnDataPointsCollectionChanged;
        }

        Repaint();
    }

    /// <summary>
    /// Repaints when the series changes. Samples are usually appended on the UI
    /// thread, but marshal back to it defensively if a worker thread feeds us.
    /// </summary>
    private void OnDataPointsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (DispatcherQueue.HasThreadAccess)
        {
            Repaint();
        }
        else
        {
            _ = DispatcherQueue.TryEnqueue(Repaint);
        }
    }

    private void ChartCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => Repaint();

    private void UpdateTitle()
    {
        bool hasTitle = !string.IsNullOrWhiteSpace(Title);
        TitleText.Text = Title ?? string.Empty;
        TitleText.Visibility = hasTitle ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Recomputes the gridlines, the trace, and the area fill from the current
    /// samples and control size. Cheap by design: a few dozen points, no
    /// per-frame work, and no animation state.
    /// </summary>
    private void Repaint()
    {
        double width = ChartCanvas.ActualWidth;
        double height = ChartCanvas.ActualHeight;

        UpdateGridLines(width, height);

        var data = DataPoints;
        if (data is null || data.Count == 0 || width <= 2 * PaddingX || height <= 1)
        {
            WaveLine.Points = new PointCollection();
            WaveFill.Points = new PointCollection();
            HintText.Visibility = Visibility.Visible;
            return;
        }

        // Normalize against the peak of the visible window.
        int count = Math.Min(data.Count, MaxSamples);
        double max = 0;
        for (int i = data.Count - count; i < data.Count; i++)
        {
            double value = data[i];
            if (value > max)
            {
                max = value;
            }
        }

        double scale = Math.Max(max, 1);
        double usable = Math.Max(height - PaddingTop - PaddingBottom, 1);
        double stepX = count > 1 ? (width - 2 * PaddingX) / (count - 1) : 0;

        var linePoints = new PointCollection();
        for (int i = 0; i < count; i++)
        {
            double value = data[data.Count - count + i];
            double x = count > 1 ? PaddingX + i * stepX : width / 2;
            double y = PaddingTop + (1 - value / scale) * usable;
            linePoints.Add(new Point(x, y));
        }

        WaveLine.Points = linePoints;

        // Closed area under the curve, filled with the translucent accent.
        var fillPoints = new PointCollection();
        foreach (var point in linePoints)
        {
            fillPoints.Add(point);
        }

        fillPoints.Add(new Point(linePoints[linePoints.Count - 1].X, height));
        fillPoints.Add(new Point(linePoints[0].X, height));
        WaveFill.Points = fillPoints;

        HintText.Visibility = Visibility.Collapsed;
    }

    private void UpdateGridLines(double width, double height)
    {
        GridLine25.X1 = 0;
        GridLine25.X2 = width;
        GridLine25.Y1 = GridLine25.Y2 = height * 0.25;

        GridLine50.X1 = 0;
        GridLine50.X2 = width;
        GridLine50.Y1 = GridLine50.Y2 = height * 0.50;

        GridLine75.X1 = 0;
        GridLine75.X2 = width;
        GridLine75.Y1 = GridLine75.Y2 = height * 0.75;
    }
}
