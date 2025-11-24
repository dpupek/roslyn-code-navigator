using MyCompany.Services.Legacy;

namespace MyCompany.Services.TextProcessing
{
    public class VbBridge
    {
        private readonly LegacyCalculator _calculator = new();

        public int Add(int x, int y) => _calculator.Add(x, y);

        public string Format(string message) => _calculator.FormatMessage(message);
    }
}
