namespace STS2SkinChanger.Core;

internal static partial class ModLocalization
{
    private static readonly IReadOnlyDictionary<string, (string Hide, string Locked, string Hint)> BundlePresetTexts =
        new Dictionary<string, (string, string, string)>
        {
            ["eng"] = ("Hide the selected skin from the skin list", "Manage this preset's name and deletion through its skin bundle.", "Each new bundle has its own original-skin presets. Yellow preset names follow the bundle name; you can apply or overwrite their contents."),
            ["zhs"] = ("在皮肤列表中隐藏选中的皮肤", "请通过皮肤包管理名称和删除；此预设只能应用或覆盖。", "新建皮肤包默认使用各分类专属的全原皮预设。黄色预设名跟随皮肤包名称，可应用或覆盖内容。"),
            ["zht"] = ("在外觀清單中隱藏選取的外觀", "請透過組合包管理名稱與刪除；此預設只能套用或覆寫。", "新組合包預設使用各分類專屬的原始外觀預設。黃色預設名稱跟隨組合包，可套用或覆寫內容。"),
            ["deu"] = ("Gewählten Skin in der Skin-Liste ausblenden", "Name und Löschung werden über das Skin-Paket verwaltet.", "Neue Pakete erhalten eigene Original-Skin-Vorlagen. Gelbe Namen folgen dem Paketnamen; Inhalte lassen sich anwenden oder überschreiben."),
            ["esp"] = ("Ocultar el aspecto seleccionado en la lista", "Gestiona el nombre y la eliminación desde el paquete.", "Cada paquete nuevo tiene preajustes propios de aspectos originales. Los nombres amarillos siguen al paquete; puedes aplicar o sobrescribir su contenido."),
            ["spa"] = ("Ocultar el aspecto seleccionado en la lista", "Gestiona el nombre y la eliminación desde el paquete.", "Cada paquete nuevo tiene preajustes propios de aspectos originales. Los nombres amarillos siguen al paquete; puedes aplicar o sobrescribir su contenido."),
            ["fra"] = ("Masquer l’apparence sélectionnée dans la liste", "Le nom et la suppression se gèrent depuis le pack.", "Chaque nouveau pack possède ses préréglages d’origine. Les noms jaunes suivent le nom du pack ; leur contenu peut être appliqué ou remplacé."),
            ["ita"] = ("Nascondi la skin scelta dall’elenco", "Gestisci nome ed eliminazione dal pacchetto.", "Ogni nuovo pacchetto ha preset propri con le skin originali. I nomi gialli seguono il pacchetto; puoi applicare o sovrascrivere i contenuti."),
            ["jpn"] = ("選択したスキンを一覧から隠す", "名前の変更と削除はスキンパックから行ってください。", "新規パックは分類ごとに専用の原版プリセットを持ちます。黄色の名前はパック名に連動し、内容は適用・上書きできます。"),
            ["kor"] = ("목록에서 선택한 스킨 숨기기", "이름 변경과 삭제는 스킨 묶음에서 관리하세요.", "새 묶음은 분류별 기본 스킨 프리셋을 가집니다. 노란 이름은 묶음 이름을 따르며 내용을 적용하거나 덮어쓸 수 있습니다."),
            ["pol"] = ("Ukryj wybraną skórkę na liście", "Zarządzaj nazwą i usuwaniem poprzez pakiet skórek.", "Nowy pakiet ma własne presety oryginalnych skórek. Żółte nazwy podążają za nazwą pakietu; zawartość można zastosować lub nadpisać."),
            ["ptb"] = ("Ocultar a skin selecionada da lista", "Gerencie o nome e a exclusão pelo pacote de skins.", "Cada pacote novo possui predefinições próprias de skins originais. Os nomes amarelos seguem o nome do pacote; o conteúdo pode ser aplicado ou sobrescrito."),
            ["rus"] = ("Скрыть выбранный облик из списка", "Название и удаление управляются через набор обликов.", "Новый набор получает свои пресеты оригинальных обликов. Жёлтые названия следуют за названием набора; содержимое можно применить или перезаписать."),
            ["tha"] = ("ซ่อนสกินที่เลือกจากรายการ", "จัดการชื่อและการลบผ่านชุดสกิน", "ชุดใหม่มีพรีเซ็ตสกินดั้งเดิมของแต่ละหมวด ชื่อสีเหลืองจะตรงกับชื่อชุด สามารถใช้หรือบันทึกทับเนื้อหาได้"),
            ["tur"] = ("Seçilen görünümü listeden gizle", "Adlandırma ve silme işlemlerini görünüm paketinden yönetin.", "Yeni paketler kendi özgün görünüm ön ayarlarını alır. Sarı adlar paket adını izler; içerikleri uygulayabilir veya üzerine yazabilirsiniz.")
        };

    internal static string BundleHideSource => BundlePresetTexts[CurrentLanguage].Hide;
    internal static string BundlePresetLocked => BundlePresetTexts[CurrentLanguage].Locked;
    internal static string BundlePresetHint => BundlePresetTexts[CurrentLanguage].Hint;
}
