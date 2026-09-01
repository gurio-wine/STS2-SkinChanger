namespace STS2SkinChanger.Core;

internal enum DeferredRegistrationResult
{
    Deferred,
    Completed,
    AlreadyCompleted
}

/// <summary>
/// Keeps provider declarations pending until the game model database is ready. A declaration is
/// completed only after its callback returns successfully, so transient startup failures remain
/// retryable instead of becoming a permanently incomplete skin session.
/// </summary>
internal sealed class DeferredRegistrationQueue<TValue>
{
    private readonly Dictionary<string, TValue> _pending =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _completed =
        new(StringComparer.OrdinalIgnoreCase);

    public int PendingCount => _pending.Count;

    public bool IsCompleted(string key) => _completed.Contains(key);

    public DeferredRegistrationResult TryRegister(
        string key,
        TValue value,
        bool isReady,
        Action<TValue> register)
    {
        if (_completed.Contains(key))
        {
            return DeferredRegistrationResult.AlreadyCompleted;
        }

        _pending[key] = value;
        if (!isReady)
        {
            return DeferredRegistrationResult.Deferred;
        }

        register(value);
        _pending.Remove(key);
        _completed.Add(key);
        return DeferredRegistrationResult.Completed;
    }

    public int RetryPending(
        bool isReady,
        Action<TValue> register,
        Action<string, Exception>? onFailure = null)
    {
        if (!isReady)
        {
            return 0;
        }

        var completed = 0;
        foreach (var pair in _pending.ToArray())
        {
            try
            {
                if (TryRegister(pair.Key, pair.Value, isReady: true, register) ==
                    DeferredRegistrationResult.Completed)
                {
                    completed++;
                }
            }
            catch (Exception exception)
            {
                onFailure?.Invoke(pair.Key, exception);
            }
        }

        return completed;
    }
}
