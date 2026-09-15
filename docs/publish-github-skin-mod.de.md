# Veröffentliche deinen Skin auf GitHub (Autoren-Anleitung)

[简体中文](投稿GitHub皮肤Mod说明.md) · [English](publish-github-skin-mod.md)

Spieler mit installiertem „Skin Changer“ sehen dein Repository im Spiel unter **☼Skin-Workshop → Quelle: GitHub** und installieren es mit einem Klick.
Du musst nur drei Dinge tun: ein Topic hinzufügen, ein Zip veröffentlichen, einen Code einfügen.

(Um das eigene Paket zu scannen, brauchst du zuerst Skin Changer — suche im Steam Workshop danach.)

## 1. Topic hinzufügen

Auf deiner Repository-Seite: Zahnrad neben About → Topics:

```
sts2-sc-mod
```

Fehlt das Topic oder ist es falsch geschrieben, findet das Spiel dein Repository nicht; geforkte Repositories erscheinen ebenfalls nicht.

## 2. Ein Release mit dem Zip veröffentlichen

- Im Zip liegt das Skin-Paket, das das Spiel lädt (`<id>.json` + `.pck` / `.dll`; auch ein Paket nur mit Karten-Grafiken funktioniert).
- Lege die Dateien entweder ins **Stammverzeichnis** des Zips oder in Ordner (beliebig tief; ein Ordner mit dem Repository-Namen wird nur bevorzugt, wenn das Zip mehrere Mods enthält).
- Gelesen wird nur `.zip`: `.rar`, `.7z` und `.tar.gz` gelten als gar kein Anhang, und das Repository bleibt „Nicht erkannt“.
- Hänge es an dein **neuestes** Release und markiere es **nicht** als Pre-Release (das Panel kann solche nicht lesen). Ein Anhang darf 128 MB nicht überschreiten.
- Ein Zip ist am einfachsten. Bei mehreren zieht das Panel das nach dem Repository benannte vor, sonst das größte.
- Das Zip darf **erst nach dem Release hinzugefügt werden**: Beim Scannen wird das Live-Release neu gelesen — neuer Tag oder erneutes Einreichen ist nicht nötig.

## 3. Den Code als sc.info speichern

1. Im Spiel → ☼Skin-Workshop → Quelle auf **GitHub** → Filter **Nicht erkannt** → dein Repository suchen.
2. Klicke auf **Scannen** (nur in diesem Moment wird dein Zip heruntergeladen, danach sofort gelöscht). Das Fenster listet einen oder mehrere **Codes**, jeder mit Kopier-Schaltfläche.
3. Erstelle im **Repository-Stamm** eine Datei namens `sc.info`, füge die Codes ein und commite.
4. Zurück im Spiel auf „Aktualisieren“ klicken: Dein Repository wird aus „Nicht erkannt“ zu einem installierbaren Skin (die Karte heißt nach dem **Repository**).

### Mehrere Codes einfügen

**Ein Code pro Zeile, von oben nach unten.** Das ist alles:

```
SCM3 6714 3f2a… (Illustration; der echte Code ist eine lange Zeile) 1/2 eJw…Cd34
SCM3 6714 3f2a… (Illustration; der echte Code ist eine lange Zeile) 2/2 eJw…Cd34
```

Nur vier harte Regeln:

- **Nie einen Code über Zeilen brechen.** Ein Code muss vollständig in einer Zeile bleiben. Dass dein Editor lange Zeilen umbricht, ist egal; Enter innerhalb eines Codes zerstört ihn.
- **Jeden Code einfügen.** So viele, wie das Fenster angezeigt hat. Fehlt einer, wird das ganze Paket unlesbar — das Repository bleibt „Nicht erkannt“.
- **Code nicht bearbeiten.** Jeder trägt eine Prüfsumme; ein einziges geändertes Zeichen (sogar ein Leerzeichen) bricht ihn.
- **Code nicht in Anführungszeichen.** `"SCM3 …"` wird als zitiert Text behandelt und ignoriert.

Alles andere ist locker: Reihenfolge egal, Leerzeilen egal, Überschriften und Text um die Codes sind fein, ein Markdown-Codeblock darum ist fein, zwei Codes durch ein Leerzeichen getrennt in einer Zeile gehen auch. Halte die Datei unter 64 KB.

## Was Spieler sehen

Die Typ-/Ziel-Labels und der Hinweis „Neustart erforderlich“ stammen aus dem Code — sie werden beim Scannen erkannt, du musst sie nie eintragen. Ein frisch installiertes Paket wird nicht vom laufenden Spiel geladen, daher bittet das Panel wie Steam um einen Neustart; nach dem Neustart greift es.

## Häufige Fehler

- **Repository umbenennen oder Konto wechseln**: Das Paar `Besitzer/Repository` ändert sich, alte Codes funktionieren nicht mehr — erneut scannen und die neuen committen.
- **Nur lose dll-/pck-Dateien, kein Zip**: Dein Repository wird gelistet, bleibt aber für immer „Nicht erkannt“, ohne Installieren-Schaltfläche.
- **Code eines anderen Repositories eingefügt**: ebenfalls „Nicht erkannt“ — ein Code ist an sein eigenes Repository gebunden.
- **Skin aktualisieren**: einfach ein neues Release veröffentlichen. Erneut scannen und `sc.info` aktualisieren musst du nur, wenn sich die **ersetzten Ziele geändert haben** oder du ein **Skript / DLL hinzugefügt hast (was die Neustart-Anforderung ändert)**; der Austausch von Bildern braucht das nicht.

Unterstützte Ziele: Charakter, Karten, Monster, Uralte, Händler, Begleiter, Ereignisse.
