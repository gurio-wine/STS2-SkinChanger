namespace STS2SkinChanger.Core;

internal enum GitHubSkinTextKey
{
    Source, Steam, GitHub, Install, Reinstall, Update, Installed, NotInstalled, Updatable, Unrecognized, OpenRepo,
    Refresh, Refreshing, RefreshFailed, RateLimited, Installing, InstallFailed, All, Stars, Updated, Name, Empty
}

// The GitHub source is part of the same panel as the Workshop browser, so it must speak the
// same 15 languages. Order matters: the keys above index into every row below.
internal static class GitHubSkinText
{
    private static readonly Dictionary<string, string[]> Packs = new()
    {
        ["zhs"] = "来源|Steam|GitHub|安装|重新安装|更新|已安装|未安装|可更新|未识别|打开仓库|刷新|刷新中…|刷新失败|接口受限，请稍后重试|安装中…|安装失败|全部|星标|最近更新|名称|没有匹配的 GitHub 皮肤".Split('|'),
        ["zht"] = "來源|Steam|GitHub|安裝|重新安裝|更新|已安裝|未安裝|可更新|未識別|開啟倉庫|重新整理|重新整理中…|重新整理失敗|介面受限，請稍後重試|安裝中…|安裝失敗|全部|星號|最近更新|名稱|沒有符合的 GitHub 面板".Split('|'),
        ["eng"] = "Source|Steam|GitHub|Install|Reinstall|Update|Installed|Not installed|Update available|Not recognized|Open repository|Refresh|Refreshing…|Refresh failed|Rate limited, try later|Installing…|Install failed|All|Stars|Recently updated|Name|No matching GitHub skins".Split('|'),
        ["deu"] = "Quelle|Steam|GitHub|Installieren|Neu installieren|Aktualisieren|Installiert|Nicht installiert|Aktualisierung verfügbar|Nicht erkannt|Repository öffnen|Aktualisieren|Wird aktualisiert…|Aktualisierung fehlgeschlagen|Limit erreicht, später erneut versuchen|Wird installiert…|Installation fehlgeschlagen|Alle|Sterne|Zuletzt aktualisiert|Name|Keine passenden GitHub-Skins".Split('|'),
        ["esp"] = "Origen|Steam|GitHub|Instalar|Reinstalar|Actualizar|Instalado|No instalado|Actualización disponible|No reconocido|Abrir repositorio|Actualizar|Actualizando…|Error al actualizar|Límite alcanzado, inténtalo más tarde|Instalando…|Error al instalar|Todos|Estrellas|Actualizado recientemente|Nombre|No hay aspectos de GitHub coincidentes".Split('|'),
        ["spa"] = "Origen|Steam|GitHub|Instalar|Reinstalar|Actualizar|Instalado|No instalado|Actualización disponible|No reconocido|Abrir repositorio|Actualizar|Actualizando…|Falló la actualización|Límite alcanzado, inténtalo más tarde|Instalando…|Falló la instalación|Todos|Estrellas|Actualizado recientemente|Nombre|No hay aspectos de GitHub que coincidan".Split('|'),
        ["fra"] = "Source|Steam|GitHub|Installer|Réinstaller|Mettre à jour|Installé|Non installé|Mise à jour disponible|Non reconnu|Ouvrir le dépôt|Actualiser|Actualisation…|Échec de l’actualisation|Limite atteinte, réessayez plus tard|Installation…|Échec de l’installation|Tous|Étoiles|Mis à jour récemment|Nom|Aucune apparence GitHub correspondante".Split('|'),
        ["ita"] = "Origine|Steam|GitHub|Installa|Reinstalla|Aggiorna|Installato|Non installato|Aggiornamento disponibile|Non riconosciuto|Apri repository|Aggiorna|Aggiornamento…|Aggiornamento non riuscito|Limite raggiunto, riprova più tardi|Installazione…|Installazione non riuscita|Tutti|Stelle|Aggiornato di recente|Nome|Nessun aspetto GitHub corrispondente".Split('|'),
        ["jpn"] = "取得元|Steam|GitHub|インストール|再インストール|更新|インストール済み|未インストール|更新あり|未認識|リポジトリを開く|更新|更新中…|更新に失敗|制限中。後でもう一度|インストール中…|インストール失敗|すべて|スター|最近の更新|名前|一致するGitHubスキンがありません".Split('|'),
        ["kor"] = "출처|Steam|GitHub|설치|재설치|업데이트|설치됨|설치 안 됨|업데이트 가능|인식되지 않음|저장소 열기|새로 고침|새로 고치는 중…|새로 고침 실패|요청 한도 초과, 나중에 다시 시도|설치 중…|설치 실패|전체|별표|최근 업데이트|이름|일치하는 GitHub 스킨 없음".Split('|'),
        ["pol"] = "Źródło|Steam|GitHub|Zainstaluj|Zainstaluj ponownie|Aktualizuj|Zainstalowano|Nie zainstalowano|Dostępna aktualizacja|Nierozpoznane|Otwórz repozytorium|Odśwież|Odświeżanie…|Odświeżanie nie powiodło się|Limit osiągnięty, spróbuj później|Instalowanie…|Instalacja nie powiodła się|Wszystkie|Gwiazdki|Ostatnio zaktualizowane|Nazwa|Brak pasujących skórek GitHub".Split('|'),
        ["ptb"] = "Origem|Steam|GitHub|Instalar|Reinstalar|Atualizar|Instalado|Não instalado|Atualização disponível|Não reconhecido|Abrir repositório|Atualizar|Atualizando…|Falha ao atualizar|Limite atingido, tente mais tarde|Instalando…|Falha na instalação|Todos|Estrelas|Atualizado recentemente|Nome|Nenhum visual do GitHub correspondente".Split('|'),
        ["rus"] = "Источник|Steam|GitHub|Установить|Переустановить|Обновить|Установлено|Не установлено|Доступно обновление|Не распознано|Открыть репозиторий|Обновить|Обновление…|Не удалось обновить|Лимит исчерпан, попробуйте позже|Установка…|Не удалось установить|Все|Звёзды|Недавно обновлено|Название|Нет подходящих обликов с GitHub".Split('|'),
        ["tha"] = "แหล่งที่มา|Steam|GitHub|ติดตั้ง|ติดตั้งใหม่|อัปเดต|ติดตั้งแล้ว|ยังไม่ได้ติดตั้ง|มีอัปเดต|ไม่รู้จัก|เปิดที่เก็บโค้ด|รีเฟรช|กำลังรีเฟรช…|รีเฟรชล้มเหลว|ถึงขีดจำกัด ลองใหม่ภายหลัง|กำลังติดตั้ง…|ติดตั้งล้มเหลว|ทั้งหมด|ดาว|อัปเดตล่าสุด|ชื่อ|ไม่พบสกิน GitHub ที่ตรงกัน".Split('|'),
        ["tur"] = "Kaynak|Steam|GitHub|Kur|Yeniden kur|Güncelle|Kurulu|Kurulu değil|Güncelleme var|Tanınmadı|Depoyu aç|Yenile|Yenileniyor…|Yenileme başarısız|Sınır aşıldı, sonra deneyin|Kuruluyor…|Kurulum başarısız|Tümü|Yıldız|Son güncellenen|Ad|Eşleşen GitHub görünümü yok".Split('|')
    };

    // The UI reads the player's language; this table stays pure so it can be tested offline.
    public static string ForLanguage(string language, GitHubSkinTextKey key) =>
        Packs.GetValueOrDefault(language, Packs["eng"])[(int)key];
}
