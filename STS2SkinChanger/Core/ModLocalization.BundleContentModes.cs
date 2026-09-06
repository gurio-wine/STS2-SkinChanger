namespace STS2SkinChanger.Core;

internal static partial class ModLocalization
{
    private static readonly IReadOnlyDictionary<string, (string Cards, string Monsters, string Presets, string Priority)> BundleContentTexts =
        new Dictionary<string, (string, string, string, string)>
        {
            ["eng"] = ("Cards", "Monsters", "Multiple presets", "Mod priority"),
            ["zhs"] = ("卡牌", "怪物", "多预设", "Mod 优先级"),
            ["zht"] = ("卡牌", "怪物", "多預設", "Mod 優先順序"),
            ["deu"] = ("Karten", "Monster", "Mehrere Vorlagen", "Mod-Priorität"),
            ["esp"] = ("Cartas", "Monstruos", "Varios preajustes", "Prioridad de mods"),
            ["spa"] = ("Cartas", "Monstruos", "Varios preajustes", "Prioridad de mods"),
            ["fra"] = ("Cartes", "Monstres", "Plusieurs préréglages", "Priorité des mods"),
            ["ita"] = ("Carte", "Mostri", "Più preset", "Priorità delle mod"),
            ["jpn"] = ("カード", "モンスター", "複数プリセット", "Mod優先順位"),
            ["kor"] = ("카드", "몬스터", "여러 프리셋", "모드 우선순위"),
            ["pol"] = ("Karty", "Potwory", "Wiele presetów", "Priorytet modów"),
            ["ptb"] = ("Cartas", "Monstros", "Várias predefinições", "Prioridade de mods"),
            ["rus"] = ("Карты", "Монстры", "Несколько пресетов", "Приоритет модов"),
            ["tha"] = ("การ์ด", "มอนสเตอร์", "หลายพรีเซ็ต", "ลำดับความสำคัญม็อด"),
            ["tur"] = ("Kartlar", "Canavarlar", "Birden çok ön ayar", "Mod önceliği")
        };

    internal static string BundleCards => BundleContentTexts[CurrentLanguage].Cards;
    internal static string BundleMonsters => BundleContentTexts[CurrentLanguage].Monsters;
    internal static string BundleMultiplePresets => BundleContentTexts[CurrentLanguage].Presets;
    internal static string BundleModPriority => BundleContentTexts[CurrentLanguage].Priority;
}
