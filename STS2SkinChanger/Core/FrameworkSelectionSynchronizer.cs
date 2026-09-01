namespace STS2SkinChanger.Core;

internal static class FrameworkSelectionSynchronizer
{
    public static int Synchronize<TCharacter>(
        IEnumerable<TCharacter> registeredCharacters,
        Func<TCharacter, string> resolveGroupId,
        Func<string, string?> resolveSkinId,
        Action<TCharacter, string> setActiveSkin)
    {
        var synchronized = 0;
        foreach (var character in registeredCharacters)
        {
            var groupId = resolveGroupId(character);
            var skinId = resolveSkinId(groupId);
            setActiveSkin(
                character,
                string.IsNullOrWhiteSpace(skinId) ? "default" : skinId);
            synchronized++;
        }

        return synchronized;
    }
}
