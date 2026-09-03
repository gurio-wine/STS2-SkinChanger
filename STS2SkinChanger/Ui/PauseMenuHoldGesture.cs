namespace STS2SkinChanger.Ui;

internal sealed class PauseMenuHoldGesture
{
    internal const ulong HoldMilliseconds = 800;
    private ulong _startedAt;
    private bool _active;
    private bool _consumeRelease;

    internal void Begin(ulong now)
    {
        _startedAt = now;
        _active = true;
        _consumeRelease = false;
    }

    internal bool Advance(ulong now)
    {
        if (!_active || now < _startedAt || now - _startedAt < HoldMilliseconds)
        {
            return false;
        }

        _active = false;
        _consumeRelease = true;
        return true;
    }

    internal void Cancel()
    {
        _consumeRelease |= _active;
        _active = false;
    }

    internal bool ConsumeRelease()
    {
        var consume = _consumeRelease;
        _active = false;
        _consumeRelease = false;
        return consume;
    }
}
