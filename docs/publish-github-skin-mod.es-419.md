# Publicá tu mod de aspectos en GitHub (guía para autores)

[简体中文](投稿GitHub皮肤Mod说明.md) · [English](publish-github-skin-mod.md)

Con «Skin Changer» instalado, los jugadores ven tu repositorio en el juego en **☼Taller de aspectos → Origen: GitHub** y lo instalan con un clic.
Solo tenés que hacer tres cosas: agregar un tema, publicar un zip y pegar un código.

(Para escanear tu propio paquete necesitás antes Skin Changer — buscalo en el Taller de Steam.)

## 1. Agregá el tema

En la página de tu repositorio: engranaje al lado de About → Topics:

```
sts2-sc-mod
```

Si falta el tema o está mal escrito, el juego nunca encuentra tu repositorio; los forks tampoco aparecen.

## 2. Publicá una Release con el zip

- El zip es el paquete de aspectos que carga el juego (`<id>.json` + `.pck` / `.dll`; un paquete solo con arte de cartas también sirve).
- Poné sus archivos en la **raíz** del zip, o dentro de carpetas (cualquier profundidad sirve; una carpeta con el nombre del repositorio solo se prefiere cuando el zip contiene varios mods).
- Solo se leen los `.zip`: `.rar`, `.7z` y `.tar.gz` cuentan como si no hubiera adjunto, y el repositorio queda «No reconocido».
- Adjuntalo a tu Release **más reciente** y **no** lo marques como pre-release (el panel no puede leerlos). Un adjunto tiene que quedar por debajo de 128 MB.
- Un solo zip es lo más simple. Con varios, el panel prefiere el que lleva el nombre del repositorio; si no, el más grande.
- El zip puede **agregarse después de publicar la Release**: al Escanear se relee la Release en vivo, así que no hace falta una etiqueta nueva ni volver a mandarlo.

## 3. Guardá el código como sc.info

1. En el juego → ☼Taller de aspectos → Origen **GitHub** → filtro **No reconocido** → buscá tu repositorio.
2. Pulsá **Analizar** (este es el único momento en que se descarga tu zip, y se borra justo después). La ventana lista uno o más **códigos**, cada uno con su botón de copiar.
3. En la **raíz del repositorio**, creá un archivo llamado `sc.info`, pegá los códigos y hacé commit.
4. Volvé al juego y pulsá Actualizar: tu repositorio pasa de «No reconocido» a un aspecto instalable (la tarjeta se llama como el **repositorio**).

### Cómo pegar varios códigos

**Un código por línea, de arriba hacia abajo.** Eso es todo:

```
SCM3 6714 3f2a… (ilustración; el código real es una sola línea larga) 1/2 eJw…Cd34
SCM3 6714 3f2a… (ilustración; el código real es una sola línea larga) 2/2 eJw…Cd34
```

Solo hay cuatro reglas estrictas:

- **Nunca partas un código en varias líneas.** Un código tiene que quedarse completo en una sola línea. Que tu editor ajuste la visualización de una línea larga no importa; pulsar Enter dentro de un código sí lo rompe.
- **Pegá todos los códigos.** Tantos como mostrara la ventana. Si falta uno, todo el paquete queda ilegible — el repositorio sigue «No reconocido».
- **No edites un código.** Cada uno lleva una suma de verificación; cambiar un solo carácter (incluso agregar un espacio) lo rompe.
- **No pongas un código entre comillas.** `"SCM3 …"` se trata como texto citado y se ignora.

Todo lo demás es flexible: el orden no importa, las líneas en blanco no importan, poner títulos y texto alrededor de los códigos está bien, rodearlos con un bloque de código Markdown está bien, y dos códigos separados por un espacio en la misma línea también funcionan. Mantené el archivo por debajo de 64 KB.

## Qué ven los jugadores

Las etiquetas de tipo/objetivo y el aviso de «requiere reinicio» salen del código — se detectan al escanear, así que nunca los completás a mano. Un paquete recién instalado no lo carga el juego en ejecución, por eso el panel pide un reinicio, igual que Steam; surte efecto después de reiniciar.

## Errores comunes

- **Renombrar el repositorio o cambiar de cuenta**: el par `propietario/repositorio` cambia y los códigos antiguos dejan de funcionar — volvé a escanear y hacé commit de los nuevos.
- **Publicar solo dll / pck sueltos, sin zip**: tu repositorio aparece en la lista, pero queda para siempre «No reconocido», sin botón de instalación.
- **Pegar un código de otro repositorio**: también «No reconocido» — un código está ligado a su propio repositorio.
- **Actualizar el aspecto**: basta con publicar una Release nueva. Solo tenés que reescanear y actualizar `sc.info` cuando **cambien los objetivos sustituidos** o cuando **agregues un script / DLL (lo que cambia el requisito de reinicio)**; cambiar imágenes no lo necesita.

Objetivos compatibles: personaje, cartas, monstruo, Antiguo, mercader, compañero, evento.
