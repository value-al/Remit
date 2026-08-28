using System.Collections.Concurrent;

namespace Remit.Funding.Psp;

/// <summary>
/// Observed success rate per provider over a sliding window of recent attempts. The router
/// reads it to rank providers; the router writes it after every call. Process-local for now —
/// the Redis-backed implementation shares the window across instances.
/// </summary>
public interface IProviderHealth
{
    /// <summary>Success rate in [0, 1]. Unknown providers score 1: everyone starts trusted.</summary>
    double SuccessRate(string provider);

    void Record(string provider, bool success);
}

public sealed class InMemoryProviderHealth(int windowSize = 50) : IProviderHealth
{
    private readonly ConcurrentDictionary<string, Window> _windows = new(StringComparer.OrdinalIgnoreCase);

    public double SuccessRate(string provider) =>
        _windows.TryGetValue(provider, out var window) ? window.SuccessRate : 1.0;

    public void Record(string provider, bool success) =>
        _windows.GetOrAdd(provider, _ => new Window(windowSize)).Add(success);

    private sealed class Window(int size)
    {
        private readonly bool[] _outcomes = new bool[size];
        private readonly object _gate = new();
        private int _count;
        private int _next;

        public double SuccessRate
        {
            get
            {
                lock (_gate)
                {
                    if (_count == 0)
                    {
                        return 1.0;
                    }

                    var successes = 0;
                    for (var i = 0; i < _count; i++)
                    {
                        if (_outcomes[i])
                        {
                            successes++;
                        }
                    }

                    return (double)successes / _count;
                }
            }
        }

        public void Add(bool success)
        {
            lock (_gate)
            {
                _outcomes[_next] = success;
                _next = (_next + 1) % _outcomes.Length;
                _count = Math.Min(_count + 1, _outcomes.Length);
            }
        }
    }
}
