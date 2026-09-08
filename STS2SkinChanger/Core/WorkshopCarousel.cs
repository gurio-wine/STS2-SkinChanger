namespace STS2SkinChanger.Core;

// One image ahead, with no timer-owned downloads. The UI drives Tick once per
// frame; it never waits for unfinished I/O and keeps the current image on failure.
internal sealed class WorkshopCarousel<T>(Func<string, CancellationToken, Task<T?>> load,
    Action<T?, int, int> present, Func<double> clock) where T : class
{
    private const double Interval = 2;
    private string[] _urls = [];
    private CancellationTokenSource? _lifetime;
    private Task<T?>? _pending;
    private int _nextIndex;
    private int _generation;
    private bool _presented;
    private double _due;

    public void Start(string[] urls, CancellationToken token)
    {
        Clear();
        if (urls.Length == 0 || token.IsCancellationRequested) return;
        _urls = urls.ToArray();
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        _pending = LoadSafe(_urls[0], _lifetime.Token);
    }

    public void Tick(bool autoAdvance = true)
    {
        if (_lifetime == null || _lifetime.IsCancellationRequested || _pending is not { IsCompleted: true } ready ||
            _presented && (!autoAdvance || clock() < _due)) return;
        var generation = _generation;
        var image = ready.GetAwaiter().GetResult(); // Completed, failure-normalized task only.
        if (image != null || !_presented) present(image, _nextIndex, _urls.Length);
        if (_generation != generation || _lifetime == null || _lifetime.IsCancellationRequested) return;
        _presented = true;
        _due = clock() + Interval;
        _pending = null;
        if (_urls.Length <= 1) return;
        _nextIndex = (_nextIndex + 1) % _urls.Length;
        // Start the next request as soon as this image is presented, not when due.
        _pending = LoadSafe(_urls[_nextIndex], _lifetime.Token);
    }

    private async Task<T?> LoadSafe(string url, CancellationToken token)
    {
        try { return await load(url, token); }
        // The image loader reports errors. Normalize them here so abandoning a
        // hover cannot leave an unobserved task, or blank a previously good image.
        catch { return null; }
    }

    public void Clear()
    {
        _generation++;
        _lifetime?.Cancel(); _lifetime?.Dispose(); _lifetime = null;
        _pending = null; _urls = []; _nextIndex = 0; _presented = false; _due = 0;
    }
}
