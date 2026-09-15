# Publiez votre apparence sur GitHub (guide auteur)

[简体中文](投稿GitHub皮肤Mod说明.md) · [English](publish-github-skin-mod.md)

Avec « Skin Changer » installé, les joueurs voient votre dépôt en jeu sous **☼Atelier d’apparences → Source : GitHub** et l’installent en un clic.
Vous n’avez que trois choses à faire : ajouter un sujet, publier un zip, coller un code.

(Pour scanner votre propre paquet, il faut d’abord Skin Changer — cherchez-le dans l’Atelier Steam.)

## 1. Ajoutez le sujet

Sur la page de votre dépôt : l’engrenage à côté de About → Topics :

```
sts2-sc-mod
```

Sujet absent ou mal orthographié : le jeu ne trouve jamais votre dépôt ; les forks sont aussi ignorés.

## 2. Publiez une Release avec le zip

- Le zip contient le paquet d’apparence que le jeu charge (`<id>.json` + `.pck` / `.dll` ; un paquet d’illustrations de cartes seul convient aussi).
- Placez ses fichiers soit à la **racine** du zip, soit dans des dossiers (toute profondeur marche ; un dossier portant le nom du dépôt n’est préféré que si le zip contient plusieurs mods).
- Seuls les `.zip` sont lus : `.rar`, `.7z` et `.tar.gz` comptent comme aucun fichier joint, et le dépôt reste « Non reconnu ».
- Attachez-le à votre **dernière** Release et **ne** la marquez **pas** pre-release (le panneau ne lit pas celles-ci). Une pièce jointe doit rester sous 128 Mo.
- Un seul zip, c’est le plus simple. Avec plusieurs, le panneau préfère celui nommé d’après le dépôt, sinon le plus gros.
- Le zip peut être **ajouté après la publication de la Release** : le Scan relit la Release en direct, pas besoin d’un nouveau tag ni d’une nouvelle soumission.

## 3. Enregistrez le code sous sc.info

1. En jeu → ☼Atelier d’apparences → Source **GitHub** → filtre **Non reconnu** → trouvez votre dépôt.
2. Cliquez sur **Analyser** (c’est le seul moment où votre zip est téléchargé, et il est supprimé juste après). La fenêtre liste un ou plusieurs **codes**, chacun avec un bouton Copier.
3. À la **racine du dépôt**, créez un fichier nommé `sc.info`, collez-y les codes et commettez.
4. De retour en jeu, cliquez sur Actualiser : votre dépôt passe de « Non reconnu » à apparence installable (la carte porte le nom du **dépôt**).

### Coller plusieurs codes

**Un code par ligne, de haut en bas.** C’est tout :

```
SCM3 6714 3f2a… (illustration ; le vrai code est une longue ligne unique) 1/2 eJw…Cd34
SCM3 6714 3f2a… (illustration ; le vrai code est une longue ligne unique) 2/2 eJw…Cd34
```

Quatre règles strictes seulement :

- **Ne coupez jamais un code sur plusieurs lignes.** Un code doit rester entier sur une seule ligne. Le retour à la ligne automatique de votre éditeur ne pose aucun problème ; appuyer sur Entrée au milieu d’un code, si.
- **Collez tous les codes.** Autant que la fenêtre en a montré. S’il en manque un, tout le paquet devient illisible — le dépôt reste « Non reconnu ».
- **Ne modifiez pas un code.** Chacun contient une somme de contrôle ; changer un seul caractère (même ajouter une espace) le casse.
- **Ne mettez pas un code entre guillemets.** `"SCM3 …"` est traité comme du texte cité et ignoré.

Tout le reste est souple : l’ordre n’importe pas, les lignes vides n’importent pas, titres et prose autour des codes sont acceptés, une barre de code Markdown autour est acceptée, et deux codes séparés par une espace sur une ligne passent aussi. Gardez le fichier sous 64 Ko.

## Ce que voient les joueurs

Les étiquettes de type/cible et l’indication « redémarrage requis » viennent du code — elles sont détectées pendant le scan, vous ne les remplissez jamais. Un paquet fraîchement installé n’est pas chargé par le jeu en cours, donc le panneau propose un redémarrage, exactement comme Steam ; il prend effet après le redémarrage.

## Erreurs fréquentes

- **Renommer le dépôt ou changer de compte** : le couple `propriétaire/dépôt` change, les anciens codes cessent de fonctionner — rescannez et commettez les nouveaux.
- **Publier seulement des dll / pck en vrac, sans zip** : votre dépôt est listé mais reste « Non reconnu », sans bouton d’installation.
- **Coller un code d’un autre dépôt** : « Non reconnu » aussi — un code est lié à son propre dépôt.
- **Mettre à jour l’apparence** : publiez simplement une nouvelle Release. Vous ne devez rescanner et mettre à jour `sc.info` que si les **cibles remplacées ont changé** ou si vous avez **ajouté un script / DLL (ce qui change l’exigence de redémarrage)** ; remplacer des images ne le demande pas.

Cibles prises en charge : personnage, cartes, monstre, Ancien, marchand, compagnon, événement.
