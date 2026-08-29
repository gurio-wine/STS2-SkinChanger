using Godot;
using MegaCrit.Sts2.Core.Localization;

namespace STS2SkinChanger.Core;

internal enum ModText
{
    AncientCompendium,
    NoAncientsAvailable,
    GameDefault,
    GameOriginal,
    SkinnedCardsOnly,
    CardCategorySkinTooltip,
    IndividualCardTooltip,
    FollowCategory,
    MonsterSize,
    Reset,
    LoadOrderTitle,
    LoadOrderMessage,
    Acknowledge,
    DoNotShowAgain,
    PrioritizeAndRestart,
    LoadOrderFailure,
    DefaultVariant,
    CardSkinPriority,
    CardPriorityTooltip,
    CardArtCoverage,
    CurrentCardSource,
    AvailableCardSources,
    EnabledForCategory,
    Close,
    MonsterSkinPriority,
    MonsterPriorityTooltip,
    CardPresets,
    CardPresetTooltip,
    CardPresetName,
    SaveCurrentPreset,
    ApplyCardPreset,
    OverwriteCardPreset,
    RenameCardPreset,
    DeleteCardPreset,
    ConfirmDeleteCardPreset,
    ActiveCardPreset,
    NoCardPresets,
    CharacterAppearance,
    SelectAppearanceTarget,
    Skin,
    CharacterScale,
    HorizontalOffset,
    VerticalOffset,
    HoldToCompare,
    AlignmentGuide,
    AlignmentHint,
    AppearanceApplied,
    AppearanceQueued,
    NoCombatPreview,
    AppearanceFailed,
    ModelTransform,
    HealthBarTransform,
    IntentTransform,
    SelectionReticleTransform,
    HealthBarScale,
    FollowModelScale,
    FollowModelMovement,
    DirectDragHint,
    DirectDragIntentHint,
    LoadOtherPlayersCustomSkins
}

internal static class ModLocalization
{
    public const string DefaultVariantMarker = "{skin-changer-default}";

    private sealed record LanguagePack(
        string AncientCompendium,
        string NoAncientsAvailable,
        string GameDefault,
        string GameOriginal,
        string SkinnedCardsOnly,
        string CardCategorySkinTooltip,
        string IndividualCardTooltip,
        string FollowCategory,
        string MonsterSize,
        string Reset,
        string LoadOrderTitle,
        string LoadOrderMessage,
        string Acknowledge,
        string DoNotShowAgain,
        string PrioritizeAndRestart,
        string LoadOrderFailure,
        string DefaultVariant)
    {
        public string Get(ModText text) => text switch
        {
            ModText.AncientCompendium => AncientCompendium,
            ModText.NoAncientsAvailable => NoAncientsAvailable,
            ModText.GameDefault => GameDefault,
            ModText.GameOriginal => GameOriginal,
            ModText.SkinnedCardsOnly => SkinnedCardsOnly,
            ModText.CardCategorySkinTooltip => CardCategorySkinTooltip,
            ModText.IndividualCardTooltip => IndividualCardTooltip,
            ModText.FollowCategory => FollowCategory,
            ModText.MonsterSize => MonsterSize,
            ModText.Reset => Reset,
            ModText.LoadOrderTitle => LoadOrderTitle,
            ModText.LoadOrderMessage => LoadOrderMessage,
            ModText.Acknowledge => Acknowledge,
            ModText.DoNotShowAgain => DoNotShowAgain,
            ModText.PrioritizeAndRestart => PrioritizeAndRestart,
            ModText.LoadOrderFailure => LoadOrderFailure,
            ModText.DefaultVariant => DefaultVariant,
            _ => throw new ArgumentOutOfRangeException(nameof(text), text, null)
        };
    }

    private sealed record AppearanceLanguagePack(
        string CharacterAppearance,
        string SelectAppearanceTarget,
        string Skin,
        string CharacterScale,
        string HorizontalOffset,
        string VerticalOffset,
        string HoldToCompare,
        string AlignmentGuide,
        string AlignmentHint,
        string AppearanceApplied,
        string AppearanceQueued,
        string NoCombatPreview,
        string AppearanceFailed)
    {
        public string Get(ModText text) => text switch
        {
            ModText.CharacterAppearance => CharacterAppearance,
            ModText.SelectAppearanceTarget => SelectAppearanceTarget,
            ModText.Skin => Skin,
            ModText.CharacterScale => CharacterScale,
            ModText.HorizontalOffset => HorizontalOffset,
            ModText.VerticalOffset => VerticalOffset,
            ModText.HoldToCompare => HoldToCompare,
            ModText.AlignmentGuide => AlignmentGuide,
            ModText.AlignmentHint => AlignmentHint,
            ModText.AppearanceApplied => AppearanceApplied,
            ModText.AppearanceQueued => AppearanceQueued,
            ModText.NoCombatPreview => NoCombatPreview,
            ModText.AppearanceFailed => AppearanceFailed,
            _ => throw new ArgumentOutOfRangeException(nameof(text), text, null)
        };
    }

    private sealed record CardPriorityLanguagePack(
        string EnabledForCategory,
        string CardSkinPriority,
        string CardPriorityTooltip,
        string CardArtCoverage,
        string CurrentCardSource,
        string AvailableCardSources,
        string Close,
        string MonsterSkinPriority,
        string MonsterPriorityTooltip)
    {
        public string Get(ModText text) => text switch
        {
            ModText.CardSkinPriority => CardSkinPriority,
            ModText.CardPriorityTooltip => CardPriorityTooltip,
            ModText.CardArtCoverage => CardArtCoverage,
            ModText.CurrentCardSource => CurrentCardSource,
            ModText.AvailableCardSources => AvailableCardSources,
            ModText.EnabledForCategory => EnabledForCategory,
            ModText.Close => Close,
            ModText.MonsterSkinPriority => MonsterSkinPriority,
            ModText.MonsterPriorityTooltip => MonsterPriorityTooltip,
            _ => throw new ArgumentOutOfRangeException(nameof(text), text, null)
        };
    }

    private sealed record CardPresetLanguagePack(
        string CardPresets,
        string CardPresetTooltip,
        string CardPresetName,
        string SaveCurrentPreset,
        string ApplyCardPreset,
        string OverwriteCardPreset,
        string RenameCardPreset,
        string DeleteCardPreset,
        string ConfirmDeleteCardPreset,
        string ActiveCardPreset,
        string NoCardPresets)
    {
        public string Get(ModText text) => text switch
        {
            ModText.CardPresets => CardPresets,
            ModText.CardPresetTooltip => CardPresetTooltip,
            ModText.CardPresetName => CardPresetName,
            ModText.SaveCurrentPreset => SaveCurrentPreset,
            ModText.ApplyCardPreset => ApplyCardPreset,
            ModText.OverwriteCardPreset => OverwriteCardPreset,
            ModText.RenameCardPreset => RenameCardPreset,
            ModText.DeleteCardPreset => DeleteCardPreset,
            ModText.ConfirmDeleteCardPreset => ConfirmDeleteCardPreset,
            ModText.ActiveCardPreset => ActiveCardPreset,
            ModText.NoCardPresets => NoCardPresets,
            _ => throw new ArgumentOutOfRangeException(nameof(text), text, null)
        };
    }

    private sealed record AdjustmentLanguagePack(
        string ModelTransform,
        string HealthBarTransform,
        string IntentTransform,
        string SelectionReticleTransform,
        string HealthBarScale,
        string FollowModelScale,
        string FollowModelMovement,
        string DirectDragHint,
        string DirectDragIntentHint)
    {
        public string Get(ModText text) => text switch
        {
            ModText.ModelTransform => ModelTransform,
            ModText.HealthBarTransform => HealthBarTransform,
            ModText.IntentTransform => IntentTransform,
            ModText.SelectionReticleTransform => SelectionReticleTransform,
            ModText.HealthBarScale => HealthBarScale,
            ModText.FollowModelScale => FollowModelScale,
            ModText.FollowModelMovement => FollowModelMovement,
            ModText.DirectDragHint => DirectDragHint,
            ModText.DirectDragIntentHint => DirectDragIntentHint,
            _ => throw new ArgumentOutOfRangeException(nameof(text), text, null)
        };
    }

    private static readonly IReadOnlyDictionary<string, LanguagePack> Packs =
        new Dictionary<string, LanguagePack>(StringComparer.OrdinalIgnoreCase)
        {
            ["eng"] = new(
                "Ancient Compendium",
                "No Ancients available to preview",
                "Game default",
                "Original game",
                "Cards with skins",
                "Card skins for this category",
                "Choose art for this card; Follow category uses the Card Compendium setting",
                "Follow category",
                "Size",
                "Reset",
                "Skin Changer load order",
                "The skin mods below load before Skin Changer, so their DLL/PCK cannot be fully managed:\n{0}\n\n[color=#F0C951][b]Automatic adjustment moves Skin Changer only before all skin mods. It will NOT move above every mod.[/b][/color]",
                "Got it",
                "Don't show again",
                "Move before skins & restart",
                "Adjusting the load order or restarting failed. The setting was not ignored. Move Skin Changer before every skin mod manually in Mod Manager, then restart the game.",
                "Default"),
            ["zhs"] = new(
                "先古图鉴",
                "没有可预览的先古之民",
                "游戏默认",
                "游戏原版",
                "有皮肤卡牌",
                "此分类的卡牌皮肤",
                "为这张卡单独选择卡图；“跟随分类”会使用卡牌总览中的设置",
                "跟随分类",
                "大小",
                "重置",
                "皮肤切换器-Skin Changer 加载顺序",
                "以下皮肤 Mod 排在本 Mod 之前，它们的 DLL/PCK 会先加载，因此无法被完整接管：\n{0}\n\n[color=#F0C951][b]自动调整只会把本 Mod 移到所有皮肤 Mod 之前，不会移到全部 Mod 的最上方。[/b][/color]",
                "知道了",
                "不再提示",
                "移到皮肤 Mod 前并重启",
                "自动调整顺序或重启失败，设置没有被静默忽略。请在 Mod 管理界面手动把皮肤切换器-Skin Changer 移到所有皮肤 Mod 之前，然后重启游戏。",
                "默认"),
            ["zht"] = new(
                "先古圖鑑",
                "沒有可預覽的先古之民",
                "遊戲預設",
                "遊戲原版",
                "有外觀卡牌",
                "此分類的卡牌外觀",
                "為這張卡單獨選擇卡圖；「跟隨分類」會使用卡牌圖鑑中的設定",
                "跟隨分類",
                "大小",
                "重設",
                "外觀切換器-Skin Changer 載入順序",
                "以下外觀 Mod 排在本 Mod 之前，其 DLL/PCK 會先載入，因此無法被完整管理：\n{0}\n\n[color=#F0C951][b]自動調整只會將本 Mod 移到所有外觀 Mod 之前，不會移到全部 Mod 的最上方。[/b][/color]",
                "知道了",
                "不再提示",
                "移到外觀 Mod 前並重新啟動",
                "自動調整順序或重新啟動失敗，設定並未被忽略。請在 Mod 管理畫面手動將 Skin Changer 移到所有外觀 Mod 之前，然後重新啟動遊戲。",
                "預設"),
            ["deu"] = new(
                "Ahnenkompendium",
                "Keine Ahnen für die Vorschau verfügbar",
                "Spielstandard",
                "Originalspiel",
                "Karten mit Skins",
                "Karten-Skins für diese Kategorie",
                "Wähle das Bild dieser Karte; Kategorie folgen nutzt die Einstellung im Kartenkompendium",
                "Kategorie folgen",
                "Größe",
                "Zurücksetzen",
                "Skin-Changer-Ladereihenfolge",
                "Die folgenden Skin-Mods werden vor Skin Changer geladen; ihre DLL/PCK können daher nicht vollständig verwaltet werden:\n{0}\n\n[color=#F0C951][b]Die automatische Anpassung verschiebt Skin Changer nur vor alle Skin-Mods, nicht an die Spitze aller Mods.[/b][/color]",
                "Verstanden",
                "Nicht mehr anzeigen",
                "Vor Skins & neu starten",
                "Das Anpassen der Ladereihenfolge oder der Neustart ist fehlgeschlagen. Verschiebe Skin Changer im Mod-Manager manuell vor alle Skin-Mods und starte das Spiel neu.",
                "Standard"),
            ["esp"] = new(
                "Compendio de Antiguos",
                "No hay Antiguos disponibles para previsualizar",
                "Predeterminado del juego",
                "Juego original",
                "Cartas con aspectos",
                "Aspectos de cartas para esta categoría",
                "Elige el arte de esta carta; Seguir categoría usa el ajuste del compendio de cartas",
                "Seguir categoría",
                "Tamaño",
                "Restablecer",
                "Orden de carga de Skin Changer",
                "Los siguientes mods de aspectos se cargan antes que Skin Changer, por lo que sus DLL/PCK no pueden administrarse por completo:\n{0}\n\n[color=#F0C951][b]El ajuste automático solo mueve Skin Changer antes de todos los mods de aspectos; no lo coloca por encima de todos los mods.[/b][/color]",
                "Entendido",
                "No volver a mostrar",
                "Mover antes de aspectos y reiniciar",
                "No se pudo ajustar el orden o reiniciar. Mueve Skin Changer manualmente antes de todos los mods de aspectos y reinicia el juego.",
                "Predeterminado"),
            ["fra"] = new(
                "Compendium des Anciens",
                "Aucun Ancien disponible pour l’aperçu",
                "Réglage du jeu",
                "Jeu d’origine",
                "Cartes avec skins",
                "Skins de cartes pour cette catégorie",
                "Choisissez l’image de cette carte ; Suivre la catégorie utilise le réglage du compendium des cartes",
                "Suivre la catégorie",
                "Taille",
                "Réinitialiser",
                "Ordre de chargement de Skin Changer",
                "Les mods de skins suivants se chargent avant Skin Changer ; leurs DLL/PCK ne peuvent donc pas être entièrement gérés :\n{0}\n\n[color=#F0C951][b]L’ajustement automatique place Skin Changer uniquement avant tous les mods de skins, pas au-dessus de tous les mods.[/b][/color]",
                "Compris",
                "Ne plus afficher",
                "Placer avant les skins et redémarrer",
                "L’ajustement de l’ordre ou le redémarrage a échoué. Placez manuellement Skin Changer avant tous les mods de skins, puis redémarrez le jeu.",
                "Par défaut"),
            ["ita"] = new(
                "Compendio degli Antichi",
                "Nessun Antico disponibile per l’anteprima",
                "Predefinito del gioco",
                "Gioco originale",
                "Carte con skin",
                "Skin delle carte per questa categoria",
                "Scegli l’immagine di questa carta; Segui categoria usa l’impostazione del compendio carte",
                "Segui categoria",
                "Dimensione",
                "Ripristina",
                "Ordine di caricamento di Skin Changer",
                "I seguenti mod delle skin vengono caricati prima di Skin Changer, quindi i loro DLL/PCK non possono essere gestiti completamente:\n{0}\n\n[color=#F0C951][b]La regolazione automatica sposta Skin Changer solo prima di tutti i mod delle skin, non sopra tutti i mod.[/b][/color]",
                "Ho capito",
                "Non mostrare più",
                "Sposta prima delle skin e riavvia",
                "La regolazione dell’ordine o il riavvio non è riuscito. Sposta manualmente Skin Changer prima di tutti i mod delle skin e riavvia il gioco.",
                "Predefinito"),
            ["jpn"] = new(
                "エンシェント図鑑",
                "プレビューできるエンシェントがありません",
                "ゲーム標準",
                "ゲーム原版",
                "スキン付きカード",
                "この分類のカードスキン",
                "このカードの画像を個別に選択。「分類に従う」はカード図鑑の設定を使用します",
                "分類に従う",
                "サイズ",
                "リセット",
                "Skin Changerのロード順",
                "次のスキンModがSkin Changerより先に読み込まれるため、そのDLL/PCKを完全には管理できません：\n{0}\n\n[color=#F0C951][b]自動調整ではSkin ChangerをすべてのスキンModより前に移動するだけで、全Modの先頭には移動しません。[/b][/color]",
                "了解",
                "今後表示しない",
                "スキンModの前へ移動して再起動",
                "ロード順の調整または再起動に失敗しました。Skin ChangerをすべてのスキンModより前に手動で移動し、ゲームを再起動してください。",
                "デフォルト"),
            ["kor"] = new(
                "고대인 도감",
                "미리 볼 수 있는 고대인이 없습니다",
                "게임 기본",
                "게임 원본",
                "스킨 카드",
                "이 분류의 카드 스킨",
                "이 카드의 그림을 따로 선택합니다. 분류 따르기는 카드 도감 설정을 사용합니다",
                "분류 따르기",
                "크기",
                "초기화",
                "Skin Changer 로드 순서",
                "다음 스킨 Mod가 Skin Changer보다 먼저 로드되어 해당 DLL/PCK를 완전히 관리할 수 없습니다:\n{0}\n\n[color=#F0C951][b]자동 조정은 Skin Changer를 모든 스킨 Mod 앞에만 옮기며, 전체 Mod 목록의 맨 위로 옮기지 않습니다.[/b][/color]",
                "확인",
                "다시 표시하지 않기",
                "스킨 Mod 앞으로 옮기고 재시작",
                "로드 순서 조정 또는 재시작에 실패했습니다. Skin Changer를 모든 스킨 Mod 앞에 직접 옮긴 뒤 게임을 재시작하세요.",
                "기본"),
            ["pol"] = new(
                "Kompendium Pradawnych",
                "Brak Pradawnych do podglądu",
                "Domyślne gry",
                "Oryginalna gra",
                "Karty ze skórkami",
                "Skórki kart dla tej kategorii",
                "Wybierz grafikę tej karty; Podążaj za kategorią używa ustawienia z kompendium kart",
                "Podążaj za kategorią",
                "Rozmiar",
                "Resetuj",
                "Kolejność ładowania Skin Changer",
                "Następujące mody skórek ładują się przed Skin Changer, więc ich DLL/PCK nie mogą być w pełni zarządzane:\n{0}\n\n[color=#F0C951][b]Automatyczna zmiana przenosi Skin Changer tylko przed wszystkie mody skórek, a nie na samą górę całej listy modów.[/b][/color]",
                "Rozumiem",
                "Nie pokazuj ponownie",
                "Przed skórki i uruchom ponownie",
                "Zmiana kolejności lub ponowne uruchomienie nie powiodło się. Ręcznie przenieś Skin Changer przed wszystkie mody skórek i uruchom grę ponownie.",
                "Domyślne"),
            ["ptb"] = new(
                "Compêndio dos Anciões",
                "Nenhum Ancião disponível para prévia",
                "Padrão do jogo",
                "Jogo original",
                "Cartas com visuais",
                "Visuais de cartas desta categoria",
                "Escolha a arte desta carta; Seguir categoria usa a configuração do compêndio de cartas",
                "Seguir categoria",
                "Tamanho",
                "Redefinir",
                "Ordem de carregamento do Skin Changer",
                "Os seguintes mods de visuais carregam antes do Skin Changer, portanto seus DLL/PCK não podem ser gerenciados por completo:\n{0}\n\n[color=#F0C951][b]O ajuste automático move o Skin Changer apenas para antes de todos os mods de visuais; ele não vai para o topo de todos os mods.[/b][/color]",
                "Entendi",
                "Não mostrar novamente",
                "Mover antes dos visuais e reiniciar",
                "Não foi possível ajustar a ordem ou reiniciar. Mova o Skin Changer manualmente para antes de todos os mods de visuais e reinicie o jogo.",
                "Padrão"),
            ["rus"] = new(
                "Энциклопедия Древних",
                "Нет Древних для предпросмотра",
                "По умолчанию игры",
                "Оригинальная игра",
                "Карты с обликами",
                "Облики карт для этой категории",
                "Выберите изображение этой карты; Следовать категории использует настройку энциклопедии карт",
                "Следовать категории",
                "Размер",
                "Сбросить",
                "Порядок загрузки Skin Changer",
                "Следующие моды обликов загружаются раньше Skin Changer, поэтому их DLL/PCK нельзя полностью контролировать:\n{0}\n\n[color=#F0C951][b]Автонастройка перемещает Skin Changer только перед всеми модами обликов, а не в самый верх общего списка модов.[/b][/color]",
                "Понятно",
                "Больше не показывать",
                "Перед обликами и перезапустить",
                "Не удалось изменить порядок или перезапустить игру. Вручную поставьте Skin Changer перед всеми модами обликов и перезапустите игру.",
                "По умолчанию"),
            ["spa"] = new(
                "Compendio de los Antiguos",
                "No hay Antiguos disponibles para previsualizar",
                "Predeterminado del juego",
                "Juego original",
                "Cartas con aspectos",
                "Aspectos de cartas para esta categoría",
                "Elige la ilustración de esta carta; Seguir categoría usa el ajuste del compendio de cartas",
                "Seguir categoría",
                "Tamaño",
                "Restablecer",
                "Orden de carga de Skin Changer",
                "Los siguientes mods de aspectos se cargan antes que Skin Changer, por lo que sus DLL/PCK no pueden gestionarse por completo:\n{0}\n\n[color=#F0C951][b]El ajuste automático solo mueve Skin Changer antes de todos los mods de aspectos; no lo coloca por encima de todos los mods.[/b][/color]",
                "Entendido",
                "No volver a mostrar",
                "Mover antes de aspectos y reiniciar",
                "No se pudo ajustar el orden o reiniciar. Mueve manualmente Skin Changer antes de todos los mods de aspectos y reinicia el juego.",
                "Predeterminado"),
            ["tha"] = new(
                "สารานุกรม Ancient",
                "ไม่มี Ancient ที่พร้อมให้ดูตัวอย่าง",
                "ค่าเริ่มต้นของเกม",
                "เกมต้นฉบับ",
                "การ์ดที่มีสกิน",
                "สกินการ์ดสำหรับหมวดนี้",
                "เลือกภาพของการ์ดใบนี้โดยเฉพาะ; ตามหมวดจะใช้ค่าจากสารานุกรมการ์ด",
                "ตามหมวด",
                "ขนาด",
                "รีเซ็ต",
                "ลำดับการโหลด Skin Changer",
                "Mod สกินต่อไปนี้โหลดก่อน Skin Changer จึงไม่สามารถจัดการ DLL/PCK ได้อย่างสมบูรณ์:\n{0}\n\n[color=#F0C951][b]การปรับอัตโนมัติจะย้าย Skin Changer ไปไว้ก่อน Mod สกินทั้งหมดเท่านั้น และจะไม่ย้ายไปบนสุดของ Mod ทั้งหมด[/b][/color]",
                "เข้าใจแล้ว",
                "ไม่ต้องแสดงอีก",
                "ย้ายไว้ก่อน Mod สกินและเริ่มใหม่",
                "ไม่สามารถปรับลำดับหรือเริ่มเกมใหม่ได้ โปรดย้าย Skin Changer ไปไว้ก่อน Mod สกินทั้งหมดด้วยตนเอง แล้วเริ่มเกมใหม่",
                "ค่าเริ่มต้น"),
            ["tur"] = new(
                "Kadimler Külliyatı",
                "Önizlenecek Kadim yok",
                "Oyun varsayılanı",
                "Orijinal oyun",
                "Görünümlü kartlar",
                "Bu kategori için kart görünümleri",
                "Bu kartın görselini seç; Kategoriyi izle, kart külliyatındaki ayarı kullanır",
                "Kategoriyi izle",
                "Boyut",
                "Sıfırla",
                "Skin Changer yükleme sırası",
                "Aşağıdaki görünüm Modları Skin Changer'dan önce yükleniyor; bu nedenle DLL/PCK dosyaları tam olarak yönetilemiyor:\n{0}\n\n[color=#F0C951][b]Otomatik ayar Skin Changer'ı yalnızca tüm görünüm Modlarının önüne taşır; bütün Modların en üstüne taşımaz.[/b][/color]",
                "Anladım",
                "Bir daha gösterme",
                "Görünümlerin önüne taşı ve yeniden başlat",
                "Sıra ayarı veya yeniden başlatma başarısız oldu. Skin Changer'ı tüm görünüm Modlarının önüne elle taşıyıp oyunu yeniden başlatın.",
                "Varsayılan")
        };

    private static readonly IReadOnlyDictionary<string, AppearanceLanguagePack> AppearancePacks =
        new Dictionary<string, AppearanceLanguagePack>(StringComparer.OrdinalIgnoreCase)
        {
            ["eng"] = new(
                "Appearance", "Click a character, monster, Ancient, companion, or merchant to adjust its appearance.",
                "Skin", "Scale", "Horizontal offset", "Vertical offset",
                "Hold to compare", "Alignment guide",
                "Gold cross: original anchor · Cyan frame: current bounds",
                "Applied immediately", "Queued until the current action finishes",
                "Saved. Live positioning preview is available during combat.", "Could not apply appearance"),
            ["zhs"] = new(
                "外观", "请选择一个角色、怪物、先古之民、同伴或商人来调整外观。",
                "皮肤", "缩放", "水平位移", "垂直位移",
                "按住对比原位", "定位参考线",
                "金色十字：原始落点 · 青色边框：当前范围",
                "已立即应用", "当前动作结束后自动应用",
                "已保存；进入战斗后可实时预览位置。", "应用角色外观失败"),
            ["zht"] = new(
                "外觀", "請選擇一個角色、怪物、先古之民、同伴或商人來調整外觀。",
                "外觀", "縮放", "水平位移", "垂直位移",
                "按住比較原位", "定位參考線",
                "金色十字：原始落點 · 青色邊框：目前範圍",
                "已立即套用", "目前動作結束後自動套用",
                "已儲存；進入戰鬥後可即時預覽位置。", "無法套用角色外觀"),
            ["deu"] = new(
                "Aussehen", "Klicke auf einen Charakter, ein Monster, einen Ahnen, einen Begleiter oder den Händler, um das Aussehen anzupassen.",
                "Skin", "Skalierung", "Horizontaler Versatz", "Vertikaler Versatz",
                "Zum Vergleichen halten", "Ausrichtungshilfe",
                "Goldenes Kreuz: Ursprung · Türkiser Rahmen: aktuelle Grenzen",
                "Sofort angewendet", "Wird nach der aktuellen Aktion angewendet",
                "Gespeichert. Die Live-Vorschau ist im Kampf verfügbar.", "Aussehen konnte nicht angewendet werden"),
            ["esp"] = new(
                "Aspecto", "Haz clic en un personaje, monstruo, Antiguo, compañero o comerciante para ajustar su aspecto.",
                "Aspecto", "Escala", "Desplazamiento horizontal", "Desplazamiento vertical",
                "Mantén para comparar", "Guía de alineación",
                "Cruz dorada: origen · Marco cian: límites actuales",
                "Aplicado al instante", "Se aplicará al terminar la acción actual",
                "Guardado. La vista previa en vivo está disponible en combate.", "No se pudo aplicar el aspecto"),
            ["fra"] = new(
                "Apparence", "Cliquez sur un personnage, un monstre, un Ancien, un compagnon ou le marchand pour modifier son apparence.",
                "Skin", "Échelle", "Décalage horizontal", "Décalage vertical",
                "Maintenir pour comparer", "Guide d’alignement",
                "Croix dorée : origine · Cadre cyan : limites actuelles",
                "Appliqué immédiatement", "Sera appliqué après l’action en cours",
                "Enregistré. L’aperçu en direct est disponible en combat.", "Impossible d’appliquer l’apparence"),
            ["ita"] = new(
                "Aspetto", "Fai clic su un personaggio, un mostro, un Antico, un compagno o il mercante per modificarne l’aspetto.",
                "Skin", "Scala", "Spostamento orizzontale", "Spostamento verticale",
                "Tieni premuto per confrontare", "Guida allineamento",
                "Croce dorata: origine · Cornice ciano: limiti attuali",
                "Applicato subito", "Verrà applicato al termine dell’azione corrente",
                "Salvato. L’anteprima dal vivo è disponibile in combattimento.", "Impossibile applicare l’aspetto"),
            ["jpn"] = new(
                "外見", "外見を調整するキャラクター、モンスター、エンシェント、仲間、または商人をクリックしてください。",
                "スキン", "拡大率", "横位置", "縦位置",
                "長押しで元と比較", "位置合わせガイド",
                "金の十字：元の基準点 · 水色の枠：現在の範囲",
                "すぐに適用しました", "現在のアクション終了後に適用します",
                "保存しました。戦闘中に位置をリアルタイム確認できます。", "外見を適用できませんでした"),
            ["kor"] = new(
                "외형", "외형을 조정할 캐릭터, 몬스터, 고대인, 동료 또는 상인을 클릭하세요.",
                "스킨", "크기", "가로 위치", "세로 위치",
                "길게 눌러 원본 비교", "정렬 안내선",
                "금색 십자: 원래 기준점 · 청록색 테두리: 현재 범위",
                "즉시 적용됨", "현재 행동이 끝나면 적용됨",
                "저장됨. 전투 중 위치를 실시간으로 확인할 수 있습니다.", "외형을 적용하지 못했습니다"),
            ["pol"] = new(
                "Wygląd", "Kliknij postać, potwora, Pradawnego, towarzysza lub kupca, aby dostosować wygląd.",
                "Skórka", "Skala", "Przesunięcie poziome", "Przesunięcie pionowe",
                "Przytrzymaj, aby porównać", "Linie wyrównania",
                "Złoty krzyż: punkt bazowy · Turkusowa ramka: aktualny obszar",
                "Zastosowano natychmiast", "Zostanie zastosowane po bieżącej akcji",
                "Zapisano. Podgląd na żywo jest dostępny w walce.", "Nie udało się zastosować wyglądu"),
            ["ptb"] = new(
                "Visual", "Clique em um personagem, monstro, Ancião, companheiro ou mercador para ajustar seu visual.",
                "Visual", "Escala", "Deslocamento horizontal", "Deslocamento vertical",
                "Segure para comparar", "Guia de alinhamento",
                "Cruz dourada: origem · Moldura ciano: limites atuais",
                "Aplicado imediatamente", "Será aplicado após a ação atual",
                "Salvo. A prévia ao vivo está disponível em combate.", "Não foi possível aplicar o visual"),
            ["rus"] = new(
                "Облик", "Нажмите на персонажа, монстра, Древнего, спутника или торговца, чтобы настроить облик.",
                "Облик", "Масштаб", "Смещение по горизонтали", "Смещение по вертикали",
                "Удерживать для сравнения", "Направляющие",
                "Золотой крест: исходная точка · Голубая рамка: текущие границы",
                "Применено сразу", "Будет применено после текущего действия",
                "Сохранено. Предпросмотр положения доступен в бою.", "Не удалось применить облик"),
            ["spa"] = new(
                "Aspecto", "Haz clic en un personaje, monstruo, Antiguo, compañero o comerciante para ajustar su aspecto.",
                "Aspecto", "Escala", "Desplazamiento horizontal", "Desplazamiento vertical",
                "Mantén para comparar", "Guía de alineación",
                "Cruz dorada: origen · Marco cian: límites actuales",
                "Aplicado al instante", "Se aplicará al terminar la acción actual",
                "Guardado. La vista previa en vivo está disponible en combate.", "No se pudo aplicar el aspecto"),
            ["tha"] = new(
                "รูปลักษณ์", "คลิกตัวละคร มอนสเตอร์ Ancient เพื่อนร่วมทาง หรือพ่อค้าเพื่อปรับรูปลักษณ์",
                "สกิน", "ขนาด", "ตำแหน่งแนวนอน", "ตำแหน่งแนวตั้ง",
                "กดค้างเพื่อเทียบ", "เส้นช่วยจัดตำแหน่ง",
                "กากบาทสีทอง: จุดเดิม · กรอบสีฟ้า: ขอบเขตปัจจุบัน",
                "ใช้ทันทีแล้ว", "จะใช้หลังแอ็กชันปัจจุบันจบ",
                "บันทึกแล้ว ดูตำแหน่งแบบสดได้ระหว่างต่อสู้", "ไม่สามารถใช้รูปลักษณ์ได้"),
            ["tur"] = new(
                "Görünüm", "Görünümü ayarlamak için bir karaktere, canavara, Kadime, yoldaşa veya tüccara tıkla.",
                "Görünüm", "Ölçek", "Yatay konum", "Dikey konum",
                "Karşılaştırmak için basılı tut", "Hizalama kılavuzu",
                "Altın artı: özgün konum · Camgöbeği çerçeve: geçerli sınırlar",
                "Hemen uygulandı", "Geçerli eylem bitince uygulanacak",
                "Kaydedildi. Canlı konum önizlemesi savaşta kullanılabilir.", "Görünüm uygulanamadı")
        };

    private static readonly IReadOnlyDictionary<string, CardPriorityLanguagePack> CardPriorityPacks =
        new Dictionary<string, CardPriorityLanguagePack>(StringComparer.OrdinalIgnoreCase)
        {
            ["eng"] = new(
                "Enabled", "Skin priority ({0})",
                "Enable card-skin mods and adjust priority. Each card uses every visual effect from the highest enabled mod that supports it; effects are never mixed between mods.",
                "Art {0}/{1}", "Current: {0}", "Available: {0}", "Close",
                "Skin priority",
                "Set the enabled skin mods and their order for this bestiary region. Each monster uses the highest enabled mod that supports it; individual choices take priority."),
            ["zhs"] = new(
                "启用", "皮肤优先级（{0}）",
                "勾选“启用”决定 Mod 是否作用于当前分类；每张牌只使用最靠上且支持它的 Mod 的全部外观效果，不会混用多个 Mod。",
                "卡图 {0}/{1}", "当前：{0}", "可用：{0}", "关闭",
                "皮肤优先级",
                "设置当前图鉴地区启用的皮肤 Mod 和顺序；每只怪物使用最靠上且支持它的 Mod，单只怪物的手动选择优先。"),
            ["zht"] = new(
                "啟用", "外觀優先順序（{0}）",
                "勾選「啟用」決定 Mod 是否作用於目前分類；每張牌只使用最上方且支援它的 Mod 的全部外觀效果，不會混用多個 Mod。",
                "卡圖 {0}/{1}", "目前：{0}", "可用：{0}", "關閉",
                "外觀優先順序",
                "設定目前圖鑑地區啟用的外觀 Mod 與順序；每隻怪物使用最上方且支援它的 Mod，單隻怪物的手動選擇優先。"),
            ["deu"] = new(
                "Aktiv", "Skin-Priorität ({0})",
                "Aktiviere und ordne Karten-Skin-Mods. Jede Karte nutzt alle Effekte des höchsten aktiven Mods, der sie unterstützt; Mods werden nie gemischt.",
                "Bilder {0}/{1}", "Aktuell: {0}", "Verfügbar: {0}", "Schließen",
                "Skin-Priorität",
                "Aktiviere und ordne Skin-Mods für diese Bestiarium-Region. Jedes Monster nutzt den höchsten unterstützten Mod; einzelne Auswahlen haben Vorrang."),
            ["esp"] = new(
                "Activo", "Prioridad de aspectos ({0})",
                "Activa y ordena los mods. Cada carta usa todos los efectos del mod activo más alto que la admita; nunca se mezclan mods.",
                "Arte {0}/{1}", "Actual: {0}", "Disponibles: {0}", "Cerrar",
                "Prioridad de aspectos",
                "Activa y ordena aspectos para esta región del bestiario. Cada monstruo usa el primer mod compatible; las elecciones individuales tienen prioridad."),
            ["fra"] = new(
                "Activé", "Priorité des skins ({0})",
                "Activez et classez les mods. Chaque carte utilise tous les effets du premier mod actif qui la prend en charge ; les mods ne sont jamais mélangés.",
                "Images {0}/{1}", "Actuel : {0}", "Disponibles : {0}", "Fermer",
                "Priorité des skins",
                "Activez et classez les skins de cette région du bestiaire. Chaque monstre utilise le premier mod compatible ; les choix individuels sont prioritaires."),
            ["ita"] = new(
                "Attivo", "Priorità skin ({0})",
                "Attiva e ordina i mod. Ogni carta usa tutti gli effetti del primo mod attivo che la supporta; i mod non vengono mai combinati.",
                "Immagini {0}/{1}", "Attuale: {0}", "Disponibili: {0}", "Chiudi",
                "Priorità skin",
                "Attiva e ordina le skin per questa regione del bestiario. Ogni mostro usa il primo mod compatibile; le scelte individuali hanno priorità."),
            ["jpn"] = new(
                "有効", "スキン優先順位（{0}）",
                "カードスキンModを有効化して並べ替えます。各カードは対応する最上位の有効Modの全外観効果だけを使い、複数Modを混在させません。",
                "画像 {0}/{1}", "現在：{0}", "利用可能：{0}", "閉じる",
                "スキン優先順位",
                "この図鑑地域で有効なスキンModと順序を設定します。各モンスターは対応する最上位Modを使い、個別指定が優先されます。"),
            ["kor"] = new(
                "사용", "스킨 우선순위 ({0})",
                "카드 스킨 Mod를 켜고 순서를 조정합니다. 각 카드는 지원하는 가장 위의 활성 Mod 효과만 모두 사용하며 여러 Mod를 섞지 않습니다.",
                "그림 {0}/{1}", "현재: {0}", "사용 가능: {0}", "닫기",
                "스킨 우선순위",
                "이 도감 지역에서 사용할 스킨 Mod와 순서를 정합니다. 각 몬스터는 지원하는 최상위 Mod를 사용하며 개별 선택이 우선합니다."),
            ["pol"] = new(
                "Włączona", "Priorytet skórek ({0})",
                "Włącz i uporządkuj mody. Każda karta używa wszystkich efektów najwyższego aktywnego moda, który ją obsługuje; mody nie są mieszane.",
                "Grafiki {0}/{1}", "Bieżąca: {0}", "Dostępne: {0}", "Zamknij",
                "Priorytet skórek",
                "Włącz i uporządkuj skórki dla tego regionu bestiariusza. Każdy potwór używa najwyższego zgodnego moda; wybór indywidualny ma pierwszeństwo."),
            ["ptb"] = new(
                "Ativo", "Prioridade dos visuais ({0})",
                "Ative e ordene os mods. Cada carta usa todos os efeitos do mod ativo mais alto que a suporta; efeitos de mods diferentes não são misturados.",
                "Artes {0}/{1}", "Atual: {0}", "Disponíveis: {0}", "Fechar",
                "Prioridade dos visuais",
                "Ative e ordene visuais para esta região do bestiário. Cada monstro usa o primeiro mod compatível; escolhas individuais têm prioridade."),
            ["rus"] = new(
                "Включено", "Приоритет обликов ({0})",
                "Включайте и упорядочивайте моды. Каждая карта использует все эффекты самого верхнего активного мода, который её поддерживает; моды не смешиваются.",
                "Изображения {0}/{1}", "Текущий: {0}", "Доступны: {0}", "Закрыть",
                "Приоритет обликов",
                "Включайте и упорядочивайте облики для этого региона бестиария. Монстр использует верхний совместимый мод; отдельный выбор имеет приоритет."),
            ["spa"] = new(
                "Activo", "Prioridad de aspectos ({0})",
                "Activa y ordena los mods. Cada carta usa todos los efectos del mod activo más alto que la admita; nunca se mezclan mods.",
                "Arte {0}/{1}", "Actual: {0}", "Disponibles: {0}", "Cerrar",
                "Prioridad de aspectos",
                "Activa y ordena aspectos para esta región del bestiario. Cada monstruo usa el primer mod compatible; las elecciones individuales tienen prioridad."),
            ["tha"] = new(
                "เปิดใช้", "ลำดับสกิน ({0})",
                "เปิดใช้และจัดลำดับ Mod สกินการ์ด การ์ดแต่ละใบจะใช้เอฟเฟกต์ทั้งหมดจาก Mod ที่เปิดใช้และอยู่สูงสุดซึ่งรองรับการ์ดนั้น โดยไม่ผสมหลาย Mod",
                "ภาพ {0}/{1}", "ปัจจุบัน: {0}", "มีให้ใช้: {0}", "ปิด",
                "ลำดับสกิน",
                "เปิดใช้และจัดลำดับสกินสำหรับพื้นที่นี้ในสารานุกรม มอนสเตอร์แต่ละตัวใช้ Mod ที่รองรับและอยู่สูงสุด โดยการเลือกเฉพาะตัวมีสิทธิ์ก่อน"),
            ["tur"] = new(
                "Etkin", "Görünüm önceliği ({0})",
                "Kart görünümü Modlarını etkinleştirip sırala. Her kart, onu destekleyen en üstteki etkin Modun tüm efektlerini kullanır; Modlar karıştırılmaz.",
                "Görsel {0}/{1}", "Geçerli: {0}", "Mevcut: {0}", "Kapat",
                "Görünüm önceliği",
                "Bu bestiyer bölgesi için görünümleri etkinleştirip sırala. Her canavar en üstteki uyumlu Modu kullanır; tekil seçimler önceliklidir.")
        };

    private static readonly IReadOnlyDictionary<string, CardPresetLanguagePack> CardPresetPacks =
        new Dictionary<string, CardPresetLanguagePack>(StringComparer.OrdinalIgnoreCase)
        {
            ["eng"] = new(
                "Presets", "Save and switch complete card-art setups.", "Preset name",
                "Save current", "Apply", "Overwrite", "Rename", "Delete", "Confirm", "Active",
                "No card-art presets saved"),
            ["zhs"] = new(
                "预设", "保存并整体切换完整的卡图设置。", "预设名称",
                "保存当前", "应用", "覆盖", "重命名", "删除", "确认删除", "当前",
                "尚未保存卡图预设"),
            ["zht"] = new(
                "預設", "儲存並整體切換完整的卡圖設定。", "預設名稱",
                "儲存目前", "套用", "覆蓋", "重新命名", "刪除", "確認刪除", "目前",
                "尚未儲存卡圖預設"),
            ["deu"] = new(
                "Profile", "Komplette Kartenbild-Einstellungen speichern und wechseln.", "Profilname",
                "Aktuelles speichern", "Anwenden", "Überschreiben", "Umbenennen", "Löschen", "Bestätigen", "Aktiv",
                "Keine Kartenbild-Profile gespeichert"),
            ["esp"] = new(
                "Perfiles", "Guarda y cambia configuraciones completas de arte.", "Nombre del perfil",
                "Guardar actual", "Aplicar", "Sobrescribir", "Renombrar", "Borrar", "Confirmar", "Activo",
                "No hay perfiles guardados"),
            ["fra"] = new(
                "Profils", "Enregistrez et changez toute la configuration des cartes.", "Nom du profil",
                "Enregistrer", "Appliquer", "Écraser", "Renommer", "Supprimer", "Confirmer", "Actif",
                "Aucun profil de cartes enregistré"),
            ["ita"] = new(
                "Profili", "Salva e cambia configurazioni complete delle carte.", "Nome profilo",
                "Salva attuale", "Applica", "Sovrascrivi", "Rinomina", "Elimina", "Conferma", "Attivo",
                "Nessun profilo carte salvato"),
            ["jpn"] = new(
                "プリセット", "カード画像設定一式を保存して切り替えます。", "プリセット名",
                "現在を保存", "適用", "上書き", "名前変更", "削除", "削除確認", "使用中",
                "カード画像プリセットはありません"),
            ["kor"] = new(
                "프리셋", "전체 카드 그림 설정을 저장하고 전환합니다.", "프리셋 이름",
                "현재 저장", "적용", "덮어쓰기", "이름 변경", "삭제", "삭제 확인", "사용 중",
                "저장된 카드 그림 프리셋이 없습니다"),
            ["pol"] = new(
                "Profile", "Zapisuj i przełączaj pełne ustawienia grafik kart.", "Nazwa profilu",
                "Zapisz bieżące", "Zastosuj", "Nadpisz", "Zmień nazwę", "Usuń", "Potwierdź", "Aktywny",
                "Brak zapisanych profili kart"),
            ["ptb"] = new(
                "Perfis", "Salve e alterne configurações completas das artes.", "Nome do perfil",
                "Salvar atual", "Aplicar", "Substituir", "Renomear", "Excluir", "Confirmar", "Ativo",
                "Nenhum perfil de artes salvo"),
            ["rus"] = new(
                "Профили", "Сохраняйте и переключайте все настройки изображений карт.", "Имя профиля",
                "Сохранить", "Применить", "Перезаписать", "Переименовать", "Удалить", "Подтвердить", "Активен",
                "Нет сохранённых профилей карт"),
            ["spa"] = new(
                "Perfiles", "Guarda y cambia configuraciones completas de ilustraciones.", "Nombre del perfil",
                "Guardar actual", "Aplicar", "Sobrescribir", "Renombrar", "Eliminar", "Confirmar", "Activo",
                "No hay perfiles de cartas guardados"),
            ["tha"] = new(
                "พรีเซ็ต", "บันทึกและสลับการตั้งค่าภาพการ์ดทั้งหมด", "ชื่อพรีเซ็ต",
                "บันทึกปัจจุบัน", "ใช้", "เขียนทับ", "เปลี่ยนชื่อ", "ลบ", "ยืนยัน", "ใช้อยู่",
                "ยังไม่มีพรีเซ็ตภาพการ์ด"),
            ["tur"] = new(
                "Profiller", "Tüm kart görseli ayarlarını kaydet ve değiştir.", "Profil adı",
                "Geçerliyi kaydet", "Uygula", "Üzerine yaz", "Yeniden adlandır", "Sil", "Onayla", "Etkin",
                "Kayıtlı kart profili yok")
        };

    private static readonly IReadOnlyDictionary<string, AdjustmentLanguagePack> AdjustmentPacks =
        new Dictionary<string, AdjustmentLanguagePack>(StringComparer.OrdinalIgnoreCase)
        {
            ["eng"] = new(
                "Model", "Health bar", "Intent", "Selection box", "Health-bar scale", "Follow model scale",
                "Follow model movement", "Drag the selected model or health bar directly to adjust its position.",
                "Drag the selected model, health bar, intent, or selection box directly to adjust its position."),
            ["zhs"] = new(
                "模型", "血条", "意图", "选择框", "血条缩放", "跟随模型缩放",
                "跟随模型移动", "可直接拖动所选模型或血条调整位置。",
                "可直接拖动所选模型、血条、意图或选择框调整位置。"),
            ["zht"] = new(
                "模型", "血條", "意圖", "選擇框", "血條縮放", "跟隨模型縮放",
                "跟隨模型移動", "可直接拖曳所選模型或血條調整位置。",
                "可直接拖曳所選模型、血條、意圖或選擇框調整位置。"),
            ["deu"] = new(
                "Modell", "Lebensleiste", "Absicht", "Auswahlrahmen", "Skalierung der Lebensleiste", "Modellskalierung folgen",
                "Modellbewegung folgen", "Ziehe das ausgewählte Modell oder die Lebensleiste direkt, um die Position anzupassen.",
                "Ziehe das Modell, die Lebensleiste, die Absicht oder den Auswahlrahmen direkt, um die Position anzupassen."),
            ["esp"] = new(
                "Modelo", "Barra de vida", "Intención", "Marco de selección", "Escala de la barra", "Seguir escala del modelo",
                "Seguir movimiento del modelo", "Arrastra directamente el modelo o la barra de vida para ajustar su posición.",
                "Arrastra directamente el modelo, la barra de vida, la intención o el marco de selección para ajustar su posición."),
            ["fra"] = new(
                "Modèle", "Barre de vie", "Intention", "Cadre de sélection", "Échelle de la barre de vie", "Suivre l’échelle du modèle",
                "Suivre le déplacement du modèle", "Faites glisser directement le modèle ou la barre de vie pour régler sa position.",
                "Faites glisser le modèle, la barre de vie, l’intention ou le cadre de sélection pour régler sa position."),
            ["ita"] = new(
                "Modello", "Barra salute", "Intento", "Riquadro selezione", "Scala barra salute", "Segui scala modello",
                "Segui movimento modello", "Trascina direttamente il modello o la barra salute per regolarne la posizione.",
                "Trascina il modello, la barra salute, l’intento o il riquadro di selezione per regolarne la posizione."),
            ["jpn"] = new(
                "モデル", "HPバー", "行動予告", "選択枠", "HPバーの拡大率", "モデルの拡大率に追従",
                "モデルの移動に追従", "選択したモデルまたはHPバーを直接ドラッグして位置を調整できます。",
                "モデル、HPバー、行動予告、選択枠を直接ドラッグして位置を調整できます。"),
            ["kor"] = new(
                "모델", "체력 바", "의도", "선택 테두리", "체력 바 크기", "모델 크기 따라가기",
                "모델 이동 따라가기", "선택한 모델이나 체력 바를 직접 끌어 위치를 조정하세요.",
                "모델, 체력 바, 의도 또는 선택 테두리를 직접 끌어 위치를 조정하세요."),
            ["pol"] = new(
                "Model", "Pasek zdrowia", "Zamiar", "Ramka wyboru", "Skala paska zdrowia", "Skaluj razem z modelem",
                "Przesuwaj razem z modelem", "Przeciągnij bezpośrednio model lub pasek zdrowia, aby zmienić położenie.",
                "Przeciągnij model, pasek zdrowia, zamiar lub ramkę wyboru, aby zmienić położenie."),
            ["ptb"] = new(
                "Modelo", "Barra de vida", "Intenção", "Moldura de seleção", "Escala da barra de vida", "Seguir escala do modelo",
                "Seguir movimento do modelo", "Arraste diretamente o modelo ou a barra de vida para ajustar a posição.",
                "Arraste o modelo, a barra de vida, a intenção ou a moldura de seleção para ajustar a posição."),
            ["rus"] = new(
                "Модель", "Полоса здоровья", "Намерение", "Рамка выбора", "Масштаб полосы здоровья", "Следовать масштабу модели",
                "Следовать перемещению модели", "Перетаскивайте модель или полосу здоровья, чтобы настроить положение.",
                "Перетаскивайте модель, полосу здоровья, намерение или рамку выбора, чтобы настроить положение."),
            ["spa"] = new(
                "Modelo", "Barra de vida", "Intención", "Marco de selección", "Escala de la barra", "Seguir escala del modelo",
                "Seguir movimiento del modelo", "Arrastra directamente el modelo o la barra de vida para ajustar su posición.",
                "Arrastra directamente el modelo, la barra de vida, la intención o el marco de selección para ajustar su posición."),
            ["tha"] = new(
                "โมเดล", "แถบพลังชีวิต", "เจตนา", "กรอบเลือก", "ขนาดแถบพลังชีวิต", "ปรับขนาดตามโมเดล",
                "เคลื่อนตามโมเดล", "ลากโมเดลหรือแถบพลังชีวิตโดยตรงเพื่อปรับตำแหน่ง",
                "ลากโมเดล แถบพลังชีวิต เจตนา หรือกรอบเลือกโดยตรงเพื่อปรับตำแหน่ง"),
            ["tur"] = new(
                "Model", "Can çubuğu", "Niyet", "Seçim çerçevesi", "Can çubuğu ölçeği", "Model ölçeğini izle",
                "Model hareketini izle", "Konumu ayarlamak için modeli veya can çubuğunu doğrudan sürükleyin.",
                "Konumu ayarlamak için modeli, can çubuğunu, niyeti veya seçim çerçevesini sürükleyin.")
        };

    private static event Action? LanguageChanged;
    private static readonly LocManager.LocaleChangeCallback LocaleChangeCallback = NotifyLanguageChanged;
    private static LocManager? _subscribedManager;

    public static IReadOnlyCollection<string> SupportedLanguages => Packs.Keys.ToArray();

    public static string CurrentLanguage
    {
        get
        {
            var language = LocManager.Instance?.Language;
            return !string.IsNullOrWhiteSpace(language) && Packs.ContainsKey(language)
                ? language
                : "eng";
        }
    }

    private static readonly IReadOnlyDictionary<string, string> MultiplayerSkinLoadingTexts =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["eng"] = "Load other players' custom skins",
            ["zhs"] = "加载其他玩家的自定义皮肤",
            ["zht"] = "載入其他玩家的自訂外觀",
            ["deu"] = "Eigene Skins anderer Spieler laden",
            ["esp"] = "Cargar aspectos personalizados de otros jugadores",
            ["fra"] = "Charger les skins personnalisés des autres joueurs",
            ["ita"] = "Carica le skin personalizzate degli altri giocatori",
            ["jpn"] = "他のプレイヤーのカスタムスキンを読み込む",
            ["kor"] = "다른 플레이어의 사용자 스킨 불러오기",
            ["pol"] = "Wczytuj niestandardowe skórki innych graczy",
            ["ptb"] = "Carregar visuais personalizados de outros jogadores",
            ["rus"] = "Загружать пользовательские облики других игроков",
            ["spa"] = "Cargar aspectos personalizados de otros jugadores",
            ["tha"] = "โหลดสกินกำหนดเองของผู้เล่นอื่น",
            ["tur"] = "Diğer oyuncuların özel görünümlerini yükle"
        };

    private sealed record MultiplayerProgressLanguagePack(
        string Preparing,
        string WaitingForReady,
        string CheckingWorkshop,
        string Downloading,
        string Verifying,
        string Applying,
        string Complete,
        string Failed);

    private static readonly IReadOnlyDictionary<string, MultiplayerProgressLanguagePack>
        MultiplayerProgressPacks =
            new Dictionary<string, MultiplayerProgressLanguagePack>(StringComparer.OrdinalIgnoreCase)
            {
                ["eng"] = new(
                    "Preparing {0}…", "Waiting until everyone is ready: {0}",
                    "Checking {0} in Workshop…", "Downloading {0}: {1}",
                    "Verifying {0}…", "Applying {0}…", "Loaded {0}",
                    "Could not load {0}; using the original skin"),
                ["zhs"] = new(
                    "正在准备 {0}…", "等待所有玩家准备后加载 {0}",
                    "正在检查工坊资源 {0}…", "正在下载 {0}：{1}",
                    "正在校验 {0}…", "正在应用 {0}…", "已加载 {0}",
                    "无法加载 {0}，已使用原皮"),
                ["zht"] = new(
                    "正在準備 {0}…", "等待所有玩家準備後載入 {0}",
                    "正在檢查工作坊資源 {0}…", "正在下載 {0}：{1}",
                    "正在驗證 {0}…", "正在套用 {0}…", "已載入 {0}",
                    "無法載入 {0}，已使用原始外觀"),
                ["deu"] = new(
                    "{0} wird vorbereitet…", "Warte auf alle Spieler: {0}",
                    "Workshop-Dateien für {0} werden geprüft…", "{0} wird heruntergeladen: {1}",
                    "{0} wird überprüft…", "{0} wird angewendet…", "{0} geladen",
                    "{0} konnte nicht geladen werden; Original-Skin wird verwendet"),
                ["esp"] = new(
                    "Preparando {0}…", "Esperando a que todos estén listos: {0}",
                    "Comprobando {0} en Workshop…", "Descargando {0}: {1}",
                    "Verificando {0}…", "Aplicando {0}…", "{0} cargado",
                    "No se pudo cargar {0}; se usa el aspecto original"),
                ["fra"] = new(
                    "Préparation de {0}…", "En attente de tous les joueurs : {0}",
                    "Vérification de {0} dans le Workshop…", "Téléchargement de {0} : {1}",
                    "Vérification de {0}…", "Application de {0}…", "{0} chargé",
                    "Impossible de charger {0} ; skin d’origine utilisé"),
                ["ita"] = new(
                    "Preparazione di {0}…", "In attesa che tutti siano pronti: {0}",
                    "Controllo di {0} nel Workshop…", "Download di {0}: {1}",
                    "Verifica di {0}…", "Applicazione di {0}…", "{0} caricato",
                    "Impossibile caricare {0}; uso della skin originale"),
                ["jpn"] = new(
                    "{0}を準備中…", "全員の準備完了を待っています：{0}",
                    "Workshopの{0}を確認中…", "{0}をダウンロード中：{1}",
                    "{0}を検証中…", "{0}を適用中…", "{0}を読み込みました",
                    "{0}を読み込めないため、原版スキンを使用します"),
                ["kor"] = new(
                    "{0} 준비 중…", "모든 플레이어의 준비를 기다리는 중: {0}",
                    "Workshop에서 {0} 확인 중…", "{0} 다운로드 중: {1}",
                    "{0} 검증 중…", "{0} 적용 중…", "{0} 불러옴",
                    "{0}을 불러올 수 없어 원본 스킨을 사용합니다"),
                ["pol"] = new(
                    "Przygotowywanie {0}…", "Oczekiwanie na gotowość wszystkich: {0}",
                    "Sprawdzanie {0} w Warsztacie…", "Pobieranie {0}: {1}",
                    "Weryfikowanie {0}…", "Stosowanie {0}…", "Wczytano {0}",
                    "Nie udało się wczytać {0}; użyto oryginalnej skórki"),
                ["ptb"] = new(
                    "Preparando {0}…", "Aguardando todos ficarem prontos: {0}",
                    "Verificando {0} na Oficina…", "Baixando {0}: {1}",
                    "Verificando {0}…", "Aplicando {0}…", "{0} carregado",
                    "Não foi possível carregar {0}; usando o visual original"),
                ["rus"] = new(
                    "Подготовка {0}…", "Ожидание готовности всех игроков: {0}",
                    "Проверка {0} в Мастерской…", "Загрузка {0}: {1}",
                    "Проверка {0}…", "Применение {0}…", "{0} загружен",
                    "Не удалось загрузить {0}; используется оригинальный облик"),
                ["spa"] = new(
                    "Preparando {0}…", "Esperando a que todos estén listos: {0}",
                    "Comprobando {0} en Workshop…", "Descargando {0}: {1}",
                    "Verificando {0}…", "Aplicando {0}…", "{0} cargado",
                    "No se pudo cargar {0}; se usa el aspecto original"),
                ["tha"] = new(
                    "กำลังเตรียม {0}…", "กำลังรอให้ผู้เล่นทุกคนพร้อม: {0}",
                    "กำลังตรวจสอบ {0} ในเวิร์กชอป…", "กำลังดาวน์โหลด {0}: {1}",
                    "กำลังตรวจสอบ {0}…", "กำลังใช้ {0}…", "โหลด {0} แล้ว",
                    "โหลด {0} ไม่สำเร็จ จึงใช้สกินดั้งเดิม"),
                ["tur"] = new(
                    "{0} hazırlanıyor…", "Herkesin hazır olması bekleniyor: {0}",
                    "Atölyede {0} denetleniyor…", "{0} indiriliyor: {1}",
                    "{0} doğrulanıyor…", "{0} uygulanıyor…", "{0} yüklendi",
                    "{0} yüklenemedi; özgün görünüm kullanılıyor")
            };

    internal static string FormatOnlineSkinCacheProgress(OnlineSkinCacheProgress progress)
    {
        var provider = string.IsNullOrWhiteSpace(progress.ProviderId)
            ? "Skin"
            : progress.ProviderId;
        var pack = MultiplayerProgressPacks[CurrentLanguage];
        return progress.Stage switch
        {
            OnlineSkinCacheStage.Preparing => string.Format(pack.Preparing, provider),
            OnlineSkinCacheStage.WaitingForReady => string.Format(pack.WaitingForReady, provider),
            OnlineSkinCacheStage.CheckingWorkshop => string.Format(pack.CheckingWorkshop, provider),
            OnlineSkinCacheStage.Downloading => string.Format(
                pack.Downloading,
                provider,
                progress.TotalBytes > 0
                    ? $"{Math.Clamp(progress.DownloadedBytes * 100d / progress.TotalBytes, 0d, 100d):F0}%"
                    : "…"),
            OnlineSkinCacheStage.Verifying => string.Format(pack.Verifying, provider),
            OnlineSkinCacheStage.Applying => string.Format(pack.Applying, provider),
            OnlineSkinCacheStage.Complete => string.Format(pack.Complete, provider),
            OnlineSkinCacheStage.Failed => string.Format(pack.Failed, provider),
            _ => string.Empty
        };
    }

    public static string Get(ModText text) =>
        text == ModText.LoadOtherPlayersCustomSkins
            ? MultiplayerSkinLoadingTexts[CurrentLanguage]
            : text >= ModText.ModelTransform
            ? AdjustmentPacks[CurrentLanguage].Get(text)
            : text >= ModText.CharacterAppearance
            ? AppearancePacks[CurrentLanguage].Get(text)
            : text >= ModText.CardPresets
            ? CardPresetPacks[CurrentLanguage].Get(text)
            : text >= ModText.CardSkinPriority
            ? CardPriorityPacks[CurrentLanguage].Get(text)
            : Packs[CurrentLanguage].Get(text);

    public static string DisplayOptionName(string name)
    {
        var suffix = " · " + DefaultVariantMarker;
        return name.EndsWith(suffix, StringComparison.Ordinal)
            ? name[..^suffix.Length] + " · " + Get(ModText.DefaultVariant)
            : name;
    }

    public static void Bind(Node owner, Action refresh)
    {
        EnsureSubscribed();
        Action? handler = null;
        handler = () =>
        {
            if (!GodotObject.IsInstanceValid(owner))
            {
                LanguageChanged -= handler;
                return;
            }

            try
            {
                refresh();
            }
            catch (Exception exception)
            {
                ModLog.Warn("刷新 Mod 本地化界面失败：" + exception.GetBaseException().Message);
            }
        };

        LanguageChanged += handler;
        owner.TreeExited += () => LanguageChanged -= handler;
        handler();
    }

    internal static void NotifyLanguageChanged()
    {
        foreach (var callback in LanguageChanged?.GetInvocationList().Cast<Action>() ?? [])
        {
            callback();
        }
    }

    private static void EnsureSubscribed()
    {
        var manager = LocManager.Instance;
        if (manager == null || ReferenceEquals(manager, _subscribedManager))
        {
            return;
        }

        _subscribedManager?.UnsubscribeToLocaleChange(LocaleChangeCallback);
        manager.SubscribeToLocaleChange(LocaleChangeCallback);
        _subscribedManager = manager;
    }
}
