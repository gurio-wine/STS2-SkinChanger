namespace STS2SkinChanger.Ui;

internal sealed class ReloadingReferenceCache<T>
    where T : class
{
    private T? _value;

    internal T? Get(Func<T?> load, Func<T, bool> isValid)
    {
        ArgumentNullException.ThrowIfNull(load);
        ArgumentNullException.ThrowIfNull(isValid);

        if (_value != null && isValid(_value))
        {
            return _value;
        }

        _value = load();
        return _value;
    }
}
