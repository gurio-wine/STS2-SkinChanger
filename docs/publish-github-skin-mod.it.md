# Pubblica il tuo aspetto su GitHub (guida per autori)

[简体中文](投稿GitHub皮肤Mod说明.md) · [English](publish-github-skin-mod.md)

Con « Skin Changer » installato, i giocatori vedono il tuo repository in gioco sotto **☼Bottega degli aspetti → Origine: GitHub** e lo installano con un clic.
Devi fare solo tre cose: aggiungere un topic, pubblicare uno zip, incollare un codice.

(Per scansionare il tuo pacchetto serve prima Skin Changer — cercalo nel Workshop di Steam.)

## 1. Aggiungi il topic

Nella pagina del repository: ingranaggio accanto ad About → Topics:

```
sts2-sc-mod
```

Topic mancante o scritto male e il gioco non trova mai il tuo repository; anche i fork vengono saltati.

## 2. Pubblica una Release con lo zip

- Lo zip è il pacchetto di aspetto che il gioco carica (`<id>.json` + `.pck` / `.dll`; va bene anche un pacchetto di sole grafiche delle carte).
- Metti i file nella **radice** dello zip, oppure dentro cartelle (qualsiasi profondità va bene; una cartella con il nome del repository è preferita solo quando lo zip contiene più Mod).
- Vengono letti solo gli `.zip`: `.rar`, `.7z` e `.tar.gz` valgono come nessun allegato, e il repository resta «Non riconosciuto».
- Allegalo alla Release **più recente** e **non** spuntare pre-release (il pannello non riesce a leggerle). Un allegato deve restare sotto 128 MB.
- Un solo zip è la cosa più semplice. Con più di uno, il pannello preferisce quello che porta il nome del repository, altrimenti il più grande.
- Lo zip può essere **aggiunto dopo la pubblicazione della Release**: la Scansione rilegge la Release in tempo reale, quindi non servono nuovi tag né nuove invii.

## 3. Salva il codice come sc.info

1. Nel gioco → ☼Bottega degli aspetti → Origine **GitHub** → filtro **Non riconosciuto** → trova il tuo repository.
2. Clicca **Analizza** (solo in questo momento il tuo zip viene scaricato, e viene eliminato subito dopo). La finestra elenca uno o più **codici**, ognuno con un pulsante Copia.
3. Nella **radice del repository** crea un file chiamato `sc.info`, incolla i codici ed esegui il commit.
4. Torna nel gioco e clicca Aggiorna: il tuo repository passa da «Non riconosciuto» ad aspetto installabile (la scheda prende il nome dal **repository**).

### Incollare più codici

**Un codice per riga, dall'alto verso il basso.** Tutto qui:

```
SCM3 6714 3f2a… (illustrazione; il codice reale è una riga unica lunga) 1/2 eJw…Cd34
SCM3 6714 3f2a… (illustrazione; il codice reale è una riga unica lunga) 2/2 eJw…Cd34
```

Solo quattro regole fisse:

- **Mai spezzare un codice su più righe.** Un codice deve restare completo su una sola riga. Se l'editor va a capo mostrando una riga lunga non succede nulla; premere Invio dentro un codice lo rovina.
- **Incolla tutti i codici.** Quanti ne ha mostrati la finestra. Se ne manca uno, l'intero pacchetto diventa illeggibile — il repository resta «Non riconosciuto».
- **Non modificare un codice.** Ognuno porta un checksum; cambiare un solo carattere (anche aggiungere uno spazio) lo invalida.
- **Non mettere un codice tra virgolette.** `"SCM3 …"` viene trattato come testo citato e ignorato.

Tutto il resto è tollerante: l'ordine non conta, le righe vuote non contano, titoli e testo intorno ai codici vanno bene, recintarli in un blocco di codice Markdown va bene, e due codici separati da uno spazio sulla stessa riga funzionano. Mantieni il file sotto 64 KB.

## Cosa vedono i giocatori

Le etichette di tipo/bersaglio e l'avviso «riavvio richiesto» arrivano dal codice — vengono rilevate durante la scansione, non devi inserirle tu. Un pacchetto appena installato non viene caricato dal gioco in esecuzione, quindi il pannello chiede un riavvio, esattamente come Steam; ha effetto dopo il riavvio.

## Errori comuni

- **Rinominare il repository o cambiare account**: la coppia `proprietario/repository` cambia, i vecchi codici smettono di funzionare — scansiona di nuovo ed esegui il commit dei nuovi.
- **Pubblicare solo file dll / pck sparsi, senza zip**: il repository viene elencato ma resta per sempre «Non riconosciuto», senza pulsante di installazione.
- **Incollare un codice di un altro repository**: anch'esso «Non riconosciuto» — un codice è vincolato al proprio repository.
- **Aggiornare l'aspetto**: pubblica semplicemente una nuova Release. Devi scansionare di nuovo e aggiornare `sc.info` solo quando i **bersagli sostituiti sono cambiati** o quando hai **aggiunto uno script / DLL (cosa che cambia il requisito di riavvio)**; scambiare immagini non serve.

Bersagli supportati: personaggio, carte, mostro, Antico, mercante, compagno, evento.
