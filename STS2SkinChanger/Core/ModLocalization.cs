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

    private sealed record AdjustmentLanguagePack(
        string ModelTransform,
        string HealthBarTransform,
        string IntentTransform,
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
                "Only cards with skins",
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
                "仅显示有皮肤的卡牌",
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
                "僅顯示有外觀的卡牌",
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
                "Nur Karten mit Skins",
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
                "Solo cartas con aspectos",
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
                "Cartes avec skins uniquement",
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
                "Solo carte con skin",
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
                "スキンのあるカードのみ",
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
                "스킨이 있는 카드만",
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
                "Tylko karty ze skórkami",
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
                "Somente cartas com visuais",
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
                "Только карты с обликами",
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
                "Solo cartas con aspectos",
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
                "เฉพาะการ์ดที่มีสกิน",
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
                "Yalnızca görünümlü kartlar",
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
                "Appearance", "Click a character, monster, Ancient, or companion to adjust its appearance.",
                "Skin", "Scale", "Horizontal offset", "Vertical offset",
                "Hold to compare", "Alignment guide",
                "Gold cross: original anchor · Cyan frame: current bounds",
                "Applied immediately", "Queued until the current action finishes",
                "Saved. Live positioning preview is available during combat.", "Could not apply appearance"),
            ["zhs"] = new(
                "外观", "请选择一个角色、怪物、先古之民或同伴来调整外观。",
                "皮肤", "缩放", "水平位移", "垂直位移",
                "按住对比原位", "定位参考线",
                "金色十字：原始落点 · 青色边框：当前范围",
                "已立即应用", "当前动作结束后自动应用",
                "已保存；进入战斗后可实时预览位置。", "应用角色外观失败"),
            ["zht"] = new(
                "外觀", "請選擇一個角色、怪物、先古之民或同伴來調整外觀。",
                "外觀", "縮放", "水平位移", "垂直位移",
                "按住比較原位", "定位參考線",
                "金色十字：原始落點 · 青色邊框：目前範圍",
                "已立即套用", "目前動作結束後自動套用",
                "已儲存；進入戰鬥後可即時預覽位置。", "無法套用角色外觀"),
            ["deu"] = new(
                "Aussehen", "Klicke auf einen Charakter, ein Monster, einen Ahnen oder einen Begleiter, um sein Aussehen anzupassen.",
                "Skin", "Skalierung", "Horizontaler Versatz", "Vertikaler Versatz",
                "Zum Vergleichen halten", "Ausrichtungshilfe",
                "Goldenes Kreuz: Ursprung · Türkiser Rahmen: aktuelle Grenzen",
                "Sofort angewendet", "Wird nach der aktuellen Aktion angewendet",
                "Gespeichert. Die Live-Vorschau ist im Kampf verfügbar.", "Aussehen konnte nicht angewendet werden"),
            ["esp"] = new(
                "Aspecto", "Haz clic en un personaje, monstruo, Antiguo o compañero para ajustar su aspecto.",
                "Aspecto", "Escala", "Desplazamiento horizontal", "Desplazamiento vertical",
                "Mantén para comparar", "Guía de alineación",
                "Cruz dorada: origen · Marco cian: límites actuales",
                "Aplicado al instante", "Se aplicará al terminar la acción actual",
                "Guardado. La vista previa en vivo está disponible en combate.", "No se pudo aplicar el aspecto"),
            ["fra"] = new(
                "Apparence", "Cliquez sur un personnage, un monstre, un Ancien ou un compagnon pour modifier son apparence.",
                "Skin", "Échelle", "Décalage horizontal", "Décalage vertical",
                "Maintenir pour comparer", "Guide d’alignement",
                "Croix dorée : origine · Cadre cyan : limites actuelles",
                "Appliqué immédiatement", "Sera appliqué après l’action en cours",
                "Enregistré. L’aperçu en direct est disponible en combat.", "Impossible d’appliquer l’apparence"),
            ["ita"] = new(
                "Aspetto", "Fai clic su un personaggio, un mostro, un Antico o un compagno per modificarne l’aspetto.",
                "Skin", "Scala", "Spostamento orizzontale", "Spostamento verticale",
                "Tieni premuto per confrontare", "Guida allineamento",
                "Croce dorata: origine · Cornice ciano: limiti attuali",
                "Applicato subito", "Verrà applicato al termine dell’azione corrente",
                "Salvato. L’anteprima dal vivo è disponibile in combattimento.", "Impossibile applicare l’aspetto"),
            ["jpn"] = new(
                "外見", "外見を調整するキャラクター、モンスター、エンシェント、または仲間をクリックしてください。",
                "スキン", "拡大率", "横位置", "縦位置",
                "長押しで元と比較", "位置合わせガイド",
                "金の十字：元の基準点 · 水色の枠：現在の範囲",
                "すぐに適用しました", "現在のアクション終了後に適用します",
                "保存しました。戦闘中に位置をリアルタイム確認できます。", "外見を適用できませんでした"),
            ["kor"] = new(
                "외형", "외형을 조정할 캐릭터, 몬스터, 고대인 또는 동료를 클릭하세요.",
                "스킨", "크기", "가로 위치", "세로 위치",
                "길게 눌러 원본 비교", "정렬 안내선",
                "금색 십자: 원래 기준점 · 청록색 테두리: 현재 범위",
                "즉시 적용됨", "현재 행동이 끝나면 적용됨",
                "저장됨. 전투 중 위치를 실시간으로 확인할 수 있습니다.", "외형을 적용하지 못했습니다"),
            ["pol"] = new(
                "Wygląd", "Kliknij postać, potwora, Pradawnego lub towarzysza, aby dostosować wygląd.",
                "Skórka", "Skala", "Przesunięcie poziome", "Przesunięcie pionowe",
                "Przytrzymaj, aby porównać", "Linie wyrównania",
                "Złoty krzyż: punkt bazowy · Turkusowa ramka: aktualny obszar",
                "Zastosowano natychmiast", "Zostanie zastosowane po bieżącej akcji",
                "Zapisano. Podgląd na żywo jest dostępny w walce.", "Nie udało się zastosować wyglądu"),
            ["ptb"] = new(
                "Visual", "Clique em um personagem, monstro, Ancião ou companheiro para ajustar seu visual.",
                "Visual", "Escala", "Deslocamento horizontal", "Deslocamento vertical",
                "Segure para comparar", "Guia de alinhamento",
                "Cruz dourada: origem · Moldura ciano: limites atuais",
                "Aplicado imediatamente", "Será aplicado após a ação atual",
                "Salvo. A prévia ao vivo está disponível em combate.", "Não foi possível aplicar o visual"),
            ["rus"] = new(
                "Облик", "Нажмите на персонажа, монстра, Древнего или спутника, чтобы настроить его облик.",
                "Облик", "Масштаб", "Смещение по горизонтали", "Смещение по вертикали",
                "Удерживать для сравнения", "Направляющие",
                "Золотой крест: исходная точка · Голубая рамка: текущие границы",
                "Применено сразу", "Будет применено после текущего действия",
                "Сохранено. Предпросмотр положения доступен в бою.", "Не удалось применить облик"),
            ["spa"] = new(
                "Aspecto", "Haz clic en un personaje, monstruo, Antiguo o compañero para ajustar su aspecto.",
                "Aspecto", "Escala", "Desplazamiento horizontal", "Desplazamiento vertical",
                "Mantén para comparar", "Guía de alineación",
                "Cruz dorada: origen · Marco cian: límites actuales",
                "Aplicado al instante", "Se aplicará al terminar la acción actual",
                "Guardado. La vista previa en vivo está disponible en combate.", "No se pudo aplicar el aspecto"),
            ["tha"] = new(
                "รูปลักษณ์", "คลิกตัวละคร มอนสเตอร์ Ancient หรือเพื่อนร่วมทางเพื่อปรับรูปลักษณ์",
                "สกิน", "ขนาด", "ตำแหน่งแนวนอน", "ตำแหน่งแนวตั้ง",
                "กดค้างเพื่อเทียบ", "เส้นช่วยจัดตำแหน่ง",
                "กากบาทสีทอง: จุดเดิม · กรอบสีฟ้า: ขอบเขตปัจจุบัน",
                "ใช้ทันทีแล้ว", "จะใช้หลังแอ็กชันปัจจุบันจบ",
                "บันทึกแล้ว ดูตำแหน่งแบบสดได้ระหว่างต่อสู้", "ไม่สามารถใช้รูปลักษณ์ได้"),
            ["tur"] = new(
                "Görünüm", "Görünümünü ayarlamak için bir karaktere, canavara, Kadime veya yoldaşa tıkla.",
                "Görünüm", "Ölçek", "Yatay konum", "Dikey konum",
                "Karşılaştırmak için basılı tut", "Hizalama kılavuzu",
                "Altın artı: özgün konum · Camgöbeği çerçeve: geçerli sınırlar",
                "Hemen uygulandı", "Geçerli eylem bitince uygulanacak",
                "Kaydedildi. Canlı konum önizlemesi savaşta kullanılabilir.", "Görünüm uygulanamadı")
        };

    private static readonly IReadOnlyDictionary<string, AdjustmentLanguagePack> AdjustmentPacks =
        new Dictionary<string, AdjustmentLanguagePack>(StringComparer.OrdinalIgnoreCase)
        {
            ["eng"] = new(
                "Model", "Health bar", "Intent", "Health-bar scale", "Follow model scale",
                "Follow model movement", "Drag the selected model or health bar directly to adjust its position.",
                "Drag the selected model, health bar, or intent directly to adjust its position."),
            ["zhs"] = new(
                "模型", "血条", "意图", "血条缩放", "跟随模型缩放",
                "跟随模型移动", "可直接拖动所选模型或血条调整位置。",
                "可直接拖动所选模型、血条或意图调整位置。"),
            ["zht"] = new(
                "模型", "血條", "意圖", "血條縮放", "跟隨模型縮放",
                "跟隨模型移動", "可直接拖曳所選模型或血條調整位置。",
                "可直接拖曳所選模型、血條或意圖調整位置。"),
            ["deu"] = new(
                "Modell", "Lebensleiste", "Absicht", "Skalierung der Lebensleiste", "Modellskalierung folgen",
                "Modellbewegung folgen", "Ziehe das ausgewählte Modell oder die Lebensleiste direkt, um die Position anzupassen.",
                "Ziehe das ausgewählte Modell, die Lebensleiste oder die Absicht direkt, um die Position anzupassen."),
            ["esp"] = new(
                "Modelo", "Barra de vida", "Intención", "Escala de la barra", "Seguir escala del modelo",
                "Seguir movimiento del modelo", "Arrastra directamente el modelo o la barra de vida para ajustar su posición.",
                "Arrastra directamente el modelo, la barra de vida o la intención para ajustar su posición."),
            ["fra"] = new(
                "Modèle", "Barre de vie", "Intention", "Échelle de la barre de vie", "Suivre l’échelle du modèle",
                "Suivre le déplacement du modèle", "Faites glisser directement le modèle ou la barre de vie pour régler sa position.",
                "Faites glisser directement le modèle, la barre de vie ou l’intention pour régler sa position."),
            ["ita"] = new(
                "Modello", "Barra salute", "Intento", "Scala barra salute", "Segui scala modello",
                "Segui movimento modello", "Trascina direttamente il modello o la barra salute per regolarne la posizione.",
                "Trascina direttamente il modello, la barra salute o l’intento per regolarne la posizione."),
            ["jpn"] = new(
                "モデル", "HPバー", "行動予告", "HPバーの拡大率", "モデルの拡大率に追従",
                "モデルの移動に追従", "選択したモデルまたはHPバーを直接ドラッグして位置を調整できます。",
                "選択したモデル、HPバー、行動予告を直接ドラッグして位置を調整できます。"),
            ["kor"] = new(
                "모델", "체력 바", "의도", "체력 바 크기", "모델 크기 따라가기",
                "모델 이동 따라가기", "선택한 모델이나 체력 바를 직접 끌어 위치를 조정하세요.",
                "선택한 모델, 체력 바 또는 의도를 직접 끌어 위치를 조정하세요."),
            ["pol"] = new(
                "Model", "Pasek zdrowia", "Zamiar", "Skala paska zdrowia", "Skaluj razem z modelem",
                "Przesuwaj razem z modelem", "Przeciągnij bezpośrednio model lub pasek zdrowia, aby zmienić położenie.",
                "Przeciągnij bezpośrednio model, pasek zdrowia lub zamiar, aby zmienić położenie."),
            ["ptb"] = new(
                "Modelo", "Barra de vida", "Intenção", "Escala da barra de vida", "Seguir escala do modelo",
                "Seguir movimento do modelo", "Arraste diretamente o modelo ou a barra de vida para ajustar a posição.",
                "Arraste diretamente o modelo, a barra de vida ou a intenção para ajustar a posição."),
            ["rus"] = new(
                "Модель", "Полоса здоровья", "Намерение", "Масштаб полосы здоровья", "Следовать масштабу модели",
                "Следовать перемещению модели", "Перетаскивайте модель или полосу здоровья, чтобы настроить положение.",
                "Перетаскивайте модель, полосу здоровья или намерение, чтобы настроить положение."),
            ["spa"] = new(
                "Modelo", "Barra de vida", "Intención", "Escala de la barra", "Seguir escala del modelo",
                "Seguir movimiento del modelo", "Arrastra directamente el modelo o la barra de vida para ajustar su posición.",
                "Arrastra directamente el modelo, la barra de vida o la intención para ajustar su posición."),
            ["tha"] = new(
                "โมเดล", "แถบพลังชีวิต", "เจตนา", "ขนาดแถบพลังชีวิต", "ปรับขนาดตามโมเดล",
                "เคลื่อนตามโมเดล", "ลากโมเดลหรือแถบพลังชีวิตโดยตรงเพื่อปรับตำแหน่ง",
                "ลากโมเดล แถบพลังชีวิต หรือเจตนาโดยตรงเพื่อปรับตำแหน่ง"),
            ["tur"] = new(
                "Model", "Can çubuğu", "Niyet", "Can çubuğu ölçeği", "Model ölçeğini izle",
                "Model hareketini izle", "Konumu ayarlamak için modeli veya can çubuğunu doğrudan sürükleyin.",
                "Konumu ayarlamak için modeli, can çubuğunu veya niyeti doğrudan sürükleyin.")
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
