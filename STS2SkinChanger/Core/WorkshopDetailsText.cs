namespace STS2SkinChanger.Core;

internal enum WorkshopDetailsTextKey
{
    Subscriptions, LifetimeSubscriptions, Rating, Published, Updated, Comments, Favorites,
    SubscriptionCount, LifetimeCount, RatingValue, PublishedDate, UpdatedDate, CommentCount, FavoriteCount,
    NoDescription, ImageUnavailable, LoadFailed, OpenSteam,
    LatestReply, Forward, Reverse, ReplyNumber
}

internal static class WorkshopDetailsText
{
    private static readonly Dictionary<string, string[]> Packs = new()
    {
        ["zhs"] = "订阅最多|累计订阅最多|好评优先|最新发布|最近更新|留言最多|收藏最多|订阅 {0}|累计订阅 {0}|评分 {0:0.#} · {1} 票|发布 {0}|更新 {0}|留言 {0}|收藏 {0}|暂无简介|暂无可用图片|简介暂不可用|在 Steam 查看|楼层最新|正序|倒序|楼层 {0}".Split('|'),
        ["zht"] = "訂閱最多|累計訂閱最多|好評優先|最新發佈|最近更新|留言最多|收藏最多|訂閱 {0}|累計訂閱 {0}|評分 {0:0.#} · {1} 票|發佈 {0}|更新 {0}|留言 {0}|收藏 {0}|暫無簡介|暫無可用圖片|簡介暫不可用|在 Steam 查看|樓層最新|正序|倒序|樓層 {0}".Split('|'),
        ["eng"] = "Most subscribed|Most lifetime subscribers|Highest rated|Newest published|Recently updated|Most comments|Most favorites|Subscribers {0}|Lifetime subscribers {0}|Score {0:0.#} · {1} votes|Published {0}|Updated {0}|Comments {0}|Favorites {0}|No description|No image available|Description unavailable|View on Steam|Newest discussion post|Normal|Reverse|Post #{0}".Split('|'),
        ["deu"] = "Meiste Abonnenten|Meiste Abonnenten gesamt|Beste Bewertung|Neueste Veröffentlichung|Zuletzt aktualisiert|Meiste Kommentare|Meiste Favoriten|Abonnenten {0}|Abonnenten gesamt {0}|Wertung {0:0.#} · {1} Stimmen|Veröffentlicht {0}|Aktualisiert {0}|Kommentare {0}|Favoriten {0}|Keine Beschreibung|Kein Bild verfügbar|Beschreibung nicht verfügbar|Auf Steam ansehen|Neuester Forumsbeitrag|Normal|Umgekehrt|Beitrag #{0}".Split('|'),
        ["esp"] = "Más suscritos|Más suscriptores históricos|Mejor valorados|Más recientes|Recién actualizados|Más comentarios|Más favoritos|Suscriptores {0}|Suscriptores históricos {0}|Nota {0:0.#} · {1} votos|Publicado {0}|Actualizado {0}|Comentarios {0}|Favoritos {0}|Sin descripción|Imagen no disponible|Descripción no disponible|Ver en Steam|Publicación del foro más reciente|Normal|Inverso|Mensaje n.º {0}".Split('|'),
        ["spa"] = "Más suscritos|Más suscriptores históricos|Mejor valorados|Más recientes|Recién actualizados|Más comentarios|Más favoritos|Suscriptores {0}|Suscriptores históricos {0}|Nota {0:0.#} · {1} votos|Publicado {0}|Actualizado {0}|Comentarios {0}|Favoritos {0}|Sin descripción|Imagen no disponible|Descripción no disponible|Ver en Steam|Publicación del foro más reciente|Normal|Inverso|Mensaje n.º {0}".Split('|'),
        ["fra"] = "Plus d’abonnés|Plus d’abonnés cumulés|Mieux notés|Publications récentes|Mises à jour récentes|Plus de commentaires|Plus de favoris|Abonnés {0}|Abonnés cumulés {0}|Note {0:0.#} · {1} votes|Publié le {0}|Mis à jour le {0}|Commentaires {0}|Favoris {0}|Aucune description|Aucune image disponible|Description indisponible|Voir sur Steam|Message du forum le plus récent|Normal|Inverse|Message nº {0}".Split('|'),
        ["ita"] = "Più iscritti|Più iscritti totali|Migliori valutazioni|Pubblicati di recente|Aggiornati di recente|Più commenti|Più preferiti|Iscritti {0}|Iscritti totali {0}|Voto {0:0.#} · {1} valutazioni|Pubblicato {0}|Aggiornato {0}|Commenti {0}|Preferiti {0}|Nessuna descrizione|Nessuna immagine|Descrizione non disponibile|Vedi su Steam|Post del forum più recente|Normale|Inverso|Post n. {0}".Split('|'),
        ["jpn"] = "登録者数順|累計登録者数順|高評価順|公開日が新しい順|更新日が新しい順|コメント数順|お気に入り数順|登録者 {0}|累計登録者 {0}|評価 {0:0.#} · {1} 票|公開 {0}|更新 {0}|コメント {0}|お気に入り {0}|説明はありません|画像はありません|説明を取得できません|Steamで見る|投稿が新しい順|通常順|逆順|投稿 #{0}".Split('|'),
        ["kor"] = "구독자 많은 순|누적 구독자 많은 순|평점 높은 순|최신 게시순|최근 업데이트순|댓글 많은 순|즐겨찾기 많은 순|구독자 {0}|누적 구독자 {0}|평점 {0:0.#} · {1}표|게시 {0}|업데이트 {0}|댓글 {0}|즐겨찾기 {0}|설명 없음|이미지 없음|설명을 불러올 수 없음|Steam에서 보기|최신 게시글순|기본순|역순|게시글 #{0}".Split('|'),
        ["pol"] = "Najwięcej subskrypcji|Najwięcej subskrypcji łącznie|Najlepiej oceniane|Najnowsze publikacje|Ostatnio aktualizowane|Najwięcej komentarzy|Najwięcej ulubionych|Subskrypcje {0}|Subskrypcje łącznie {0}|Ocena {0:0.#} · {1} głosów|Publikacja {0}|Aktualizacja {0}|Komentarze {0}|Ulubione {0}|Brak opisu|Brak obrazu|Opis niedostępny|Zobacz na Steam|Najnowszy wpis na forum|Normalnie|Odwrotnie|Wpis nr {0}".Split('|'),
        ["ptb"] = "Mais inscritos|Mais inscritos acumulados|Melhor avaliados|Mais recentes|Atualizados recentemente|Mais comentários|Mais favoritos|Inscritos {0}|Inscritos acumulados {0}|Nota {0:0.#} · {1} votos|Publicado {0}|Atualizado {0}|Comentários {0}|Favoritos {0}|Sem descrição|Imagem indisponível|Descrição indisponível|Ver no Steam|Postagem mais recente do fórum|Normal|Inverso|Postagem nº {0}".Split('|'),
        ["rus"] = "Больше подписчиков|Больше подписчиков за всё время|Лучшие оценки|Новые публикации|Недавно обновлённые|Больше комментариев|Больше добавлений в избранное|Подписчики {0}|Всего подписчиков {0}|Оценка {0:0.#} · {1} голосов|Публикация {0}|Обновление {0}|Комментарии {0}|В избранном {0}|Нет описания|Нет изображения|Описание недоступно|Открыть в Steam|Новый пост обсуждения|Обычный|Обратный|Пост №{0}".Split('|'),
        ["tha"] = "สมาชิกมากที่สุด|สมาชิกสะสมมากที่สุด|คะแนนสูงสุด|เผยแพร่ล่าสุด|อัปเดตล่าสุด|ความคิดเห็นมากที่สุด|รายการโปรดมากที่สุด|สมาชิก {0}|สมาชิกสะสม {0}|คะแนน {0:0.#} · {1} โหวต|เผยแพร่ {0}|อัปเดต {0}|ความคิดเห็น {0}|รายการโปรด {0}|ไม่มีคำอธิบาย|ไม่มีรูปภาพ|โหลดคำอธิบายไม่ได้|ดูบน Steam|โพสต์สนทนาล่าสุด|ปกติ|ย้อนกลับ|โพสต์ #{0}".Split('|'),
        ["tur"] = "En çok abone|Toplamda en çok abone|En yüksek puan|En yeni yayınlar|Son güncellenenler|En çok yorum|En çok favori|Aboneler {0}|Toplam aboneler {0}|Puan {0:0.#} · {1} oy|Yayın {0}|Güncelleme {0}|Yorumlar {0}|Favoriler {0}|Açıklama yok|Görsel yok|Açıklama alınamadı|Steam'de görüntüle|En yeni tartışma gönderisi|Normal|Ters|Gönderi #{0}".Split('|')
    };
    public static string Get(WorkshopDetailsTextKey key) => ForLanguage(ModLocalization.CurrentLanguage, key);
    public static string ForLanguage(string language, WorkshopDetailsTextKey key) => Packs.GetValueOrDefault(language, Packs["eng"])[(int)key];
}
