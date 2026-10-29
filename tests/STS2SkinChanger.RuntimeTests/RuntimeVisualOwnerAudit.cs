using STS2SkinChanger.Catalog;

internal static class RuntimeVisualOwnerAudit
{
    /// <summary>
    /// Reads one installed package and reports whether it replaces the character's own visual
    /// creation at runtime. Only metadata is read: the provider DLL is never loaded or executed,
    /// and no game process is started. This cannot replace an in-game check of what the player
    /// sees; it only shows which classification the catalog reaches for a real package.
    /// </summary>
    internal static void Run(string providerRoot, string providerId)
    {
        var root = Path.GetFullPath(providerRoot);
        var hasDll = File.Exists(Path.Combine(root, providerId + ".dll"));
        var descriptor = new SkinModDescriptor(
            providerId,
            providerId,
            File.Exists(Path.Combine(root, providerId + ".pck"))
                ? Path.Combine(root, providerId + ".pck")
                : null,
            AffectsGameplay: false,
            RootPath: root,
            HasDll: hasDll);
        var replaces = SkinCatalog.ReplacesCharacterVisualsAtRuntime(descriptor);
        Console.WriteLine(
            $"{providerId}: has-dll={hasDll}, " +
            $"replaces-character-visuals-at-runtime={replaces}");
    }
}
