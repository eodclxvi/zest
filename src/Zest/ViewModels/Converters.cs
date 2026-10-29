using Avalonia.Data.Converters;

namespace Zest.ViewModels;

public static class Converters
{
    /// <summary>
    /// Details page: the info column sits beside the 380px cover column when there's room,
    /// and takes the full width (wrapping under the cover) when there isn't.
    /// </summary>
    public static readonly IValueConverter DetailsInfoWidth = new FuncValueConverter<double, double>(available =>
        available - 380 >= 420 ? available - 380 - 4 : Math.Max(0, available - 4));
}
