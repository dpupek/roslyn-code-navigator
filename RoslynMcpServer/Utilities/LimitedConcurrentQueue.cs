using System.Collections.Concurrent;

namespace RoslynMcpServer.Utilities;

/// <summary>
/// Thread-safe bounded queue that drops oldest entries when capacity is exceeded.
/// </summary>
public sealed class LimitedConcurrentQueue<T>
{
    private readonly ConcurrentQueue<T> _queue = new();
    private readonly int _capacity;

    public LimitedConcurrentQueue(int capacity)
    {
        _capacity = capacity <= 0 ? 1 : capacity;
    }

    public void Enqueue(T item)
    {
        _queue.Enqueue(item);
        while (_queue.Count > _capacity && _queue.TryDequeue(out _))
        {
        }
    }

    public T[] ToArray() => _queue.ToArray();
}
