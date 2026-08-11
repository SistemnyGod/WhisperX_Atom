using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace WhisperX_Atom_Desktop.Services;

internal enum PageLayoutMode
{
    Compact,
    Standard,
    Wide
}

internal static class ResponsiveLayout
{
    public static PageLayoutMode GetMode(double width) => width < 920
        ? PageLayoutMode.Compact
        : width < 1200
            ? PageLayoutMode.Standard
            : PageLayoutMode.Wide;

    public static bool IsWide(double width) => GetMode(width) == PageLayoutMode.Wide;

    public static void SetTwoColumn(Grid grid, FrameworkElement first, FrameworkElement second, double secondColumnWidth, double width)
    {
        var wide = IsWide(width);
        grid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
        grid.ColumnDefinitions[1].Width = wide ? new GridLength(secondColumnWidth) : new GridLength(0);
        grid.RowDefinitions[0].Height = wide ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        grid.RowDefinitions[1].Height = wide ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Grid.SetColumn(first, 0);
        Grid.SetRow(first, 0);
        Grid.SetColumn(second, wide ? 1 : 0);
        Grid.SetRow(second, wide ? 0 : 1);
    }

    public static void SetCardColumns(Grid grid, IReadOnlyList<FrameworkElement> cards, double width, int wideMaxColumns = int.MaxValue)
    {
        var columns = GetMode(width) switch
        {
            PageLayoutMode.Compact => 1,
            PageLayoutMode.Standard => 2,
            _ => Math.Min(cards.Count, Math.Max(1, wideMaxColumns))
        };
        grid.ColumnDefinitions.Clear();
        grid.RowDefinitions.Clear();
        for (var index = 0; index < columns; index++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var rows = (int)Math.Ceiling(cards.Count / (double)columns);
        for (var index = 0; index < rows; index++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var index = 0; index < cards.Count; index++)
        {
            Grid.SetColumn(cards[index], index % columns);
            Grid.SetRow(cards[index], index / columns);
        }
    }
}
