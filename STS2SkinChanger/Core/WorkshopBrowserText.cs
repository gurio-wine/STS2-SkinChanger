namespace STS2SkinChanger.Core;

internal enum WorkshopBrowserTextKey { Unsubscribe, Unsubscribed, Unsubscribing, Retained, Region, Shared, Other, Colorless, AncientCards, MiscCards, Curse, Event, Status, Token, Quest }
internal static class WorkshopBrowserText
{
    private static readonly Dictionary<string, string[]> Packs = new()
    {
        ["zhs"] = "取消订阅|已取消订阅|取消订阅中…|本次已加载的皮肤仍可使用；退出游戏后由 Steam 移除。|地区|跨地区|其它地区|无色牌|先古之民给予的牌|其它牌|诅咒牌|事件牌|状态牌|衍生牌|任务牌".Split('|'),
        ["zht"] = "取消訂閱|已取消訂閱|取消訂閱中…|本次已載入的皮膚仍可使用；退出遊戲後由 Steam 移除。|地區|跨地區|其他地區|無色牌|先古之民給予的牌|其他牌|詛咒牌|事件牌|狀態牌|衍生牌|任務牌".Split('|'),
        ["eng"] = "Unsubscribe|Unsubscribed|Unsubscribing…|Loaded skins remain available this session; Steam removes the files after the game exits.|Region|Across regions|Other regions|Colorless cards|Cards from Ancients|Other cards|Curse cards|Event cards|Status cards|Generated cards|Quest cards".Split('|'),
        ["deu"] = "Abbestellen|Abbestellt|Wird abbestellt…|Geladene Skins bleiben in dieser Sitzung verfügbar; Steam entfernt die Dateien nach Spielende.|Region|Regionsübergreifend|Andere Regionen|Farblose Karten|Karten der Uralten|Andere Karten|Fluchkarten|Ereigniskarten|Statuskarten|Erzeugte Karten|Questkarten".Split('|'),
        ["esp"] = "Cancelar suscripción|Suscripción cancelada|Cancelando suscripción…|Los aspectos cargados siguen disponibles esta sesión; Steam elimina los archivos al salir del juego.|Región|Varias regiones|Otras regiones|Cartas incoloras|Cartas de los Ancestros|Otras cartas|Maldiciones|Cartas de evento|Cartas de estado|Cartas generadas|Cartas de misión".Split('|'),
        ["spa"] = "Cancelar suscripción|Suscripción cancelada|Cancelando suscripción…|Los aspectos cargados siguen disponibles en esta sesión; Steam elimina los archivos al salir del juego.|Región|Varias regiones|Otras regiones|Cartas incoloras|Cartas de los Ancestros|Otras cartas|Maldiciones|Cartas de evento|Cartas de estado|Cartas generadas|Cartas de misión".Split('|'),
        ["fra"] = "Se désabonner|Désabonné|Désabonnement…|Les apparences chargées restent disponibles pour cette session ; Steam supprime les fichiers à la fermeture du jeu.|Région|Plusieurs régions|Autres régions|Cartes incolores|Cartes des Anciens|Autres cartes|Malédictions|Cartes d’événement|Cartes de statut|Cartes générées|Cartes de quête".Split('|'),
        ["ita"] = "Annulla iscrizione|Iscrizione annullata|Annullamento…|Gli aspetti caricati restano disponibili in questa sessione; Steam rimuove i file alla chiusura del gioco.|Regione|Più regioni|Altre regioni|Carte incolori|Carte degli Antichi|Altre carte|Maledizioni|Carte evento|Carte stato|Carte generate|Carte missione".Split('|'),
        ["jpn"] = "登録解除|登録解除済み|登録解除中…|読み込み済みのスキンは今回の起動中は使用可能です。ゲーム終了後に Steam がファイルを削除します。|地域|地域共通|その他の地域|無色カード|古の存在からのカード|その他のカード|呪いカード|イベントカード|状態異常カード|生成カード|クエストカード".Split('|'),
        ["kor"] = "구독 취소|구독 취소됨|구독 취소 중…|이미 로딩된 스킨은 이번 실행 중 계속 사용할 수 있습니다. 게임 종료 후 Steam이 파일을 제거합니다.|지역|공통 지역|기타 지역|무색 카드|고대인이 준 카드|기타 카드|저주 카드|이벤트 카드|상태 카드|생성 카드|퀘스트 카드".Split('|'),
        ["pol"] = "Anuluj subskrypcję|Subskrypcja anulowana|Anulowanie…|Wczytane skórki pozostaną dostępne w tej sesji; Steam usunie pliki po zamknięciu gry.|Region|Wiele regionów|Pozostałe regiony|Karty bezbarwne|Karty Pradawnych|Inne karty|Klątwy|Karty wydarzeń|Karty statusu|Karty tworzone|Karty zadań".Split('|'),
        ["ptb"] = "Cancelar inscrição|Inscrição cancelada|Cancelando inscrição…|As aparências carregadas continuam disponíveis nesta sessão; o Steam remove os arquivos ao fechar o jogo.|Região|Várias regiões|Outras regiões|Cartas incolores|Cartas dos Ancestrais|Outras cartas|Maldições|Cartas de evento|Cartas de estado|Cartas geradas|Cartas de missão".Split('|'),
        ["rus"] = "Отписаться|Подписка отменена|Отмена подписки…|Загруженные облики доступны до конца сеанса; Steam удалит файлы после выхода из игры.|Регион|Разные регионы|Прочие регионы|Бесцветные карты|Карты Древних|Другие карты|Проклятия|Карты событий|Карты состояния|Создаваемые карты|Карты заданий".Split('|'),
        ["tha"] = "ยกเลิกสมาชิก|ยกเลิกแล้ว|กำลังยกเลิก…|สกินที่โหลดแล้วจะยังใช้ได้ในครั้งนี้ Steam จะลบไฟล์เมื่อออกจากเกม|พื้นที่|หลายพื้นที่|พื้นที่อื่น|การ์ดไร้สี|การ์ดจากบรรพกาล|การ์ดอื่น|การ์ดคำสาป|การ์ดเหตุการณ์|การ์ดสถานะ|การ์ดที่สร้างขึ้น|การ์ดภารกิจ".Split('|'),
        ["tur"] = "Abonelikten çık|Abonelik iptal edildi|İptal ediliyor…|Yüklü görünümler bu oturumda kullanılabilir; Steam, oyun kapandıktan sonra dosyaları kaldırır.|Bölge|Bölgeler arası|Diğer bölgeler|Renksiz kartlar|Kadimlerin kartları|Diğer kartlar|Lanet kartları|Olay kartları|Durum kartları|Oluşturulan kartlar|Görev kartları".Split('|')
    };
    public static string Get(WorkshopBrowserTextKey key) => ForLanguage(ModLocalization.CurrentLanguage, key);
    public static string ForLanguage(string language, WorkshopBrowserTextKey key) => Packs.GetValueOrDefault(language, Packs["eng"])[(int)key];
}
