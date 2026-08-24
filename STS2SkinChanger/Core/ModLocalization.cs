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
    DefaultVariant
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

    public static string Get(ModText text) => Packs[CurrentLanguage].Get(text);

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
