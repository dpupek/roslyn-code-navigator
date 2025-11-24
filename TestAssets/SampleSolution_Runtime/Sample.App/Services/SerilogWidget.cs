using MyCompany.Services.TextProcessing;

namespace MyCompany.Services.Logging
{
    public class SerilogWidget
    {
        private readonly TextProcessor _processor = new();

        public string ProcessAndLog(string message)
        {
            var processed = _processor.Process(message);
            return $"[SerilogWidget]{processed}";
        }

        public int Score(string message) => _processor.CalculateScore(message);
    }
}
