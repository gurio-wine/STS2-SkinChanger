# Opublikuj swoją skórkę na GitHubie (poradnik dla autorów)

[简体中文](投稿GitHub皮肤Mod说明.md) · [English](publish-github-skin-mod.md)

Gracze z zainstalowanym „Skin Changerem" zobaczą Twoje repozytorium w grze: **☼Warsztat skórek → Źródło: GitHub** — i zainstalują je jednym kliknięciem.
Do zrobienia są tylko trzy rzeczy: dodaj jeden temat, opublikuj jedno zip, wklej jeden kod.

(Aby zeskanować własny pakiet, najpierw zainstaluj Skin Changera — poszukaj go w Warsztacie Steam.)

## 1. Dodaj temat

Na stronie repozytorium: koło zębate obok About → Topics:

```
sts2-sc-mod
```

Bez tematu albo z literówką gra nigdy nie znajdzie Twojego repozytorium; forki też są pomijane.

## 2. Opublikuj Release z zipem

- W zipie znajduje się pakiet skórki, który wczytuje gra (`<id>.json` + `.pck` / `.dll`; pakiet samych grafik kart też działa).
- Pliki włożysz albo do **głównego katalogu** zipa, albo do folderów (dowolna głębokość; folder o nazwie repozytorium jest preferowany tylko wtedy, gdy zip zawiera kilka modów).
- Czytane są tylko `.zip`: `.rar`, `.7z` i `.tar.gz` traktowane są jak brak załącznika — repozytorium zostaje „Nierozpoznane".
- Dołącz go do **najnowszego** Release i **nie** zaznaczaj pre-release (panel ich nie odczyta). Jeden załącznik musi mieścić się w 128 MB.
- Jeden zip to najprościej. Przy kilku panel preferuje ten nazwany jak repozytorium, w przeciwnym razie największy.
- Zip można **dodać już po opublikowaniu Release**: przy „Skanuj" panel ponownie czyta aktualne Release — nowy tag ani ponowne przesyłanie nie są potrzebne.

## 3. Zapisz kod jako sc.info

1. W grze → ☼Warsztat skórek → Źródło **GitHub** → filtr **Nierozpoznane** → znajdź swoje repozytorium.
2. Kliknij **Skanuj** (tylko wtedy twój zip jest pobierany, a zaraz potem usuwany). Okno wypisze jeden lub więcej **kodów**, każdy z przyciskiem kopiowania.
3. W **głównym katalogu repozytorium** utwórz plik o nazwie `sc.info`, wklej kody i zrób commit.
4. Wróć do gry i kliknij Odśwież: repozytorium zmieni się z „Nierozpoznane" w instalowalną skórkę (karta nazywa się po **repozytorium**).

### Wklejanie kilku kodów

**Jeden kod na wiersz, od góry do dołu.** To wszystko:

```
SCM3 6714 3f2a… (przykład; prawdziwy kod to jeden długi wiersz) 1/2 eJw…Cd34
SCM3 6714 3f2a… (przykład; prawdziwy kod to jeden długi wiersz) 2/2 eJw…Cd34
```

Tylko cztery twarde zasady:

- **Nigdy nie łam kodu między wierszami.** Kod musi w całości pozostać w jednym wierszu. Zawijanie długiego wiersza przez edytor niczemu nie szkodzi; Enter w środku kodu — tak.
- **Wklej wszystkie kody.** Tyle, ile pokazało okno. Gdy jednego zabraknie, cały pakiet nie da się odczytać — repozytorium zostaje „Nierozpoznane".
- **Nie edytuj kodu.** Każdy ma sumę kontrolną; zmiana jednego znaku (nawet dodanie spacji) go psuje.
- **Nie bierz kodu w cudzysłów.** `"SCM3 …"` jest traktowane jako tekst cytowany i ignorowane.

Cała reszta jest luźna: kolejność bez znaczenia, puste wiersze bez znaczenia, nagłówki i tekst wokół kodów są w porządku, ogrodzenie kodem Markdown jest w porządku, a dwa kody rozdzielone spacją w jednym wierszu też działają. Trzymaj plik poniżej 64 KB.

## Co widzą gracze

Etykiety typu/celu i podpowiedź „wymagany restart" pochodzą z kodu — są wykrywane przy skanowaniu, więc nigdy nie wypełniasz ich ręcznie. Świeżo zainstalowany pakiet nie jest wczytywany przez działającą grę, dlatego panel jak Steam proponuje restart; efekt następuje po restarcie.

## Częste błędy

- **Zmiana nazwy repozytorium albo konta**: para `właściciel/repozytorium` się zmienia, stare kody przestają działać — zeskanuj ponownie i zcommituj nowe.
- **Publikacja samych luźnych plików dll / pck bez zipa**: repozytorium jest na liście, ale na zawsze „Nierozpoznane", bez przycisku instalacji.
- **Wklejenie kodu z innego repozytorium**: również „Nierozpoznane" — kod jest związany ze swoim repozytorium.
- **Aktualizacja skórki**: po prostu opublikuj nowe Release. Ponowne skanowanie i aktualizacja `sc.info` są potrzebne tylko wtedy, gdy **zmieniły się zastępowane cele** albo gdy **dodałeś skrypt / DLL (co zmienia wymóg restartu)**; podmiana obrazków tego nie wymaga.

Obsługiwane cele: postać, karty, potwór, Pradawny, kupiec, towarzysz, wydarzenie.
