using System.Reflection;
using STS2SkinChanger.Core;

// Godot's script map uses Dictionary.Add keyed by resource path, so a repeat aborts the whole scan.
// Skin packs register their scenes twice by design (Skin Changer first, their own initializer
// later), and on the reported machines that duplicate ended a whole visual session - the skin kept
// its resources but lost its model. These rules decide which repeats are harmless.
internal static class GodotScriptPathRegistrationPolicyTests
{
    public static void Run()
    {
        void Require(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }

        var thisType = typeof(GodotScriptPathRegistrationPolicyTests);
        var otherTypeInSameModule = typeof(AnotherDeclaration);
        var otherModuleType = typeof(Uri);

        Require(GodotScriptPathRegistrationPolicy.IsIdempotentRepeat(thisType, thisType),
            "同一个类型重复注册是同一份声明。");

        // A second copy of one pack (another load context, a second install, an in-memory rewrite)
        // declares the same script through different Type objects while the path stays identical.
        var copy = Assembly.LoadFile(thisType.Assembly.Location);
        var typeFromCopy = copy.GetType(thisType.FullName!, throwOnError: true)!;
        Require(!ReferenceEquals(typeFromCopy, thisType) &&
            typeFromCopy.Module.ModuleVersionId == thisType.Module.ModuleVersionId,
            "测试前提：第二个副本必须是独立加载的同一份编译结果。");
        Require(GodotScriptPathRegistrationPolicy.IsIdempotentRepeat(thisType, typeFromCopy),
            "同一份编译结果的第二个副本必须被认成重复注册；认不出来就会抛出重复键，把整场视觉会话回滚掉。");

        // Two declarations of one path inside the same pack are the pack's own defect. Keeping the
        // first mapping lets the skin run; letting Godot throw would take the whole session down.
        Require(GodotScriptPathRegistrationPolicy.IsIdempotentRepeat(thisType, otherTypeInSameModule),
            "同一个文件里两个类型抢同一条路径时，保留先注册的映射，不能让整款皮肤陪葬。");

        Require(!GodotScriptPathRegistrationPolicy.IsIdempotentRepeat(thisType, otherModuleType),
            "不同程序集的两个类型抢同一条路径是真正的冲突，这条规则不能静默合并。");
        Require(!GodotScriptPathRegistrationPolicy.IsIdempotentRepeat(null, thisType) &&
            !GodotScriptPathRegistrationPolicy.IsIdempotentRepeat(thisType, null) &&
            !GodotScriptPathRegistrationPolicy.IsIdempotentRepeat(null, null),
            "读不到类型时不能当作重复注册。");

        // Godot reports a path it already holds exactly like the reported log line.
        var duplicate = new ArgumentException(
            "An item with the same key has already been added. Key: " +
            "res://src/Core/Nodes/Animation/HeadBoneClickToggle.cs");
        Require(GodotScriptPathRegistrationPolicy.IsHarmlessDuplicate(duplicate),
            "重复键异常必须被识别出来。");
        Require(GodotScriptPathRegistrationPolicy.IsHarmlessDuplicate(
                new TargetInvocationException(duplicate)),
            "作者初始化器是通过反射调用的，反射包装下也要能识别。");
        Require(GodotScriptPathRegistrationPolicy.DuplicatePath(new TargetInvocationException(duplicate)) ==
            "res://src/Core/Nodes/Animation/HeadBoneClickToggle.cs",
            "日志必须能点名具体路径，玩家才能把问题反馈给皮肤作者。");

        Require(!GodotScriptPathRegistrationPolicy.IsHarmlessDuplicate(
                new ArgumentException("Value does not fall within the expected range.")) &&
            !GodotScriptPathRegistrationPolicy.IsHarmlessDuplicate(
                new KeyNotFoundException("res://x.cs")) &&
            !GodotScriptPathRegistrationPolicy.IsHarmlessDuplicate(
                new MissingMethodException("SilentSkinMod.MainFile.Initialize")) &&
            !GodotScriptPathRegistrationPolicy.IsHarmlessDuplicate(null),
            "只有重复键这一种异常可以被吞掉，其它注册失败必须继续抛出。");
        Require(GodotScriptPathRegistrationPolicy.DuplicatePath(new ArgumentException("boom")) == null &&
            GodotScriptPathRegistrationPolicy.DuplicatePath(null) == null,
            "消息里没有路径时不编造路径。");

        Console.WriteLine(
            "Godot script path registration passed: one declaration seen twice stays idempotent, real conflicts and other failures still surface.");
    }

    private sealed class AnotherDeclaration;
}
