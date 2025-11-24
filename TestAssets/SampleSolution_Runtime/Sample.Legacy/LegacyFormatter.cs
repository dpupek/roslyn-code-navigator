using System;

namespace Sample.Legacy
{
    public class LegacyFormatter
    {
        public string Format(DateTime timestamp, string message)
        {
            return $"[{timestamp:O}] {message}";
        }
    }
}
