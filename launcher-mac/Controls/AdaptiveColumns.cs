using Avalonia;
using Avalonia.Controls;

namespace DustoreLauncherV.Mac.Controls;

/// <summary>Two readable columns become one in a small window; the controls keep their identity and focus.</summary>
public sealed class AdaptiveColumns : Grid
{
    public static readonly StyledProperty<double> BreakpointProperty =
        AvaloniaProperty.Register<AdaptiveColumns, double>(nameof(Breakpoint), 720);
    public double Breakpoint { get => GetValue(BreakpointProperty); set => SetValue(BreakpointProperty, value); }
    private bool? _stacked;

    protected override Size MeasureOverride(Size availableSize)
    {
        bool stacked = availableSize.Width < Breakpoint;
        if (_stacked != stacked || RowDefinitions.Count == 0)
        {
            _stacked = stacked;
            ColumnDefinitions = new ColumnDefinitions(stacked ? "*" : "*,18,*");
            RowDefinitions = new RowDefinitions(stacked ? "Auto,18,Auto" : "Auto");
            if (Children.Count > 0) { SetColumn(Children[0], 0); SetRow(Children[0], 0); }
            if (Children.Count > 1) { SetColumn(Children[1], stacked ? 0 : 2); SetRow(Children[1], stacked ? 2 : 0); }
        }
        return base.MeasureOverride(availableSize);
    }
}
