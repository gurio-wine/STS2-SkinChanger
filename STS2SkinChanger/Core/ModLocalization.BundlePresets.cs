namespace STS2SkinChanger.Core;

internal static partial class ModLocalization
{
    private static readonly IReadOnlyDictionary<string, (string Hide, string Locked)> BundlePresetTexts =
        new Dictionary<string, (string, string)>
        {
            ["eng"] = ("Hide the selected skin from the skin list", "Manage this preset's name and deletion through its skin bundle."),
            ["zhs"] = ("在皮肤列表中隐藏选中的皮肤", "请通过皮肤包管理名称和删除；此预设只能应用或覆盖。"),
            ["zht"] = ("在外觀清單中隱藏選取的外觀", "請透過組合包管理名稱與刪除；此預設只能套用或覆寫。"),
            ["deu"] = ("Gewählten Skin in der Skin-Liste ausblenden", "Name und Löschung werden über das Skin-Paket verwaltet."),
            ["esp"] = ("Ocultar el aspecto seleccionado en la lista", "Gestiona el nombre y la eliminación desde el paquete."),
            ["spa"] = ("Ocultar el aspecto seleccionado en la lista", "Gestiona el nombre y la eliminación desde el paquete."),
            ["fra"] = ("Masquer l’apparence sélectionnée dans la liste", "Le nom et la suppression se gèrent depuis le pack."),
            ["ita"] = ("Nascondi la skin scelta dall’elenco", "Gestisci nome ed eliminazione dal pacchetto."),
            ["jpn"] = ("選択したスキンを一覧から隠す", "名前の変更と削除はスキンパックから行ってください。"),
            ["kor"] = ("목록에서 선택한 스킨 숨기기", "이름 변경과 삭제는 스킨 묶음에서 관리하세요."),
            ["pol"] = ("Ukryj wybraną skórkę na liście", "Zarządzaj nazwą i usuwaniem poprzez pakiet skórek."),
            ["ptb"] = ("Ocultar a skin selecionada da lista", "Gerencie o nome e a exclusão pelo pacote de skins."),
            ["rus"] = ("Скрыть выбранный облик из списка", "Название и удаление управляются через набор обликов."),
            ["tha"] = ("ซ่อนสกินที่เลือกจากรายการ", "จัดการชื่อและการลบผ่านชุดสกิน"),
            ["tur"] = ("Seçilen görünümü listeden gizle", "Adlandırma ve silme işlemlerini görünüm paketinden yönetin.")
        };

    internal static string BundleHideSource => BundlePresetTexts[CurrentLanguage].Hide;
    internal static string BundlePresetLocked => BundlePresetTexts[CurrentLanguage].Locked;

}
