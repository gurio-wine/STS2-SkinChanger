using System.Text.RegularExpressions;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Bindings.MegaSpine;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Events;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Potions;
using MegaCrit.Sts2.Core.Nodes.Relics;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Unlocks;
using STS2SkinChanger.Catalog;
using STS2SkinChanger.Core;

namespace STS2SkinChanger.Ui;

internal static class AncientCompendiumEntry
{
    private const string ButtonName = "STS2AncientCompendiumButton";
    private const string ScreenName = "STS2AncientCompendium";
    private const string OverlayName = "STS2AncientCompendiumEntryOverlay";
    private static readonly System.Reflection.FieldInfo StackField = AccessTools.Field(typeof(NSubmenu), "_stack");
    private static readonly ConditionalWeakTable<NCompendiumSubmenu, AttachmentState> AttachmentStates = new();
    private static NCompendiumBottomButton? _entryButton;

    public static void Attach(NCompendiumSubmenu compendium)
    {
        if (!GodotObject.IsInstanceValid(compendium))
        {
            return;
        }

        var state = AttachmentStates.GetOrCreateValue(compendium);
        var button = FindOwnButton(compendium) ?? state.Button;
        if (!GodotObject.IsInstanceValid(button))
        {
            button = null;
        }

        var nativeRow = compendium.GetNodeOrNull<HBoxContainer>("MarginContainer/VBoxContainer/BottomRow");
        var visibleAnchor = FindVisibleArchiveButton(compendium);
        var visibleHost = visibleAnchor == null
            ? null
            : FindInsertionHost(visibleAnchor, compendium);
        if (button == null)
        {
            if (nativeRow == null && visibleHost == null)
            {
                ScheduleDeferredAttach(compendium, state);
                return;
            }

            button = CreateButton(compendium);
            state.Button = button;
            if (nativeRow != null)
            {
                nativeRow.AddChild(button);
            }
            else
            {
                visibleHost!.AddChild(button);
            }
        }

        state.Button = button;
        _entryButton = button;
        if (!TryAttachToVisibleHost(compendium, button, visibleAnchor, visibleHost))
        {
            ScheduleDeferredAttach(compendium, state);
        }
    }

    private static NCompendiumBottomButton CreateButton(NCompendiumSubmenu compendium)
    {
        var scenePath = SceneHelper.GetScenePath("screens/main_menu/compendium_bottom_button");
        var button = PreloadManager.Cache.GetScene(scenePath)
            .Instantiate<NCompendiumBottomButton>(PackedScene.GenEditState.Disabled);
        button.Name = ButtonName;
        button.FocusMode = Control.FocusModeEnum.All;

        var buttonLabel = button.GetNodeOrNull<MegaLabel>("Label");
        buttonLabel?.SetTextAutoSize(ModLocalization.Get(ModText.OtherCompendium));
        if (buttonLabel != null)
        {
            ModLocalization.Bind(button, () =>
                buttonLabel.SetTextAutoSize(ModLocalization.Get(ModText.OtherCompendium)));
        }
        var icon = button.GetNodeOrNull<TextureRect>("Icon");
        if (icon != null)
        {
            icon.OffsetLeft = 70;
            icon.OffsetTop = 22;
            icon.OffsetRight = -70;
            icon.OffsetBottom = -62;
            icon.StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered;
            var firstAncient = GetAncients().FirstOrDefault();
            if (firstAncient != null)
            {
                icon.Texture = firstAncient.MapIcon;
            }
        }

        button.Connect(
            NClickableControl.SignalName.Released,
            Callable.From((Action<NButton>)(_ => Open(compendium))));
        return button;
    }

    private static bool TryAttachToVisibleHost(
        NCompendiumSubmenu compendium,
        NCompendiumBottomButton button,
        Node? visibleAnchor,
        Node? visibleHost)
    {
        if (!GodotObject.IsInstanceValid(button))
        {
            return false;
        }

        visibleAnchor ??= FindVisibleArchiveButton(compendium);
        visibleHost ??= visibleAnchor == null
            ? null
            : FindInsertionHost(visibleAnchor, compendium);
        if (visibleHost == null || !IsVisibleInTree(visibleHost))
        {
            // The vanilla layout may be the only valid host. It is often not visible during
            // _Ready, so defer until the submenu has entered the tree instead of declaring the
            // button lost.
            var nativeRow = compendium.GetNodeOrNull<HBoxContainer>("MarginContainer/VBoxContainer/BottomRow");
            if (nativeRow == null || !IsVisibleInTree(nativeRow))
            {
                return false;
            }

            button.Show();
            ConfigureFocus(compendium, button, nativeRow);
            return true;
        }

        var nativeHost = compendium.GetNodeOrNull<HBoxContainer>(
            "MarginContainer/VBoxContainer/BottomRow");
        if (visibleHost != nativeHost)
        {
            // A replacement compendium often fills its grid completely and clips any seventh
            // child added to that grid.  Keep the foreign layout intact and put our entry in an
            // un-clipped full-screen overlay instead.  This is also safer for custom hosts that
            // use fixed slots rather than a resizable Container.
            return TryAttachToOverlay(compendium, button, visibleAnchor);
        }

        var parent = button.GetParent();
        if (parent != visibleHost)
        {
            parent?.RemoveChild(button);
            visibleHost.AddChild(button);
        }

        button.Show();
        if (visibleAnchor?.GetParent() == visibleHost)
        {
            visibleHost.MoveChild(button, Math.Min(
                visibleAnchor.GetIndex() + 1,
                Math.Max(visibleHost.GetChildCount() - 1, 0)));

            // A custom page may use a plain Control instead of a Container.  Keep the entry
            // beside the visible archive tile in that case; otherwise a newly added child
            // would default to (0, 0) and appear to be missing under the page decorations.
            if (visibleHost is not Container &&
                visibleAnchor is Control anchorControl &&
                button is Control buttonControl)
            {
                buttonControl.Size = anchorControl.Size;
                buttonControl.Position = anchorControl.Position +
                                          new Vector2(anchorControl.Size.X + 24f, 0f);
            }
        }

        ConfigureFocus(compendium, button, visibleHost);
        return true;
    }

    private static bool TryAttachToOverlay(
        NCompendiumSubmenu compendium,
        NCompendiumBottomButton button,
        Node? visibleAnchor)
    {
        if (compendium is not Control compendiumControl || !compendiumControl.IsVisibleInTree())
        {
            return false;
        }

        var overlay = compendium.GetNodeOrNull<Control>(OverlayName);
        if (overlay == null)
        {
            overlay = new Control
            {
                Name = OverlayName,
                MouseFilter = Control.MouseFilterEnum.Ignore,
                ZIndex = 20
            };
            compendium.AddChild(overlay);
            overlay.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        }

        var parent = button.GetParent();
        if (parent != overlay)
        {
            parent?.RemoveChild(button);
            overlay.AddChild(button);
        }

        button.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopLeft);
        button.Show();
        button.Scale = new Vector2(0.68f, 0.68f);

        var buttonSize = button.Size;
        if (buttonSize.X <= 1f || buttonSize.Y <= 1f)
        {
            buttonSize = visibleAnchor is Control anchorControl && anchorControl.Size.X > 1f
                ? anchorControl.Size
                : new Vector2(260f, 110f);
            button.Size = buttonSize;
        }

        var archiveRect = GetVisibleArchiveRect(compendium);
        var viewportRect = compendium.GetViewport().GetVisibleRect();
        var effectiveSize = buttonSize * button.Scale;
        const float margin = 24f;
        var globalPosition = new Vector2(
            viewportRect.End.X - effectiveSize.X - margin,
            viewportRect.End.Y - effectiveSize.Y - margin);
        if (archiveRect != null)
        {
            var rightCandidate = new Vector2(
                archiveRect.Value.End.X + margin,
                archiveRect.Value.Position.Y);
            var belowCandidate = new Vector2(
                archiveRect.Value.Position.X,
                archiveRect.Value.End.Y + margin);
            if (rightCandidate.X + effectiveSize.X <= viewportRect.End.X - margin)
            {
                globalPosition = rightCandidate;
            }
            else if (belowCandidate.Y + effectiveSize.Y <= viewportRect.End.Y - margin)
            {
                globalPosition = belowCandidate;
            }
        }

        var overlayTransform = overlay.GetGlobalTransformWithCanvas();
        button.Position = overlayTransform.AffineInverse() * globalPosition;
        ConfigureOverlayFocus(button, visibleAnchor);
        return true;
    }

    private static Rect2? GetVisibleArchiveRect(Node root)
    {
        var controls = EnumerateDescendants(root)
            .Where(node => node is NCompendiumBottomButton or NShortSubmenuButton)
            .Where(node => !node.Name.ToString().Equals(ButtonName, StringComparison.Ordinal) &&
                           node is Control control && control.IsVisibleInTree())
            .OfType<Control>()
            .ToArray();
        if (controls.Length == 0)
        {
            return null;
        }

        var result = controls[0].GetGlobalRect();
        foreach (var control in controls.Skip(1))
        {
            result = result.Merge(control.GetGlobalRect());
        }

        return result;
    }

    private static void ConfigureOverlayFocus(
        NCompendiumBottomButton button,
        Node? visibleAnchor)
    {
        if (visibleAnchor is not Control anchor)
        {
            return;
        }

        button.FocusNeighborLeft = anchor.GetPath();
        anchor.FocusNeighborRight = button.GetPath();
    }

    private static void ConfigureFocus(
        NCompendiumSubmenu compendium,
        NCompendiumBottomButton button,
        Node host)
    {
        var controls = host.GetChildren()
            .OfType<Control>()
            .Where(control => control != button)
            .ToArray();
        var index = button.GetIndex();
        var previous = controls.LastOrDefault(control => control.GetIndex() < index);
        var next = controls.FirstOrDefault(control => control.GetIndex() > index);
        if (previous != null)
        {
            button.FocusNeighborLeft = previous.GetPath();
        }

        if (next != null)
        {
            button.FocusNeighborRight = next.GetPath();
        }

        var statistics = compendium.GetNodeOrNull<NCompendiumBottomButton>("%StatisticsButton");
        var runHistory = compendium.GetNodeOrNull<NCompendiumBottomButton>("%RunHistoryButton");
        if (statistics == null || runHistory == null)
        {
            return;
        }

        var bestiary = compendium.GetNodeOrNull<NShortSubmenuButton>("%BestiaryButton");
        statistics.FocusNeighborRight = button.GetPath();
        button.FocusNeighborLeft = statistics.GetPath();
        button.FocusNeighborRight = runHistory.GetPath();
        button.FocusNeighborTop = bestiary?.GetPath() ?? button.GetPath();
        button.FocusNeighborBottom = button.GetPath();
        runHistory.FocusNeighborLeft = button.GetPath();
        if (bestiary != null)
        {
            bestiary.FocusNeighborBottom = button.GetPath();
        }
    }

    private static void ScheduleDeferredAttach(
        NCompendiumSubmenu compendium,
        AttachmentState state)
    {
        if (state.DeferredScheduled || state.DeferredAttempts >= 12)
        {
            return;
        }

        state.DeferredScheduled = true;
        state.DeferredAttempts++;
        Callable.From(() =>
        {
            state.DeferredScheduled = false;
            if (GodotObject.IsInstanceValid(compendium))
            {
                Attach(compendium);
            }
        }).CallDeferred();
    }

    private static NCompendiumBottomButton? FindOwnButton(Node root) =>
        EnumerateDescendants(root)
            .OfType<NCompendiumBottomButton>()
            .FirstOrDefault(button => button.Name.ToString().Equals(ButtonName, StringComparison.Ordinal));

    private static Node? FindVisibleArchiveButton(Node root) =>
        EnumerateDescendants(root)
            .Where(node => node is NCompendiumBottomButton or NShortSubmenuButton)
            .Where(node => !node.Name.ToString().Equals(ButtonName, StringComparison.Ordinal) &&
                           IsVisibleInTree(node))
            .OrderBy(node => node.GetPath().ToString().Length)
            .FirstOrDefault();

    private static Node? FindInsertionHost(Node anchor, Node root)
    {
        var current = anchor.GetParent();
        while (current != null && current != root)
        {
            if (current is Container && IsVisibleInTree(current))
            {
                return current;
            }

            current = current.GetParent();
        }

        return anchor.GetParent() is { } parent && IsVisibleInTree(parent)
            ? parent
            : null;
    }

    private static bool IsVisibleInTree(Node node) =>
        node is CanvasItem canvasItem && canvasItem.IsVisibleInTree();

    private static IEnumerable<Node> EnumerateDescendants(Node root)
    {
        foreach (var child in root.GetChildren())
        {
            yield return child;
            foreach (var descendant in EnumerateDescendants(child))
            {
                yield return descendant;
            }
        }
    }

    private sealed class AttachmentState
    {
        public NCompendiumBottomButton? Button { get; set; }
        public int DeferredAttempts { get; set; }
        public bool DeferredScheduled { get; set; }
    }

    private static void Open(NCompendiumSubmenu compendium)
    {
        if (StackField.GetValue(compendium) is not NSubmenuStack stack)
        {
            ModLog.Error("无法取得图鉴菜单栈，其它图鉴未打开。");
            return;
        }

        OpenFromStack(stack);
    }

    internal static void OpenFromStack(NSubmenuStack stack)
    {
        if (!GodotObject.IsInstanceValid(stack))
        {
            ModLog.Error("无法取得图鉴菜单栈，其它图鉴未打开。");
            return;
        }

        var gallery = stack.GetNodeOrNull<AncientCompendiumScreen>(ScreenName);
        if (gallery == null)
        {
            gallery = new AncientCompendiumScreen
            {
                Name = ScreenName,
                Visible = false,
                MouseFilter = Control.MouseFilterEnum.Ignore
            };
            stack.AddChild(gallery);
        }

        stack.Push(gallery);
    }

    internal static AncientEventModel[] GetAncients() => ModelDb.AllAncients
        .Where(ancient => ResourceLoader.Exists(GetScenePath(ancient)))
        .DistinctBy(ancient => ancient.Id)
        .OrderBy(ancient => GetTitle(ancient), StringComparer.CurrentCultureIgnoreCase)
        .ToArray();

    internal static string GetScenePath(AncientEventModel ancient) =>
        "res://scenes/events/background_scenes/" + ancient.Id.Entry.ToLowerInvariant() + ".tscn";

    internal static string GetTitle(AncientEventModel ancient)
    {
        try
        {
            return ancient.Title.GetFormattedText();
        }
        catch
        {
            return ancient.Id.Entry.Replace('_', ' ').CapitalizeWords();
        }
    }

    internal static SkinGroup? FindGroup(string modelId)
    {
        var catalog = SkinService.Catalog;
        if (catalog == null)
        {
            return null;
        }

        var token = NormalizeToken(modelId);
        return catalog.Groups.FirstOrDefault(candidate => NormalizeToken(candidate.Id) == token);
    }

    internal static void ReplaceAncientIcon(
        AncientEventModel ancient,
        string resourcePath,
        ref Texture2D result)
    {
        var group = FindGroup(ancient.Id.Entry);
        if (group == null)
        {
            return;
        }

        try
        {
            // An external full-scene illustration does not imply ownership of the Ancient's
            // map/run-history icons. Resolve the requested icon normally: a PCK-backed skin that
            // actually supplies it wins, while an image-only provider falls back to the game icon.
            result = SkinService.GetOrLoadRuntimeResource(group.Id, resourcePath) as Texture2D ??
                     throw new InvalidOperationException($"独立先古皮肤资源不是贴图：{resourcePath}");
        }
        catch (Exception exception)
        {
            ModLog.Error($"最终接管先古头像 {resourcePath} 失败：{exception}");
        }
    }

    internal static void RefreshCompendiumEntryIcon(Node context)
    {
        // 优先用缓存的按钮引用，避免每次换肤都全树查找。
        var button = _entryButton;
        if (!GodotObject.IsInstanceValid(button))
        {
            button = context.GetTree().Root.FindChild(ButtonName, recursive: true, owned: false)
                as NCompendiumBottomButton;
            _entryButton = button;
        }

        var firstAncient = GetAncients().FirstOrDefault();
        if (button != null && firstAncient != null)
        {
            button.GetNode<TextureRect>("Icon").Texture = firstAncient.MapIcon;
        }
    }

    private static string NormalizeToken(string value) =>
        Regex.Replace(value, "[^a-zA-Z0-9]", string.Empty).ToLowerInvariant();
}

internal partial class AncientCompendiumScreen : NSubmenu
{
    private enum OtherCategory
    {
        Ancients,
        Merchants,
        Creatures
    }

    private sealed record OtherEntry(
        string Id,
        string Title,
        string ScenePath,
        string? GroupLookupId = null);

    private sealed record OtherPreviewLanguagePack(
        string OpenShop,
        string ShopPreview,
        string Hint,
        string Cards,
        string Relics,
        string Potions,
        string Item,
        string Standing,
        string Attack);

    private static readonly IReadOnlyDictionary<string, OtherPreviewLanguagePack>
        OtherPreviewPacks = new Dictionary<string, OtherPreviewLanguagePack>(StringComparer.OrdinalIgnoreCase)
        {
            ["eng"] = new("Open shop preview", "Shop preview", "A visual mockup only; nothing can be bought here.", "Cards", "Relics", "Potions", "Item {0}", "Standing", "Attack"),
            ["zhs"] = new("打开商店预览", "商店预览", "这里只是查看商店布局的模拟界面，不会购买或改变游戏内容。", "卡牌", "遗物", "药水", "商品 {0}", "站姿", "攻击"),
            ["zht"] = new("開啟商店預覽", "商店預覽", "這只是查看商店配置的模擬介面，不會購買或改變遊戲內容。", "卡牌", "遺物", "藥水", "商品 {0}", "站姿", "攻擊"),
            ["deu"] = new("Shop-Vorschau öffnen", "Shop-Vorschau", "Nur eine visuelle Vorschau; hier kann nichts gekauft werden.", "Karten", "Relikte", "Tränke", "Artikel {0}", "Stehen", "Angriff"),
            ["esp"] = new("Abrir vista de tienda", "Vista de tienda", "Solo es una maqueta visual; aquí no se puede comprar nada.", "Cartas", "Reliquias", "Pociones", "Objeto {0}", "De pie", "Ataque"),
            ["fra"] = new("Ouvrir l’aperçu de la boutique", "Aperçu de la boutique", "Ceci est une maquette visuelle ; aucun achat n’est possible ici.", "Cartes", "Reliques", "Potions", "Objet {0}", "Debout", "Attaque"),
            ["ita"] = new("Apri anteprima negozio", "Anteprima negozio", "Solo un modello visivo: qui non è possibile acquistare nulla.", "Carte", "Reliquie", "Pozioni", "Oggetto {0}", "In piedi", "Attacco"),
            ["jpn"] = new("ショッププレビューを開く", "ショッププレビュー", "これは見た目だけのプレビューです。ここでは購入できません。", "カード", "レリック", "ポーション", "商品 {0}", "立ち姿", "攻撃"),
            ["kor"] = new("상점 미리보기 열기", "상점 미리보기", "시각적 모형일 뿐이며 여기서는 아무것도 구매할 수 없습니다.", "카드", "유물", "물약", "상품 {0}", "서 있기", "공격"),
            ["pol"] = new("Otwórz podgląd sklepu", "Podgląd sklepu", "To tylko wizualna makieta; tutaj niczego nie można kupić.", "Karty", "Relikty", "Mikstury", "Przedmiot {0}", "Postawa", "Atak"),
            ["ptb"] = new("Abrir prévia da loja", "Prévia da loja", "Apenas uma simulação visual; nada pode ser comprado aqui.", "Cartas", "Relíquias", "Poções", "Item {0}", "Parado", "Ataque"),
            ["rus"] = new("Открыть предпросмотр магазина", "Предпросмотр магазина", "Это только визуальная имитация; покупать здесь нельзя.", "Карты", "Реликвии", "Зелья", "Товар {0}", "Стойка", "Атака"),
            ["spa"] = new("Abrir vista de tienda", "Vista de tienda", "Solo es una maqueta visual; aquí no se puede comprar nada.", "Cartas", "Reliquias", "Pociones", "Objeto {0}", "De pie", "Ataque"),
            ["tha"] = new("เปิดตัวอย่างร้านค้า", "ตัวอย่างร้านค้า", "เป็นเพียงหน้าจอจำลองเพื่อดูรูปแบบร้านค้า ไม่สามารถซื้อของได้", "การ์ด", "ของที่ระลึก", "โพชัน", "สินค้า {0}", "ท่ายืน", "โจมตี"),
            ["tur"] = new("Mağaza önizlemesini aç", "Mağaza önizlemesi", "Bu yalnızca görsel bir makettir; burada alışveriş yapılamaz.", "Kartlar", "Kalıntılar", "İksirler", "Eşya {0}", "Ayakta", "Saldırı")
        };

    private static OtherPreviewLanguagePack OtherPreviewText =>
        OtherPreviewPacks.TryGetValue(ModLocalization.CurrentLanguage, out var pack)
            ? pack
            : OtherPreviewPacks["eng"];

    private readonly Dictionary<AncientEventModel, Button> _entryButtons = [];
    private readonly Dictionary<string, Button> _otherEntryButtons =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<OtherCategory, Button> _categoryButtons = [];
    private VBoxContainer _entryList = null!;
    private Label _nameLabel = null!;
    private Label _epithetLabel = null!;
    private Label _headingLabel = null!;
    private HBoxContainer _skinSelector = null!;
    private OptionButton _skinDropdown = null!;
    private SubViewport _previewViewport = null!;
    private SubViewportContainer _previewContainer = null!;
    private Button _merchantClickArea = null!;
    private VBoxContainer _otherActionSelector = null!;
    private Control? _shopPreviewOverlay;
    private Node? _otherPreviewInstance;
    private string? _otherPreviewGroupId;
    private readonly List<(Button Button, string[] Aliases)> _otherActionButtons = [];
    private AncientEventModel? _selectedAncient;
    private OtherEntry? _selectedOther;
    private OtherCategory _selectedCategory = OtherCategory.Ancients;
    private int _otherPreviewRequest;
    private bool _updatingDropdown;
    private static readonly ConditionalWeakTable<MerchantInventory, object> PreviewInventories = new();
    private static readonly object PreviewInventoryMarker = new();

    protected override Control? InitialFocusedControl =>
        _categoryButtons.Values.FirstOrDefault() ?? _entryButtons.Values.FirstOrDefault();

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        BuildUi();
        ConnectSignals();
    }

    public override void OnSubmenuOpened()
    {
        base.OnSubmenuOpened();
        _previewViewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
        RefreshAncients();
    }

    public override void OnSubmenuClosed()
    {
        // Dynamic Ancient providers can register scene-tree-wide input hooks. Merely hiding the
        // compendium leaves their preview nodes alive, so those hooks keep receiving input after
        // the player returns to the compendium or enters a run. Release the preview scene at the
        // same lifecycle boundary as the submenu itself; reopening rebuilds the selected preview.
        ClearPreview();
        CloseSimulatedShopPreview();
        _merchantClickArea.Visible = false;
        _otherActionSelector.Visible = false;
        _previewViewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
        base.OnSubmenuClosed();
    }

    private void BuildUi()
    {
        _previewContainer = new SubViewportContainer
        {
            AnchorLeft = 0,
            AnchorTop = 0,
            AnchorRight = 1,
            AnchorBottom = 1,
            OffsetLeft = 0,
            OffsetTop = 0,
            OffsetRight = 0,
            OffsetBottom = 0,
            Stretch = true,
            MouseFilter = MouseFilterEnum.Ignore
        };
        AddChild(_previewContainer);

        _previewViewport = new SubViewport
        {
            Size = new Vector2I(1920, 1080),
            TransparentBg = false,
            GuiDisableInput = true,
            // 子菜单打开时切为 Always 以持续播放 Spine/AnimationPlayer；关闭时禁用。
            RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled
        };
        _previewContainer.AddChild(_previewViewport);

        // Merchant previews are visual-only nodes inside a SubViewport. A transparent
        // screen-level hit target makes the interaction reliable on both game versions and
        // lets the compendium show a shop-shaped mockup without forwarding clicks to the game.
        _merchantClickArea = new Button
        {
            Name = "MerchantPreviewClickArea",
            Visible = false,
            Flat = true,
            FocusMode = FocusModeEnum.None,
            MouseFilter = MouseFilterEnum.Stop,
            ZIndex = 4,
            TooltipText = OtherPreviewText.OpenShop
        };
        _merchantClickArea.SetAnchorsAndOffsetsPreset(LayoutPreset.TopLeft);
        _merchantClickArea.Position = new Vector2(570f, 170f);
        _merchantClickArea.Size = new Vector2(780f, 720f);
        _merchantClickArea.AddThemeStyleboxOverride(
            "normal",
            ContextualSkinControls.CreateStyleBox(Colors.Transparent, Colors.Transparent, 0));
        _merchantClickArea.AddThemeStyleboxOverride(
            "hover",
            ContextualSkinControls.CreateStyleBox(new Color(1f, 1f, 1f, 0.035f),
                new Color("efc85066"), 2));
        _merchantClickArea.Pressed += OpenSimulatedShopPreview;
        AddChild(_merchantClickArea);

        _nameLabel = BuildLabel(48, new Color("efc850"));
        _nameLabel.HorizontalAlignment = HorizontalAlignment.Left;
        _nameLabel.AnchorLeft = 0;
        _nameLabel.AnchorRight = 0;
        _nameLabel.OffsetLeft = 82;
        _nameLabel.OffsetTop = 900;
        _nameLabel.OffsetRight = 750;
        _nameLabel.OffsetBottom = 958;
        AddChild(_nameLabel);

        _epithetLabel = BuildLabel(24, new Color("87ceeB"));
        _epithetLabel.HorizontalAlignment = HorizontalAlignment.Left;
        _epithetLabel.AnchorLeft = 0;
        _epithetLabel.AnchorRight = 0;
        _epithetLabel.OffsetLeft = 86;
        _epithetLabel.OffsetTop = 958;
        _epithetLabel.OffsetRight = 750;
        _epithetLabel.OffsetBottom = 998;
        AddChild(_epithetLabel);

        _skinSelector = new HBoxContainer
        {
            AnchorLeft = 0,
            AnchorTop = 0,
            AnchorRight = 0,
            AnchorBottom = 0,
            OffsetLeft = 818,
            OffsetTop = 826,
            OffsetRight = 1102,
            OffsetBottom = 874,
            Visible = false,
            ZIndex = 10
        };
        AddChild(_skinSelector);

        _skinDropdown = new OptionButton
        {
            CustomMinimumSize = new Vector2(284, 48),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            FitToLongestItem = false,
            ClipText = true,
            Alignment = HorizontalAlignment.Center
        };
        ContextualSkinControls.ApplyGameTheme(_skinDropdown);
        _skinDropdown.ItemSelected += index => OnSkinSelected(checked((int)index));
        _skinSelector.AddChild(_skinDropdown);

        _otherActionSelector = new VBoxContainer
        {
            Name = "OtherPreviewActionSelector",
            Visible = false,
            ZIndex = 11
        };
        _otherActionSelector.AddThemeConstantOverride("separation", 10);
        _otherActionSelector.SetAnchorsAndOffsetsPreset(LayoutPreset.TopLeft);
        // 与怪物图鉴一致：只有确实存在的非默认动作才显示；异鸟宝宝默认进入站姿，
        // 不再提供一个重复的“站姿”按钮。
        _otherActionSelector.Position = new Vector2(650f, 430f);
        AddChild(_otherActionSelector);
        AddOtherActionButton(OtherPreviewText.Attack, ["attack", "attack1", "attack_1", "atk", "bite"], loop: false);

        var sidebar = new MarginContainer
        {
            AnchorLeft = 1,
            AnchorTop = 0,
            AnchorRight = 1,
            AnchorBottom = 1,
            OffsetLeft = -380,
            OffsetTop = 0,
            OffsetRight = 0,
            OffsetBottom = 0
        };
        AddChild(sidebar);

        sidebar.AddThemeConstantOverride("margin_left", 34);
        sidebar.AddThemeConstantOverride("margin_top", 58);
        sidebar.AddThemeConstantOverride("margin_right", 34);
        sidebar.AddThemeConstantOverride("margin_bottom", 90);

        var sidebarContent = new VBoxContainer();
        sidebarContent.AddThemeConstantOverride("separation", 22);
        sidebar.AddChild(sidebarContent);

        _headingLabel = BuildLabel(30, new Color("efc850"));
        _headingLabel.Text = ModLocalization.Get(ModText.OtherCompendium);
        _headingLabel.CustomMinimumSize = new Vector2(0, 54);
        sidebarContent.AddChild(_headingLabel);

        var divider = new HSeparator();
        divider.AddThemeConstantOverride("separation", 12);
        sidebarContent.AddChild(divider);

        var categoryRow = new HBoxContainer();
        categoryRow.AddThemeConstantOverride("separation", 8);
        AddCategoryButton(categoryRow, OtherCategory.Ancients, ModText.OtherCategoryAncients);
        AddCategoryButton(categoryRow, OtherCategory.Merchants, ModText.OtherCategoryMerchants);
        AddCategoryButton(categoryRow, OtherCategory.Creatures, ModText.OtherCategoryCreatures);
        sidebarContent.AddChild(categoryRow);

        var scroll = new ScrollContainer
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled
        };
        sidebarContent.AddChild(scroll);

        _entryList = new VBoxContainer
        {
            CustomMinimumSize = new Vector2(312, 0),
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        _entryList.AddThemeConstantOverride("separation", 10);
        scroll.AddChild(_entryList);

        var backButton = PreloadManager.Cache
            .GetScene(SceneHelper.GetScenePath("ui/back_button"))
            .Instantiate<NBackButton>(PackedScene.GenEditState.Disabled);
        backButton.Name = "BackButton";
        AddChild(backButton);
        ModLocalization.Bind(this, RefreshLocalizedText);
    }

    private void RefreshLocalizedText()
    {
        _headingLabel.Text = ModLocalization.Get(ModText.OtherCompendium);
        var previewText = OtherPreviewText;
        _merchantClickArea.TooltipText = previewText.OpenShop;
        if (_otherActionButtons.Count > 0)
        {
            _otherActionButtons[0].Button.Text = previewText.Attack;
        }
        foreach (var pair in _categoryButtons)
        {
            pair.Value.Text = GetCategoryText(pair.Key);
        }

        if (_selectedAncient == null && _selectedOther == null &&
            _entryButtons.Count == 0 && _otherEntryButtons.Count == 0)
        {
            _nameLabel.Text = ModLocalization.Get(ModText.NoAncientsAvailable);
        }

        if (_selectedAncient != null)
        {
            _nameLabel.Text = AncientCompendiumEntry.GetTitle(_selectedAncient);
        }
        else if (_selectedOther != null)
        {
            _nameLabel.Text = _selectedOther.Title;
        }

        var groupId = _skinDropdown.GetMeta("sts2_skin_group", string.Empty).AsString();
        if (!string.IsNullOrWhiteSpace(groupId))
        {
            PopulateSkinDropdown(SkinService.Catalog?.Groups.FirstOrDefault(group =>
                group.Id.Equals(groupId, StringComparison.OrdinalIgnoreCase)));
        }
    }

    private void AddOtherActionButton(string text, string[] animationAliases, bool loop)
    {
        var button = new Button
        {
            Text = text,
            CustomMinimumSize = new Vector2(104f, 44f),
            FocusMode = FocusModeEnum.All,
            Alignment = HorizontalAlignment.Center
        };
        ContextualSkinControls.ApplyGameTheme(button);
        button.Pressed += () =>
        {
            if (_otherPreviewInstance == null)
            {
                return;
            }

            ManagedAncientSceneAnimation.TryPlay(
                _otherPreviewInstance,
                _otherPreviewGroupId,
                animationAliases,
                loop);
        };
        _otherActionSelector.AddChild(button);
        _otherActionButtons.Add((button, animationAliases));
    }

    private void OpenSimulatedShopPreview()
    {
        try
        {
            OpenSimulatedShopPreviewCore();
        }
        catch (Exception exception)
        {
            // A provider may replace the inventory scene with a resource that cannot be
            // displayed outside a live run. Never leave the catalogue behind a dead black mask.
            ModLog.Error($"商店预览打开失败：{exception}");
            CloseSimulatedShopPreview();
        }
    }

    private void OpenSimulatedShopPreviewCore()
    {
        if (_selectedOther == null ||
            (!_selectedOther.Id.Equals("merchant", StringComparison.OrdinalIgnoreCase) &&
             !_selectedOther.Id.Equals("fake_merchant_monster", StringComparison.OrdinalIgnoreCase)) ||
            GodotObject.IsInstanceValid(_shopPreviewOverlay))
        {
            return;
        }

        _merchantClickArea.Visible = false;
        // Keep the merchant skin selector above the vanilla shop preview. It is still a
        // catalogue control, so changing the merchant skin should rebuild the merchant without
        // forcing the player to close the simulated shop first.
        _skinSelector.Visible = _skinDropdown.ItemCount > 0;
        _skinSelector.ZIndex = 90;
        _otherActionSelector.Visible = false;

        var overlay = new Control
        {
            Name = "VanillaShopPreview",
            MouseFilter = MouseFilterEnum.Stop,
            ZIndex = 80
        };
        overlay.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(overlay);
        _shopPreviewOverlay = overlay;

        var mask = new ColorRect
        {
            Color = new Color(0f, 0f, 0f, 0.48f),
            MouseFilter = MouseFilterEnum.Stop
        };
        mask.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        overlay.AddChild(mask);

        // Use the game's own merchant inventory scene and its normal Initialize path. The
        // catalogue supplies a disposable test player/run only because there is no live shop
        // model outside a run; every visual, hover tip, hand animation and inspect action is
        // still created by the same NMerchantInventory/NMerchantSlot code used in a real shop.
        var inventoryPath = _selectedOther.Id.Equals(
            "fake_merchant_monster", StringComparison.OrdinalIgnoreCase)
            ? "res://scenes/events/custom/fake_merchant_inventory.tscn"
            : "res://scenes/merchant/merchant_inventory.tscn";
        var group = FindOtherGroup(_selectedOther);
        var fakeMerchant = _selectedOther.Id.Equals(
            "fake_merchant_monster", StringComparison.OrdinalIgnoreCase);
        MerchantInventory model;
        try
        {
            // A real MerchantInventory requires a Player.RunState even though the catalogue is
            // outside a run. This transient test run never becomes NRun's active state and is
            // discarded with the overlay; it exists solely for the game's own FillSlot logic.
            var previewPlayer = Player.CreateForNewRun(
                ModelDb.Character<Ironclad>(),
                UnlockState.all,
                0x534b494e50525631UL);
            _ = RunState.CreateForTest(
                [previewPlayer],
                seed: "SkinChangerMerchantPreview");
            previewPlayer.Gold = 9999;
            model = fakeMerchant
                ? CreateFakeMerchantPreviewInventory(previewPlayer)
                : MerchantInventory.CreateForNormalMerchant(previewPlayer);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException("无法创建商店预览商品数据。", exception);
        }

        using (group == null
                   ? null
                   : SkinService.BeginRuntimeResourceScope(group.Id, inventoryPath))
        {
            NMerchantInventory inventory;
            if (group != null)
            {
                inventory = SkinService.InstantiateRuntimeScene<NMerchantInventory>(
                    group.Id,
                    inventoryPath);
            }
            else
            {
                var scene = ResourceLoader.Load<PackedScene>(
                    inventoryPath,
                    null,
                    ResourceLoader.CacheMode.IgnoreDeep) ??
                            throw new InvalidOperationException($"无法加载原版商店界面：{inventoryPath}");
                inventory = scene.Instantiate<NMerchantInventory>(PackedScene.GenEditState.Disabled);
            }

            inventory.Name = "VanillaMerchantInventory";
            inventory.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            overlay.AddChild(inventory);
            inventory.Initialize(
                model,
                fakeMerchant ? FakeMerchant.Dialogue : MerchantRoom.Dialogue);
            PreviewInventories.Add(model, PreviewInventoryMarker);
            inventory.Connect(
                NMerchantInventory.SignalName.InventoryClosed,
                Callable.From(CloseSimulatedShopPreview));

            // NMerchantInventory.Open normally applies these exact end values after its tween.
            // Calling Open here would mutate the real merchant FTUE/screen context, so keep the
            // native layout and hand/hover process without those run-level side effects.
            var slots = inventory.GetNodeOrNull<Control>("%SlotsContainer") ??
                        inventory.GetNodeOrNull<Control>("SlotsContainer");
            if (slots != null)
            {
                slots.Position = new Vector2(slots.Position.X, 80f);
            }

            var backstop = inventory.GetNodeOrNull<CanvasItem>("Backstop");
            if (backstop != null)
            {
                backstop.Modulate = new Color(1f, 1f, 1f, 0.8f);
            }

            var backButton = inventory.GetNodeOrNull<NBackButton>("%BackButton") ??
                             inventory.GetNodeOrNull<NBackButton>("BackButton");
            backButton?.Enable();

            var removal = inventory.GetNodeOrNull<NMerchantCardRemoval>("%MerchantCardRemoval") ??
                          inventory.GetNodeOrNull<NMerchantCardRemoval>("MerchantCardRemoval");
            if (removal != null)
            {
                // Card removal has a separate purchase wrapper. Keep its native artwork but make
                // the purchase-only target inert in the catalogue.
                removal.MouseFilter = Control.MouseFilterEnum.Ignore;
                removal.FocusMode = FocusModeEnum.None;
                removal.Hitbox.MouseFilter = Control.MouseFilterEnum.Ignore;
            }
        }
    }

    private static MerchantInventory CreateFakeMerchantPreviewInventory(Player player)
    {
        var inventory = new MerchantInventory(player);
        // FakeMerchant keeps its six relics in a private encounter table.  The catalogue only
        // needs the same native six-slot layout, so use the first six canonical relics as
        // disposable preview entries; their visuals, hover tips and costs still come from the
        // game's NMerchantRelic/MerchantRelicEntry implementation.
        foreach (var relic in ModelDb.AllRelics
                     .OrderBy(relic => relic.Id.Entry, StringComparer.OrdinalIgnoreCase)
                     .Take(6))
        {
            inventory.AddRelicEntry(new MerchantRelicEntry(relic.ToMutable(), player));
        }

        return inventory;
    }

    internal static bool IsPreviewInventory(MerchantInventory? inventory) =>
        inventory != null && PreviewInventories.TryGetValue(inventory, out _);

    private static void PreparePreviewShopInteraction(Node root)
    {
        foreach (var node in EnumerateNodeTree(root))
        {
            // All game scripts were removed by StripPreviewScripts, so re-enable the native UI
            // process path. This keeps hover/focus/animation behaviour alive without allowing a
            // MerchantEntry to touch the run state or attempt a purchase.
            node.ProcessMode = ProcessModeEnum.Inherit;
            if (node is Control control)
            {
                if (control.Name.ToString().Equals("InputBlocker", StringComparison.OrdinalIgnoreCase))
                {
                    control.Visible = false;
                    control.MouseFilter = Control.MouseFilterEnum.Ignore;
                    continue;
                }

                // The inventory itself is visual-only; item interaction is supplied by the
                // transparent buttons added in FillPreviewShopSlots. Do not let a stripped
                // child consume input before those buttons see it.
                control.MouseFilter = Control.MouseFilterEnum.Ignore;
                control.FocusMode = Control.FocusModeEnum.None;
            }
        }
    }

    private static Node StripPreviewScripts(Node root)
    {
        // SetScript disposes the managed wrapper for that node. Snapshot every instance id
        // first, then reacquire each wrapper independently; otherwise the next GetChildren call
        // observes a disposed NMerchantInventory/NBackButton and the whole preview aborts.
        var rootInstanceId = root.GetInstanceId();
        var nodes = new[] { root }
            .Concat(EnumerateNodeTree(root))
            .Where(node => GodotObject.IsInstanceValid(node))
            .Select(node => node.GetInstanceId())
            .ToArray();
        foreach (var instanceId in nodes.Reverse())
        {
            if (GodotObject.InstanceFromId(instanceId) is Node node &&
                GodotObject.IsInstanceValid(node))
            {
                node.SetScript(default(Variant));
            }
        }

        return GodotObject.InstanceFromId(rootInstanceId) as Node ??
               throw new InvalidOperationException("商店预览根节点无法重新获取。");
    }

    private static void FillPreviewShopSlots(Control inventory, bool fakeMerchant)
    {
        var categories = fakeMerchant
            ? new[] { ("Relics", "遗物") }
            : new[]
            {
                ("CharacterCards", "卡牌"),
                ("ColorlessCards", "卡牌"),
                ("Relics", "遗物"),
                ("Potions", "药水")
            };
        var allCards = ModelDb.AllCards
            .OrderBy(card => card.Id.Entry, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var firstCharacterCard = allCards.FirstOrDefault(card => !IsColorlessCard(card))
            ?? allCards.FirstOrDefault();
        var firstColorlessCard = allCards.FirstOrDefault(IsColorlessCard)
            ?? firstCharacterCard;
        var firstRelic = ModelDb.AllRelics
            .OrderBy(relic => relic.Id.Entry, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        var firstPotion = ModelDb.AllPotions
            .OrderBy(potion => potion.Id.Entry, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        foreach (var (containerName, labelText) in categories)
        {
            var container = inventory.GetNodeOrNull<Control>($"SlotsContainer/{containerName}");
            if (container == null)
            {
                continue;
            }

            foreach (var slot in container.GetChildren().OfType<Control>())
            {
                var holder = slot.GetNodeOrNull<Control>("CardHolder") ??
                             slot.GetNodeOrNull<Control>("RelicHolder") ??
                             slot.GetNodeOrNull<Control>("PotionHolder");
                if (holder == null || holder.GetNodeOrNull<Label>("SkinChangerPreviewItem") != null)
                {
                    continue;
                }

                // In a live shop NCard/NRelic/NPotion creates the visual child and sizes its
                // holder during FillSlot. Use the first model of the corresponding type for each
                // slot, but do not create a MerchantEntry: the resulting preview can be hovered
                // and inspected while a purchase is impossible by construction.
                if (holder.Name.ToString().Equals("CardHolder", StringComparison.OrdinalIgnoreCase))
                {
                    holder.Position = new Vector2(-150f, -211f);
                    holder.Size = new Vector2(300f, 422f);
                }
                else if (holder.Name.ToString().Equals("RelicHolder", StringComparison.OrdinalIgnoreCase))
                {
                    holder.Position = new Vector2(-64f, -80f);
                    holder.Size = new Vector2(128f, 128f);
                }
                else
                {
                    holder.Position = new Vector2(-40f, -40f);
                    holder.Size = new Vector2(80f, 80f);
                }

                switch (containerName)
                {
                    case "CharacterCards" when firstCharacterCard != null:
                        AddPreviewCard(holder, firstCharacterCard);
                        break;
                    case "ColorlessCards" when firstColorlessCard != null:
                        AddPreviewCard(holder, firstColorlessCard);
                        break;
                    case "Relics" when firstRelic != null:
                        AddPreviewRelic(holder, firstRelic);
                        break;
                    case "Potions" when firstPotion != null:
                        AddPreviewPotion(holder, firstPotion);
                        break;
                    default:
                        AddPreviewItemLabel(holder, labelText);
                        break;
                }
            }
        }
    }

    private static bool IsColorlessCard(CardModel card)
    {
        try
        {
            return card.Pool.Title.Equals("Colorless", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // A malformed third-party card should not prevent the merchant catalogue from
            // opening. Treat it as a character card and let the next valid model be selected.
            return false;
        }
    }

    private static void AddPreviewCard(Control holder, CardModel canonicalCard)
    {
        try
        {
            var card = NCard.Create(canonicalCard.ToMutable());
            if (card == null)
            {
                AddPreviewItemLabel(holder, "卡牌");
                return;
            }

            card.MouseFilter = Control.MouseFilterEnum.Ignore;
            holder.AddChild(card);
            card.Position = Vector2.Zero;
            card.Scale = Vector2.One;
            card.UpdateVisuals(PileType.None, CardPreviewMode.Normal);
            AddPreviewInspectButton(holder, () =>
            {
                try
                {
                    if (NGame.Instance != null && card.Model != null)
                    {
                        NGame.Instance.GetInspectCardScreen().Open([card.Model], 0);
                    }
                }
                catch (Exception exception)
                {
                    ModLog.Warn($"打开商店预览卡牌失败：{exception.Message}");
                }
            });
        }
        catch (Exception exception)
        {
            ModLog.Warn($"创建商店预览卡牌失败：{exception.Message}");
            AddPreviewItemLabel(holder, "卡牌");
        }
    }

    private static void AddPreviewRelic(Control holder, RelicModel canonicalRelic)
    {
        try
        {
            var relic = NRelic.Create(canonicalRelic.ToMutable(), NRelic.IconSize.Large);
            if (relic == null)
            {
                AddPreviewItemLabel(holder, "遗物");
                return;
            }

            relic.MouseFilter = Control.MouseFilterEnum.Ignore;
            holder.AddChild(relic);
            relic.Position = Vector2.Zero;
            relic.Size = new Vector2(128f, 128f);
            AddPreviewInspectButton(holder, () =>
            {
                try
                {
                    if (NGame.Instance != null)
                    {
                        NGame.Instance.GetInspectRelicScreen().Open([relic.Model], relic.Model);
                    }
                }
                catch (Exception exception)
                {
                    ModLog.Warn($"打开商店预览遗物失败：{exception.Message}");
                }
            });
        }
        catch (Exception exception)
        {
            ModLog.Warn($"创建商店预览遗物失败：{exception.Message}");
            AddPreviewItemLabel(holder, "遗物");
        }
    }

    private static void AddPreviewPotion(Control holder, PotionModel canonicalPotion)
    {
        try
        {
            var potion = NPotion.Create(canonicalPotion.ToMutable());
            if (potion == null)
            {
                AddPreviewItemLabel(holder, "药水");
                return;
            }

            potion.MouseFilter = Control.MouseFilterEnum.Ignore;
            holder.AddChild(potion);
            potion.Position = Vector2.Zero;
            AddPreviewInspectButton(holder, () =>
            {
                // The game has no standalone potion-inspect screen. Keep the normal shop
                // hover surface active; the potion itself remains a real NPotion visual.
            });
        }
        catch (Exception exception)
        {
            ModLog.Warn($"创建商店预览药水失败：{exception.Message}");
            AddPreviewItemLabel(holder, "药水");
        }
    }

    private static void AddPreviewItemLabel(Control holder, string text)
    {
        if (holder.GetNodeOrNull<Label>("SkinChangerPreviewItem") != null)
        {
            return;
        }

        var item = new Label
        {
            Name = "SkinChangerPreviewItem",
            Text = text,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Modulate = new Color(1f, 1f, 1f, 0.9f)
        };
        item.AddThemeFontSizeOverride("font_size", 28);
        item.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        holder.AddChild(item);
    }

    private static void AddPreviewInspectButton(Control holder, Action pressed)
    {
        var button = new Button
        {
            Name = "SkinChangerPreviewInspect",
            Flat = true,
            MouseFilter = Control.MouseFilterEnum.Stop,
            FocusMode = Control.FocusModeEnum.All,
            ZIndex = 2
        };
        button.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        button.AddThemeStyleboxOverride(
            "normal",
            ContextualSkinControls.CreateStyleBox(Colors.Transparent, Colors.Transparent, 0));
        button.AddThemeStyleboxOverride(
            "hover",
            ContextualSkinControls.CreateStyleBox(new Color(1f, 1f, 1f, 0.04f),
                new Color("efc85066"), 1));
        button.Pressed += pressed;
        holder.AddChild(button);
    }

    private static IEnumerable<Node> EnumerateNodeTree(Node root)
    {
        foreach (var child in root.GetChildren())
        {
            yield return child;
            foreach (var descendant in EnumerateNodeTree(child))
            {
                yield return descendant;
            }
        }
    }

    private void CloseSimulatedShopPreview()
    {
        if (GodotObject.IsInstanceValid(_shopPreviewOverlay))
        {
            _shopPreviewOverlay!.Visible = false;
            _shopPreviewOverlay!.QueueFree();
        }

        _shopPreviewOverlay = null;
        _skinSelector.ZIndex = 10;
        RefreshAuxiliaryPreviewControls();
    }

    private void RefreshAuxiliaryPreviewControls()
    {
        _skinSelector.ZIndex = _shopPreviewOverlay == null ? 10 : 90;
        var merchant = _selectedOther != null &&
                       (_selectedOther.Id.Equals("merchant", StringComparison.OrdinalIgnoreCase) ||
                        _selectedOther.Id.Equals("fake_merchant_monster", StringComparison.OrdinalIgnoreCase));
        _merchantClickArea.Visible = merchant && _shopPreviewOverlay == null;
        _otherActionSelector.Visible = _selectedOther?.Id.Equals("byrdpip", StringComparison.OrdinalIgnoreCase) == true &&
                                       _shopPreviewOverlay == null;
    }

    private void RefreshAncients()
    {
        foreach (var child in _entryList.GetChildren())
        {
            _entryList.RemoveChild(child);
            child.QueueFree();
        }

        _entryButtons.Clear();
        _otherEntryButtons.Clear();
        if (_selectedCategory == OtherCategory.Ancients)
        {
            var ancients = AncientCompendiumEntry.GetAncients();
            foreach (var ancient in ancients)
            {
                var button = CreateEntryButton(AncientCompendiumEntry.GetTitle(ancient));
                button.Pressed += () => SelectAncient(ancient);
                _entryList.AddChild(button);
                _entryButtons[ancient] = button;
            }

            if (ancients.Length == 0)
            {
                ShowEmptyCategory();
                return;
            }

            var selection = _selectedAncient == null
                ? ancients[0]
                : ancients.FirstOrDefault(candidate => candidate.Id == _selectedAncient.Id) ??
                  ancients[0];
            SelectAncient(selection);
            return;
        }

        var entries = GetOtherEntries(_selectedCategory);
        foreach (var entry in entries)
        {
            var button = CreateEntryButton(entry.Title);
            button.Pressed += () => SelectOther(entry);
            _entryList.AddChild(button);
            _otherEntryButtons[entry.Id] = button;
        }

        if (entries.Length == 0)
        {
            ShowEmptyCategory();
            return;
        }

        var otherSelection = _selectedOther != null &&
                             entries.Any(entry => entry.Id.Equals(
                                 _selectedOther.Id,
                                 StringComparison.OrdinalIgnoreCase))
            ? entries.First(entry => entry.Id.Equals(
                _selectedOther.Id,
                StringComparison.OrdinalIgnoreCase))
            : entries[0];
        SelectOther(otherSelection);
    }

    private Button CreateEntryButton(string title)
    {
        var button = new Button
        {
            Text = title,
            CustomMinimumSize = new Vector2(312, 58),
            FocusMode = FocusModeEnum.All,
            Alignment = HorizontalAlignment.Left,
            Flat = true
        };
        ApplyEntryTheme(button, selected: false);
        return button;
    }

    private void AddCategoryButton(
        HBoxContainer row,
        OtherCategory category,
        ModText text)
    {
        var button = new Button
        {
            Text = ModLocalization.Get(text),
            CustomMinimumSize = new Vector2(96, 44),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            FocusMode = FocusModeEnum.All,
            Alignment = HorizontalAlignment.Center,
            Flat = true
        };
        button.Pressed += () => SelectCategory(category);
        _categoryButtons[category] = button;
        row.AddChild(button);
        ApplyCategoryTheme(button, category == _selectedCategory);
    }

    private string GetCategoryText(OtherCategory category) => category switch
    {
        OtherCategory.Ancients => ModLocalization.Get(ModText.OtherCategoryAncients),
        OtherCategory.Merchants => ModLocalization.Get(ModText.OtherCategoryMerchants),
        OtherCategory.Creatures => ModLocalization.Get(ModText.OtherCategoryCreatures),
        _ => string.Empty
    };

    private void SelectCategory(OtherCategory category)
    {
        if (_selectedCategory == category && _entryList.GetChildCount() > 0)
        {
            return;
        }

        _selectedCategory = category;
        _selectedAncient = null;
        _selectedOther = null;
        _otherPreviewRequest++;
        foreach (var pair in _categoryButtons)
        {
            ApplyCategoryTheme(pair.Value, pair.Key == category);
        }
        RefreshAncients();
    }

    private void ShowEmptyCategory()
    {
        _nameLabel.Text = ModLocalization.Get(ModText.NoAncientsAvailable);
        _epithetLabel.Text = string.Empty;
        _skinSelector.Visible = false;
        _merchantClickArea.Visible = false;
        _otherActionSelector.Visible = false;
        ClearPreview();
    }

    private static OtherEntry[] GetOtherEntries(OtherCategory category) => category switch
    {
        OtherCategory.Merchants =>
        [
            new OtherEntry(
                "merchant",
                GetLocalizedTitle("map", "LEGEND_MERCHANT.title", "商人"),
                // v0.107 embeds NMerchantButton in merchant_room.tscn; v0.111 also ships a
                // standalone merchant_button scene. The room scene exists in both versions
                // and lets the preview use the same resource graph on either branch.
                "res://scenes/rooms/merchant_room.tscn"),
            new OtherEntry(
                "fake_merchant_monster",
                GetLocalizedTitle("events", "FAKE_MERCHANT.title", "商人？？？"),
                // The standalone fake-merchant button is beta-only; the event scene is shared
                // by formal and beta and still contains the MerchantButton node.
                "res://scenes/events/custom/fake_merchant.tscn")
        ],
        OtherCategory.Creatures =>
        [
            new OtherEntry(
                "byrdpip",
                GetLocalizedTitle("monsters", "BYRDPIP.name", "异鸟宝宝"),
                "res://scenes/creature_visuals/byrdpip.tscn")
        ],
        _ => []
    };

    private static string GetLocalizedTitle(string table, string key, string fallback)
    {
        try
        {
            return new LocString(table, key).GetFormattedText();
        }
        catch
        {
            return fallback;
        }
    }

    private void SelectOther(OtherEntry entry)
    {
        CloseSimulatedShopPreview();
        _otherPreviewRequest++;
        _selectedAncient = null;
        _selectedOther = entry;
        _nameLabel.Text = entry.Title;
        _epithetLabel.Text = string.Empty;
        foreach (var pair in _otherEntryButtons)
        {
            ApplyEntryTheme(
                pair.Value,
                pair.Key.Equals(entry.Id, StringComparison.OrdinalIgnoreCase));
        }

        PopulateSkinDropdown(FindOtherGroup(entry));
        RebuildOtherPreview(entry);
        RefreshAuxiliaryPreviewControls();
    }

    private static SkinGroup? FindOtherGroup(OtherEntry entry)
    {
        var catalog = SkinService.Catalog;
        if (catalog == null)
        {
            return null;
        }

        var lookup = entry.GroupLookupId ?? entry.Id;
        return catalog.Groups.FirstOrDefault(group =>
            group.Id.Equals(lookup, StringComparison.OrdinalIgnoreCase));
    }

    private static void ApplyCategoryTheme(Button button, bool selected)
    {
        var ivory = new Color("fff6e2");
        var gold = new Color("efc850");
        button.AddThemeColorOverride("font_color", selected ? gold : ivory);
        button.AddThemeColorOverride("font_hover_color", Colors.White);
        button.AddThemeFontSizeOverride("font_size", 19);
        button.AddThemeStyleboxOverride(
            "normal",
            ContextualSkinControls.CreateStyleBox(
                Colors.Transparent,
                Colors.Transparent,
                0));
        button.AddThemeStyleboxOverride(
            "hover",
            ContextualSkinControls.CreateStyleBox(new Color("3c627e44"), Colors.Transparent, 0));
        button.AddThemeStyleboxOverride(
            "pressed",
            ContextualSkinControls.CreateStyleBox(Colors.Transparent, gold, 1));
        button.AddThemeStyleboxOverride(
            "focus",
            ContextualSkinControls.CreateStyleBox(Colors.Transparent, gold, 1));
        var font = ContextualSkinControls.GameFont;
        if (font != null)
        {
            button.AddThemeFontOverride("font", font);
        }
    }

    private void SelectAncient(AncientEventModel ancient)
    {
        CloseSimulatedShopPreview();
        _selectedCategory = OtherCategory.Ancients;
        _selectedAncient = ancient;
        _selectedOther = null;
        _nameLabel.Text = AncientCompendiumEntry.GetTitle(ancient);
        try
        {
            _epithetLabel.Text = ancient.Epithet.GetFormattedText();
        }
        catch
        {
            _epithetLabel.Text = string.Empty;
        }

        foreach (var pair in _entryButtons)
        {
            ApplyEntryTheme(pair.Value, pair.Key.Id == ancient.Id);
        }

        PopulateSkinDropdown(AncientCompendiumEntry.FindGroup(ancient.Id.Entry));
        RebuildPreview(ancient);
        RefreshAuxiliaryPreviewControls();
    }

    private void PopulateSkinDropdown(SkinGroup? group)
    {
        _updatingDropdown = true;
        _skinDropdown.Clear();
        if (group == null || group.Options.Count == 0)
        {
            _skinSelector.Visible = false;
            _updatingDropdown = false;
            return;
        }

        _skinDropdown.AddItem(ModLocalization.Get(ModText.GameDefault));
        _skinDropdown.SetItemMetadata(0, SkinCatalog.BaseOptionId);
        foreach (var option in group.Options)
        {
            var index = _skinDropdown.ItemCount;
            _skinDropdown.AddItem(ModLocalization.DisplayOptionName(option.Name));
            _skinDropdown.SetItemMetadata(index, option.Id);
        }

        var current = SkinService.Config.GetSelection(group.Id);
        var selectedIndex = Enumerable.Range(0, _skinDropdown.ItemCount)
            .FirstOrDefault(index => _skinDropdown.GetItemMetadata(index).AsString()
                .Equals(current, StringComparison.OrdinalIgnoreCase));
        _skinDropdown.Select(selectedIndex);
        _skinDropdown.SetMeta("sts2_skin_group", group.Id);
        _updatingDropdown = false;
        _skinSelector.Visible = true;
    }

    private void OnSkinSelected(int index)
    {
        if (_updatingDropdown || (_selectedAncient == null && _selectedOther == null))
        {
            return;
        }

        var groupId = _skinDropdown.GetMeta("sts2_skin_group", string.Empty).AsString();
        var optionId = _skinDropdown.GetItemMetadata(index).AsString();
        if (!SkinService.ApplySelection(groupId, optionId))
        {
            ModLog.Error($"其它图鉴皮肤切换失败：{SkinService.LastError}");
            PopulateSkinDropdown(_selectedAncient != null
                ? AncientCompendiumEntry.FindGroup(_selectedAncient.Id.Entry)
                : FindOtherGroup(_selectedOther!));
            return;
        }

        if (_selectedAncient != null)
        {
            AncientCompendiumEntry.RefreshCompendiumEntryIcon(this);
            var ancient = _selectedAncient;
            Callable.From(() => RebuildPreview(ancient)).CallDeferred();
        }
        else if (_selectedOther != null)
        {
            var entry = _selectedOther;
            var request = ++_otherPreviewRequest;
            var expectedOption = optionId;
            var reopenShop = GodotObject.IsInstanceValid(_shopPreviewOverlay);
            Callable.From(() =>
            {
                // Multiple dropdown clicks can queue several deferred rebuilds. An old rebuild
                // must never put an earlier merchant/creature resource back over the latest
                // selection.
                if (request != _otherPreviewRequest ||
                    _selectedOther == null ||
                    !_selectedOther.Id.Equals(entry.Id, StringComparison.OrdinalIgnoreCase) ||
                    !SkinService.Config.GetSelection(groupId).Equals(
                        expectedOption,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                RebuildOtherPreview(entry);
                if (reopenShop)
                {
                    // The simulated shop is a separate vanilla scene overlay. Recreate it
                    // after a skin change so its hand, merchant-specific textures, and item
                    // previews follow the same selection as the catalogue model underneath.
                    CloseSimulatedShopPreview();
                    OpenSimulatedShopPreview();
                }
            }).CallDeferred();
        }
    }

    private void RebuildOtherPreview(OtherEntry entry)
    {
        try
        {
            ClearPreview();
            _otherPreviewInstance = null;
            _otherPreviewGroupId = null;
            var group = FindOtherGroup(entry);
            // PackedScene external resources (notably Spine skeleton data) can resolve lazily
            // during Instantiate. Keep the selected provider overlay mounted for the complete
            // load+instantiate operation so a previous skin cannot leak its skeleton into this
            // preview. v0.111 has standalone merchant button scenes; v0.107 embeds the same
            // button in the room/event scene, so try the standalone resource first and fall back
            // to the shared scene without assuming either version.
            var previewScene = InstantiateOtherPreviewScene(entry, group);
            Node instance = previewScene.Instance;
            if (entry.Id.Equals("merchant", StringComparison.OrdinalIgnoreCase))
            {
                // Keep NMerchantButton itself instead of extracting only MerchantVisual. ATA
                // and other DLL-backed merchant skins replace the skeleton from that node's
                // _Ready; extracting the child bypassed their generic initialization and left
                // the default/ATA preview empty. The detached button is still isolated from the
                // shop room, but its normal game and provider setup remains intact.
                instance = ExtractPreviewNode(instance, "MerchantButton");
            }
            else if (entry.Id.Equals("fake_merchant_monster", StringComparison.OrdinalIgnoreCase))
            {
                instance = ExtractPreviewNode(instance, "MerchantButton", "FakeMerchantButton");
            }
            else if (entry.Id.Equals("byrdpip", StringComparison.OrdinalIgnoreCase))
            {
                // Byrdpip's root is NCreatureVisuals. That root expects a combat creature and
                // can overwrite a provider's skeleton during _Ready. The authored Visuals node
                // already contains the selected Spine resource, so preview it in isolation.
                instance = ExtractPreviewNode(instance, "Visuals", "SpineSprite");
            }

            // Keep the authored MerchantButton/FakeMerchantButton name. Several provider DLLs
            // use the node path/name to distinguish the real shop from the fake merchant; giving
            // both roots a generic compendium name makes a fake/normal skin choose the wrong
            // branch even though the Spine resource itself loaded correctly.
            if (!entry.Id.Equals("merchant", StringComparison.OrdinalIgnoreCase) &&
                !entry.Id.Equals("fake_merchant_monster", StringComparison.OrdinalIgnoreCase))
            {
                instance.Name = "OtherCompendiumPreview";
            }
            instance.ProcessMode = ProcessModeEnum.Always;
            // Adding NMerchantButton to the tree runs its normal _Ready callback. Keep the
            // selected overlay mounted through that callback, then replay the selected
            // provider's isolated NMerchantButton presentation hook (ATA changes its skeleton
            // there instead of replacing merchant_button.tscn).
            using (group == null
                       ? null
                       : SkinService.BeginRuntimeResourceScope(group.Id, previewScene.ScenePath))
            {
                _previewViewport.AddChild(instance);
                if (entry.Id.Equals("merchant", StringComparison.OrdinalIgnoreCase) ||
                    entry.Id.Equals("fake_merchant_monster", StringComparison.OrdinalIgnoreCase))
                {
                    var providerId = group == null
                        ? null
                        : SkinService.GetSelectedFullRuntimeProvider(group.Id);
                    if (providerId != null)
                    {
                        ManagedSkinModLoader.ReplaySelectedNodeReadyBehavior(providerId, instance);
                    }
                }
            }
            _otherPreviewInstance = instance;
            _otherPreviewGroupId = group?.Id;
            if (instance is Control control)
            {
                var isMerchant = entry.Id.Equals("merchant", StringComparison.OrdinalIgnoreCase);
                var isFakeMerchant = entry.Id.Equals("fake_merchant_monster", StringComparison.OrdinalIgnoreCase);
                if (isMerchant || isFakeMerchant)
                {
                    // MerchantButton/FakeMerchantButton already use the game's center anchors
                    // and authored offsets. Re-centering the root a second time moves the
                    // MerchantVisual far outside the viewport (and was the reason both the
                    // default and ATA merchant disappeared). Preserve that native layout and
                    // only apply the catalogue size adjustment.
                    control.Scale *= isMerchant ? 0.72f : 1.00f;
                }
                else
                {
                    control.SetAnchorsAndOffsetsPreset(LayoutPreset.Center);
                    control.Position = new Vector2(960f, 540f);
                    control.Scale *= 1.35f;
                }
                control.MouseFilter = MouseFilterEnum.Ignore;
            }
            else if (instance is Node2D node)
            {
                node.Position = new Vector2(960f, 600f);
                var previewMultiplier = entry.Id.Equals("merchant", StringComparison.OrdinalIgnoreCase)
                    ? 0.72f
                    : entry.Id.Equals("fake_merchant_monster", StringComparison.OrdinalIgnoreCase)
                        ? 1.00f
                    : 1.35f;
                // Keep the scale authored by the scene/provider. Replacing it with a unit
                // scale was what made the merchant fill the whole compendium on some skins.
                node.Scale *= previewMultiplier;
            }

            // Creature scenes do not get NCreature's normal animation setup when previewed in
            // isolation. Reuse the same generic Spine starter used by Ancient previews so a
            // Byrdpip/merchant skin with an idle loop remains animated in the compendium.
            ManagedAncientSceneAnimation.TryStart(group?.Id, instance);
            ManagedAncientSceneAnimation.ConfigureActions(
                instance,
                group?.Id,
                _otherActionButtons);
            if (entry.Id.Equals("merchant", StringComparison.OrdinalIgnoreCase) ||
                entry.Id.Equals("fake_merchant_monster", StringComparison.OrdinalIgnoreCase))
            {
                // NMerchantButton normally starts this animation from _Ready. Explicitly select
                // the neutral pose as well, because provider hooks may replace the skeleton or
                // leave its initial track empty after the preview is detached from a room.
                ManagedAncientSceneAnimation.TryPlay(
                    instance,
                    group?.Id,
                    ["idle_loop", "idle", "stand", "standing", "default", "animation"],
                    loop: true);
            }
            else if (entry.Id.Equals("byrdpip", StringComparison.OrdinalIgnoreCase))
            {
                // The creature catalogue opens in its neutral standing pose, matching the
                // monster bestiary. Attack remains an optional action when the selected Spine
                // asset actually exposes one.
                ManagedAncientSceneAnimation.TryPlay(
                    instance,
                    group?.Id,
                    ["idle_loop", "idle", "stand", "standing"],
                    loop: true);
            }
            RefreshAuxiliaryPreviewControls();

            ModLog.Info($"其它图鉴已展示 {entry.Id}。");
        }
        catch (Exception exception)
        {
            ModLog.Error($"其它图鉴预览 {entry.Id} 失败：{exception}");
        }
    }

    private static (Node Instance, string ScenePath) InstantiateOtherPreviewScene(
        OtherEntry entry,
        SkinGroup? group)
    {
        var scenePaths = entry.Id.Equals("merchant", StringComparison.OrdinalIgnoreCase)
            // The standalone button is the smallest common scene and gives provider DLLs the
            // same _Ready hook as the live shop. v0.107 does not ship it, so the room scene is a
            // fallback for that branch.
            ? new[] { "res://scenes/rooms/merchant_button.tscn", entry.ScenePath }
            : entry.Id.Equals("fake_merchant_monster", StringComparison.OrdinalIgnoreCase)
                ? new[] { "res://scenes/events/custom/fake_merchant_button.tscn", entry.ScenePath }
                : new[] { entry.ScenePath };
        Exception? lastException = null;
        foreach (var scenePath in scenePaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (group != null)
                {
                    // Use the runtime alias path even for the game's default option. Directly
                    // loading the canonical scene after a provider switch lets Godot reuse the
                    // previous skin's cached Spine dependency, which is why default merchant and
                    // Byrdpip previews could alternate between two skins.
                    return (SkinService.InstantiateRuntimeScene<Node>(group.Id, scenePath), scenePath);
                }

                var scene = ResourceLoader.Load<PackedScene>(
                    scenePath,
                    null,
                    ResourceLoader.CacheMode.ReplaceDeep);
                if (scene != null)
                {
                    return (scene.Instantiate(PackedScene.GenEditState.Disabled), scenePath);
                }

                lastException = new InvalidOperationException($"无法加载其它图鉴场景：{scenePath}");
            }
            catch (Exception exception)
            {
                lastException = exception;
            }
        }

        throw new InvalidOperationException(
            $"无法加载其它图鉴场景：{entry.Id}。",
            lastException);
    }

    private static Node ExtractPreviewNode(Node root, params string[] names)
    {
        // The beta/formal builds differ here: the standalone merchant scene's root is already
        // named MerchantButton (or FakeMerchantButton), while the formal fallback embeds that
        // same node below MerchantRoom/FakeMerchant. FindChild does not include the root itself,
        // so treating only descendants as targets left standalone normal/ATA previews fully
        // transparent (their authored root uses self_modulate = 0).
        var target = names.Any(name => root.Name.ToString().Equals(
                                           name,
                                           StringComparison.OrdinalIgnoreCase))
            ? root
            : names
                .Select(name => root.FindChild(name, recursive: true, owned: false))
                .FirstOrDefault(node => node != null);
        if (target == null || target.GetParent() == null)
        {
            if (ReferenceEquals(target, root))
            {
                ReownDetachedScene(target);
                MakePreviewCanvasVisible(target);
            }
            return root;
        }

        target.GetParent().RemoveChild(target);
        // The merchant button is authored transparent because the real room supplies a separate
        // focus overlay. We are displaying it directly, so clear that state and re-own its
        // unique-name children after detaching from the room. Re-owning is important for the
        // game's NMerchantButton and provider patches, which resolve %MerchantVisual.
        ReownDetachedScene(target);
        MakePreviewCanvasVisible(target);

        root.Free();
        return target;
    }

    private static void MakePreviewCanvasVisible(Node root)
    {
        static void Reveal(Node node)
        {
            if (node is not CanvasItem canvasItem)
            {
                return;
            }

            canvasItem.Visible = true;
            canvasItem.Modulate = Colors.White;
            canvasItem.SelfModulate = Colors.White;
        }

        Reveal(root);
        // Do not reveal every child: MerchantSelectionReticle, HotkeyIcon, and provider helper
        // nodes are intentionally hidden until their own interaction state enables them. Only
        // the actual rendered model needs its inherited transparent state cleared.
        foreach (var name in new[] { "MerchantVisual", "Visuals", "SpineSprite" })
        {
            var visual = root.FindChild(name, recursive: true, owned: false);
            if (visual != null)
            {
                Reveal(visual);
            }
        }
    }

    private static void ReownDetachedScene(Node node)
    {
        node.Owner = null;
        foreach (var child in node.GetChildren().OfType<Node>())
        {
            child.Owner = node;
            ReownDescendantOwnership(child, node);
        }
    }

    private static void ReownDescendantOwnership(Node node, Node owner)
    {
        foreach (var child in node.GetChildren().OfType<Node>())
        {
            child.Owner = owner;
            ReownDescendantOwnership(child, owner);
        }
    }

    private void RebuildPreview(AncientEventModel ancient)
    {
        try
        {
            ClearPreview();
            var scenePath = AncientCompendiumEntry.GetScenePath(ancient);
            var group = AncientCompendiumEntry.FindGroup(ancient.Id.Entry);
            var interactive = group != null &&
                              SkinService.IsInteractiveRuntimeProviderSelected(group.Id);
            _previewContainer.MouseFilter = interactive
                ? MouseFilterEnum.Pass
                : MouseFilterEnum.Ignore;
            _previewViewport.GuiDisableInput = !interactive;
            PackedScene scene;
            if (group != null && SkinService.IsExternalRuntimeProviderSelected(group.Id))
            {
                scene = BuildSelectedRuntimeImageScene(group.Id);
            }
            else if (group != null)
            {
                scene = SkinService.LoadRuntimeScene(group.Id, scenePath);
            }
            else
            {
                scene = ResourceLoader.Load<PackedScene>(scenePath, null, ResourceLoader.CacheMode.IgnoreDeep)
                        ?? throw new InvalidOperationException($"无法加载先古场景：{scenePath}");
            }

            var preview = scene.Instantiate<Control>(PackedScene.GenEditState.Disabled);
            preview.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            var previewHost = new Control
            {
                Name = "PreviewHost",
                MouseFilter = MouseFilterEnum.Pass
            };
            previewHost.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            _previewViewport.AddChild(previewHost);
            previewHost.AddChild(preview);
            ManagedAncientLayeredImage.TryApply(group?.Id, preview);
            ManagedAncientSceneAnimation.TryStart(group?.Id, preview);
            ModLog.Info($"其它图鉴已展示先古 {ancient.Id.Entry}。");
        }
        catch (Exception exception)
        {
            ModLog.Error($"其它图鉴预览先古 {ancient.Id.Entry} 失败：{exception}");
        }
    }

    internal static PackedScene BuildSelectedRuntimeImageScene(string groupId)
    {
        var root = new Control { Name = "RuntimeAncientBackground" };
        root.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        ManagedAncientStaticBackground.Mark(root);
        var image = new TextureRect
        {
            Name = "Image",
            Texture = SkinService.GetSelectedRuntimeImageTexture(groupId),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
            MouseFilter = MouseFilterEnum.Ignore
        };
        image.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        root.AddChild(image);
        image.Owner = root;

        var scene = new PackedScene();
        var error = scene.Pack(root);
        root.Free();
        if (error != Error.Ok)
        {
            throw new InvalidOperationException($"无法创建先古图片场景：{error}");
        }

        return scene;
    }

    private void ClearPreview()
    {
        _otherPreviewInstance = null;
        _otherPreviewGroupId = null;
        if (_merchantClickArea != null)
        {
            _merchantClickArea.Visible = false;
        }
        if (_otherActionSelector != null)
        {
            _otherActionSelector.Visible = false;
        }
        _previewContainer.MouseFilter = MouseFilterEnum.Ignore;
        _previewViewport.GuiDisableInput = true;
        foreach (var child in _previewViewport.GetChildren())
        {
            // Some provider input routers inspect CanvasItem.Visible rather than
            // IsVisibleInTree(). Hide and stop the subtree before detaching it so there is no
            // one-frame window in which an old interactive preview can still consume input.
            if (child is CanvasItem canvasItem)
            {
                canvasItem.Visible = false;
            }

            child.ProcessMode = ProcessModeEnum.Disabled;
            _previewViewport.RemoveChild(child);
            child.QueueFree();
        }
    }

    private static Label BuildLabel(int fontSize, Color color)
    {
        var label = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore
        };
        label.AddThemeColorOverride("font_color", color);
        label.AddThemeColorOverride("font_outline_color", new Color("332f27"));
        label.AddThemeConstantOverride("outline_size", fontSize >= 34 ? 10 : 5);
        label.AddThemeFontSizeOverride("font_size", fontSize);
        var font = ContextualSkinControls.GameFont;
        if (font != null)
        {
            label.AddThemeFontOverride("font", font);
        }

        return label;
    }

    private static void ApplyEntryTheme(Button button, bool selected)
    {
        var ivory = new Color("fff6e2");
        var gold = new Color("efc850");
        button.AddThemeColorOverride("font_color", selected ? gold : ivory);
        button.AddThemeColorOverride("font_hover_color", Colors.White);
        button.AddThemeColorOverride("font_pressed_color", gold);
        button.AddThemeFontSizeOverride("font_size", 24);
        var font = ContextualSkinControls.GameFont;
        if (font != null)
        {
            button.AddThemeFontOverride("font", font);
        }

        button.AddThemeStyleboxOverride(
            "normal",
            ContextualSkinControls.CreateStyleBox(
                Colors.Transparent,
                Colors.Transparent,
                0));
        button.AddThemeStyleboxOverride(
            "hover",
            ContextualSkinControls.CreateStyleBox(new Color("3c627e44"), Colors.Transparent, 0));
        button.AddThemeStyleboxOverride(
            "pressed",
            ContextualSkinControls.CreateStyleBox(Colors.Transparent, gold, 1));
        button.AddThemeStyleboxOverride(
            "focus",
            ContextualSkinControls.CreateStyleBox(Colors.Transparent, gold, 1));
    }

}

[HarmonyPatch(typeof(MerchantEntry), nameof(MerchantEntry.OnTryPurchaseWrapper))]
internal static class AncientCompendiumPreviewPurchaseGuardPatch
{
    private static bool Prefix(MerchantInventory? inventory, ref Task<bool> __result)
    {
        if (!AncientCompendiumScreen.IsPreviewInventory(inventory))
        {
            return true;
        }

        // Keep the native NMerchantCard/NMerchantRelic/NMerchantPotion interaction surface, but
        // make every catalogue entry read-only so a click can never spend gold or mutate a run.
        __result = Task.FromResult(false);
        return false;
    }
}

internal static class ManagedAncientLayeredImage
{
    private const string CoverNodeName = "SkinChangerAncientBackgroundCover";
    private const string CharacterNodeName = "SkinChangerAncientCharacter";
    private const string CharacterMaskShaderCode = """
        shader_type canvas_item;
        uniform sampler2D mask_texture;

        void fragment() {
            vec4 base_color = texture(TEXTURE, UV);
            vec4 mask_color = texture(mask_texture, UV);
            float mask_value = mask_color.a * dot(mask_color.rgb, vec3(0.299, 0.587, 0.114));
            COLOR = vec4(base_color.rgb, base_color.a * mask_value);
        }
        """;

    public static void TryApply(string? groupId, Node sceneRoot)
    {
        if (string.IsNullOrWhiteSpace(groupId))
        {
            return;
        }

        try
        {
            var layers = SkinService.GetSelectedAncientLayeredImageTextures(groupId);
            if (layers == null)
            {
                return;
            }

            var placeholderMarker = groupId + "_placeholder";
            var target = DescendantsAndSelf(sceneRoot)
                .OfType<TextureRect>()
                .FirstOrDefault(textureRect =>
                    textureRect.Texture?.ResourcePath.Contains(
                        placeholderMarker,
                        StringComparison.OrdinalIgnoreCase) == true);
            if (target?.GetParent() is not Node parent)
            {
                ModLog.Warn($"先古图层皮肤 {groupId} 找不到原场景占位图。");
                return;
            }

            RemoveOldLayer(parent, CoverNodeName);
            RemoveOldLayer(parent, CharacterNodeName);

            var insertIndex = target.GetIndex() + 1;
            if (layers.BackgroundCover != null)
            {
                var cover = DuplicateLayer(target, CoverNodeName, layers.BackgroundCover);
                cover.Material = null;
                parent.AddChild(cover);
                parent.MoveChild(cover, insertIndex++);
            }

            var character = DuplicateLayer(target, CharacterNodeName, layers.Character);
            if (layers.Mask != null)
            {
                var shader = new Shader { Code = CharacterMaskShaderCode };
                var material = new ShaderMaterial { Shader = shader };
                material.SetShaderParameter("mask_texture", layers.Mask);
                character.Material = material;
            }
            else
            {
                character.Material = null;
            }

            parent.AddChild(character);
            parent.MoveChild(character, insertIndex);
            ModLog.Info($"已应用 {groupId} 的代码型先古图层皮肤。");
        }
        catch (Exception exception)
        {
            ModLog.Warn($"应用 {groupId} 的先古图层皮肤失败：{exception.Message}");
        }
    }

    private static TextureRect DuplicateLayer(
        TextureRect source,
        string name,
        Texture2D texture)
    {
        var layer = source.Duplicate() as TextureRect ??
                    throw new InvalidOperationException("无法复制先古场景占位图节点。");
        layer.Name = name;
        layer.Texture = texture;
        return layer;
    }

    private static void RemoveOldLayer(Node parent, string name)
    {
        var existing = parent.GetNodeOrNull<Node>(name);
        existing?.Free();
    }

    private static IEnumerable<Node> DescendantsAndSelf(Node root)
    {
        yield return root;
        foreach (Node child in root.GetChildren())
        {
            foreach (var descendant in DescendantsAndSelf(child))
            {
                yield return descendant;
            }
        }
    }
}

internal static class ManagedAncientSceneAnimation
{
    public static void TryStart(string? groupId, Node sceneRoot)
    {
        if (string.IsNullOrWhiteSpace(groupId) ||
            (!SkinService.IsManagedResourceOptionSelected(groupId) &&
             SkinService.GetSelectedFullRuntimeProvider(groupId) == null))
        {
            return;
        }

        // Most ancient scenes call the node SpineSprite, while creature/merchant previews use
        // names such as Visuals or MerchantVisual. Resolve by class as a fallback and include
        // the root itself when a single SpineSprite was extracted from a larger scene.
        var spineNode = FindSpineNode(sceneRoot);
        if (spineNode == null)
        {
            return;
        }

        try
        {
            var sprite = new MegaSprite(spineNode);
            sceneRoot.RunWhenSpineReady(sprite, animationState =>
                StartDefaultAnimation(groupId, sprite, animationState));
        }
        catch (Exception exception)
        {
            ModLog.Warn($"准备 {groupId} 的先古 Spine 动画失败：{exception.Message}");
        }
    }

    public static void TryPlay(
        Node sceneRoot,
        string? groupId,
        IReadOnlyList<string> aliases,
        bool loop)
    {
        var spineNode = FindSpineNode(sceneRoot);
        if (spineNode == null)
        {
            ModLog.Warn($"{groupId ?? "其它图鉴"} 没有可播放的 Spine 节点。");
            return;
        }

        try
        {
            var sprite = new MegaSprite(spineNode);
            sceneRoot.RunWhenSpineReady(sprite, animationState =>
            {
                try
                {
                    var animationNames = sprite.GetSkeleton()?.GetData()?.GetAnimationNames();
                    var animation = animationNames == null
                        ? null
                        : aliases.Select(alias => FindAnimation(animationNames, alias))
                            .FirstOrDefault(candidate => candidate != null);
                    // Some skin providers keep the authored animation but rename their neutral
                    // loop. Only the idle/standing action may use a generic fallback; attack
                    // buttons must stay hidden when no attack animation was supplied.
                    if (animation == null && aliases.Any(IsIdleAlias))
                    {
                        animation = animationNames?.FirstOrDefault(name =>
                            !name.Equals("Dummy", StringComparison.OrdinalIgnoreCase) &&
                            !name.StartsWith("Touch_", StringComparison.OrdinalIgnoreCase));
                    }
                    if (animation == null)
                    {
                        ModLog.Warn($"{groupId ?? "其它图鉴"} 没有匹配动作：{string.Join(", ", aliases)}");
                        return;
                    }

                    SetAnimationCompat(animationState, animation, loop);
                    if (!loop)
                    {
                        var idle = animationNames == null
                            ? null
                            : FindAnimation(animationNames, "idle_loop") ??
                              FindAnimation(animationNames, "idle") ??
                              FindAnimation(animationNames, "stand") ??
                              FindAnimation(animationNames, "standing") ??
                              FindAnimation(animationNames, "default") ??
                              FindAnimation(animationNames, "animation");
                        if (idle != null)
                        {
                            AddAnimationCompat(animationState, idle, delay: 0f, loop: true);
                        }
                    }

                    ModLog.Info($"已播放 {groupId ?? "其它图鉴"} 动作：{animation}");
                }
                catch (Exception exception)
                {
                    ModLog.Warn($"播放 {groupId ?? "其它图鉴"} 动作失败：{exception.Message}");
                }
            });
        }
        catch (Exception exception)
        {
            ModLog.Warn($"准备 {groupId ?? "其它图鉴"} 动作失败：{exception.Message}");
        }
    }

    public static void ConfigureActions(
        Node sceneRoot,
        string? groupId,
        IReadOnlyList<(Button Button, string[] Aliases)> actions)
    {
        foreach (var action in actions)
        {
            action.Button.Visible = false;
        }

        var spineNode = FindSpineNode(sceneRoot);
        if (spineNode == null)
        {
            return;
        }

        try
        {
            var sprite = new MegaSprite(spineNode);
            sceneRoot.RunWhenSpineReady(sprite, _ =>
            {
                var names = sprite.GetSkeleton()?.GetData()?.GetAnimationNames();
                if (names == null)
                {
                    return;
                }

                foreach (var action in actions)
                {
                    action.Button.Visible = action.Aliases.Any(alias =>
                        FindAnimation(names, alias) != null);
                }
            });
        }
        catch (Exception exception)
        {
            ModLog.Warn($"检查 {groupId ?? "其它图鉴"} 动作失败：{exception.Message}");
        }
    }

    private static Node? FindSpineNode(Node sceneRoot) =>
        sceneRoot.GetNodeOrNull<Node>("SpineSprite") ??
        sceneRoot.FindChild("SpineSprite", recursive: true, owned: false) ??
        (sceneRoot.GetClass().ToString().Equals("SpineSprite", StringComparison.Ordinal)
            ? sceneRoot
            : sceneRoot.GetChildren()
                .OfType<Node>()
                .SelectMany(DescendantsAndSelf)
                .FirstOrDefault(node => node.GetClass().ToString()
                    .Equals("SpineSprite", StringComparison.Ordinal)));

    private static void StartDefaultAnimation(
        string groupId,
        MegaSprite sprite,
        MegaAnimationState animationState)
    {
        try
        {
            var animationNames = sprite.GetSkeleton()?.GetData()?.GetAnimationNames();
            if (animationNames == null || animationNames.Count == 0)
            {
                return;
            }

            // 两个支持版本都提供这个值类型入口，并由各自版本负责
            // MegaTrackEntry 的正确释放方式。
            var currentName = animationState.GetCurrentAnimationName(0);
            if (!string.IsNullOrWhiteSpace(currentName) &&
                animationNames.Any(name =>
                    name.Equals(currentName, StringComparison.OrdinalIgnoreCase)) &&
                !currentName.Equals("Dummy", StringComparison.OrdinalIgnoreCase) &&
                !currentName.StartsWith("Touch_", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var intro = FindAnimation(animationNames, "Intro");
            var idle = FindAnimation(animationNames, "Idle");
            if (intro != null)
            {
                SetAnimationCompat(animationState, intro, loop: false);
                if (idle != null)
                {
                    AddAnimationCompat(animationState, idle, delay: 0f, loop: true);
                }

                ModLog.Info($"已启动 {groupId} 的先古 Spine 动画：{intro}" +
                            (idle == null ? string.Empty : $" -> {idle}"));
                return;
            }

            var loopAnimation = idle ??
                                FindAnimation(animationNames, "animation") ??
                                FindAnimation(animationNames, "default") ??
                                animationNames.FirstOrDefault(name =>
                                    !name.Equals("Dummy", StringComparison.OrdinalIgnoreCase) &&
                                    !name.StartsWith("Touch_", StringComparison.OrdinalIgnoreCase));
            if (loopAnimation == null)
            {
                return;
            }

            SetAnimationCompat(animationState, loopAnimation, loop: true);
            ModLog.Info($"已启动 {groupId} 的先古 Spine 循环动画：{loopAnimation}");
        }
        catch (Exception exception)
        {
            ModLog.Warn($"启动 {groupId} 的先古 Spine 动画失败：{exception.Message}");
        }
    }

    private static void SetAnimationCompat(
        MegaAnimationState animationState,
        string animationName,
        bool loop)
    {
        // SetAnimation 在 0.107.1 返回 MegaTrackEntry，到 0.111.0 改为 void。
        // 两版底层 Spine 方法签名不变，直接调用它可避免发布 DLL 绑定某一版。
        using var result = animationState.BoundObject.Call(
            "set_animation", animationName, loop, 0);
    }

    private static void AddAnimationCompat(
        MegaAnimationState animationState,
        string animationName,
        float delay,
        bool loop)
    {
        // AddAnimation 也发生了相同的返回类型变化。
        using var result = animationState.BoundObject.Call(
            "add_animation", animationName, delay, loop, 0);
    }

    private static string? FindAnimation(
        IReadOnlyList<string> animationNames,
        string expectedName) =>
        animationNames.FirstOrDefault(name =>
            name.Equals(expectedName, StringComparison.OrdinalIgnoreCase));

    private static bool IsIdleAlias(string alias) =>
        alias.Equals("idle_loop", StringComparison.OrdinalIgnoreCase) ||
        alias.Equals("idle", StringComparison.OrdinalIgnoreCase) ||
        alias.Equals("stand", StringComparison.OrdinalIgnoreCase) ||
        alias.Equals("standing", StringComparison.OrdinalIgnoreCase) ||
        alias.Equals("default", StringComparison.OrdinalIgnoreCase) ||
        alias.Equals("animation", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<Node> DescendantsAndSelf(Node root)
    {
        yield return root;
        foreach (Node child in root.GetChildren())
        {
            foreach (var descendant in DescendantsAndSelf(child))
            {
                yield return descendant;
            }
        }
    }
}

internal static class AncientRuntimeAppearance
{
    private static readonly System.Reflection.FieldInfo AncientEventField =
        AccessTools.Field(typeof(NAncientEventLayout), "_ancientEvent");

    internal static bool TryGetCurrent(
        out AncientEventModel ancient,
        out NAncientEventLayout layout,
        out SkinGroup group)
    {
        ancient = null!;
        layout = null!;
        group = null!;
        if (NEventRoom.Instance?.Layout is not NAncientEventLayout currentLayout ||
            !TryGet(currentLayout, out var currentAncient, out var currentGroup))
        {
            return false;
        }

        ancient = currentAncient;
        layout = currentLayout;
        group = currentGroup;
        return true;
    }

    internal static bool TryGet(
        NAncientEventLayout layout,
        out AncientEventModel ancient,
        out SkinGroup group)
    {
        ancient = null!;
        group = null!;
        if (AncientEventField.GetValue(layout) is not AncientEventModel currentAncient)
        {
            return false;
        }

        var currentGroup = AncientCompendiumEntry.FindGroup(currentAncient.Id.Entry);
        if (currentGroup == null)
        {
            return false;
        }

        ancient = currentAncient;
        group = currentGroup;
        return true;
    }

    internal static Control? GetBackgroundTarget(NAncientEventLayout layout) =>
        layout.GetNodeOrNull<Control>("%AncientBgContainer");

    internal static bool TryRefresh(string groupId, out string? error)
    {
        error = null;
        if (!TryGetCurrent(out var ancient, out var layout, out var group) ||
            !group.Id.Equals(groupId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var container = layout.GetNodeOrNull<Node>("%AncientBgContainer");
        if (container == null)
        {
            error = "Ancient background container is unavailable";
            return false;
        }

        var oldRoots = container.GetChildren().Cast<Node>().ToArray();
        Node? newRoot = null;
        try
        {
            newRoot = ancient.CreateBackgroundScene()
                .Instantiate<Control>(PackedScene.GenEditState.Disabled);
            if (oldRoots.Length > 0)
            {
                newRoot.Name = oldRoots[0].Name;
            }

            foreach (var oldRoot in oldRoots)
            {
                container.RemoveChild(oldRoot);
            }

            container.AddChild(newRoot);
            ManagedAncientStaticBackground.Fit(newRoot);
            ManagedAncientLayeredImage.TryApply(group.Id, newRoot);
            ManagedAncientSceneAnimation.TryStart(group.Id, newRoot);
            foreach (var oldRoot in oldRoots)
            {
                oldRoot.QueueFree();
            }

            return true;
        }
        catch (Exception exception)
        {
            if (newRoot != null && GodotObject.IsInstanceValid(newRoot))
            {
                newRoot.GetParent()?.RemoveChild(newRoot);
                newRoot.QueueFree();
            }

            foreach (var oldRoot in oldRoots.Where(GodotObject.IsInstanceValid))
            {
                if (oldRoot.GetParent() == null)
                {
                    container.AddChild(oldRoot);
                }
            }

            error = exception.GetBaseException().Message;
            ModLog.Error("热重载当前先古场景失败：" + exception);
            return false;
        }
    }
}

internal static class ManagedAncientStaticBackground
{
    private const string ManagedRootMeta = "sts2_skin_changer_static_ancient_background";

    internal static void Mark(Control root) => root.SetMeta(ManagedRootMeta, true);

    internal static void Fit(Node sceneRoot)
    {
        if (sceneRoot is not Control root ||
            !root.HasMeta(ManagedRootMeta) ||
            !root.GetMeta(ManagedRootMeta).AsBool())
        {
            return;
        }

        root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        if (root.GetParent() is not NAncientBgContainer container)
        {
            root.OffsetLeft = 0f;
            root.OffsetTop = 0f;
            root.OffsetRight = 0f;
            root.OffsetBottom = 0f;
            return;
        }

        // NAncientBgContainer deliberately scales and shifts the game's authored 2560x1200
        // scenes for each window ratio. A generated full-rect image would be scaled a second
        // time and expose the black room background. Expand it through the inverse container
        // transform so its visible result still covers the complete event layout.
        var scale = container.Scale;
        if (Mathf.IsZeroApprox(scale.X) || Mathf.IsZeroApprox(scale.Y))
        {
            return;
        }

        var pivot = container.PivotOffset;
        var position = container.Position;
        var size = container.Size;
        var topLeft = new Vector2(
            pivot.X + (-position.X - pivot.X) / scale.X,
            pivot.Y + (-position.Y - pivot.Y) / scale.Y);
        var bottomRight = new Vector2(
            pivot.X + (size.X - position.X - pivot.X) / scale.X,
            pivot.Y + (size.Y - position.Y - pivot.Y) / scale.Y);

        root.OffsetLeft = topLeft.X;
        root.OffsetTop = topLeft.Y;
        root.OffsetRight = bottomRight.X - size.X;
        root.OffsetBottom = bottomRight.Y - size.Y;
    }

    internal static void FitChildren(NAncientBgContainer container)
    {
        foreach (Node child in container.GetChildren())
        {
            Fit(child);
        }
    }
}

[HarmonyPatch(typeof(NAncientEventLayout), "InitializeVisuals")]
internal static class ManagedAncientSceneAnimationPatch
{
    private static void Postfix(NAncientEventLayout __instance)
    {
        try
        {
            if (!AncientRuntimeAppearance.TryGet(__instance, out _, out var group))
            {
                return;
            }

            var container = __instance.GetNodeOrNull<Node>("%AncientBgContainer");
            var sceneRoot = container?.GetChildCount() > 0 ? container.GetChild(0) : null;
            if (sceneRoot != null)
            {
                ManagedAncientStaticBackground.Fit(sceneRoot);
                ManagedAncientLayeredImage.TryApply(group.Id, sceneRoot);
                ManagedAncientSceneAnimation.TryStart(group.Id, sceneRoot);
            }
        }
        catch (Exception exception)
        {
            ModLog.Warn("在游戏内启动先古 Spine 动画失败：" + exception.Message);
        }
    }
}

[HarmonyPatch(typeof(NAncientBgContainer), "OnWindowChange")]
internal static class ManagedAncientStaticBackgroundWindowPatch
{
    private static void Postfix(NAncientBgContainer __instance) =>
        ManagedAncientStaticBackground.FitChildren(__instance);
}

[HarmonyPatch(typeof(EventModel), nameof(EventModel.CreateBackgroundScene))]
internal static class AncientSceneResultPatch
{
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(EventModel __instance, ref PackedScene __result)
    {
        if (__instance is not AncientEventModel ancient)
        {
            return;
        }

        var group = AncientCompendiumEntry.FindGroup(ancient.Id.Entry);
        if (group == null)
        {
            return;
        }

        try
        {
            if (SkinService.IsExternalRuntimeProviderSelected(group.Id))
            {
                __result = AncientCompendiumScreen.BuildSelectedRuntimeImageScene(group.Id);
                return;
            }

            var scenePath = AncientCompendiumEntry.GetScenePath(ancient);
            var scene = SkinService.GetOrLoadRuntimeScene(group.Id, scenePath);
            __result = scene;
        }
        catch (Exception exception)
        {
            ModLog.Error($"最终应用 {ancient.Id.Entry} 的先古皮肤失败：{exception}");
        }
    }
}

[HarmonyPatch(typeof(AncientEventModel), nameof(AncientEventModel.MapIcon), MethodType.Getter)]
internal static class AncientMapIconResultPatch
{
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(AncientEventModel __instance, ref Texture2D __result)
    {
        var id = __instance.Id.Entry.ToLowerInvariant();
        var path = ImageHelper.GetImagePath("packed/map/ancients/ancient_node_" + id + ".png");
        AncientCompendiumEntry.ReplaceAncientIcon(__instance, path, ref __result);
    }
}

[HarmonyPatch(typeof(AncientEventModel), nameof(AncientEventModel.MapIconOutline), MethodType.Getter)]
internal static class AncientMapIconOutlineResultPatch
{
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(AncientEventModel __instance, ref Texture2D __result)
    {
        var id = __instance.Id.Entry.ToLowerInvariant();
        var path = ImageHelper.GetImagePath("packed/map/ancients/ancient_node_" + id + "_outline.png");
        AncientCompendiumEntry.ReplaceAncientIcon(__instance, path, ref __result);
    }
}

[HarmonyPatch(typeof(AncientEventModel), nameof(AncientEventModel.RunHistoryIcon), MethodType.Getter)]
internal static class AncientRunHistoryIconResultPatch
{
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(AncientEventModel __instance, ref Texture2D __result)
    {
        var id = __instance.Id.Entry.ToLowerInvariant();
        var path = ImageHelper.GetImagePath("ui/run_history/" + id + ".png");
        AncientCompendiumEntry.ReplaceAncientIcon(__instance, path, ref __result);
    }
}

[HarmonyPatch(typeof(AncientEventModel), nameof(AncientEventModel.RunHistoryIconOutline), MethodType.Getter)]
internal static class AncientRunHistoryIconOutlineResultPatch
{
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(AncientEventModel __instance, ref Texture2D __result)
    {
        var id = __instance.Id.Entry.ToLowerInvariant();
        var path = ImageHelper.GetImagePath("ui/run_history/" + id + "_outline.png");
        AncientCompendiumEntry.ReplaceAncientIcon(__instance, path, ref __result);
    }
}
