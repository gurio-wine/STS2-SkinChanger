namespace STS2SkinChanger.Core;

internal enum AppearanceTargetKind
{
    Character,
    Monster,
    MapBoss,
    Ancient,
    Companion,
    Merchant
}

internal static partial class ModLocalization
{
    private sealed record AppearanceTargetHintPack(string Template, string Empty, string Separator, string[] Names);

    private static readonly IReadOnlyDictionary<string, AppearanceTargetHintPack> AppearanceTargetHintPacks =
        new Dictionary<string, AppearanceTargetHintPack>(StringComparer.OrdinalIgnoreCase)
        {
            ["eng"] = new("Choose what to customize: {0}.", "No adjustable targets here.", ", ",
                ["characters", "monsters", "boss icons on the map", "Ancients", "companions", "merchants"]),
            ["zhs"] = new("请选择要调整外观的目标：{0}。", "当前没有可调整的目标。", "、",
                ["角色", "怪物", "地图上的 Boss 图标", "先古之民", "同伴", "商人"]),
            ["zht"] = new("請選擇要調整外觀的目標：{0}。", "目前沒有可調整的目標。", "、",
                ["角色", "怪物", "地圖上的 Boss 圖示", "先古之民", "同伴", "商人"]),
            ["deu"] = new("Wähle, was du anpassen möchtest: {0}.", "Hier gibt es keine anpassbaren Ziele.", ", ",
                ["Charaktere", "Monster", "Boss-Symbole auf der Karte", "Uralte", "Begleiter", "Händler"]),
            ["esp"] = new("Elige qué personalizar: {0}.", "No hay objetivos que ajustar aquí.", ", ",
                ["personajes", "monstruos", "iconos de jefe del mapa", "Antiguos", "compañeros", "mercaderes"]),
            ["spa"] = new("Elige qué personalizar: {0}.", "No hay objetivos que ajustar aquí.", ", ",
                ["personajes", "monstruos", "iconos de jefe del mapa", "Antiguos", "compañeros", "mercaderes"]),
            ["fra"] = new("Choisissez quoi personnaliser : {0}.", "Aucune cible à personnaliser ici.", ", ",
                ["personnages", "monstres", "icônes de boss sur la carte", "Anciens", "compagnons", "marchands"]),
            ["ita"] = new("Scegli cosa personalizzare: {0}.", "Qui non ci sono bersagli da personalizzare.", ", ",
                ["personaggi", "mostri", "icone dei boss sulla mappa", "Antichi", "compagni", "mercanti"]),
            ["jpn"] = new("外見を変更する対象を選択：{0}", "現在、外見を変更できる対象はありません。", "、",
                ["キャラクター", "モンスター", "マップのボスアイコン", "エンシェント", "仲間", "商人"]),
            ["kor"] = new("외형을 조정할 대상을 선택하세요: {0}", "현재 조정할 수 있는 대상이 없습니다.", ", ",
                ["캐릭터", "몬스터", "지도의 보스 아이콘", "고대인", "동료", "상인"]),
            ["pol"] = new("Wybierz, co dostosować: {0}.", "Brak celów do dostosowania.", ", ",
                ["postacie", "potwory", "ikony bossów na mapie", "Pradawni", "towarzysze", "kupcy"]),
            ["ptb"] = new("Escolha o que personalizar: {0}.", "Não há alvos para ajustar aqui.", ", ",
                ["personagens", "monstros", "ícones de chefes no mapa", "Anciões", "companheiros", "mercadores"]),
            ["rus"] = new("Выберите, что настроить: {0}.", "Здесь нет доступных целей для настройки.", ", ",
                ["персонажи", "монстры", "значки боссов на карте", "Древние", "спутники", "торговцы"]),
            ["tha"] = new("เลือกเป้าหมายที่ต้องการปรับรูปลักษณ์: {0}", "ขณะนี้ไม่มีเป้าหมายที่ปรับได้", ", ",
                ["ตัวละคร", "มอนสเตอร์", "ไอคอนบอสบนแผนที่", "Ancient", "สหาย", "พ่อค้า"]),
            ["tur"] = new("Özelleştirmek istediğini seç: {0}.", "Burada ayarlanabilecek hedef yok.", ", ",
                ["karakterler", "canavarlar", "haritadaki Boss simgeleri", "Kadimler", "yoldaşlar", "tüccarlar"])
        };

    internal static string FormatAppearanceTargetHint(IEnumerable<AppearanceTargetKind> kinds, string? language = null)
    {
        var pack = AppearanceTargetHintPacks.GetValueOrDefault(language ?? CurrentLanguage)
            ?? AppearanceTargetHintPacks["eng"];
        var names = kinds.Distinct().OrderBy(kind => kind)
            .Where(kind => (int)kind >= 0 && (int)kind < pack.Names.Length)
            .Select(kind => pack.Names[(int)kind]).ToArray();
        return names.Length == 0 ? pack.Empty : string.Format(pack.Template, string.Join(pack.Separator, names));
    }
}
