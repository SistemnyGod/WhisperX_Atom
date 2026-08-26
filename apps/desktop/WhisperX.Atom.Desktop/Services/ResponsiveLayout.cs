using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Runtime.CompilerServices;

namespace WhisperX_Atom_Desktop.Services;

internal enum PageLayoutMode
{
    Compact,
    Standard,
    Wide
}

internal static class ResponsiveLayout
{
    private sealed class LayoutCache
    {
        public PageLayoutMode? Mode { get; set; }
        public int CardCount { get; set; } = -1;
        public bool AllowStandard { get; set; }
    }

    // SizeChanged fires continuously while a window is resized. Keep the last
    // breakpoint per grid so an in-breakpoint resize only changes available
    // space and never tears down/rebuilds the visual tree.
    private static readonly ConditionalWeakTable<Grid, LayoutCache> Cache = new();

    public static PageLayoutMode GetMode(double width) => width < 920
        ? PageLayoutMode.Compact
        : width < 1200
            ? PageLayoutMode.Standard
            : PageLayoutMode.Wide;

    public static bool IsWide(double width) => GetMode(width) == PageLayoutMode.Wide;

    public static void SetTwoColumn(Grid? grid, FrameworkElement? first, FrameworkElement? second, double secondColumnWidth, double width, bool allowStandard = false)
    {
        // SizeChanged can fire while a freshly navigated XAML tree is still
        // materialising. A transient null must not take down the Desktop.
        if (grid is null || first is null || second is null) return;
        while (grid.ColumnDefinitions.Count < 2)
            grid.ColumnDefinitions.Add(new ColumnDefinition());
        while (grid.RowDefinitions.Count < 2)
            grid.RowDefinitions.Add(new RowDefinition());

        var mode = GetMode(width);
        var cache = Cache.GetOrCreateValue(grid);
        if (cache.Mode == mode && cache.AllowStandard == allowStandard) return;
        var wide = mode == PageLayoutMode.Wide || allowStandard && mode == PageLayoutMode.Standard;
        grid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
        grid.ColumnDefinitions[1].Width = wide ? new GridLength(secondColumnWidth) : new GridLength(0);
        grid.RowDefinitions[0].Height = wide ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        grid.RowDefinitions[1].Height = wide ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Grid.SetColumn(first, 0);
        Grid.SetRow(first, 0);
        Grid.SetColumn(second, wide ? 1 : 0);
        Grid.SetRow(second, wide ? 0 : 1);
        cache.Mode = mode;
        cache.AllowStandard = allowStandard;
    }

    public static void SetCardColumns(Grid? grid, IReadOnlyList<FrameworkElement?> cards, double width, int wideMaxColumns = int.MaxValue)
    {
        if (grid is null || cards is null || cards.Count == 0) return;
        var validCards = cards.Where(card => card is not null).ToArray();
        if (validCards.Length == 0) return;
        var mode = GetMode(width);
        var cache = Cache.GetOrCreateValue(grid);
        if (cache.Mode == mode && cache.CardCount == validCards.Length) return;
        var columns = GetMode(width) switch
        {
            PageLayoutMode.Compact => 1,
            PageLayoutMode.Standard => 2,
            _ => Math.Min(validCards.Length, Math.Max(1, wideMaxColumns))
        };
        grid.ColumnDefinitions.Clear();
        grid.RowDefinitions.Clear();
        for (var index = 0; index < columns; index++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var rows = (int)Math.Ceiling(validCards.Length / (double)columns);
        for (var index = 0; index < rows; index++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var index = 0; index < validCards.Length; index++)
        {
            Grid.SetColumn(validCards[index], index % columns);
            Grid.SetRow(validCards[index], index / columns);
        }
        cache.Mode = mode;
        cache.CardCount = validCards.Length;
    }
}
