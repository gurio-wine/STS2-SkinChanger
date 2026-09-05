using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Events;
using MegaCrit.Sts2.Core.Nodes.Events.Custom;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.Capstones;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.PauseMenu;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;
using MegaCrit.Sts2.Core.Runs;
using STS2SkinChanger.Catalog;
using STS2SkinChanger.Core;

namespace STS2SkinChanger.Ui;

internal enum CharacterAppearanceDragTarget
{
    None,
    Model,
    HealthBar,
    Intent,
    SelectionReticle
}

internal partial class CharacterAppearanceScreen : NSubmenu
{
    private const string BackButtonScenePath = "res://scenes/ui/back_button.tscn";

    private OptionButton _skinDropdown = null!;
    private HSlider _scaleSlider = null!;
    private Label _scaleValue = null!;
    private SpinBox _offsetX = null!;
    private SpinBox _offsetY = null!;
    private HSlider _healthBarScaleSlider = null!;
    private Label _healthBarScaleValue = null!;
    private SpinBox _healthBarOffsetX = null!;
    private SpinBox _healthBarOffsetY = null!;
    private CheckButton _healthBarFollowScale = null!;
    private CheckButton _healthBarFollowMovement = null!;
    private HSlider _intentScaleSlider = null!;
    private Label _intentScaleValue = null!;
    private SpinBox _intentOffsetX = null!;
    private SpinBox _intentOffsetY = null!;
    private CheckButton _intentFollowScale = null!;
    private CheckButton _intentFollowMovement = null!;
    private HSlider _selectionReticleScaleSlider = null!;
    private Label _selectionReticleScaleValue = null!;
    private SpinBox _selectionReticleOffsetX = null!;
    private SpinBox _selectionReticleOffsetY = null!;
    private CheckButton _selectionReticleFollowScale = null!;
    private CheckButton _selectionReticleFollowMovement = null!;
    private Button _compareButton = null!;
    private Button _skinResetButton = null!;
    private Button _modelResetButton = null!;
    private Button _healthBarResetButton = null!;
    private Button _intentResetButton = null!;
    private Button _selectionReticleResetButton = null!;
    private Button _restorePlayerButton = null!;
    private Label _title = null!;
    private Label _skinLabel = null!;
    private Label _modelSectionLabel = null!;
    private Label _scaleLabel = null!;
    private Label _offsetXLabel = null!;
    private Label _offsetYLabel = null!;
    private Label _healthBarSectionLabel = null!;
    private Label _healthBarScaleLabel = null!;
    private Label _healthBarOffsetXLabel = null!;
    private Label _healthBarOffsetYLabel = null!;
    private Label _intentSectionLabel = null!;
    private Label _intentScaleLabel = null!;
    private Label _intentOffsetXLabel = null!;
    private Label _intentOffsetYLabel = null!;
    private Label _selectionReticleSectionLabel = null!;
    private Label _selectionReticleScaleLabel = null!;
    private Label _selectionReticleOffsetXLabel = null!;
    private Label _selectionReticleOffsetYLabel = null!;
    private Label _hint = null!;
    private Label _selectionHint = null!;
    private Label _status = null!;
    private CharacterDragSurface _dragSurface = null!;
    private CanvasLayer _hintLayer = null!;
    private Tween? _hintPulseTween;
    private Tween? _selectionHintPulseTween;
    private PanelContainer _panel = null!;
    private ScrollContainer _settingsScroll = null!;
    private VBoxContainer _settingsContent = null!;
    private NBackButton _backButton = null!;
    private Control _modelSpacer = null!;
    private readonly List<CanvasItem> _skinControls = [];
    private readonly List<CanvasItem> _modelControls = [];
    private readonly List<CanvasItem> _creatureOnlyControls = [];
    private readonly List<CanvasItem> _intentOnlyControls = [];
    private readonly List<CanvasItem> _selectionReticleOnlyControls = [];
    private NCreature? _targetCreature;
    private AncientEventModel? _targetAncient;
    private NMerchantCharacter? _targetShopPlayerVisual;
    private NMerchantButton? _targetMerchantButton;
    private NFakeMerchant? _targetFakeMerchant;
    private NBossMapPoint? _targetBossMapPoint;
    private string? _targetBossTitle;
    private Player? _player;
    private SkinGroup? _group;
    private string? _transformKey;
    private bool _selectionMode = true;
    private bool _updating;
    private bool _comparing;
    private bool _canEditSkin = true;
    private bool _canEditTransform = true;
    private CharacterAppearanceDragTarget _dragTarget;
    private Vector2 _dragStartPosition;
    private Vector2 _dragStartOffset;
    private NSelectionReticle? _previewSelectionReticle;
    private bool _previewSelectionReticleWasSelected;
    private Color _previewSelectionReticleModulate;
    private Vector2 _previewSelectionReticleScale;

    protected override Control? InitialFocusedControl =>
        _selectionMode ? _backButton : GetTargetInitialFocus();

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        BuildInterface();
        // Sample only while selecting, not every frame or while playing/adjusting a model.
        _selectionHintTimer = new Godot.Timer { WaitTime = 0.35, ProcessMode = ProcessModeEnum.Always };
        _selectionHintTimer.Timeout += RefreshSelectionHint;
        AddChild(_selectionHintTimer);
        ConnectSignals();
        ModLocalization.Bind(this, RefreshLocalizedText);
    }

    protected override void ConnectSignals()
    {
        _backButton.Connect(
            NClickableControl.SignalName.Released,
            Callable.From<NButton>(OnBackPressed));
        _backButton.Disable();
        VisibilityChanged += OnScreenVisibilityChanged;
    }

    private void OnBackPressed(NButton _)
    {
        if (!_selectionMode)
        {
            EnterSelectionMode();
            return;
        }

        _stack.Pop();
    }

    private void OnScreenVisibilityChanged()
    {
        if (Visible)
        {
            _backButton.MoveToHidePosition();
            _backButton.Enable();
            OnSubmenuShown();
            (_selectionMode ? (Control)_backButton : GetTargetInitialFocus()).TryGrabFocus();
            return;
        }

        _lastFocusedControl = GetViewport()?.GuiGetFocusOwner();
        _backButton.Disable();
        OnSubmenuHidden();
    }

    internal void Initialize(Player player)
    {
        _player = player;
        EnterSelectionMode();
    }

    public override void OnSubmenuOpened()
    {
        CharacterAppearanceRuntime.QueuedSelectionFinished += OnQueuedSelectionFinished;
        NCapstoneContainer.Instance?.DisableBackstopInstantly();
        _hintLayer.Visible = true;
        EnterSelectionMode();
    }

    public override void OnSubmenuClosed()
    {
        FinishDrag(save: true);
        EndComparison();
        EndSelectionReticlePreview();
        CharacterAppearanceRuntime.QueuedSelectionFinished -= OnQueuedSelectionFinished;
        StopHintPulseAnimations();
        _hintLayer.Visible = false;
        NCapstoneContainer.Instance?.EnableBackstopInstantly();
        base.OnSubmenuClosed();
    }

    protected override void OnSubmenuShown()
    {
        _hintLayer.Visible = true;
        BeginSelectionReticlePreview(_targetCreature);
        UpdateDragSurfaceCreature();
        base.OnSubmenuShown();
    }

    protected override void OnSubmenuHidden()
    {
        FinishDrag(save: true);
        EndComparison();
        EndSelectionReticlePreview();
        StopHintPulseAnimations();
        _hintLayer.Visible = false;
        base.OnSubmenuHidden();
    }

    private void BuildInterface()
    {
        _dragSurface = new CharacterDragSurface
        {
            Name = "CharacterDragSurface",
            MouseFilter = MouseFilterEnum.Stop,
            MouseDefaultCursorShape = CursorShape.Move,
            ProcessMode = ProcessModeEnum.Always,
            ZIndex = 5
        };
        AddChild(_dragSurface);
        _dragSurface.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _dragSurface.GuiInput += OnDragSurfaceInput;

        // CanvasLayer keeps the instruction above the paused combat scene without putting the input
        // surface above the settings panel.
        _hintLayer = new CanvasLayer
        {
            Name = "CharacterAppearanceHintLayer",
            Layer = 100,
            Visible = false
        };
        AddChild(_hintLayer);

        _hint = new Label
        {
            Name = "DragHint",
            Position = new Vector2(34f, 28f),
            Size = new Vector2(760f, 64f),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            MouseFilter = MouseFilterEnum.Ignore,
            ProcessMode = ProcessModeEnum.Always
        };
        ApplyTextTheme(_hint, 22);
        _hint.AddThemeColorOverride("font_color", new Color("efc850"));
        _hint.AddThemeColorOverride("font_outline_color", new Color("241d12"));
        _hint.AddThemeConstantOverride("outline_size", 6);
        _hintLayer.AddChild(_hint);

        _selectionHint = new Label
        {
            Name = "SelectionHint",
            AnchorLeft = 0.15f,
            AnchorTop = 0f,
            AnchorRight = 0.85f,
            AnchorBottom = 0f,
            OffsetTop = 24f,
            OffsetBottom = 94f,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            MouseFilter = MouseFilterEnum.Ignore,
            ProcessMode = ProcessModeEnum.Always
        };
        ApplyTextTheme(_selectionHint, 28);
        _selectionHint.AddThemeColorOverride("font_color", new Color("efc850"));
        _selectionHint.AddThemeColorOverride("font_outline_color", new Color("241d12"));
        _selectionHint.AddThemeConstantOverride("outline_size", 7);
        _hintLayer.AddChild(_selectionHint);

        _restorePlayerButton = BuildButton(string.Empty);
        _restorePlayerButton.Name = "RestorePlayerPosition";
        _restorePlayerButton.AnchorLeft = 0.5f;
        _restorePlayerButton.AnchorTop = 0f;
        _restorePlayerButton.AnchorRight = 0.5f;
        _restorePlayerButton.AnchorBottom = 0f;
        _restorePlayerButton.OffsetLeft = -145f;
        _restorePlayerButton.OffsetTop = 102f;
        _restorePlayerButton.OffsetRight = 145f;
        _restorePlayerButton.OffsetBottom = 148f;
        _restorePlayerButton.MouseFilter = MouseFilterEnum.Stop;
        _restorePlayerButton.ProcessMode = ProcessModeEnum.Always;
        _restorePlayerButton.ZIndex = 1;
        _restorePlayerButton.Visible = false;
        _restorePlayerButton.Pressed += RestoreLocalPlayerModelTransform;
        _hintLayer.AddChild(_restorePlayerButton);

        // The hints are animated with native Godot tweens instead of a managed Label _Process
        // callback.  This keeps the animation alive while the game is paused and makes the
        // effect reliable for controls created at runtime.
        _hint.SelfModulate = Colors.White;
        _selectionHint.SelfModulate = Colors.White;

        _panel = new PanelContainer
        {
            Name = "AppearancePanel",
            AnchorLeft = 0.61f,
            AnchorTop = 0.075f,
            AnchorRight = 0.98f,
            AnchorBottom = 0.075f,
            OffsetLeft = -10f,
            OffsetRight = 0f,
            GrowVertical = GrowDirection.End,
            MouseFilter = MouseFilterEnum.Stop,
            ZIndex = 10,
            Visible = false
        };
        _panel.AddThemeStyleboxOverride(
            "panel",
            ContextualSkinControls.CreateStyleBox(
                new Color(0.10f, 0.16f, 0.22f, 0.94f),
                new Color("7394ad"),
                2));
        AddChild(_panel);

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 28);
        margin.AddThemeConstantOverride("margin_top", 24);
        margin.AddThemeConstantOverride("margin_right", 28);
        margin.AddThemeConstantOverride("margin_bottom", 24);
        _panel.AddChild(margin);

        _settingsScroll = new ScrollContainer
        {
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Disabled,
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        margin.AddChild(_settingsScroll);

        var content = new VBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(460f, 0f)
        };
        _settingsContent = content;
        content.AddThemeConstantOverride("separation", 7);
        _settingsScroll.AddChild(content);

        _title = BuildLabel(string.Empty, 31, HorizontalAlignment.Center);
        _title.CustomMinimumSize = new Vector2(0f, 46f);
        content.AddChild(_title);
        content.AddChild(BuildVerticalSpacer(12f));

        var skinHeader = BuildSectionHeader(out _skinLabel, out _skinResetButton);
        content.AddChild(skinHeader);
        _skinResetButton.Pressed += ResetSkin;
        _skinDropdown = new OptionButton
        {
            CustomMinimumSize = new Vector2(0f, 50f),
            FitToLongestItem = false,
            ClipText = true,
            Alignment = HorizontalAlignment.Center,
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        ContextualSkinControls.ApplyGameTheme(_skinDropdown);
        _skinDropdown.ItemSelected += index => OnSkinSelected(checked((int)index));
        content.AddChild(_skinDropdown);
        _skinControls.AddRange([skinHeader, _skinDropdown]);
        _modelSpacer = BuildVerticalSpacer(15f);
        content.AddChild(_modelSpacer);

        var modelHeader = BuildSectionHeader(out _modelSectionLabel, out _modelResetButton);
        content.AddChild(modelHeader);
        _modelResetButton.Pressed += ResetModelTransform;

        var scaleRow = new HBoxContainer();
        scaleRow.AddThemeConstantOverride("separation", 12);
        _scaleLabel = BuildLabel(string.Empty, 21);
        _scaleLabel.CustomMinimumSize = new Vector2(74f, 44f);
        scaleRow.AddChild(_scaleLabel);
        _scaleSlider = new HSlider
        {
            MinValue = SkinService.MinimumCharacterScale,
            MaxValue = SkinService.MaximumCharacterScale,
            Step = SkinService.CharacterScaleStep,
            Value = 1d,
            CustomMinimumSize = new Vector2(230f, 44f),
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        _scaleSlider.ValueChanged += _ => OnTransformChanged();
        scaleRow.AddChild(_scaleSlider);
        _scaleValue = BuildLabel("100%", 21, HorizontalAlignment.Right);
        _scaleValue.CustomMinimumSize = new Vector2(82f, 44f);
        scaleRow.AddChild(_scaleValue);
        content.AddChild(scaleRow);

        var offsetRow = new HBoxContainer();
        offsetRow.AddThemeConstantOverride("separation", 10);
        _offsetXLabel = BuildLabel(string.Empty, 21);
        _offsetYLabel = BuildLabel(string.Empty, 21);
        _offsetXLabel.CustomMinimumSize = new Vector2(24f, 44f);
        _offsetYLabel.CustomMinimumSize = new Vector2(24f, 44f);
        _offsetX = BuildOffsetSpinBox();
        _offsetY = BuildOffsetSpinBox();
        _offsetX.ValueChanged += _ => OnTransformChanged();
        _offsetY.ValueChanged += _ => OnTransformChanged();
        offsetRow.AddChild(_offsetXLabel);
        offsetRow.AddChild(_offsetX);
        offsetRow.AddChild(_offsetYLabel);
        offsetRow.AddChild(_offsetY);
        content.AddChild(offsetRow);
        var healthBarSpacer = BuildVerticalSpacer(15f);
        content.AddChild(healthBarSpacer);

        var healthBarHeader = BuildSectionHeader(
            out _healthBarSectionLabel,
            out _healthBarResetButton);
        content.AddChild(healthBarHeader);
        _healthBarResetButton.Pressed += ResetHealthBarTransform;

        var healthScaleRow = new HBoxContainer();
        healthScaleRow.AddThemeConstantOverride("separation", 12);
        _healthBarScaleLabel = BuildLabel(string.Empty, 21);
        _healthBarScaleLabel.CustomMinimumSize = new Vector2(74f, 44f);
        healthScaleRow.AddChild(_healthBarScaleLabel);
        _healthBarScaleSlider = new HSlider
        {
            MinValue = SkinService.MinimumCharacterScale,
            MaxValue = SkinService.MaximumCharacterScale,
            Step = SkinService.CharacterScaleStep,
            Value = 1d,
            CustomMinimumSize = new Vector2(230f, 44f),
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        _healthBarScaleSlider.ValueChanged += _ => OnTransformChanged();
        healthScaleRow.AddChild(_healthBarScaleSlider);
        _healthBarScaleValue = BuildLabel("100%", 21, HorizontalAlignment.Right);
        _healthBarScaleValue.CustomMinimumSize = new Vector2(82f, 44f);
        healthScaleRow.AddChild(_healthBarScaleValue);
        content.AddChild(healthScaleRow);

        var healthOffsetRow = new HBoxContainer();
        healthOffsetRow.AddThemeConstantOverride("separation", 10);
        _healthBarOffsetXLabel = BuildLabel(string.Empty, 21);
        _healthBarOffsetYLabel = BuildLabel(string.Empty, 21);
        _healthBarOffsetXLabel.CustomMinimumSize = new Vector2(24f, 44f);
        _healthBarOffsetYLabel.CustomMinimumSize = new Vector2(24f, 44f);
        _healthBarOffsetX = BuildOffsetSpinBox();
        _healthBarOffsetY = BuildOffsetSpinBox();
        _healthBarOffsetX.ValueChanged += _ => OnTransformChanged();
        _healthBarOffsetY.ValueChanged += _ => OnTransformChanged();
        healthOffsetRow.AddChild(_healthBarOffsetXLabel);
        healthOffsetRow.AddChild(_healthBarOffsetX);
        healthOffsetRow.AddChild(_healthBarOffsetYLabel);
        healthOffsetRow.AddChild(_healthBarOffsetY);
        content.AddChild(healthOffsetRow);

        var followRow = new HBoxContainer();
        followRow.AddThemeConstantOverride("separation", 12);
        _healthBarFollowScale = BuildCheckButton();
        _healthBarFollowScale.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _healthBarFollowScale.Toggled += _ => OnTransformChanged();
        followRow.AddChild(_healthBarFollowScale);
        _healthBarFollowMovement = BuildCheckButton();
        _healthBarFollowMovement.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _healthBarFollowMovement.Toggled += _ => OnTransformChanged();
        followRow.AddChild(_healthBarFollowMovement);
        content.AddChild(followRow);

        var intentSpacer = BuildVerticalSpacer(15f);
        content.AddChild(intentSpacer);
        var intentHeader = BuildSectionHeader(
            out _intentSectionLabel,
            out _intentResetButton);
        content.AddChild(intentHeader);
        _intentResetButton.Pressed += ResetIntentTransform;

        var intentScaleRow = new HBoxContainer();
        intentScaleRow.AddThemeConstantOverride("separation", 12);
        _intentScaleLabel = BuildLabel(string.Empty, 21);
        _intentScaleLabel.CustomMinimumSize = new Vector2(74f, 44f);
        intentScaleRow.AddChild(_intentScaleLabel);
        _intentScaleSlider = new HSlider
        {
            MinValue = SkinService.MinimumCharacterScale,
            MaxValue = SkinService.MaximumCharacterScale,
            Step = SkinService.CharacterScaleStep,
            Value = 1d,
            CustomMinimumSize = new Vector2(230f, 44f),
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        _intentScaleSlider.ValueChanged += _ => OnTransformChanged();
        intentScaleRow.AddChild(_intentScaleSlider);
        _intentScaleValue = BuildLabel("100%", 21, HorizontalAlignment.Right);
        _intentScaleValue.CustomMinimumSize = new Vector2(82f, 44f);
        intentScaleRow.AddChild(_intentScaleValue);
        content.AddChild(intentScaleRow);

        var intentOffsetRow = new HBoxContainer();
        intentOffsetRow.AddThemeConstantOverride("separation", 10);
        _intentOffsetXLabel = BuildLabel(string.Empty, 21);
        _intentOffsetYLabel = BuildLabel(string.Empty, 21);
        _intentOffsetXLabel.CustomMinimumSize = new Vector2(24f, 44f);
        _intentOffsetYLabel.CustomMinimumSize = new Vector2(24f, 44f);
        _intentOffsetX = BuildOffsetSpinBox();
        _intentOffsetY = BuildOffsetSpinBox();
        _intentOffsetX.ValueChanged += _ => OnTransformChanged();
        _intentOffsetY.ValueChanged += _ => OnTransformChanged();
        intentOffsetRow.AddChild(_intentOffsetXLabel);
        intentOffsetRow.AddChild(_intentOffsetX);
        intentOffsetRow.AddChild(_intentOffsetYLabel);
        intentOffsetRow.AddChild(_intentOffsetY);
        content.AddChild(intentOffsetRow);

        var intentFollowRow = new HBoxContainer();
        intentFollowRow.AddThemeConstantOverride("separation", 12);
        _intentFollowScale = BuildCheckButton();
        _intentFollowScale.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _intentFollowScale.Toggled += _ => OnTransformChanged();
        intentFollowRow.AddChild(_intentFollowScale);
        _intentFollowMovement = BuildCheckButton();
        _intentFollowMovement.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _intentFollowMovement.Toggled += _ => OnTransformChanged();
        intentFollowRow.AddChild(_intentFollowMovement);
        content.AddChild(intentFollowRow);

        var selectionReticleSpacer = BuildVerticalSpacer(15f);
        content.AddChild(selectionReticleSpacer);
        var selectionReticleHeader = BuildSectionHeader(
            out _selectionReticleSectionLabel,
            out _selectionReticleResetButton);
        content.AddChild(selectionReticleHeader);
        _selectionReticleResetButton.Pressed += ResetSelectionReticleTransform;

        var selectionReticleScaleRow = new HBoxContainer();
        selectionReticleScaleRow.AddThemeConstantOverride("separation", 12);
        _selectionReticleScaleLabel = BuildLabel(string.Empty, 21);
        _selectionReticleScaleLabel.CustomMinimumSize = new Vector2(74f, 44f);
        selectionReticleScaleRow.AddChild(_selectionReticleScaleLabel);
        _selectionReticleScaleSlider = new HSlider
        {
            MinValue = SkinService.MinimumCharacterScale,
            MaxValue = SkinService.MaximumCharacterScale,
            Step = SkinService.CharacterScaleStep,
            Value = 1d,
            CustomMinimumSize = new Vector2(230f, 44f),
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        _selectionReticleScaleSlider.ValueChanged += _ => OnTransformChanged();
        selectionReticleScaleRow.AddChild(_selectionReticleScaleSlider);
        _selectionReticleScaleValue = BuildLabel("100%", 21, HorizontalAlignment.Right);
        _selectionReticleScaleValue.CustomMinimumSize = new Vector2(82f, 44f);
        selectionReticleScaleRow.AddChild(_selectionReticleScaleValue);
        content.AddChild(selectionReticleScaleRow);

        var selectionReticleOffsetRow = new HBoxContainer();
        selectionReticleOffsetRow.AddThemeConstantOverride("separation", 10);
        _selectionReticleOffsetXLabel = BuildLabel(string.Empty, 21);
        _selectionReticleOffsetYLabel = BuildLabel(string.Empty, 21);
        _selectionReticleOffsetXLabel.CustomMinimumSize = new Vector2(24f, 44f);
        _selectionReticleOffsetYLabel.CustomMinimumSize = new Vector2(24f, 44f);
        _selectionReticleOffsetX = BuildOffsetSpinBox();
        _selectionReticleOffsetY = BuildOffsetSpinBox();
        _selectionReticleOffsetX.ValueChanged += _ => OnTransformChanged();
        _selectionReticleOffsetY.ValueChanged += _ => OnTransformChanged();
        selectionReticleOffsetRow.AddChild(_selectionReticleOffsetXLabel);
        selectionReticleOffsetRow.AddChild(_selectionReticleOffsetX);
        selectionReticleOffsetRow.AddChild(_selectionReticleOffsetYLabel);
        selectionReticleOffsetRow.AddChild(_selectionReticleOffsetY);
        content.AddChild(selectionReticleOffsetRow);

        var selectionReticleFollowRow = new HBoxContainer();
        selectionReticleFollowRow.AddThemeConstantOverride("separation", 12);
        _selectionReticleFollowScale = BuildCheckButton();
        _selectionReticleFollowScale.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _selectionReticleFollowScale.Toggled += _ => OnTransformChanged();
        selectionReticleFollowRow.AddChild(_selectionReticleFollowScale);
        _selectionReticleFollowMovement = BuildCheckButton();
        _selectionReticleFollowMovement.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _selectionReticleFollowMovement.Toggled += _ => OnTransformChanged();
        selectionReticleFollowRow.AddChild(_selectionReticleFollowMovement);
        content.AddChild(selectionReticleFollowRow);

        var compareSpacer = BuildVerticalSpacer(8f);
        content.AddChild(compareSpacer);

        _compareButton = BuildButton(string.Empty);
        _compareButton.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _compareButton.ButtonDown += BeginComparison;
        _compareButton.ButtonUp += EndComparison;
        content.AddChild(_compareButton);

        _status = BuildLabel(string.Empty, 19, HorizontalAlignment.Center);
        _status.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _status.Visible = false;
        content.AddChild(_status);

        _modelControls.AddRange([
            modelHeader,
            scaleRow,
            offsetRow
        ]);
        _creatureOnlyControls.AddRange([
            healthBarSpacer,
            healthBarHeader,
            healthScaleRow,
            healthOffsetRow,
            followRow,
            compareSpacer,
            _compareButton
        ]);
        _intentOnlyControls.AddRange([
            intentSpacer,
            intentHeader,
            intentScaleRow,
            intentOffsetRow,
            intentFollowRow
        ]);
        _selectionReticleOnlyControls.AddRange([
            selectionReticleSpacer,
            selectionReticleHeader,
            selectionReticleScaleRow,
            selectionReticleOffsetRow,
            selectionReticleFollowRow
        ]);

        var backScene = ResourceLoader.Load<PackedScene>(BackButtonScenePath) ??
                        throw new InvalidOperationException("无法加载游戏返回按钮场景。");
        _backButton = backScene.Instantiate<NBackButton>(PackedScene.GenEditState.Disabled);
        _backButton.Name = "BackButton";
        _backButton.ZIndex = 20;
        AddChild(_backButton);
    }

    private void RefreshLocalizedText()
    {
        RefreshTargetTitle();
        _skinLabel.Text = ModLocalization.Get(ModText.Skin);
        _modelSectionLabel.Text = ModLocalization.Get(ModText.ModelTransform);
        _scaleLabel.Text = ModLocalization.Get(ModText.CharacterScale);
        _healthBarSectionLabel.Text = ModLocalization.Get(ModText.HealthBarTransform);
        _healthBarScaleLabel.Text = ModLocalization.Get(ModText.CharacterScale);
        _intentSectionLabel.Text = ModLocalization.Get(ModText.IntentTransform);
        _intentScaleLabel.Text = ModLocalization.Get(ModText.CharacterScale);
        _selectionReticleSectionLabel.Text = ModLocalization.Get(ModText.SelectionReticleTransform);
        _selectionReticleScaleLabel.Text = ModLocalization.Get(ModText.CharacterScale);
        _offsetXLabel.Text = "X";
        _offsetYLabel.Text = "Y";
        _healthBarOffsetXLabel.Text = "X";
        _healthBarOffsetYLabel.Text = "Y";
        _intentOffsetXLabel.Text = "X";
        _intentOffsetYLabel.Text = "Y";
        _selectionReticleOffsetXLabel.Text = "X";
        _selectionReticleOffsetYLabel.Text = "Y";
        _offsetX.TooltipText = ModLocalization.Get(ModText.HorizontalOffset);
        _offsetY.TooltipText = ModLocalization.Get(ModText.VerticalOffset);
        _healthBarOffsetX.TooltipText = ModLocalization.Get(ModText.HorizontalOffset);
        _healthBarOffsetY.TooltipText = ModLocalization.Get(ModText.VerticalOffset);
        _intentOffsetX.TooltipText = ModLocalization.Get(ModText.HorizontalOffset);
        _intentOffsetY.TooltipText = ModLocalization.Get(ModText.VerticalOffset);
        _selectionReticleOffsetX.TooltipText = ModLocalization.Get(ModText.HorizontalOffset);
        _selectionReticleOffsetY.TooltipText = ModLocalization.Get(ModText.VerticalOffset);
        _healthBarFollowScale.Text = ModLocalization.Get(ModText.FollowModelScale);
        _healthBarFollowMovement.Text = ModLocalization.Get(ModText.FollowModelMovement);
        _healthBarFollowScale.TooltipText = _healthBarFollowScale.Text;
        _healthBarFollowMovement.TooltipText = _healthBarFollowMovement.Text;
        _intentFollowScale.Text = ModLocalization.Get(ModText.FollowModelScale);
        _intentFollowMovement.Text = ModLocalization.Get(ModText.FollowModelMovement);
        _intentFollowScale.TooltipText = _intentFollowScale.Text;
        _intentFollowMovement.TooltipText = _intentFollowMovement.Text;
        _selectionReticleFollowScale.Text = ModLocalization.Get(ModText.FollowModelScale);
        _selectionReticleFollowMovement.Text = ModLocalization.Get(ModText.FollowModelMovement);
        _selectionReticleFollowScale.TooltipText = _selectionReticleFollowScale.Text;
        _selectionReticleFollowMovement.TooltipText = _selectionReticleFollowMovement.Text;
        _compareButton.Text = ModLocalization.Get(ModText.HoldToCompare);
        _skinResetButton.Text = ModLocalization.Get(ModText.Reset);
        _modelResetButton.Text = ModLocalization.Get(ModText.Reset);
        _healthBarResetButton.Text = ModLocalization.Get(ModText.Reset);
        _intentResetButton.Text = ModLocalization.Get(ModText.Reset);
        _selectionReticleResetButton.Text = ModLocalization.Get(ModText.Reset);
        _restorePlayerButton.Text = ModLocalization.Get(ModText.RestorePlayerPosition);
        RefreshDragHint();
        RefreshSelectionHint();
        PopulateSkinDropdown();
        RefreshStatusForCurrentContext();
    }

    private void PopulateSkinDropdown()
    {
        if (_skinDropdown == null)
        {
            return;
        }

        if (!_skinDropdown.Visible)
        {
            _skinDropdown.Disabled = true;
            return;
        }

        _updating = true;
        _skinDropdown.Clear();
        if (_group == null)
        {
            _skinDropdown.Disabled = true;
            _updating = false;
            return;
        }

        _skinDropdown.Disabled = false;
        var hasMonsterPriorityContext = SkinService.HasMonsterSkinCategory(_group.Id);
        if (hasMonsterPriorityContext)
        {
            _skinDropdown.AddItem(ModLocalization.Get(ModText.FollowCategory));
            _skinDropdown.SetItemMetadata(0, SkinService.InheritMonsterSelectionId);
        }
        var defaultIndex = _skinDropdown.ItemCount;
        _skinDropdown.AddItem(ModLocalization.Get(ModText.GameDefault));
        _skinDropdown.SetItemMetadata(defaultIndex, SkinCatalog.BaseOptionId);
        var options = SkinService.Catalog?.IsCharacterAppearanceGroup(_group.Id) == true
            ? SkinService.GetCharacterSkinOptions(_group.Id)
            : _group.Options;
        foreach (var option in options)
        {
            var index = _skinDropdown.ItemCount;
            _skinDropdown.AddItem(ModLocalization.DisplayOptionName(option.Name));
            _skinDropdown.SetItemMetadata(index, option.Id);
        }

        var selection = CharacterAppearanceRuntime.GetRequestedOption(_group.Id) ??
                        (hasMonsterPriorityContext
                            ? SkinService.GetMonsterOverrideSelection(_group.Id)
                            : _targetCreature != null
                            ? MultiplayerSkinSync.GetSelectionForCreature(
                                _targetCreature.Entity,
                                _group.Id)
                            : SkinService.Config.GetSelection(_group.Id));
        var selectedIndex = Enumerable.Range(0, _skinDropdown.ItemCount)
            .FirstOrDefault(index =>
                _skinDropdown.GetItemMetadata(index).AsString()
                    .Equals(selection, StringComparison.OrdinalIgnoreCase));
        _skinDropdown.Select(selectedIndex);
        _updating = false;
    }

    private void OnSkinSelected(int index)
    {
        if (_updating || _group == null || index < 0 || index >= _skinDropdown.ItemCount)
        {
            return;
        }

        var optionId = _skinDropdown.GetItemMetadata(index).AsString();
        RequestSkinSelection(optionId);
    }

    private void ResetSkin()
    {
        if (_group != null)
        {
            RequestSkinSelection(SkinCatalog.BaseOptionId);
        }
    }

    private void RequestSkinSelection(string optionId)
    {
        if (_group == null || !_canEditSkin)
        {
            return;
        }

        if (_targetCreature != null &&
            MultiplayerSkinSync.UsesLocalFallbackControls(_targetCreature.Entity))
        {
            if (!MultiplayerSkinSync.TrySetLocalFallbackSkin(
                    _targetCreature.Entity,
                    _group.Id,
                    optionId,
                    out var fallbackError))
            {
                SetStatus(
                    ModLocalization.Get(ModText.AppearanceFailed) + ": " + fallbackError,
                    warning: true);
                return;
            }
            PopulateSkinDropdown();
            SyncTransformControls();
            SetAppliedStatus(null);
            return;
        }

        EndComparison();
        // A failure belongs to exactly one selection attempt. Clear it before starting the next
        // one so a slow load or a successful retry is never presented as the previous skin's
        // error.
        SetStatus(string.Empty, warning: false);
        if (_targetMerchantButton != null)
        {
            if (_targetFakeMerchant != null)
            {
                RequestFakeMerchantSkinSelection(optionId);
                return;
            }

            var previousOptionId = SkinService.Config.GetSelection(_group.Id);
            MerchantRuntimeAppearance.PrepareMerchantSelectionChange();
            if (!SkinService.ApplySelection(_group.Id, optionId))
            {
                MerchantRuntimeAppearance.TryRefreshMerchant(out _);
                PopulateSkinDropdown();
                SetStatus(
                    ModLocalization.Get(ModText.AppearanceFailed) + ": " + SkinService.LastError,
                    warning: true);
                return;
            }

            PopulateSkinDropdown();
            if (!MerchantRuntimeAppearance.TryRefreshMerchant(out var merchantError))
            {
                // Resource mounting succeeded but the selected merchant scene could still be
                // malformed. Restore both the saved choice and the live shop so reopening the
                // inventory cannot inherit a half-applied skin.
                var selectionError = merchantError;
                MerchantRuntimeAppearance.PrepareMerchantSelectionChange();
                var rollbackApplied = SkinService.ApplySelection(_group.Id, previousOptionId);
                string? rollbackError = null;
                var rollbackRefreshed = rollbackApplied &&
                                        MerchantRuntimeAppearance.TryRefreshMerchant(out rollbackError);
                PopulateSkinDropdown();
                _targetMerchantButton = NMerchantRoom.Instance?.MerchantButton;
                if (!rollbackRefreshed)
                {
                    ModLog.Error("回滚商人皮肤选择失败：" +
                                 (rollbackApplied ? rollbackError : SkinService.LastError));
                }

                SetStatus(
                    ModLocalization.Get(ModText.AppearanceFailed) + ": " + selectionError,
                    warning: true);
                return;
            }

            _targetMerchantButton = NMerchantRoom.Instance?.MerchantButton;
            SetAppliedStatus(null);
            return;
        }

        if (_targetAncient != null)
        {
            if (!SkinService.ApplySelection(_group.Id, optionId))
            {
                PopulateSkinDropdown();
                SetStatus(
                    ModLocalization.Get(ModText.AppearanceFailed) + ": " + SkinService.LastError,
                    warning: true);
                return;
            }

            PopulateSkinDropdown();
            if (!AncientRuntimeAppearance.TryRefresh(_group.Id, out var ancientError))
            {
                SetStatus(
                    ModLocalization.Get(ModText.AppearanceFailed) + ": " + ancientError,
                    warning: true);
                return;
            }

            SetAppliedStatus(null);
            return;
        }

        var result = CharacterAppearanceRuntime.RequestSelection(_group.Id, optionId);
        switch (result.State)
        {
            case AppearanceSelectionRequestState.Applied:
                var shopRefreshError = RefreshShopPlayerVisualAfterSelection();
                PopulateSkinDropdown();
                RefreshTargetTitle();
                SetTransformControlsEnabled(true);
                SyncTransformControls();
                UpdateDragSurfaceCreature();
                SetAppliedStatus(result.Error ?? shopRefreshError);
                break;
            case AppearanceSelectionRequestState.Queued:
                PopulateSkinDropdown();
                SetTransformControlsEnabled(false);
                SetStatus(ModLocalization.Get(ModText.AppearanceQueued), warning: true);
                break;
            case AppearanceSelectionRequestState.Failed:
                PopulateSkinDropdown();
                SetStatus(
                    ModLocalization.Get(ModText.AppearanceFailed) +
                    (string.IsNullOrWhiteSpace(result.Error) ? string.Empty : ": " + result.Error),
                    warning: true);
                break;
        }
    }

    private void OnQueuedSelectionFinished(string groupId, bool success, string? error)
    {
        if (_group == null || !groupId.Equals(_group.Id, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (success)
        {
            var shopRefreshError = RefreshShopPlayerVisualAfterSelection();
            PopulateSkinDropdown();
            RefreshTargetTitle();
            SyncTransformControls();
            SetTransformControlsEnabled(true);
            UpdateDragSurfaceCreature();
            SetAppliedStatus(error ?? shopRefreshError);
        }
        else
        {
            PopulateSkinDropdown();
            SetTransformControlsEnabled(true);
            SetStatus(
                ModLocalization.Get(ModText.AppearanceFailed) +
                (string.IsNullOrWhiteSpace(error) ? string.Empty : ": " + error),
                warning: true);
        }
    }

    private void SyncTransformControls()
    {
        if (_scaleSlider == null)
        {
            return;
        }

        if (_targetCreature != null)
        {
            SetTransformControlValues(
                CharacterAppearanceRuntime.GetCreatureCombatTransform(_targetCreature));
            return;
        }

        if (_targetShopPlayerVisual != null && _group != null)
        {
            SetTransformControlValues(
                MerchantRuntimeAppearance.GetLocalPlayerTransform(_group.Id));
        }
    }

    private void RefreshTargetTitle()
    {
        if (_targetCreature != null && GodotObject.IsInstanceValid(_targetCreature))
        {
            _title.Text = _targetCreature.Entity.Name;
            return;
        }

        if (_targetAncient != null)
        {
            _title.Text = AncientCompendiumEntry.GetTitle(_targetAncient);
            return;
        }

        if (_targetBossMapPoint != null && GodotObject.IsInstanceValid(_targetBossMapPoint))
        {
            _title.Text = _targetBossTitle ?? _group?.DisplayName ?? string.Empty;
            return;
        }

        if (_targetMerchantButton != null)
        {
            _title.Text = new LocString("map", "LEGEND_MERCHANT.title").GetFormattedText();
            return;
        }

        if (_targetShopPlayerVisual != null && _player != null)
        {
            _title.Text = _player.Character.Title.GetFormattedText();
            return;
        }

        _title.Text = ModLocalization.Get(ModText.CharacterAppearance);
    }

    private static IEnumerable<T> EnumerateDescendants<T>(Node root) where T : Node
    {
        foreach (var child in root.GetChildren())
        {
            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in EnumerateDescendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private void OnTransformChanged(bool save = true)
    {
        _scaleValue.Text = $"{Mathf.RoundToInt((float)_scaleSlider.Value * 100f)}%";
        _healthBarScaleValue.Text =
            $"{Mathf.RoundToInt((float)_healthBarScaleSlider.Value * 100f)}%";
        _intentScaleValue.Text =
            $"{Mathf.RoundToInt((float)_intentScaleSlider.Value * 100f)}%";
        _selectionReticleScaleValue.Text =
            $"{Mathf.RoundToInt((float)_selectionReticleScaleSlider.Value * 100f)}%";
        if (_updating || _group == null ||
            CharacterAppearanceRuntime.GetRequestedOption(_group.Id) != null)
        {
            return;
        }

        try
        {
            if (_targetShopPlayerVisual != null &&
                GodotObject.IsInstanceValid(_targetShopPlayerVisual))
            {
                var shopTransform = MerchantRuntimeAppearance.SetLocalPlayerTransform(
                    _group.Id,
                    ReadTransformControls(),
                    save);
                SetTransformControlValues(shopTransform);
                MerchantRuntimeAppearance.ApplyLocalPlayerTransform(
                    _targetShopPlayerVisual,
                    _group.Id,
                    shopTransform);
                return;
            }

            if (_targetCreature == null)
            {
                return;
            }

            var normalized = CharacterAppearanceRuntime.SetCreatureCombatTransform(
                _targetCreature,
                ReadTransformControls(),
                save);
            SetTransformControlValues(normalized);
            if (_transformKey != null)
            {
                CharacterAppearanceRuntime.ApplyTransformToKey(_transformKey);
            }
        }
        catch (Exception exception)
        {
            SetStatus(
                ModLocalization.Get(ModText.AppearanceFailed) + ": " +
                exception.GetBaseException().Message,
                warning: true);
        }
    }

    private CharacterCombatTransform ReadTransformControls() =>
        new CharacterCombatTransform(
            (float)_scaleSlider.Value,
            (float)_offsetX.Value,
            (float)_offsetY.Value)
        {
            HealthBarScale = (float)_healthBarScaleSlider.Value,
            HealthBarOffsetX = (float)_healthBarOffsetX.Value,
            HealthBarOffsetY = (float)_healthBarOffsetY.Value,
            HealthBarFollowsModelScale = _healthBarFollowScale.ButtonPressed,
            HealthBarFollowsModelMovement = _healthBarFollowMovement.ButtonPressed,
            IntentScale = (float)_intentScaleSlider.Value,
            IntentOffsetX = (float)_intentOffsetX.Value,
            IntentOffsetY = (float)_intentOffsetY.Value,
            IntentFollowsModelScale = _intentFollowScale.ButtonPressed,
            IntentFollowsModelMovement = _intentFollowMovement.ButtonPressed,
            SelectionReticleScale = (float)_selectionReticleScaleSlider.Value,
            SelectionReticleOffsetX = (float)_selectionReticleOffsetX.Value,
            SelectionReticleOffsetY = (float)_selectionReticleOffsetY.Value,
            SelectionReticleFollowsModelScale = _selectionReticleFollowScale.ButtonPressed,
            SelectionReticleFollowsModelMovement = _selectionReticleFollowMovement.ButtonPressed
        };

    private void SetTransformControlValues(CharacterCombatTransform value)
    {
        var wasUpdating = _updating;
        _updating = true;
        _scaleSlider.Value = value.Scale;
        _offsetX.Value = value.OffsetX;
        _offsetY.Value = value.OffsetY;
        _healthBarScaleSlider.Value = value.HealthBarScale;
        _healthBarOffsetX.Value = value.HealthBarOffsetX;
        _healthBarOffsetY.Value = value.HealthBarOffsetY;
        _healthBarFollowScale.ButtonPressed = value.HealthBarFollowsModelScale;
        _healthBarFollowMovement.ButtonPressed = value.HealthBarFollowsModelMovement;
        _intentScaleSlider.Value = value.IntentScale;
        _intentOffsetX.Value = value.IntentOffsetX;
        _intentOffsetY.Value = value.IntentOffsetY;
        _intentFollowScale.ButtonPressed = value.IntentFollowsModelScale;
        _intentFollowMovement.ButtonPressed = value.IntentFollowsModelMovement;
        _selectionReticleScaleSlider.Value = value.SelectionReticleScale;
        _selectionReticleOffsetX.Value = value.SelectionReticleOffsetX;
        _selectionReticleOffsetY.Value = value.SelectionReticleOffsetY;
        _selectionReticleFollowScale.ButtonPressed = value.SelectionReticleFollowsModelScale;
        _selectionReticleFollowMovement.ButtonPressed = value.SelectionReticleFollowsModelMovement;
        _scaleValue.Text = $"{Mathf.RoundToInt(value.Scale * 100f)}%";
        _healthBarScaleValue.Text = $"{Mathf.RoundToInt(value.HealthBarScale * 100f)}%";
        _intentScaleValue.Text = $"{Mathf.RoundToInt(value.IntentScale * 100f)}%";
        _selectionReticleScaleValue.Text =
            $"{Mathf.RoundToInt(value.SelectionReticleScale * 100f)}%";
        _updating = wasUpdating;
    }

    private void EnterSelectionMode()
    {
        FinishDrag(save: true);
        EndComparison();
        EndSelectionReticlePreview();
        _selectionMode = true;
        _targetCreature = null;
        _targetAncient = null;
        _targetShopPlayerVisual = null;
        _targetMerchantButton = null;
        _targetFakeMerchant = null;
        _targetBossMapPoint = null;
        _targetBossTitle = null;
        _group = null;
        _transformKey = null;
        _canEditSkin = true;
        _canEditTransform = true;
        _panel.Visible = false;
        SetStatus(string.Empty, warning: false);
        UpdateDragSurfaceCreature();
        _backButton?.TryGrabFocus();
    }

    private bool TrySelectTarget(Vector2 localPosition)
    {
        var target = GetSelectableTargets()
            .Where(candidate => candidate.Rect.HasPoint(localPosition))
            .OrderBy(candidate => candidate.Priority)
            .ThenBy(candidate => candidate.Rect.Size.X * candidate.Rect.Size.Y)
            .ThenBy(candidate => candidate.Rect.GetCenter().DistanceSquaredTo(localPosition))
            .FirstOrDefault();
        return target?.Select(localPosition) == true;
    }

    private bool SelectAncientTarget(AncientEventModel ancient, SkinGroup ancientGroup, Vector2 localPosition)
    {
        _selectionMode = false;
        EndSelectionReticlePreview();
        _targetAncient = ancient;
        _targetShopPlayerVisual = null;
        _targetMerchantButton = null;
        _targetFakeMerchant = null;
        _targetBossMapPoint = null;
        _targetBossTitle = null;
        _group = ancientGroup;
        _transformKey = null;
        _canEditSkin = true;
        _canEditTransform = false;
        SetTargetControlsVisible(
            supportsModel: false,
            isCreature: false,
            canSelectSkin: true,
            supportsIntent: false);
        PositionPanelAwayFrom(localPosition);
        _panel.Visible = true;
        RefreshTargetTitle();
        PopulateSkinDropdown();
        SetTransformControlsEnabled(true);
        UpdateDragSurfaceCreature();
        SetStatus(string.Empty, warning: false);
        GetTargetInitialFocus().TryGrabFocus();
        return true;
    }

    private bool SelectBossMapTarget(NBossMapPoint bossPoint, Vector2 targetCenter)
    {
        if (!CharacterAppearanceRuntime.TryGetBossMapAppearance(
                bossPoint,
                out var group,
                out var title))
        {
            return false;
        }

        _selectionMode = false;
        EndSelectionReticlePreview();
        _targetCreature = null;
        _targetAncient = null;
        _targetShopPlayerVisual = null;
        _targetMerchantButton = null;
        _targetFakeMerchant = null;
        _targetBossMapPoint = bossPoint;
        _targetBossTitle = title;
        _group = group;
        _transformKey = null;
        _canEditSkin = true;
        _canEditTransform = false;
        SetTargetControlsVisible(
            supportsModel: false,
            isCreature: false,
            canSelectSkin: true,
            supportsIntent: false);
        PositionPanelAwayFrom(targetCenter);
        _panel.Visible = true;
        RefreshTargetTitle();
        PopulateSkinDropdown();
        SetTransformControlsEnabled(true);
        UpdateDragSurfaceCreature();
        SetStatus(string.Empty, warning: false);
        GetTargetInitialFocus().TryGrabFocus();
        return true;
    }

    private bool SelectCreatureTarget(NCreature creature, Vector2 targetCenter)
    {
        if (!CharacterAppearanceRuntime.TryGetCreatureAppearance(creature, out var binding))
        {
            return false;
        }

        _selectionMode = false;
        EndSelectionReticlePreview();
        _targetCreature = creature;
        _targetAncient = null;
        _targetShopPlayerVisual = null;
        _targetMerchantButton = null;
        _targetFakeMerchant = null;
        _targetBossMapPoint = null;
        _targetBossTitle = null;
        _group = binding.Group;
        _transformKey = binding.TransformKey;
        _canEditSkin = binding.CanSelectSkin &&
                       MultiplayerSkinSync.CanEditSkinForCreature(creature.Entity);
        _canEditTransform = MultiplayerSkinSync.CanEditTransformForCreature(creature.Entity);
        SetTargetControlsVisible(
            supportsModel: true,
            isCreature: binding.SupportsCombatControls,
            canSelectSkin: binding.CanSelectSkin,
            supportsIntent: binding.SupportsIntent);
        PositionPanelAwayFrom(targetCenter);
        _panel.Visible = true;
        RefreshTargetTitle();
        PopulateSkinDropdown();
        SyncTransformControls();
        SetTransformControlsEnabled(true);
        UpdateDragSurfaceCreature();
        BeginSelectionReticlePreview(creature);
        RefreshStatusForCurrentContext();
        GetTargetInitialFocus().TryGrabFocus();
        return true;
    }

    private bool SelectShopPlayerTarget(
        NMerchantCharacter visual,
        Vector2 targetCenter)
    {
        if (_player == null)
        {
            return false;
        }

        var group = ContextualSkinControls.FindGroup(
            _player.Character.Id.Entry,
            _player.Character.GetType().Name);
        if (group == null)
        {
            return false;
        }

        MerchantRuntimeAppearance.ApplyLocalPlayerTransform(visual, group.Id);

        _selectionMode = false;
        EndSelectionReticlePreview();
        _targetCreature = null;
        _targetAncient = null;
        _targetShopPlayerVisual = visual;
        _targetMerchantButton = null;
        _targetFakeMerchant = null;
        _targetBossMapPoint = null;
        _targetBossTitle = null;
        _group = group;
        _transformKey = null;
        _canEditSkin = MultiplayerSkinSync.CanEditLocalPlayerSkinInRun();
        _canEditTransform = true;
        SetTargetControlsVisible(
            supportsModel: true,
            isCreature: false,
            canSelectSkin: true,
            supportsIntent: false);
        PositionPanelAwayFrom(targetCenter);
        _panel.Visible = true;
        RefreshTargetTitle();
        PopulateSkinDropdown();
        SyncTransformControls();
        SetTransformControlsEnabled(true);
        UpdateDragSurfaceCreature();
        SetStatus(string.Empty, warning: false);
        GetTargetInitialFocus().TryGrabFocus();
        return true;
    }

    private bool SelectMerchantTarget(
        NMerchantButton merchantButton,
        Vector2 targetCenter,
        string groupId = MerchantRuntimeAppearance.GroupId,
        NFakeMerchant? fakeMerchant = null)
    {
        var group = SkinService.Catalog?.Groups.FirstOrDefault(candidate =>
            candidate.Id.Equals(
                groupId,
                StringComparison.OrdinalIgnoreCase));
        if (group == null)
        {
            return false;
        }

        _selectionMode = false;
        EndSelectionReticlePreview();
        _targetCreature = null;
        _targetAncient = null;
        _targetShopPlayerVisual = null;
        _targetMerchantButton = merchantButton;
        _targetFakeMerchant = fakeMerchant;
        _targetBossMapPoint = null;
        _targetBossTitle = null;
        _group = group;
        _transformKey = null;
        _canEditSkin = true;
        _canEditTransform = false;
        SetTargetControlsVisible(
            supportsModel: false,
            isCreature: false,
            canSelectSkin: true,
            supportsIntent: false);
        PositionPanelAwayFrom(targetCenter);
        _panel.Visible = true;
        RefreshTargetTitle();
        PopulateSkinDropdown();
        SetTransformControlsEnabled(true);
        UpdateDragSurfaceCreature();
        SetStatus(string.Empty, warning: false);
        GetTargetInitialFocus().TryGrabFocus();
        return true;
    }

    private void RequestFakeMerchantSkinSelection(string optionId)
    {
        if (_group == null || _targetFakeMerchant == null)
        {
            return;
        }

        var previousOptionId = SkinService.Config.GetSelection(_group.Id);
        if (!SkinService.ApplySelection(_group.Id, optionId))
        {
            PopulateSkinDropdown();
            SetStatus(
                ModLocalization.Get(ModText.AppearanceFailed) + ": " + SkinService.LastError,
                warning: true);
            return;
        }

        PopulateSkinDropdown();
        if (!MerchantRuntimeAppearance.TryRefreshFakeMerchant(
                _targetFakeMerchant,
                out var fakeMerchantError))
        {
            var selectionError = fakeMerchantError;
            var rollbackApplied = SkinService.ApplySelection(_group.Id, previousOptionId);
            string? rollbackError = null;
            var rollbackRefreshed = rollbackApplied &&
                                    MerchantRuntimeAppearance.TryRefreshFakeMerchant(
                                        _targetFakeMerchant,
                                        out rollbackError);
            PopulateSkinDropdown();
            _targetMerchantButton = _targetFakeMerchant.MerchantButton;
            if (!rollbackRefreshed)
            {
                ModLog.Error("回滚假商人皮肤选择失败：" +
                             (rollbackApplied ? rollbackError : SkinService.LastError));
            }

            SetStatus(
                ModLocalization.Get(ModText.AppearanceFailed) + ": " + selectionError,
                warning: true);
            return;
        }

        _targetMerchantButton = _targetFakeMerchant.MerchantButton;
        SetAppliedStatus(null);
    }

    private string? RefreshShopPlayerVisualAfterSelection()
    {
        if (_targetShopPlayerVisual == null || _player == null || _group == null)
        {
            return null;
        }

        if (!MerchantRuntimeAppearance.TryRefreshLocalPlayer(
                _player,
                _group.Id,
                out var refreshedVisual,
                out var error))
        {
            return error;
        }

        _targetShopPlayerVisual = refreshedVisual;
        return null;
    }

    private void SetTargetControlsVisible(
        bool supportsModel,
        bool isCreature,
        bool canSelectSkin,
        bool supportsIntent)
    {
        foreach (var control in _skinControls)
        {
            control.Visible = canSelectSkin;
        }

        foreach (var control in _modelControls)
        {
            control.Visible = supportsModel;
        }

        foreach (var control in _creatureOnlyControls)
        {
            control.Visible = isCreature;
        }

        foreach (var control in _intentOnlyControls)
        {
            control.Visible = isCreature && supportsIntent;
        }

        foreach (var control in _selectionReticleOnlyControls)
        {
            control.Visible = isCreature && supportsIntent;
        }

        _modelSpacer.Visible = supportsModel && canSelectSkin;
        ResizePanelToContent();
    }

    private void ResizePanelToContent()
    {
        Callable.From(() =>
        {
            if (!GodotObject.IsInstanceValid(_settingsScroll) ||
                !GodotObject.IsInstanceValid(_settingsContent))
            {
                return;
            }

            var maximumHeight = Math.Max(300f, GetViewportRect().Size.Y * 0.9f - 48f);
            var desiredHeight = _settingsContent.GetCombinedMinimumSize().Y;
            var height = Math.Min(desiredHeight, maximumHeight);
            _settingsScroll.CustomMinimumSize = new Vector2(460f, height);
            _settingsScroll.VerticalScrollMode = desiredHeight > maximumHeight
                ? ScrollContainer.ScrollMode.Auto
                : ScrollContainer.ScrollMode.Disabled;
            _panel.ResetSize();
        }).CallDeferred();
    }

    private Control GetTargetInitialFocus() =>
        _skinDropdown.Visible ? _skinDropdown : _scaleSlider;

    private void PositionPanelAwayFrom(Vector2 targetCenter)
    {
        var putOnRight = targetCenter.X <= Math.Max(1f, _dragSurface.Size.X) * 0.5f;
        _panel.AnchorLeft = putOnRight ? 0.61f : 0.02f;
        _panel.AnchorRight = putOnRight ? 0.98f : 0.39f;
        _panel.OffsetLeft = putOnRight ? -10f : 0f;
        _panel.OffsetRight = putOnRight ? 0f : 10f;
        _panel.AnchorTop = 0.075f;
        _panel.AnchorBottom = 0.075f;
        _panel.GrowVertical = GrowDirection.End;
    }

    private void ResetModelTransform()
    {
        if (_targetCreature == null && _targetShopPlayerVisual == null)
        {
            return;
        }

        EndComparison();
        var value = CharacterTransformResetPolicy.ResetModel(ReadTransformControls());
        SetTransformControlValues(value);
        OnTransformChanged();
    }

    private void RestoreLocalPlayerModelTransform()
    {
        FinishDrag(save: true);
        var creature = CharacterAppearanceRuntime.GetCurrentCreature(_player);
        if (creature != null && GodotObject.IsInstanceValid(creature))
        {
            var current = CharacterAppearanceRuntime.GetCreatureCombatTransform(creature);
            CharacterAppearanceRuntime.SetCreatureCombatTransform(
                creature,
                CharacterTransformResetPolicy.ResetModel(current));
            CharacterAppearanceRuntime.ApplyStoredTransform(creature);
            UpdateDragSurfaceCreature();
            return;
        }

        var visual = MerchantRuntimeAppearance.GetLocalPlayerVisual();
        if (_player == null || visual == null || !GodotObject.IsInstanceValid(visual))
        {
            UpdateDragSurfaceCreature();
            return;
        }

        var group = ContextualSkinControls.FindGroup(
            _player.Character.Id.Entry,
            _player.Character.GetType().Name);
        if (group == null)
        {
            UpdateDragSurfaceCreature();
            return;
        }

        var restored = MerchantRuntimeAppearance.SetLocalPlayerTransform(
            group.Id,
            CharacterTransformResetPolicy.ResetModel(
                MerchantRuntimeAppearance.GetLocalPlayerTransform(group.Id)),
            save: true);
        MerchantRuntimeAppearance.ApplyLocalPlayerTransform(visual, group.Id, restored);
        UpdateDragSurfaceCreature();
    }

    private void ResetHealthBarTransform()
    {
        if (_targetCreature == null)
        {
            return;
        }

        EndComparison();
        var value = ReadTransformControls() with
        {
            HealthBarScale = 1f,
            HealthBarOffsetX = 0f,
            HealthBarOffsetY = 0f,
            HealthBarFollowsModelScale = false,
            HealthBarFollowsModelMovement = true
        };
        SetTransformControlValues(value);
        OnTransformChanged();
    }

    private void ResetIntentTransform()
    {
        if (_targetCreature == null)
        {
            return;
        }

        EndComparison();
        var value = ReadTransformControls() with
        {
            IntentScale = 1f,
            IntentOffsetX = 0f,
            IntentOffsetY = 0f,
            IntentFollowsModelScale = false,
            IntentFollowsModelMovement = true
        };
        SetTransformControlValues(value);
        OnTransformChanged();
    }

    private void ResetSelectionReticleTransform()
    {
        if (_targetCreature == null)
        {
            return;
        }

        EndComparison();
        var value = ReadTransformControls() with
        {
            SelectionReticleScale = 1f,
            SelectionReticleOffsetX = 0f,
            SelectionReticleOffsetY = 0f,
            SelectionReticleFollowsModelScale = true,
            SelectionReticleFollowsModelMovement = true
        };
        SetTransformControlValues(value);
        OnTransformChanged();
    }

    private void BeginSelectionReticlePreview(NCreature? creature)
    {
        if (_selectionMode ||
            !CharacterAppearanceRuntime.SupportsIntentAppearance(creature))
        {
            return;
        }

        var reticle = CharacterAppearanceRuntime.GetSelectionReticle(creature);
        if (reticle == null || !GodotObject.IsInstanceValid(reticle) ||
            ReferenceEquals(reticle, _previewSelectionReticle))
        {
            return;
        }

        EndSelectionReticlePreview();
        _previewSelectionReticle = reticle;
        _previewSelectionReticleWasSelected = reticle.IsSelected;
        _previewSelectionReticleModulate = reticle.IsSelected
            ? reticle.Modulate
            : Colors.Transparent;
        _previewSelectionReticleScale = reticle.IsSelected
            ? reticle.Scale
            : Vector2.One;
        CharacterAppearanceRuntime.StopSelectionReticleAnimation(reticle);
        if (!reticle.IsSelected)
        {
            // This preview must not call OnSelect: doing so would change the game's targeting state.
            reticle.Modulate = Colors.White;
            reticle.Scale = Vector2.One;
        }
    }

    private void EndSelectionReticlePreview()
    {
        if (_previewSelectionReticle != null &&
            GodotObject.IsInstanceValid(_previewSelectionReticle) &&
            !_previewSelectionReticleWasSelected &&
            !_previewSelectionReticle.IsSelected)
        {
            _previewSelectionReticle.Modulate = _previewSelectionReticleModulate;
            _previewSelectionReticle.Scale = _previewSelectionReticleScale;
        }

        _previewSelectionReticle = null;
        _previewSelectionReticleWasSelected = false;
    }

    private void BeginComparison()
    {
        if (_comparing)
        {
            return;
        }

        if (_targetCreature == null || !GodotObject.IsInstanceValid(_targetCreature))
        {
            return;
        }

        _comparing = true;
        CharacterAppearanceRuntime.ApplyPreviewTransform(
            _targetCreature,
            new CharacterCombatTransform());
    }

    private void EndComparison()
    {
        if (!_comparing)
        {
            return;
        }

        _comparing = false;
        if (_targetCreature != null && GodotObject.IsInstanceValid(_targetCreature))
        {
            CharacterAppearanceRuntime.ApplyStoredTransform(_targetCreature);
        }

    }

    private void UpdateDragSurfaceCreature()
    {
        var creature = !_selectionMode &&
                       _targetCreature != null &&
                       GodotObject.IsInstanceValid(_targetCreature)
            ? _targetCreature
            : null;
        _dragSurface.SetCreature(creature);
        var shopPlayer = !_selectionMode &&
                         _targetShopPlayerVisual != null &&
                         GodotObject.IsInstanceValid(_targetShopPlayerVisual)
            ? _targetShopPlayerVisual
            : null;
        _dragSurface.SetShopPlayerVisual(shopPlayer);
        _dragSurface.SetSelectionMode(_selectionMode);
        _dragSurface.SetDragEnabled(
            _canEditTransform && (creature != null || shopPlayer != null));
        var showSelectionHint = _selectionMode;
        var showDragHint = _canEditTransform && (creature != null || shopPlayer != null);
        _selectionHint.Visible = showSelectionHint;
        _hint.Visible = showDragHint;
        _restorePlayerButton.Visible = _selectionMode && CanRestoreLocalPlayerModelTransform();
        RefreshHintPulse(_selectionHint, showSelectionHint, ref _selectionHintPulseTween);
        RefreshHintPulse(_hint, showDragHint, ref _hintPulseTween);
        _compareButton.Disabled = creature == null;
        RefreshDragHint();
        UpdateSelectionHintRefresh();
    }

    private bool CanRestoreLocalPlayerModelTransform()
    {
        var creature = CharacterAppearanceRuntime.GetCurrentCreature(_player);
        if (creature != null && GodotObject.IsInstanceValid(creature))
        {
            return CharacterTransformResetPolicy.NeedsModelReset(
                CharacterAppearanceRuntime.GetCreatureCombatTransform(creature));
        }

        var visual = MerchantRuntimeAppearance.GetLocalPlayerVisual();
        if (_player == null || visual == null || !GodotObject.IsInstanceValid(visual))
        {
            return false;
        }

        var group = ContextualSkinControls.FindGroup(
            _player.Character.Id.Entry,
            _player.Character.GetType().Name);
        return group != null && CharacterTransformResetPolicy.NeedsModelReset(
            MerchantRuntimeAppearance.GetLocalPlayerTransform(group.Id));
    }

    private void StopHintPulseAnimations()
    {
        _selectionHintTimer?.Stop();
        StopHintPulse(_selectionHint, ref _selectionHintPulseTween);
        StopHintPulse(_hint, ref _hintPulseTween);
    }

    private void RefreshHintPulse(Label label, bool shouldShow, ref Tween? tween)
    {
        // EnterSelectionMode can run before the screen is pushed onto the stack.  Do not create
        // a tween until the label is actually visible in the scene tree.
        if (!shouldShow || !_hintLayer.Visible || !IsVisibleInTree())
        {
            StopHintPulse(label, ref tween);
            return;
        }

        if (tween != null && tween.IsValid() && tween.IsRunning())
        {
            return;
        }

        label.PivotOffset = label.Size * 0.5f;
        label.SelfModulate = Colors.White;
        var pulse = label.CreateTween()
            .SetProcessMode(Tween.TweenProcessMode.Idle)
            .SetPauseMode(Tween.TweenPauseMode.Process)
            .SetIgnoreTimeScale();
        pulse.SetLoops();
        pulse.TweenProperty(label, "self_modulate:a", 0.38f, 0.72)
            .SetTrans(Tween.TransitionType.Sine)
            .SetEase(Tween.EaseType.InOut);
        pulse.Chain()
            .TweenProperty(label, "self_modulate:a", 1f, 0.72)
            .SetTrans(Tween.TransitionType.Sine)
            .SetEase(Tween.EaseType.InOut);
        tween = pulse;
    }

    private static void StopHintPulse(Label label, ref Tween? tween)
    {
        if (tween != null && tween.IsValid())
        {
            tween.Kill();
        }

        tween = null;
        if (GodotObject.IsInstanceValid(label))
        {
            label.SelfModulate = Colors.White;
            label.Scale = Vector2.One;
        }
    }

    private void RefreshDragHint()
    {
        var text = CharacterAppearanceRuntime.SupportsIntentAppearance(_targetCreature)
            ? ModText.DirectDragIntentHint
            : ModText.DirectDragHint;
        _hint.Text = ModLocalization.Get(text);
    }

    private void OnDragSurfaceInput(InputEvent inputEvent)
    {
        if (inputEvent is InputEventMouseButton button &&
            button.ButtonIndex == MouseButton.Left)
        {
            if (button.Pressed)
            {
                if (_selectionMode)
                {
                    if (TrySelectTarget(button.Position))
                    {
                        _dragSurface.AcceptEvent();
                    }

                    return;
                }

                if (!_dragSurface.DragEnabled)
                {
                    return;
                }

                var target = _dragSurface.HitTest(button.Position);
                if (target == CharacterAppearanceDragTarget.None)
                {
                    return;
                }

                EndComparison();
                _dragTarget = target;
                _dragStartPosition = button.Position;
                _dragStartOffset = target switch
                {
                    CharacterAppearanceDragTarget.Model =>
                        new Vector2((float)_offsetX.Value, (float)_offsetY.Value),
                    CharacterAppearanceDragTarget.HealthBar =>
                        new Vector2(
                            (float)_healthBarOffsetX.Value,
                            (float)_healthBarOffsetY.Value),
                    CharacterAppearanceDragTarget.Intent =>
                        new Vector2((float)_intentOffsetX.Value, (float)_intentOffsetY.Value),
                    CharacterAppearanceDragTarget.SelectionReticle =>
                        new Vector2(
                            (float)_selectionReticleOffsetX.Value,
                            (float)_selectionReticleOffsetY.Value),
                    _ => Vector2.Zero
                };
                _dragSurface.GrabClickFocus();
                _dragSurface.AcceptEvent();
                return;
            }

            if (_dragTarget != CharacterAppearanceDragTarget.None)
            {
                FinishDrag(save: true);
                _dragSurface.AcceptEvent();
            }

            return;
        }

        if (inputEvent is not InputEventMouseMotion motion ||
            _dragTarget == CharacterAppearanceDragTarget.None)
        {
            return;
        }

        var delta = _dragSurface.GetTargetLocalDelta(_dragStartPosition, motion.Position);
        _updating = true;
        switch (_dragTarget)
        {
            case CharacterAppearanceDragTarget.Model:
                _offsetX.Value = _dragStartOffset.X + delta.X;
                _offsetY.Value = _dragStartOffset.Y + delta.Y;
                break;
            case CharacterAppearanceDragTarget.HealthBar:
                _healthBarOffsetX.Value = _dragStartOffset.X + delta.X;
                _healthBarOffsetY.Value = _dragStartOffset.Y + delta.Y;
                break;
            case CharacterAppearanceDragTarget.Intent:
                _intentOffsetX.Value = _dragStartOffset.X + delta.X;
                _intentOffsetY.Value = _dragStartOffset.Y + delta.Y;
                break;
            case CharacterAppearanceDragTarget.SelectionReticle:
                _selectionReticleOffsetX.Value = _dragStartOffset.X + delta.X;
                _selectionReticleOffsetY.Value = _dragStartOffset.Y + delta.Y;
                break;
        }

        _updating = false;
        OnTransformChanged(save: false);
        _dragSurface.AcceptEvent();
    }

    private void FinishDrag(bool save)
    {
        if (_dragTarget == CharacterAppearanceDragTarget.None)
        {
            return;
        }

        _dragTarget = CharacterAppearanceDragTarget.None;
        if (save)
        {
            OnTransformChanged(save: true);
        }

    }

    private void RefreshStatusForCurrentContext()
    {
        if (_status == null)
        {
            return;
        }

        if (!_selectionMode &&
            _targetCreature != null &&
            _group != null &&
            CharacterAppearanceRuntime.GetRequestedOption(_group.Id) != null)
        {
            SetTransformControlsEnabled(false);
            SetStatus(ModLocalization.Get(ModText.AppearanceQueued), warning: true);
            return;
        }

        SetTransformControlsEnabled(!_selectionMode);
        SetStatus(string.Empty, warning: false);
    }

    private void SetTransformControlsEnabled(bool enabled)
    {
        var transformEnabled = enabled && _canEditTransform;
        var skinEnabled = enabled && _canEditSkin;
        _scaleSlider.Editable = transformEnabled;
        _offsetX.Editable = transformEnabled;
        _offsetY.Editable = transformEnabled;
        _healthBarScaleSlider.Editable = transformEnabled;
        _healthBarOffsetX.Editable = transformEnabled;
        _healthBarOffsetY.Editable = transformEnabled;
        _healthBarFollowScale.Disabled = !transformEnabled;
        _healthBarFollowMovement.Disabled = !transformEnabled;
        _intentScaleSlider.Editable = transformEnabled;
        _intentOffsetX.Editable = transformEnabled;
        _intentOffsetY.Editable = transformEnabled;
        _intentFollowScale.Disabled = !transformEnabled;
        _intentFollowMovement.Disabled = !transformEnabled;
        _selectionReticleScaleSlider.Editable = transformEnabled;
        _selectionReticleOffsetX.Editable = transformEnabled;
        _selectionReticleOffsetY.Editable = transformEnabled;
        _selectionReticleFollowScale.Disabled = !transformEnabled;
        _selectionReticleFollowMovement.Disabled = !transformEnabled;
        _skinDropdown.Disabled = !skinEnabled || _group == null || !_skinDropdown.Visible;
        _dragSurface.SetDragEnabled(
            transformEnabled &&
            (_targetCreature != null || _targetShopPlayerVisual != null));
        _skinResetButton.Disabled = !skinEnabled || !_skinResetButton.Visible;
        _modelResetButton.Disabled = !transformEnabled;
        _healthBarResetButton.Disabled = !transformEnabled;
        _intentResetButton.Disabled = !transformEnabled || !_intentResetButton.Visible;
        _selectionReticleResetButton.Disabled =
            !transformEnabled || !_selectionReticleResetButton.Visible;
        _compareButton.Disabled = !transformEnabled || _targetCreature == null;
    }

    private void SetStatus(string text, bool warning)
    {
        _status.Text = text;
        _status.Visible = !string.IsNullOrWhiteSpace(text);
        _status.Modulate = warning ? new Color("efc850") : new Color("b8e6c0");
    }

    private void SetAppliedStatus(string? liveRefreshError)
    {
        if (string.IsNullOrWhiteSpace(liveRefreshError))
        {
            SetStatus(string.Empty, warning: false);
            return;
        }

        SetStatus(
            ModLocalization.Get(ModText.AppearanceFailed) + ": " + liveRefreshError,
            warning: true);
    }

    private static SpinBox BuildOffsetSpinBox()
    {
        var spinBox = new SpinBox
        {
            MinValue = SkinService.MinimumCharacterOffset,
            MaxValue = SkinService.MaximumCharacterOffset,
            Step = SkinService.CharacterOffsetStep,
            CustomMinimumSize = new Vector2(125f, 44f),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            AllowGreater = false,
            AllowLesser = false,
            UpdateOnTextChanged = true,
            Suffix = " px"
        };
        ApplyTextTheme(spinBox.GetLineEdit(), 20);
        return spinBox;
    }

    private static Label BuildSectionLabel()
    {
        var label = BuildLabel(string.Empty, 23);
        label.CustomMinimumSize = new Vector2(0f, 38f);
        label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        label.Modulate = new Color("efc850");
        return label;
    }

    private static HBoxContainer BuildSectionHeader(
        out Label label,
        out Button resetButton)
    {
        var row = new HBoxContainer
        {
            CustomMinimumSize = new Vector2(0f, 40f)
        };
        row.AddThemeConstantOverride("separation", 12);
        label = BuildSectionLabel();
        resetButton = BuildButton(string.Empty);
        resetButton.CustomMinimumSize = new Vector2(78f, 34f);
        resetButton.SizeFlagsHorizontal = SizeFlags.ShrinkEnd;
        resetButton.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        ApplyTextTheme(resetButton, 16);
        row.AddChild(label);
        row.AddChild(resetButton);
        return row;
    }

    private static Control BuildVerticalSpacer(float height) => new()
    {
        CustomMinimumSize = new Vector2(0f, height),
        MouseFilter = MouseFilterEnum.Ignore
    };

    private static CheckButton BuildCheckButton()
    {
        var button = new CheckButton
        {
            CustomMinimumSize = new Vector2(0f, 40f),
            MouseDefaultCursorShape = CursorShape.PointingHand
        };
        ApplyTextTheme(button, 18);
        return button;
    }

    private static Label BuildLabel(
        string text,
        int fontSize,
        HorizontalAlignment alignment = HorizontalAlignment.Left)
    {
        var label = new Label
        {
            Text = text,
            HorizontalAlignment = alignment,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore
        };
        ApplyTextTheme(label, fontSize);
        return label;
    }

    private static Button BuildButton(string text)
    {
        var button = new Button
        {
            Text = text,
            CustomMinimumSize = new Vector2(0f, 48f),
            MouseDefaultCursorShape = CursorShape.PointingHand
        };
        ApplyTextTheme(button, 19);
        button.AddThemeStyleboxOverride(
            "normal",
            ContextualSkinControls.CreateStyleBox(new Color("3c5f82"), new Color("7394ad")));
        button.AddThemeStyleboxOverride(
            "hover",
            ContextualSkinControls.CreateStyleBox(new Color("4b7392"), new Color("afcdde")));
        button.AddThemeStyleboxOverride(
            "pressed",
            ContextualSkinControls.CreateStyleBox(new Color("45104e"), new Color("efc850"), 2));
        return button;
    }

    private static void ApplyTextTheme(Control control, int fontSize)
    {
        control.AddThemeColorOverride("font_color", new Color("fff6e2"));
        control.AddThemeColorOverride("font_hover_color", Colors.White);
        control.AddThemeColorOverride("font_pressed_color", new Color("efc850"));
        control.AddThemeColorOverride("font_outline_color", new Color("332f27"));
        control.AddThemeConstantOverride("outline_size", 4);
        control.AddThemeFontSizeOverride("font_size", fontSize);
        if (ContextualSkinControls.GameFont != null)
        {
            control.AddThemeFontOverride("font", ContextualSkinControls.GameFont);
        }
    }
}

internal partial class CharacterDragSurface : Control
{
    private NCreature? _creature;
    private NMerchantCharacter? _shopPlayerVisual;

    public bool DragEnabled { get; private set; }

    public bool SelectionMode { get; private set; }

    public void SetCreature(NCreature? creature)
    {
        _creature = creature;
    }

    public void SetShopPlayerVisual(NMerchantCharacter? visual)
    {
        _shopPlayerVisual = visual;
    }

    public void SetDragEnabled(bool enabled)
    {
        DragEnabled = enabled;
        RefreshCursor();
    }

    public void SetSelectionMode(bool enabled)
    {
        SelectionMode = enabled;
        RefreshCursor();
    }

    private void RefreshCursor()
    {
        MouseDefaultCursorShape = SelectionMode
            ? CursorShape.PointingHand
            : DragEnabled
                ? CursorShape.Move
                : CursorShape.Arrow;
    }

    public CharacterAppearanceDragTarget HitTest(Vector2 localPosition)
    {
        if (!DragEnabled)
        {
            return CharacterAppearanceDragTarget.None;
        }

        var healthBar = CharacterAppearanceRuntime.GetHealthBarBounds(_creature);
        if (healthBar != null && TryGetCanvasRect(healthBar, 12f, out var healthRect) &&
            healthRect.HasPoint(localPosition))
        {
            return CharacterAppearanceDragTarget.HealthBar;
        }

        if (CharacterAppearanceRuntime.SupportsIntentAppearance(_creature) &&
            TryGetIntentTargetRect(out var intentRect) &&
            intentRect.HasPoint(localPosition))
        {
            return CharacterAppearanceDragTarget.Intent;
        }

        var reticle = CharacterAppearanceRuntime.GetSelectionReticle(_creature);
        if (reticle != null &&
            reticle.IsVisibleInTree() &&
            TryGetCanvasRect(reticle, 0f, out var reticleRect) &&
            IsNearRectBorder(reticleRect, localPosition, 20f))
        {
            return CharacterAppearanceDragTarget.SelectionReticle;
        }

        var modelBounds = CharacterAppearanceRuntime.GetModelBounds(_creature);
        if (modelBounds != null &&
            TryGetCanvasRect(modelBounds, 8f, out var modelRect) &&
            modelRect.HasPoint(localPosition))
        {
            return CharacterAppearanceDragTarget.Model;
        }

        if (_shopPlayerVisual != null &&
            TryGetNode2DTargetRect(
                _shopPlayerVisual,
                new Rect2(-190f, -450f, 380f, 520f),
                out var shopPlayerRect) &&
            shopPlayerRect.HasPoint(localPosition))
        {
            return CharacterAppearanceDragTarget.Model;
        }

        return CharacterAppearanceDragTarget.None;
    }

    private static bool IsNearRectBorder(Rect2 rect, Vector2 point, float width)
    {
        if (!rect.Grow(width).HasPoint(point))
        {
            return false;
        }

        var innerWidth = Math.Max(0f, rect.Size.X - width * 2f);
        var innerHeight = Math.Max(0f, rect.Size.Y - width * 2f);
        var inner = new Rect2(
            rect.Position + Vector2.One * width,
            new Vector2(innerWidth, innerHeight));
        return innerWidth <= 0f || innerHeight <= 0f || !inner.HasPoint(point);
    }

    private bool TryGetIntentTargetRect(out Rect2 rect)
    {
        rect = default;
        if (_creature == null ||
            !GodotObject.IsInstanceValid(_creature) ||
            !GodotObject.IsInstanceValid(_creature.IntentContainer))
        {
            return false;
        }

        var found = false;
        var minimum = Vector2.Zero;
        var maximum = Vector2.Zero;
        foreach (var intent in _creature.IntentContainer.GetChildren()
                     .OfType<Control>()
                     .Where(control => control.IsVisibleInTree()))
        {
            if (!TryGetCanvasRect(intent, 10f, out var intentRect))
            {
                continue;
            }

            if (!found)
            {
                minimum = intentRect.Position;
                maximum = intentRect.End;
                found = true;
                continue;
            }

            minimum = new Vector2(
                Math.Min(minimum.X, intentRect.Position.X),
                Math.Min(minimum.Y, intentRect.Position.Y));
            maximum = new Vector2(
                Math.Max(maximum.X, intentRect.End.X),
                Math.Max(maximum.Y, intentRect.End.Y));
        }

        if (!found)
        {
            return false;
        }

        rect = new Rect2(minimum, maximum - minimum);
        return true;
    }

    public Vector2 GetTargetLocalDelta(Vector2 from, Vector2 to)
    {
        var surfaceTransform = GetGlobalTransformWithCanvas();
        if (_creature != null && GodotObject.IsInstanceValid(_creature))
        {
            var creatureInverse = _creature.GetGlobalTransformWithCanvas().AffineInverse();
            return creatureInverse * (surfaceTransform * to) -
                   creatureInverse * (surfaceTransform * from);
        }

        if (_shopPlayerVisual == null ||
            !GodotObject.IsInstanceValid(_shopPlayerVisual) ||
            _shopPlayerVisual.GetParent() is not CanvasItem parent)
        {
            return Vector2.Zero;
        }

        var parentInverse = parent.GetGlobalTransformWithCanvas().AffineInverse();
        return parentInverse * (surfaceTransform * to) -
               parentInverse * (surfaceTransform * from);
    }

    public bool TryGetCreatureTargetRect(NCreature creature, out Rect2 rect)
    {
        rect = default;
        return GodotObject.IsInstanceValid(creature.Hitbox) &&
               TryGetCanvasRect(creature.Hitbox, 8f, out rect);
    }

    public bool TryGetCanvasRect(Control control, float padding, out Rect2 rect)
    {
        rect = default;
        if (!GodotObject.IsInstanceValid(control))
        {
            return false;
        }

        var inverse = GetGlobalTransformWithCanvas().AffineInverse();
        var transform = control.GetGlobalTransformWithCanvas();
        var first = inverse * (transform * Vector2.Zero);
        var second = inverse * (transform * new Vector2(control.Size.X, 0f));
        var third = inverse * (transform * control.Size);
        var fourth = inverse * (transform * new Vector2(0f, control.Size.Y));
        var minimum = new Vector2(
            Math.Min(Math.Min(first.X, second.X), Math.Min(third.X, fourth.X)),
            Math.Min(Math.Min(first.Y, second.Y), Math.Min(third.Y, fourth.Y)));
        var maximum = new Vector2(
            Math.Max(Math.Max(first.X, second.X), Math.Max(third.X, fourth.X)),
            Math.Max(Math.Max(first.Y, second.Y), Math.Max(third.Y, fourth.Y)));
        var margin = Vector2.One * padding;
        rect = new Rect2(minimum - margin, maximum - minimum + margin * 2f);
        return rect.Size.X > 0f && rect.Size.Y > 0f;
    }

    public bool TryGetNode2DTargetRect(Node2D node, Rect2 localRect, out Rect2 rect)
    {
        rect = default;
        if (!GodotObject.IsInstanceValid(node))
        {
            return false;
        }

        var inverse = GetGlobalTransformWithCanvas().AffineInverse();
        var transform = node.GetGlobalTransformWithCanvas();
        var first = inverse * (transform * localRect.Position);
        var second = inverse * (transform * new Vector2(localRect.End.X, localRect.Position.Y));
        var third = inverse * (transform * localRect.End);
        var fourth = inverse * (transform * new Vector2(localRect.Position.X, localRect.End.Y));
        var minimum = new Vector2(
            Math.Min(Math.Min(first.X, second.X), Math.Min(third.X, fourth.X)),
            Math.Min(Math.Min(first.Y, second.Y), Math.Min(third.Y, fourth.Y)));
        var maximum = new Vector2(
            Math.Max(Math.Max(first.X, second.X), Math.Max(third.X, fourth.X)),
            Math.Max(Math.Max(first.Y, second.Y), Math.Max(third.Y, fourth.Y)));
        rect = new Rect2(minimum, maximum - minimum);
        return rect.Size.X > 0f && rect.Size.Y > 0f;
    }
}

internal static class CharacterAppearancePauseMenu
{
    private const string ButtonName = "SkinChangerCharacterAppearance";
    private const string ScreenName = "SkinChangerCharacterAppearanceScreen";
    private const string PauseButtonScenePath = "res://scenes/pause_menu/pause_menu_button.tscn";
    private static readonly FieldInfo? RunStateField =
        AccessTools.Field(typeof(NPauseMenu), "_runState");

    internal static void Attach(NPauseMenu pauseMenu)
    {
        try
        {
            var container = pauseMenu.GetNode<Control>("%ButtonContainer");
            // Always bind Resume, even when the appearance entry was hidden in an earlier run.
            var resume = container.GetNode<NPauseMenuButton>("Resume");
            PauseMenuAppearanceRightClick.Attach(resume,
                () => SetEntryVisible(pauseMenu, true), ModText.ShowAppearanceHoldHint);
            var existing = container.GetNodeOrNull<NPauseMenuButton>(ButtonName);
            var visibility = PauseMenuAppearanceEntryPolicy.Resolve(
                SkinService.ShouldShowInRunAppearanceEntry(),
                existing != null);
            if (existing != null)
            {
                existing.Visible = visibility.ShowButton;
                RebuildFocusNeighbors(container);
                return;
            }

            if (!visibility.CreateButton)
            {
                return;
            }

            var scene = ResourceLoader.Load<PackedScene>(PauseButtonScenePath) ??
                        throw new InvalidOperationException("无法加载暂停菜单按钮场景。");
            var button = scene.Instantiate<NPauseMenuButton>(PackedScene.GenEditState.Disabled);
            button.Name = ButtonName;
            container.AddChild(button);
            var compendium = container.GetNodeOrNull<NPauseMenuButton>("Compendium");
            if (compendium != null)
            {
                container.MoveChild(button, compendium.GetIndex() + 1);
            }

            button.Connect(
                NClickableControl.SignalName.Released,
                Callable.From<NButton>(_ => Open(pauseMenu)));
            PauseMenuAppearanceRightClick.Attach(button,
                () => SetEntryVisible(pauseMenu, false), ModText.HideAppearanceHoldHint);
            ModLocalization.Bind(button, () =>
                button.GetNode<MegaLabel>("Label")
                    .SetTextAutoSize(ModLocalization.Get(ModText.CharacterAppearance)));
            RebuildFocusNeighbors(container);
        }
        catch (Exception exception)
        {
            ModLog.Error("添加游戏内角色外观入口失败：" + exception);
        }
    }

    private static void SetEntryVisible(NPauseMenu pauseMenu, bool visible)
    {
        SkinService.SetShowInRunAppearanceEntry(visible);
        Attach(pauseMenu);
        if (!visible)
        {
            pauseMenu.GetNode<Control>("%ButtonContainer/Resume").GrabFocus();
        }
    }

    private static void Open(NPauseMenu pauseMenu)
    {
        try
        {
            if (RunStateField?.GetValue(pauseMenu) is not IRunState runState)
            {
                throw new InvalidOperationException("暂停菜单尚未绑定当前游戏。");
            }

            var player = LocalContext.GetMe(runState) ??
                         throw new InvalidOperationException("找不到当前玩家。");
            if (pauseMenu.GetParent() is not NSubmenuStack stack)
            {
                throw new InvalidOperationException("找不到游戏内子菜单容器。");
            }

            var screen = stack.GetNodeOrNull<CharacterAppearanceScreen>(ScreenName);
            if (screen == null)
            {
                screen = new CharacterAppearanceScreen
                {
                    Name = ScreenName,
                    Visible = false,
                    MouseFilter = Control.MouseFilterEnum.Ignore
                };
                stack.AddChild(screen);
            }

            screen.Initialize(player);
            stack.Push(screen);
        }
        catch (Exception exception)
        {
            ModLog.Error("打开游戏内角色外观界面失败：" + exception);
        }
    }

    private static void RebuildFocusNeighbors(Control container)
    {
        var buttons = container.GetChildren()
            .OfType<NPauseMenuButton>()
            .Where(button => button.Visible)
            .ToArray();
        for (var index = 0; index < buttons.Length; index++)
        {
            var button = buttons[index];
            button.FocusNeighborLeft = button.GetPath();
            button.FocusNeighborRight = button.GetPath();
            button.FocusNeighborTop = buttons[(index - 1 + buttons.Length) % buttons.Length].GetPath();
            button.FocusNeighborBottom = buttons[(index + 1) % buttons.Length].GetPath();
        }
    }
}

[HarmonyPatch]
internal static class CharacterAppearancePauseMenuPatch
{
    private static IEnumerable<MethodBase> TargetMethods() =>
    [
        AccessTools.Method(typeof(NPauseMenu), nameof(NPauseMenu._Ready)),
        AccessTools.Method(typeof(NPauseMenu), nameof(NPauseMenu.OnSubmenuOpened))
    ];

    [HarmonyPriority(Priority.Last)]
    private static void Postfix(NPauseMenu __instance) =>
        CharacterAppearancePauseMenu.Attach(__instance);
}
