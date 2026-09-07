namespace STS2SkinChanger.Core;

internal enum WorkshopCommunityTextKey { SubmitMod, SubmitPreset, Hot, Removed, OpenFailed, Subscribed, NotSubscribed }
internal static class WorkshopCommunityText
{
    private static readonly Dictionary<string, string[]> Packs = new()
    {
        ["zhs"] = "投稿模组|投稿预设|可免重启|已取消订阅\n关闭游戏后自动删除|无法打开 Steam 页面|已订阅|未订阅".Split('|'),
        ["zht"] = "投稿模組|投稿預設|可免重啟|已取消訂閱\n關閉遊戲後自動刪除|無法開啟 Steam 頁面|已訂閱|未訂閱".Split('|'),
        ["eng"] = "Suggest a Mod|Share a preset|No restart needed|Unsubscribed\nRemoved automatically after closing the game|Cannot open the Steam page|Subscribed|Not subscribed".Split('|'),
        ["deu"] = "Mod vorschlagen|Vorlage teilen|Ohne Neustart|Abbestellt\nWird nach Spielende automatisch entfernt|Steam-Seite kann nicht geöffnet werden|Abonniert|Nicht abonniert".Split('|'),
        ["esp"] = "Proponer un mod|Compartir preajuste|Sin reinicio|Suscripción cancelada\nSe eliminará al cerrar el juego|No se puede abrir la página de Steam|Con suscripción|Sin suscripción".Split('|'),
        ["spa"] = "Proponer un mod|Compartir preajuste|Sin reinicio|Suscripción cancelada\nSe eliminará al cerrar el juego|No se puede abrir la página de Steam|Con suscripción|Sin suscripción".Split('|'),
        ["fra"] = "Proposer un mod|Partager un préréglage|Sans redémarrage|Désabonné\nSuppression automatique à la fermeture du jeu|Impossible d’ouvrir la page Steam|Abonné|Non abonné".Split('|'),
        ["ita"] = "Proponi un mod|Condividi preset|Senza riavvio|Iscrizione annullata\nRimozione automatica alla chiusura del gioco|Impossibile aprire la pagina Steam|Iscrizione attiva|Senza iscrizione".Split('|'),
        ["jpn"] = "Modを推薦|プリセットを共有|再起動不要|登録解除済み\nゲーム終了後に自動削除|Steamページを開けません|登録済み|未登録".Split('|'),
        ["kor"] = "모드 추천|프리셋 공유|재시작 불필요|구독 취소됨\n게임 종료 후 자동 삭제|Steam 페이지를 열 수 없습니다|구독함|미구독".Split('|'),
        ["pol"] = "Zaproponuj mod|Udostępnij preset|Bez restartu|Subskrypcja anulowana\nPliki zostaną usunięte po zamknięciu gry|Nie można otworzyć strony Steam|Subskrybowane|Niesubskrybowane".Split('|'),
        ["ptb"] = "Sugerir mod|Compartilhar preset|Sem reinício|Inscrição cancelada\nRemoção automática ao fechar o jogo|Não foi possível abrir a página do Steam|Inscritos|Não inscritos".Split('|'),
        ["rus"] = "Предложить мод|Поделиться темой|Без перезапуска|Подписка отменена\nФайлы будут удалены после закрытия игры|Не удалось открыть страницу Steam|С подпиской|Без подписки".Split('|'),
        ["tha"] = "เสนอ Mod|แชร์พรีเซ็ต|ไม่ต้องเริ่มใหม่|ยกเลิกแล้ว\nลบอัตโนมัติหลังปิดเกม|เปิดหน้า Steam ไม่ได้|สมัครแล้ว|ยังไม่ได้สมัคร".Split('|'),
        ["tur"] = "Mod öner|Ön ayar paylaş|Yeniden başlatmadan|Abonelik iptal edildi\nOyun kapanınca otomatik silinir|Steam sayfası açılamadı|Abone olunan|Abone olunmayan".Split('|')
    };
    public static string Get(WorkshopCommunityTextKey key) => ForLanguage(ModLocalization.CurrentLanguage, key);
    public static string ForLanguage(string language, WorkshopCommunityTextKey key) => Packs.GetValueOrDefault(language, Packs["eng"])[(int)key];
}
