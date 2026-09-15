# Publique seu visual no GitHub (guia para autores)

[简体中文](投稿GitHub皮肤Mod说明.md) · [English](publish-github-skin-mod.md)

Com o « Skin Changer » instalado, os jogadores veem seu repositório no jogo em **☼Oficina de visuais → Origem: GitHub** e o instalam com um clique.
Você só precisa fazer três coisas: adicionar um tópico, publicar um zip, colar um código.

(Para examinar o seu próprio pacote, primeiro instale o Skin Changer — procure-o na Oficina do Steam.)

## 1. Adicione o tópico

Na página do seu repositório: engrenagem ao lado de About → Topics:

```
sts2-sc-mod
```

Sem o tópico ou com erro de digitação, o jogo nunca encontra seu repositório; forks também são ignorados.

## 2. Publique uma Release com o zip

- O zip é o pacote de visual que o jogo carrega (`<id>.json` + `.pck` / `.dll`; um pacote só com artes de cartas também serve).
- Coloque os arquivos na **raiz** do zip, ou dentro de pastas (qualquer profundidade serve; uma pasta com o nome do repositório só é preferida quando o zip contém vários mods).
- Só `.zip` é lido: `.rar`, `.7z` e `.tar.gz` contam como nenhum anexo, e o repositório fica «Não reconhecido».
- Anexe-o à sua Release **mais recente** e **não** marque como pre-release (o painel não consegue ler essas). Um anexo deve ficar abaixo de 128 MB.
- Um único zip é o mais simples. Com vários, o painel prefere o que tem o nome do repositório; caso contrário, o maior.
- O zip pode ser **adicionado depois de a Release ter sido publicada**: o Examinar relê a Release ao vivo, então não é preciso criar nova tag nem reenviar.

## 3. Salve o código como sc.info

1. No jogo → ☼Oficina de visuais → Origem **GitHub** → filtro **Não reconhecido** → encontre seu repositório.
2. Clique em **Analisar** (este é o único momento em que seu zip é baixado, e ele é apagado logo em seguida). A janela lista um ou mais **códigos**, cada um com um botão Copiar.
3. Na **raiz do repositório**, crie um arquivo chamado `sc.info`, cole os códigos e faça o commit.
4. Volte ao jogo e clique em Atualizar: seu repositório passa de «Não reconhecido» a um visual instalável (o cartão recebe o nome do **repositório**).

### Colando vários códigos

**Um código por linha, de cima para baixo.** É só isso:

```
SCM3 6714 3f2a… (ilustração; o código real é uma linha longa única) 1/2 eJw…Cd34
SCM3 6714 3f2a… (ilustração; o código real é uma linha longa única) 2/2 eJw…Cd34
```

Só quatro regras duras:

- **Nunca quebre um código entre linhas.** Um código precisa ficar inteiro numa única linha. O editor quebrar a exibição de uma linha longa não tem problema; apertar Enter dentro de um código, sim.
- **Cole todos os códigos.** Quantos a janela mostrar. Se faltar um, o pacote inteiro fica ilegível — o repositório continua «Não reconhecido».
- **Não edite um código.** Cada um carrega uma soma de verificação; mudar um único caractere (até adicionar um espaço) o quebra.
- **Não coloque um código entre aspas.** `"SCM3 …"` é tratado como texto citado e ignorado.

Todo o resto é flexível: a ordem não importa, linhas em branco não importam, títulos e texto ao redor dos códigos tudo bem, cercá-los num bloco de código Markdown tudo bem, e dois códigos separados por espaço na mesma linha também funcionam. Mantenha o arquivo abaixo de 64 KB.

## O que os jogadores veem

Os rótulos de tipo/alvo e a dica de «reinício necessário» vêm do código — são detectados durante a análise, então você nunca os preenche. Um pacote recém-instalado não é carregado pelo jogo em execução, por isso o painel pede um reinício, exatamente como o Steam; ele passa a valer depois do reinício.

## Erros comuns

- **Renomear o repositório ou trocar de conta**: o par `dono/repositório` muda, os códigos antigos param de funcionar — analise de novo e faça o commit dos novos.
- **Publicar apenas dll / pck soltos, sem zip**: seu repositório aparece na lista, mas fica para sempre «Não reconhecido», sem botão de instalação.
- **Colar um código de outro repositório**: também «Não reconhecido» — um código é vinculado ao próprio repositório.
- **Atualizar o visual**: basta publicar uma nova Release. Só é preciso reanalisar e atualizar o `sc.info` quando os **alvos substituídos mudaram** ou quando você **adicionou um script / DLL (o que muda o requisito de reinício)**; trocar imagens não precisa.

Alvos suportados: personagem, cartas, monstro, Ancião, mercador, companheiro, evento.
