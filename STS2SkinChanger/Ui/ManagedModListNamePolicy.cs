namespace STS2SkinChanger.Ui;

internal static class ManagedModListNamePolicy
{
    private const string Prefix = "[SC] ";

    public static string Format(string name, bool isManagedProvider)
    {
        if (!isManagedProvider ||
            name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return name;
        }

        return Prefix + name;
    }
}
