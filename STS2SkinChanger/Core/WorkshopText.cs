namespace STS2SkinChanger.Core;

internal enum WorkshopTextKey { Title, All, Subscribe, Download, Waiting, Checking, Ready, Restart, Failed, Offline, Empty, Target, Character, Cards, Monster, Ancient, Merchant, Companion, Event, Previous, Next }
internal static class WorkshopText
{
    // Same language set as the Workshop descriptions and the rest of the Mod UI.
    private static readonly Dictionary<string, string[]> Packs = new()
    {
        ["zhs"] = "创意工坊…|全部|订阅|下载|等待下载|检查兼容性…|可使用|需要重启|失败|Steam 不可用|没有匹配的皮肤|当前对象|角色|卡牌|怪物|先古之民|商人|生物|事件|上一页|下一页".Split('|'),
        ["zht"] = "創意工坊…|全部|訂閱|下載|等待下載|檢查相容性…|可使用|需要重新啟動|失敗|Steam 無法使用|沒有符合的皮膚|目前對象|角色|卡牌|怪物|先古之民|商人|生物|事件|上一頁|下一頁".Split('|'),
        ["eng"] = "Workshop…|All|Subscribe|Download|Waiting for download|Checking compatibility…|Available|Restart required|Failed|Steam unavailable|No matching skins|Current target|Characters|Cards|Monsters|Ancients|Merchants|Companions|Events|Previous|Next".Split('|'),
        ["deu"] = "Workshop…|Alle|Abonnieren|Herunterladen|Warte auf Download|Prüfe Kompatibilität…|Verfügbar|Neustart erforderlich|Fehlgeschlagen|Steam nicht verfügbar|Keine passenden Skins|Aktuelles Ziel|Charaktere|Karten|Monster|Uralte|Händler|Begleiter|Ereignisse|Zurück|Weiter".Split('|'),
        ["esp"] = "Workshop…|Todos|Suscribirse|Descargar|Esperando descarga|Comprobando compatibilidad…|Disponible|Requiere reinicio|Error|Steam no disponible|No hay aspectos compatibles|Objetivo actual|Personajes|Cartas|Monstruos|Ancestros|Mercaderes|Compañeros|Eventos|Anterior|Siguiente".Split('|'),
        ["spa"] = "Workshop…|Todos|Suscribirse|Descargar|Esperando descarga|Comprobando compatibilidad…|Disponible|Requiere reinicio|Error|Steam no disponible|No hay aspectos coincidentes|Objetivo actual|Personajes|Cartas|Monstruos|Ancestros|Mercaderes|Compañeros|Eventos|Anterior|Siguiente".Split('|'),
        ["fra"] = "Workshop…|Tous|S’abonner|Télécharger|En attente du téléchargement|Vérification de compatibilité…|Disponible|Redémarrage requis|Échec|Steam indisponible|Aucune apparence correspondante|Cible actuelle|Personnages|Cartes|Monstres|Anciens|Marchands|Compagnons|Événements|Précédent|Suivant".Split('|'),
        ["ita"] = "Workshop…|Tutti|Iscriviti|Scarica|In attesa del download|Verifica compatibilità…|Disponibile|Riavvio richiesto|Non riuscito|Steam non disponibile|Nessun aspetto corrispondente|Bersaglio attuale|Personaggi|Carte|Mostri|Antichi|Mercanti|Compagni|Eventi|Precedente|Successiva".Split('|'),
        ["jpn"] = "ワークショップ…|すべて|サブスクライブ|ダウンロード|ダウンロード待機中|互換性を確認中…|使用可能|再起動が必要|失敗|Steamに接続できません|該当するスキンはありません|現在の対象|キャラクター|カード|モンスター|古の存在|商人|仲間|イベント|前へ|次へ".Split('|'),
        ["kor"] = "창작마당…|전체|구독|다운로드|다운로드 대기 중|호환성 확인 중…|사용 가능|재시작 필요|실패|Steam 사용 불가|일치하는 스킨 없음|현재 대상|캐릭터|카드|몬스터|고대인|상인|동료|이벤트|이전|다음".Split('|'),
        ["pol"] = "Warsztat…|Wszystkie|Subskrybuj|Pobierz|Oczekiwanie na pobieranie|Sprawdzanie zgodności…|Dostępne|Wymagany restart|Błąd|Steam niedostępny|Brak pasujących skórek|Bieżący cel|Postacie|Karty|Potwory|Pradawni|Kupcy|Towarzysze|Wydarzenia|Poprzednia|Następna".Split('|'),
        ["ptb"] = "Oficina…|Todos|Inscrever-se|Baixar|Aguardando download|Verificando compatibilidade…|Disponível|Reinício necessário|Falha|Steam indisponível|Nenhuma aparência correspondente|Alvo atual|Personagens|Cartas|Monstros|Ancestrais|Mercadores|Companheiros|Eventos|Anterior|Próxima".Split('|'),
        ["rus"] = "Мастерская…|Все|Подписаться|Скачать|Ожидание загрузки|Проверка совместимости…|Доступно|Требуется перезапуск|Ошибка|Steam недоступен|Нет подходящих обликов|Текущая цель|Персонажи|Карты|Монстры|Древние|Торговцы|Спутники|События|Назад|Далее".Split('|'),
        ["tha"] = "เวิร์กชอป…|ทั้งหมด|สมัครสมาชิก|ดาวน์โหลด|รอดาวน์โหลด|กำลังตรวจสอบความเข้ากันได้…|พร้อมใช้|ต้องเริ่มเกมใหม่|ล้มเหลว|Steam ไม่พร้อมใช้งาน|ไม่พบสกินที่ตรงกัน|เป้าหมายปัจจุบัน|ตัวละคร|การ์ด|มอนสเตอร์|บรรพกาล|พ่อค้า|สหาย|เหตุการณ์|ก่อนหน้า|ถัดไป".Split('|'),
        ["tur"] = "Atölye…|Tümü|Abone ol|İndir|İndirme bekleniyor|Uyumluluk denetleniyor…|Kullanılabilir|Yeniden başlatılmalı|Başarısız|Steam kullanılamıyor|Eşleşen görünüm yok|Geçerli hedef|Karakterler|Kartlar|Canavarlar|Kadimler|Tüccarlar|Yoldaşlar|Olaylar|Önceki|Sonraki".Split('|')
    };
    public static string Get(WorkshopTextKey key) => ForLanguage(ModLocalization.CurrentLanguage, key);
    public static string ForLanguage(string language, WorkshopTextKey key) => Packs.GetValueOrDefault(language, Packs["eng"])[(int)key];
    public static string Kind(string kind) => Get(kind switch
    {
        "character" => WorkshopTextKey.Character,
        "cards" => WorkshopTextKey.Cards,
        "monster" => WorkshopTextKey.Monster,
        "ancient" => WorkshopTextKey.Ancient,
        "merchant" => WorkshopTextKey.Merchant,
        "companion" => WorkshopTextKey.Companion,
        "event" => WorkshopTextKey.Event,
        _ => WorkshopTextKey.All
    });
    public static string SteamLanguage => ModLocalization.CurrentLanguage switch
    {
        "zhs" => "schinese",
        "zht" => "tchinese",
        "deu" => "german",
        "esp" => "spanish",
        "spa" => "latam",
        "fra" => "french",
        "ita" => "italian",
        "jpn" => "japanese",
        "kor" => "koreana",
        "pol" => "polish",
        "ptb" => "brazilian",
        "rus" => "russian",
        "tha" => "thai",
        "tur" => "turkish",
        _ => "english"
    };
}
