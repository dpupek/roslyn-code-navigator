using System.Linq;

namespace MyCompany.Services.TextProcessing.Diagnostics
{
    public static class TextAnalyzer
    {
        public static bool ContainsAllCaps(string text)
            => !string.IsNullOrEmpty(text) && text.All(char.IsUpper);

        public static bool LooksNumeric(string text)
            => !string.IsNullOrEmpty(text) && text.All(char.IsDigit);
    }
}
