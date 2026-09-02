namespace STS2SkinChanger.Core;

internal sealed record CharacterCombatTransform(
    float Scale = 1f,
    float OffsetX = 0f,
    float OffsetY = 0f)
{
    public float HealthBarScale { get; init; } = 1f;

    public float HealthBarOffsetX { get; init; }

    public float HealthBarOffsetY { get; init; }

    public bool HealthBarFollowsModelScale { get; init; }

    public bool HealthBarFollowsModelMovement { get; init; } = true;

    public float IntentScale { get; init; } = 1f;

    public float IntentOffsetX { get; init; }

    public float IntentOffsetY { get; init; }

    public bool IntentFollowsModelScale { get; init; }

    public bool IntentFollowsModelMovement { get; init; } = true;

    public float SelectionReticleScale { get; init; } = 1f;

    public float SelectionReticleOffsetX { get; init; }

    public float SelectionReticleOffsetY { get; init; }

    public bool SelectionReticleFollowsModelScale { get; init; } = true;

    public bool SelectionReticleFollowsModelMovement { get; init; } = true;
}
