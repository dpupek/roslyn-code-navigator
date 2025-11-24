using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace MyCompany.Services.TextProcessing
{
    public interface IMessageProcessor
    {
        string Process(string message);
        Task<string> ProcessAsync(string message);
    }

    public class TextProcessor : IMessageProcessor
    {
        public string Process(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return string.Empty;
            }

            return input.Trim().ToUpperInvariant();
        }

        public Task<string> ProcessAsync(string input)
            => Task.FromResult(Process(input));

        public int CalculateScore(string input)
        {
            var score = 0;
            foreach (var ch in input)
            {
                if (char.IsLetter(ch))
                {
                    score++;
                }
                else if (char.IsDigit(ch))
                {
                    score += 2;
                }
                else
                {
                    score += 3;
                }
            }

            if (score > 10 && score % 2 == 0)
            {
                score += 5;
            }

            if (score > 20)
            {
                score += 10;
            }

            return score;
        }
    }

    public class TextWorkflow
    {
        private readonly TextProcessor _processor = new();

        public string Execute(IEnumerable<string> lines)
        {
            var buffer = new List<string>();
            foreach (var line in lines)
            {
                var processed = _processor.Process(line);
                if (!string.IsNullOrEmpty(processed))
                {
                    buffer.Add(processed);
                }
            }

            return string.Join("|", buffer);
        }
    }
}
