using System.Text;
using System.Text.RegularExpressions;

namespace OpenGym.Nutrition.Services;

public static partial class ProductSearch
{
    public static string Normalize(string value) => Spaces().Replace(value.Normalize(NormalizationForm.FormKC).Trim(), " ").ToUpperInvariant();
    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
    [GeneratedRegex(@"^[0-9]{8,14}$")]
    private static partial Regex Code();
    public static bool ValidCode(string? value) => value is not null && Code().IsMatch(value);
    // OFF canonicalizes zero padding: UPC/EAN aliases must share one external identity.
    // https://openfoodfacts.github.io/openfoodfacts-server/api/ref-barcode-normalization/
    public static string CanonicalCode(string code)
    {
        var digits = code.TrimStart('0');
        return digits.PadLeft(digits.Length <= 8 ? 8 : 13, '0');
    }
}
