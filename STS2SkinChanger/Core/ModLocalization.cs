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
    DirectDragIntentHint
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
                "Skin Changer is not first in the mod load order. Skin mods above it load their DLL/PCK first, so they cannot be fully managed. Move it to the top and restart now, or adjust the order manually later in Mod Manager.",
                "Got it",
                "Don't show again",
                "Move to top & restart",
                "Moving to the top or restarting failed. The setting was not ignored. Move Skin Changer to the first position manually in Mod Manager, then restart the game.",
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
                "本 Mod 当前不在 Mod 加载顺序第一位。排在它前面的皮肤 Mod 会先加载自己的 DLL/PCK，因此无法被完整接管。可以立即置顶并重启，也可以稍后在 Mod 管理界面手动调整。",
                "知道了",
                "不再提示",
                "置顶并重启",
                "自动置顶或重启失败，设置没有被静默忽略。请在 Mod 管理界面手动把皮肤切换器-Skin Changer 移到第一位并重启游戏。",
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
                "本 Mod 目前不在 Mod 載入順序第一位。排在它前面的外觀 Mod 會先載入自己的 DLL/PCK，因此無法被完整管理。可立即置頂並重新啟動，也可稍後在 Mod 管理畫面手動調整。",
                "知道了",
                "不再提示",
                "置頂並重新啟動",
                "自動置頂或重新啟動失敗，設定並未被忽略。請在 Mod 管理畫面手動將外觀切換器-Skin Changer 移到第一位，然後重新啟動遊戲。",
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
                "Skin Changer steht nicht an erster Stelle der Mod-Ladereihenfolge. Darüber stehende Skin-Mods laden ihre DLL/PCK zuerst und können daher nicht vollständig verwaltet werden. Verschiebe ihn jetzt nach oben und starte neu oder ändere die Reihenfolge später manuell im Mod-Manager.",
                "Verstanden",
                "Nicht mehr anzeigen",
                "Nach oben & neu starten",
                "Das Verschieben nach oben oder der Neustart ist fehlgeschlagen. Die Einstellung wurde nicht ignoriert. Verschiebe Skin Changer im Mod-Manager manuell an die erste Stelle und starte das Spiel neu.",
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
                "Skin Changer no está primero en el orden de carga de mods. Los mods de aspectos que están arriba cargan antes sus DLL/PCK y no pueden administrarse por completo. Muévelo al inicio y reinicia ahora, o cambia el orden manualmente después en el administrador de mods.",
                "Entendido",
                "No volver a mostrar",
                "Mover al inicio y reiniciar",
                "No se pudo mover al inicio o reiniciar. El ajuste no fue ignorado. Mueve Skin Changer manualmente al primer lugar en el administrador de mods y reinicia el juego.",
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
                "Skin Changer n’est pas en tête de l’ordre de chargement des mods. Les mods de skins placés au-dessus chargent d’abord leur DLL/PCK et ne peuvent donc pas être entièrement gérés. Placez-le en tête et redémarrez maintenant, ou modifiez l’ordre plus tard dans le gestionnaire de mods.",
                "Compris",
                "Ne plus afficher",
                "Placer en tête et redémarrer",
                "Le déplacement en tête ou le redémarrage a échoué. Le réglage n’a pas été ignoré. Placez manuellement Skin Changer en première position dans le gestionnaire de mods, puis redémarrez le jeu.",
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
                "Skin Changer non è al primo posto nell’ordine di caricamento dei mod. I mod delle skin sopra di esso caricano prima i propri DLL/PCK e non possono essere gestiti completamente. Spostalo in cima e riavvia ora, oppure modifica l’ordine manualmente in seguito nel gestore dei mod.",
                "Ho capito",
                "Non mostrare più",
                "Sposta in cima e riavvia",
                "Lo spostamento in cima o il riavvio non è riuscito. L’impostazione non è stata ignorata. Sposta manualmente Skin Changer al primo posto nel gestore dei mod e riavvia il gioco.",
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
                "Skin ChangerがModのロード順の先頭ではありません。上にあるスキンModは先にDLL/PCKを読み込むため、完全には管理できません。今すぐ先頭へ移動して再起動するか、後でMod管理画面から手動で変更してください。",
                "了解",
                "今後表示しない",
                "先頭へ移動して再起動",
                "先頭への移動または再起動に失敗しました。設定は無視されていません。Mod管理画面でSkin Changerを手動で先頭に移動し、ゲームを再起動してください。",
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
                "Skin Changer가 Mod 로드 순서의 첫 번째가 아닙니다. 위에 있는 스킨 Mod가 DLL/PCK를 먼저 불러오므로 완전히 관리할 수 없습니다. 지금 맨 위로 옮겨 재시작하거나 나중에 Mod 관리 화면에서 직접 순서를 바꾸세요.",
                "확인",
                "다시 표시하지 않기",
                "맨 위로 옮기고 재시작",
                "맨 위로 이동하거나 재시작하지 못했습니다. 설정은 무시되지 않았습니다. Mod 관리 화면에서 Skin Changer를 직접 첫 번째로 옮긴 뒤 게임을 재시작하세요.",
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
                "Skin Changer nie jest pierwszy w kolejności ładowania modów. Mody skórek powyżej niego najpierw ładują swoje DLL/PCK, więc nie można nimi w pełni zarządzać. Przenieś go na górę i uruchom ponownie teraz albo później zmień kolejność ręcznie w menedżerze modów.",
                "Rozumiem",
                "Nie pokazuj ponownie",
                "Na górę i uruchom ponownie",
                "Przeniesienie na górę lub ponowne uruchomienie nie powiodło się. Ustawienie nie zostało zignorowane. Ręcznie przenieś Skin Changer na pierwsze miejsce w menedżerze modów i uruchom grę ponownie.",
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
                "Skin Changer não está em primeiro na ordem de carregamento dos mods. Mods de visuais acima dele carregam seus DLL/PCK antes e não podem ser gerenciados por completo. Mova-o para o topo e reinicie agora ou ajuste a ordem manualmente depois no gerenciador de mods.",
                "Entendi",
                "Não mostrar novamente",
                "Mover para o topo e reiniciar",
                "Não foi possível mover para o topo ou reiniciar. A configuração não foi ignorada. Mova o Skin Changer manualmente para o primeiro lugar no gerenciador de mods e reinicie o jogo.",
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
                "Skin Changer стоит не первым в порядке загрузки модов. Моды обликов выше него загружают свои DLL/PCK раньше, поэтому ими нельзя управлять полностью. Переместите его наверх и перезапустите сейчас либо позже измените порядок вручную в менеджере модов.",
                "Понятно",
                "Больше не показывать",
                "Наверх и перезапустить",
                "Не удалось переместить мод наверх или перезапустить игру. Настройка не была проигнорирована. Вручную поставьте Skin Changer первым в менеджере модов и перезапустите игру.",
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
                "Skin Changer no está en primer lugar en el orden de carga de mods. Los mods de aspectos situados encima cargan antes sus DLL/PCK y no pueden gestionarse por completo. Muévelo al principio y reinicia ahora, o cambia el orden manualmente más tarde en el gestor de mods.",
                "Entendido",
                "No volver a mostrar",
                "Mover al principio y reiniciar",
                "No se pudo mover al principio o reiniciar. El ajuste no se ha ignorado. Mueve manualmente Skin Changer al primer lugar en el gestor de mods y reinicia el juego.",
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
                "Skin Changer ไม่ได้อยู่ลำดับแรกของ Mod โดย Mod สกินที่อยู่ด้านบนจะโหลด DLL/PCK ก่อน จึงไม่สามารถจัดการได้อย่างสมบูรณ์ ย้ายขึ้นบนสุดแล้วเริ่มเกมใหม่ตอนนี้ หรือปรับลำดับเองภายหลังในตัวจัดการ Mod",
                "เข้าใจแล้ว",
                "ไม่ต้องแสดงอีก",
                "ย้ายขึ้นบนสุดและเริ่มใหม่",
                "ไม่สามารถย้ายขึ้นบนสุดหรือเริ่มเกมใหม่ได้ การตั้งค่าไม่ได้ถูกละเลย โปรดย้าย Skin Changer ไปไว้ลำดับแรกด้วยตนเองในตัวจัดการ Mod แล้วเริ่มเกมใหม่",
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
                "Skin Changer Mod yükleme sırasının ilk sırasında değil. Üstündeki görünüm Modları kendi DLL/PCK dosyalarını önce yüklediği için tam olarak yönetilemez. Şimdi en üste taşıyıp yeniden başlatın veya daha sonra Mod yöneticisinden sırayı elle değiştirin.",
                "Anladım",
                "Bir daha gösterme",
                "En üste taşı ve yeniden başlat",
                "En üste taşıma veya yeniden başlatma başarısız oldu. Ayar yok sayılmadı. Skin Changer'ı Mod yöneticisinde elle ilk sıraya taşıyıp oyunu yeniden başlatın.",
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

    public static string Get(ModText text) =>
        text >= ModText.ModelTransform
            ? AdjustmentPacks[CurrentLanguage].Get(text)
            : text >= ModText.CharacterAppearance
            ? AppearancePacks[CurrentLanguage].Get(text)
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
