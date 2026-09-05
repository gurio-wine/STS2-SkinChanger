using System.Runtime.CompilerServices;
using MegaCrit.Sts2.Core.Models;

public sealed class RegisteredSkinProfile
{
    public string ProfileId => "registered";
    public Type TargetCharacterType => typeof(CharacterModel);
    public string BodyTexturePath => "res://fixture.png";
    public string BodySkeletonDataPath => "res://fixture.tres";

    [MethodImpl(MethodImplOptions.NoInlining)]
    public bool AppliesTo(CharacterModel character) => character != null;
}
