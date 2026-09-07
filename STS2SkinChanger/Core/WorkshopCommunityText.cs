namespace STS2SkinChanger.Core;

internal enum WorkshopCommunityTextKey { SubmitMod, SubmitPreset, Hot, Unknown, Blocked, LoadFilter, Removed, OpenFailed }
internal static class WorkshopCommunityText
{
    private static readonly Dictionary<string, string[]> Packs = new()
    {
        ["zhs"] = "投稿模组|投稿预设|可免重启|待确认|不兼容|重启要求|已取消订阅\n关闭游戏后自动删除|无法打开 Steam 页面".Split('|'),
        ["zht"] = "投稿模組|投稿預設|可免重啟|待確認|不相容|重啟要求|已取消訂閱\n關閉遊戲後自動刪除|無法開啟 Steam 頁面".Split('|'),
        ["eng"] = "Suggest a Mod|Share a preset|No restart needed|Not yet checked|Incompatible|Restart requirement|Unsubscribed\nRemoved automatically after closing the game|Cannot open the Steam page".Split('|'),
        ["deu"] = "Mod vorschlagen|Vorlage teilen|Ohne Neustart|Ungeprüft|Inkompatibel|Neustartbedarf|Abbestellt\nWird nach Spielende automatisch entfernt|Steam-Seite kann nicht geöffnet werden".Split('|'),
        ["esp"] = "Proponer un mod|Compartir preajuste|Sin reinicio|Sin comprobar|Incompatible|Reinicio|Suscripción cancelada\nSe eliminará al cerrar el juego|No se puede abrir la página de Steam".Split('|'),
        ["spa"] = "Proponer un mod|Compartir preajuste|Sin reinicio|Sin comprobar|Incompatible|Reinicio|Suscripción cancelada\nSe eliminará al cerrar el juego|No se puede abrir la página de Steam".Split('|'),
        ["fra"] = "Proposer un mod|Partager un préréglage|Sans redémarrage|Non vérifié|Incompatible|Redémarrage|Désabonné\nSuppression automatique à la fermeture du jeu|Impossible d’ouvrir la page Steam".Split('|'),
        ["ita"] = "Proponi un mod|Condividi preset|Senza riavvio|Da verificare|Incompatibile|Riavvio|Iscrizione annullata\nRimozione automatica alla chiusura del gioco|Impossibile aprire la pagina Steam".Split('|'),
        ["jpn"] = "Modを推薦|プリセットを共有|再起動不要|未確認|非対応|再起動の要否|登録解除済み\nゲーム終了後に自動削除|Steamページを開けません".Split('|'),
        ["kor"] = "모드 추천|프리셋 공유|재시작 불필요|미확인|호환 불가|재시작 여부|구독 취소됨\n게임 종료 후 자동 삭제|Steam 페이지를 열 수 없습니다".Split('|'),
        ["pol"] = "Zaproponuj mod|Udostępnij preset|Bez restartu|Niesprawdzone|Niezgodne|Wymóg restartu|Subskrypcja anulowana\nPliki zostaną usunięte po zamknięciu gry|Nie można otworzyć strony Steam".Split('|'),
        ["ptb"] = "Sugerir mod|Compartilhar preset|Sem reinício|Não verificado|Incompatível|Reinício|Inscrição cancelada\nRemoção automática ao fechar o jogo|Não foi possível abrir a página do Steam".Split('|'),
        ["rus"] = "Предложить мод|Поделиться темой|Без перезапуска|Не проверено|Несовместимо|Перезапуск|Подписка отменена\nФайлы будут удалены после закрытия игры|Не удалось открыть страницу Steam".Split('|'),
        ["tha"] = "เสนอ Mod|แชร์พรีเซ็ต|ไม่ต้องเริ่มใหม่|ยังไม่ตรวจสอบ|ไม่เข้ากัน|การเริ่มเกมใหม่|ยกเลิกแล้ว\nลบอัตโนมัติหลังปิดเกม|เปิดหน้า Steam ไม่ได้".Split('|'),
        ["tur"] = "Mod öner|Ön ayar paylaş|Yeniden başlatmadan|Henüz denetlenmedi|Uyumsuz|Yeniden başlatma|Abonelik iptal edildi\nOyun kapanınca otomatik silinir|Steam sayfası açılamadı".Split('|')
    };
    public static string Get(WorkshopCommunityTextKey key) => ForLanguage(ModLocalization.CurrentLanguage, key);
    public static string ForLanguage(string language, WorkshopCommunityTextKey key) => Packs.GetValueOrDefault(language, Packs["eng"])[(int)key];
}
