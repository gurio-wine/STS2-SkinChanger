namespace STS2SkinChanger.Ui;

internal enum OtherCreatureActionKind
{
    Attack,
    Block,
    Sleep,
    Wake
}

internal sealed record OtherCreatureActionDefinition(
    OtherCreatureActionKind Kind,
    IReadOnlyList<string> AnimationAliases,
    bool Loop,
    IReadOnlyList<string> FollowUpAliases,
    bool FollowUpLoop,
    string? SfxPath = null);

internal sealed record OtherCreatureDefinition(
    string Id,
    string LocalizationTable,
    string LocalizationKey,
    string FallbackTitle,
    string ScenePath,
    IReadOnlyList<string> IdleAliases,
    IReadOnlyList<OtherCreatureActionDefinition> Actions);

internal static class OtherCreatureCatalog
{
    internal const string ByrdpipAttackSfx = "event:/sfx/byrdpip/byrdpip_attack";

    private static readonly string[] IdleAliases =
        ["idle_loop", "idle", "stand", "standing"];

    internal static IReadOnlyList<OtherCreatureDefinition> All { get; } =
    [
        new OtherCreatureDefinition(
            "byrdpip",
            "monsters",
            "BYRDPIP.name",
            "异鸟宝宝",
            "res://scenes/creature_visuals/byrdpip.tscn",
            IdleAliases,
            [
                new OtherCreatureActionDefinition(
                    OtherCreatureActionKind.Attack,
                    ["attack", "attack1", "attack_1", "atk", "bite"],
                    Loop: false,
                    FollowUpAliases: IdleAliases,
                    FollowUpLoop: true,
                    SfxPath: ByrdpipAttackSfx)
            ]),
        new OtherCreatureDefinition(
            "paels_legion",
            "relics",
            "PAELS_LEGION.title",
            "佩尔的士兵",
            "res://scenes/creature_visuals/paels_legion.tscn",
            IdleAliases,
            [
                new OtherCreatureActionDefinition(
                    OtherCreatureActionKind.Block,
                    ["block"],
                    Loop: false,
                    FollowUpAliases: ["block_loop"],
                    FollowUpLoop: true),
                new OtherCreatureActionDefinition(
                    OtherCreatureActionKind.Sleep,
                    ["sleep"],
                    Loop: false,
                    FollowUpAliases: ["sleep_loop"],
                    FollowUpLoop: true),
                new OtherCreatureActionDefinition(
                    OtherCreatureActionKind.Wake,
                    ["wake_up", "wake", "wakeup"],
                    Loop: false,
                    FollowUpAliases: IdleAliases,
                    FollowUpLoop: true)
            ])
    ];

    internal static OtherCreatureDefinition? Find(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        return All.FirstOrDefault(creature =>
            creature.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
    }
}
