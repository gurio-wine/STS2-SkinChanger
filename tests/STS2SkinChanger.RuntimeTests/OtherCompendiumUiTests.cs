using System.Reflection;
using System.Collections;
using Godot;
using HarmonyLib;
using STS2SkinChanger;

internal static class OtherCompendiumUiTests
{
    internal static void Run()
    {
        var assembly = typeof(Entry).Assembly;
        var regions = assembly.GetType("STS2SkinChanger.Core.EventRegionPolicy");
        Require(regions != null, "事件图鉴需要使用地区事件池分组，而非全事件平铺列表。");
        var grouped = (IReadOnlyDictionary<string, string[]>)AccessTools.Method(regions, "Group").Invoke(null,
            [new[] { "A", "B", "SHARED", "MODDED", "A" },
                new Dictionary<string, IEnumerable<string>> { ["act1"] = new[] { "A", "SHARED", "UNKNOWN" },
                    ["act2"] = new[] { "B", "A" }, ["empty"] = Array.Empty<string>() }, new[] { "SHARED", "MISSING" }])!;
        Require(grouped.Keys.SequenceEqual(new[] { "act1", "act2", "__shared__", "__other__" }) &&
                grouped["act1"].SequenceEqual(new[] { "A" }) && grouped["act2"].SequenceEqual(new[] { "B", "A" }) &&
                grouped["__shared__"].SequenceEqual(new[] { "SHARED" }) && grouped["__other__"].SequenceEqual(new[] { "MODDED" }),
            "按真实地区归属保留多地区事件，跨地区事件单列，不收录不可预览条目，不遗漏未知 Mod 地区事件。");
        var screen = assembly.GetType("STS2SkinChanger.Ui.AncientCompendiumScreen", true)!;
        var regionRefresh = AccessTools.Method(screen, "RefreshEventRegions");
        Require(Calls(regionRefresh, typeof(MegaCrit.Sts2.Core.Models.ModelDb), "get_Acts") &&
                Calls(regionRefresh, typeof(MegaCrit.Sts2.Core.Models.ModelDb), "get_AllSharedEvents") &&
                Calls(AccessTools.Method(screen, "RefreshAncients"), screen, "RefreshEventRegions"),
            "实际图鉴列表必须读取游戏地区池，并在切换分类和重开时刷新，不能只测试未接入的分组函数。");
        var translations = (IDictionary)AccessTools.Field(screen, "EventRegionNames").GetValue(null)!;
        Require(translations.Count == 15 && translations.Values.Cast<(string Shared, string Other)>()
            .All(t => !string.IsNullOrWhiteSpace(t.Shared) && !string.IsNullOrWhiteSpace(t.Other)), "地区补充分组须提供全部语言。");

        var boundsMethod = AccessTools.Method(assembly.GetType("STS2SkinChanger.Core.EventPreviewPolicy", true), "TextBounds");
        Require(boundsMethod.GetParameters().Length == 3, "事件正文高度不能再以皮肤选择按钮的固定 Y 坐标截断。");
        Rect2 Bounds(float content) => (Rect2)boundsMethod.Invoke(null,
            [new Vector2(1920, 1080), new Rect2(922, 255, 800, 40), content])!;
        var ordinary = Bounds(700);
        Require(ordinary.Position == new Vector2(922, 255) && ordinary.Size.X == 820 && ordinary.Size.Y >= 700,
            "普通页应保留原生正文位置和选项宽度，并完整显示原本能容纳的内容。");
        var tall = Bounds(940);
        Require(tall.Position.Y < 255 && tall.Position.Y >= 96 && tall.Size.Y >= 940 && tall.End.Y <= 1056,
            "较长页面应先利用上方空余高度，不缩小字体或提前滚动。");
        var overflow = Bounds(1300);
        Require(overflow.Position.Y == 96 && overflow.Size.Y == 960,
            "真正超出屏幕的页面仍需滚动，不能把按钮放到屏幕外。");

        var drawer = assembly.GetType("STS2SkinChanger.Ui.CompendiumSidebarPolicy");
        Require(drawer != null, "右侧面板需要统一悬停策略，不能依赖子按钮的 MouseExited 收起。");
        float X(float viewport, float width, bool open) => (float)AccessTools.Method(drawer, "X").Invoke(null, [viewport, width, open])!;
        foreach (var width in new[] { 300f, 380f, 440f })
        {
            Require(Math.Abs(1920 - X(1920, width, false) - width * .1f) < .001f && X(1920, width, true) == 1920 - width,
                "收起只留面板宽度的 10%，展开回原位置，不能误用屏幕的 10%。");
        }
        bool Expand(bool mouse, bool popup, bool keyboard, bool allowed) => (bool)AccessTools.Method(drawer, "ShouldExpand")
            .Invoke(null, [mouse, popup, keyboard, allowed])!;
        Require(Expand(true, false, false, true) && Expand(false, true, false, true) && Expand(false, false, true, true) &&
                !Expand(false, false, false, true) && !Expand(true, true, true, false),
            "子项悬停、下拉菜单和键盘操作保持展开，离开收起，商店库存覆盖期间不能抢输入。");
        var controller = assembly.GetType("STS2SkinChanger.Ui.CompendiumSidebarDrawer", true)!;
        Require(Calls(AccessTools.Method(controller, "Connect"), typeof(Window), "add_WindowInput") &&
                Calls(AccessTools.Method(controller, "Disconnect"), typeof(Window), "remove_WindowInput"),
            "面板只在可见时监听原生输入，隐藏和退出必须停止监听，不增加永久逐帧轮询。");
        Console.WriteLine("Other compendium UI passed: event regions, full-height text and hover drawer policy/lifecycle.");
    }

    private static bool Calls(MethodBase method, Type type, string name) => PatchProcessor.GetOriginalInstructions(method)
        .Any(i => i.operand is MethodInfo m && m.DeclaringType == type && m.Name == name);
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
