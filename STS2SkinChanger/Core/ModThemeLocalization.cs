namespace STS2SkinChanger.Core;

internal enum ThemeText
{
    Theme, Editor, Panel, Selection, Buttons, TextBorder, Color, Opacity, Blur,
    HoverColor, TextColor, AccentColor, BorderColor, BorderWidth, Radius, FontScale,
    Outline, Revert, Collapse, Expand, Saved, SaveFailed,
    Shadow, EnableShadow, OffsetX, OffsetY, ShadowSize
}

internal static class ModThemeLocalization
{
    internal static readonly IReadOnlyDictionary<string, string[]> Packs = new Dictionary<string, string[]>
    {
        ["eng"] = "Theme|Theme tuning|Panel|Selected item|Buttons|Text and borders|Color|Opacity|Blur|Hover color|Text color|Accent color|Border color|Border width|Corner radius|Text size|Text outline|Revert|Collapse|Expand|Saved|Save failed|Text shadow|Enable shadow|Horizontal offset|Vertical offset|Shadow spread".Split('|'),
        ["zhs"] = "主题|主题调节|面板|选中项|按钮|文字与边框|颜色|不透明度|模糊|悬停颜色|文字颜色|强调颜色|边框颜色|边框宽度|圆角|文字大小|文字描边|撤销|收起|展开|已保存|保存失败|文字阴影|启用阴影|水平偏移|垂直偏移|阴影扩散".Split('|'),
        ["zht"] = "主題|主題調整|面板|選取項目|按鈕|文字與邊框|顏色|不透明度|模糊|懸停顏色|文字顏色|強調顏色|邊框顏色|邊框寬度|圓角|文字大小|文字描邊|復原|收合|展開|已儲存|儲存失敗|文字陰影|啟用陰影|水平偏移|垂直偏移|陰影擴散".Split('|'),
        ["deu"] = "Design|Design anpassen|Fläche|Auswahl|Schaltflächen|Text und Rahmen|Farbe|Deckkraft|Unschärfe|Hover-Farbe|Textfarbe|Akzentfarbe|Rahmenfarbe|Rahmenbreite|Eckenradius|Textgröße|Textkontur|Verwerfen|Einklappen|Ausklappen|Gespeichert|Speichern fehlgeschlagen|Textschatten|Schatten aktivieren|Horizontaler Versatz|Vertikaler Versatz|Schattenausdehnung".Split('|'),
        ["esp"] = "Tema|Ajustar tema|Panel|Selección|Botones|Texto y bordes|Color|Opacidad|Desenfoque|Color al señalar|Color del texto|Color de énfasis|Color del borde|Grosor del borde|Redondeado|Tamaño del texto|Contorno del texto|Revertir|Contraer|Expandir|Guardado|Error al guardar|Sombra del texto|Activar sombra|Desplazamiento horizontal|Desplazamiento vertical|Extensión de sombra".Split('|'),
        ["spa"] = "Tema|Ajustar tema|Panel|Selección|Botones|Texto y bordes|Color|Opacidad|Desenfoque|Color al señalar|Color del texto|Color de énfasis|Color del borde|Grosor del borde|Redondeado|Tamaño del texto|Contorno del texto|Revertir|Contraer|Expandir|Guardado|Error al guardar|Sombra del texto|Activar sombra|Desplazamiento horizontal|Desplazamiento vertical|Extensión de sombra".Split('|'),
        ["fra"] = "Thème|Réglage du thème|Panneau|Sélection|Boutons|Texte et bordures|Couleur|Opacité|Flou|Couleur au survol|Couleur du texte|Couleur d'accent|Couleur de bordure|Épaisseur|Arrondi|Taille du texte|Contour du texte|Annuler|Réduire|Développer|Enregistré|Échec de l'enregistrement|Ombre du texte|Activer l’ombre|Décalage horizontal|Décalage vertical|Étendue de l’ombre".Split('|'),
        ["ita"] = "Tema|Regola tema|Pannello|Selezione|Pulsanti|Testo e bordi|Colore|Opacità|Sfocatura|Colore al passaggio|Colore testo|Colore accento|Colore bordo|Spessore bordo|Angoli|Dimensione testo|Contorno testo|Annulla|Comprimi|Espandi|Salvato|Salvataggio fallito|Ombra del testo|Abilita ombra|Spostamento orizzontale|Spostamento verticale|Estensione ombra".Split('|'),
        ["jpn"] = "テーマ|テーマ調整|パネル|選択項目|ボタン|文字と枠線|色|不透明度|ぼかし|ホバー色|文字色|強調色|枠線の色|枠線の幅|角の丸み|文字サイズ|文字の縁取り|元に戻す|折りたたむ|展開|保存しました|保存に失敗|文字の影|影を有効にする|横方向のずれ|縦方向のずれ|影の広がり".Split('|'),
        ["kor"] = "테마|테마 조정|패널|선택 항목|버튼|글자와 테두리|색상|불투명도|흐림|호버 색상|글자 색상|강조 색상|테두리 색상|테두리 두께|모서리|글자 크기|글자 윤곽선|되돌리기|접기|펼치기|저장됨|저장 실패|글자 그림자|그림자 사용|가로 오프셋|세로 오프셋|그림자 크기".Split('|'),
        ["pol"] = "Motyw|Dostosuj motyw|Panel|Zaznaczenie|Przyciski|Tekst i obramowanie|Kolor|Krycie|Rozmycie|Kolor najechania|Kolor tekstu|Kolor akcentu|Kolor obramowania|Grubość ramki|Zaokrąglenie|Rozmiar tekstu|Obrys tekstu|Cofnij|Zwiń|Rozwiń|Zapisano|Błąd zapisu|Cień tekstu|Włącz cień|Przesunięcie poziome|Przesunięcie pionowe|Zasięg cienia".Split('|'),
        ["ptb"] = "Tema|Ajustar tema|Painel|Seleção|Botões|Texto e bordas|Cor|Opacidade|Desfoque|Cor ao apontar|Cor do texto|Cor de destaque|Cor da borda|Espessura da borda|Cantos|Tamanho do texto|Contorno do texto|Reverter|Recolher|Expandir|Salvo|Falha ao salvar|Sombra do texto|Ativar sombra|Deslocamento horizontal|Deslocamento vertical|Expansão da sombra".Split('|'),
        ["rus"] = "Тема|Настройка темы|Панель|Выбор|Кнопки|Текст и рамки|Цвет|Непрозрачность|Размытие|Цвет наведения|Цвет текста|Цвет акцента|Цвет рамки|Толщина рамки|Скругление|Размер текста|Обводка текста|Отменить|Свернуть|Развернуть|Сохранено|Ошибка сохранения|Тень текста|Включить тень|Смещение по горизонтали|Смещение по вертикали|Размер тени".Split('|'),
        ["tha"] = "ธีม|ปรับแต่งธีม|แผง|รายการที่เลือก|ปุ่ม|ข้อความและขอบ|สี|ความทึบ|ความเบลอ|สีเมื่อชี้|สีข้อความ|สีเน้น|สีขอบ|ความหนาขอบ|มุมโค้ง|ขนาดข้อความ|เส้นขอบข้อความ|ย้อนกลับ|ย่อ|ขยาย|บันทึกแล้ว|บันทึกไม่สำเร็จ|เงาข้อความ|เปิดใช้เงา|ระยะเลื่อนแนวนอน|ระยะเลื่อนแนวตั้ง|ขนาดเงา".Split('|'),
        ["tur"] = "Tema|Tema ayarı|Panel|Seçim|Düğmeler|Metin ve kenarlık|Renk|Opaklık|Bulanıklık|Üzerine gelme rengi|Metin rengi|Vurgu rengi|Kenarlık rengi|Kenarlık kalınlığı|Köşe yuvarlaklığı|Metin boyutu|Metin çizgisi|Geri al|Daralt|Genişlet|Kaydedildi|Kaydetme başarısız|Metin gölgesi|Gölgeyi etkinleştir|Yatay kaydırma|Dikey kaydırma|Gölge yayılımı".Split('|')
    };
    public static string Get(ThemeText text) => (Packs.TryGetValue(ModLocalization.CurrentLanguage, out var pack) ? pack : Packs["eng"])[(int)text];
}
