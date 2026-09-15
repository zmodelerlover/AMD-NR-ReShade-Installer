# Handoff — v0.5.1: os cinco defeitos, o instalador novo, e o que falta pra lançar

15/09/2026. Duas branches `v0.5.1`, uma em cada repositório, **nada empurrado**.

| | |
|---|---|
| Add-on | `D:\dlss5-hotkey` (worktree), branch `v0.5.1`, base `origin/master` (`425ed4d`) — 3 commits, 8 arquivos, +503/−64 |
| Instalador | `D:\AMD-NR-ReShade-Installer`, branch `v0.5.1`, base `master` (`46097a9`) — 10 commits, 39 arquivos, +4250/−171 |
| Beta pro tester | `Desktop\AMD-NR-ReShade-Installer-beta-20260915.zip`, 41,4 MB, sha256 `9c6b9b9e4c83ab1e…` |

---

## 0. Em uma linha

O rebind do atalho não funcionava por **três** causas somadas, não uma; o Oblivion não rodava por
uma quarta, que não tem nada a ver com atalho; e o instalador ganhou menu de versão lido do GitHub,
pré-download, créditos e um conserto de layout que estava escondendo informação em três telas.
**Nada disso foi testado em jogo ainda.**

---

# PARTE I — O QUE FOI FEITO

## 1. Add-on (`D:\dlss5-hotkey`, branch `v0.5.1`)

```
497cbc3 Fix the toggle hotkey rebind, and the ini a byte-order mark was hiding
c134a9e Carry an X8R8G8B8 back buffer as its A8 twin, which has a typed UAV
b6e0e4d Record the hotkey, ini and back-buffer format findings in the handoff
```

### 1.1 Causa nº 3 do rebind — o ReShade zera o `GetAsyncKeyState`

A que faltava, e a que fazia o log terminar em `rebind armed` e mais nada, para sempre.
`reshade/source/input_windows.cpp`:

```cpp
extern "C" auto WINAPI HookGetAsyncKeyState(int vKey) -> SHORT
{
    if ((vKey & 0xF8) != 0)                       // toda tecla de 8 pra cima
        if (reshade::input::is_blocking_any_keyboard_input())
            return 0;
    ...
}
```

O ReShade engancha `GetAsyncKeyState`, `GetKeyState` e `GetKeyboardState` e responde **0 para todas
as teclas** enquanto o overlay bloqueia o teclado — que é exatamente o tempo em que o painel está
aberto. A varredura lia um teclado vazio: não achava tecla, não desistia (o `overlayAt` era
recarimbado a cada quadro do painel aberto) e não desarmava.

Também explica por que uma vez ela pegou `VK_HOME`: foi no primeiro quadro depois de abrir o
overlay, antes do bloqueio valer. A correção da causa nº 2 (espera por soltura) fechou essa janela
e o defeito virou 100% reprodutível.

**Correção:** a captura lê `effect_runtime::is_key_down()`, o estado de tecla do próprio ReShade,
que vem das mensagens de janela e não passa pelo gancho — é o que o widget de atalho dele usa. O
ponteiro do runtime é carimbado em `OnOverlay32` (único lugar onde o ReShade entrega um) e zerado
em `OnDestroy`.

### 1.2 Causa nº 4 — a tecla capturada nunca chegava na cópia sincronizada

Escrevia em `g.toggleKey` e só. O painel lê `controls.shadow`, então **o rótulo do botão não
mudava**; `SyncControls` só empurra quando `shadow.settings_revision` sobe, então o host nunca
sabia; o Save é do host, então nada ia pro ini; e o próximo `GetState`/`SetState` chamava
`OperationalSettings()`, que copia o shadow por cima de `g` — desfazendo a tecla nova.

Do ponto de vista de quem usa: "a bind não muda", mesmo nas vezes em que a tecla foi lida.

**Correção:** escreve em `controls.shadow.toggleKey/toggleMods`, sobe `settings_revision` e chama
`OperationalSettings()`.

### 1.3 O `ToggleKey=35` com o ini dizendo 120 — BOM de UTF-8

`bin\dlss5-neural.ini` do Half-Life 2 começa com **EF BB BF**. `GetPrivateProfileInt` lê o arquivo
como bytes, a primeira linha vira `<BOM>[dlss5]`, que não casa com seção nenhuma: **o arquivo
inteiro fica invisível e todo valor cai no default** (35 = `VK_END`). Sem aviso, sem log, e o
arquivo parece certo em qualquer editor. Quem põe é `Set-Content -Encoding utf8` do PowerShell 5.1
— foi uma edição manual durante a investigação anterior. O instalador escreve sem BOM.

**Correção:** `src/neural/ini_text.h`, chamado antes de ler o ini nos dois lados. Tira o BOM no
lugar, por temporário + `MoveFileEx`, e registra no log. Instalação que já está com BOM se conserta
sozinha ao abrir o jogo.

### 1.4 Causa nº 5 — `B8G8R8X8_UNORM` não tem UAV tipado (o Oblivion)

Do relatório `amd-nr-report-20260915-014136.zip`:

```
this GPU/driver has no typed UAV store for the back buffer format (DXGI 88, read as 88),
so there is no way to write the corrected image back. Stopping instead of drawing garbage.
```

Medido nesta máquina (mesma GPU do relatório, RX 9070 XT, driver 32.0.31041), com uma sonda D3D12
de `CheckFeatureSupport`:

| Formato | texture2d | uav | typed_load | typed_store |
|---|---|---|---|---|
| `B8G8R8A8_UNORM` (87) | 1 | 1 | 1 | 1 |
| `B8G8R8X8_UNORM` (88) | 1 | **0** | **0** | **0** |
| `R8G8B8A8_UNORM` (28) | 1 | 1 | 1 | 1 |

**Correção:** `D3D9CpuFormat()` passa a mapear `D3DFMT_X8R8G8B8` para `B8G8R8A8_UNORM` — os mesmos
quatro bytes por pixel com um canal que o jogo não lê. O par `X8B8G8R8` já era carregado como
`R8G8B8A8_UNORM` exatamente assim; o par BGRA é que era a exceção. Só a rota CPU muda: na rota
compartilhada o formato vem da textura do D3D9 e os dois lados do `CopyResource` continuam iguais.
Sem risco de alfa: `compose` escreve `float4(v, c.a)` e o destino é uma superfície X8.

**O Half-Life 2 ia bater nisto também** assim que a bind funcionasse: mesmo back buffer X8R8G8B8.

### 1.5 O que mudou no add-on

```
src/neural/hotkey_capture.h     novo   máquina de estados da captura, compartilhada pelas duas pontas
src/neural/ini_text.h           novo   remoção do BOM do ini
src/x86bridge/capture_test.cpp  novo   teste da captura, roda no build x86 e x64
src/x86bridge/frontend32.cpp           captura em OnPresent com is_key_down, shadow, formato
src/x86bridge/overlay32.inc            o botão só arma; carimba o runtime
src/neural/neural.cpp                  mesma captura no add-on 64-bit, e o BOM
build-x86bridge.ps1                    compila e roda o capture_test nas duas arquiteturas
```

O add-on 64-bit tinha as **mesmas** causas nº 2 e nº 3, expostas (lá não há bridge). Agora as duas
pontas usam o mesmo cabeçalho.

**Build:** `.\build-x86bridge.ps1` passa — x86 e x64, testes de protocolo, IPC e captura, PE,
imports e o addon64 integrado. O par novo já está instalado em
`D:\SteamLibrary\steamapps\common\Half-Life 2\bin\` (os antigos viraram `.bak`).

---

## 2. Instalador (`D:\AMD-NR-ReShade-Installer`, branch `v0.5.1`)

```
7f19788 Add the first-run setup, emulator routes, install logs and problem reports   <- sessão anterior
5e14701 Tell a remaster from the game it remade when reading the API database
667bb15 Offer a menu of add-on versions read from the GitHub releases
789d1fe Add a button that downloads every payload into the cache up front
04af8cc Never let a record for another build replace what the executable says
82792ab Show what is in the cache, what it would cost, and how far a download has got
02b8fcc Credit the network, name the repositories, and say that nobody should be paying for this
c0d7555 Give the credits their own panel: the logos are the links, and each one says what it is
67a360e Let the setup window resize, keep both windows inside the screen, and lead with the Discord
c20f311 Fix the page gutter that made wrapping content overflow and cut the scroll short
```

O primeiro commit é o que já estava na árvore de trabalho da sessão anterior; separei por hunk pra
não misturar, mas ele entra na mesma branch e no mesmo review.

### 2.1 Menu de versão, lido do GitHub

`src/AmdNr.Core/Releases.cs` (novo). Uma chamada à API de releases de `zmodelerlover/dlss5-neural-amd`
por abertura, cacheada em `%AppData%\AmdNrInstaller\cache\releases.json` (offline usa a última
lista). O caminho de download continua sem tocar a API: os arquivos vêm de endereço de asset.

Uma versão só é oferecida quando publica **o que a pina**: `SHA256SUMS.txt` mais os arquivos que a
rota instala. Tamanho vem do registro do asset na API, hash do `SHA256SUMS.txt` — então versão
descoberta hoje fica pinada tão apertado quanto uma escrita no `payload.json` meses atrás.

Conferido ao vivo contra o GitHub: lista a v0.5.0, e o hash que ele tira do `SHA256SUMS.txt`
(`203f0278b64c…`) bate byte a byte com o que o `payload.json` já pinava.

**A v0.5.0 publicou o par 32-bit só dentro do zip**, então pra rota x86 ela vem do manifesto. Da
v0.5.1 em diante, subindo os quatro soltos, o menu cresce sozinho (ver §5.4).

### 2.2 Oblivion — dois defeitos, não um

O relatório do tester dizia:

```
apis          DX12
source        PCGamingWiki
why           PCGamingWiki (The Elder Scrolls IV: Oblivion Remastered): DX12.
platform      Gog
```

`NormaliseTitle` tirava "Remastered", então "Oblivion" e "Oblivion Remastered" viravam a mesma
chave e o índice por título ficava com o primeiro que aparecesse — D3D12, 64-bit. Cópia GOG não tem
appid da Steam, então caía no título.

Varri o `api-db.json` que a gente distribui atrás de títulos que colidem com registros
**discordantes**:

```
antes:  9 títulos ambíguos
  Age of Empires III | : Definitive Edition          Mafia | Mafia: Definitive Edition
  BioShock | BioShock Remastered                     Mafia II | Mafia II: Definitive Edition
  BioShock 2 | BioShock 2 Remastered                 Mass Effect | Mass Effect Legendary Edition
  Oblivion | Oblivion Remastered                     Sleeping Dogs | : Definitive Edition
  Skyrim | Skyrim Special Edition

depois: 6 (os três "Remastered" saíram da colisão)
```

Não era um jogo, eram nove — o Oblivion foi só o que alguém instalou. Três camadas agora:

1. remaster **não** é edição (saiu da regex de qualificadores);
2. título que dois registros discordantes dividem **não responde nada** — cai na leitura do exe, que
   foi medida no disco (os 6 que sobraram caem aqui);
3. mesmo com registro na mão, se ele descreve uma arquitetura que o exe não é (x64-only contra um
   exe de 32 bits), ele **não substitui** a leitura local, e a linha "Detectado" diz por quê.

### 2.3 Pré-download e o cartão de arquivos

"Baixar tudo agora", na aba Esta máquina: confere o hash de tudo fora da thread da UI; se já está
tudo lá, **diz que já estava** em vez de parecer que não fez nada; se falta, baixa só o que falta.
Barra de progresso **do conjunto** (bytes baixados / total), não uma por arquivo indo de 0 a 100
seis vezes. Linha de resumo ("5 de 5 componentes prontos — 161 MB no cache") e o tamanho de cada
componente na linha dele.

A conferência do cache saiu da thread da UI: entrar nessa aba congelava a janela ~1s enquanto
hasheava os 141 MB de pesos.

### 2.4 Créditos, links e o aviso

`src/AmdNr.App/CreditsPanel.axaml` (novo controle, usado na **primeira tela** e em
**Configurações** — uma definição só):

1. **Discord** no topo de tudo, logo dele no azul #5865F2;
2. faixa de aviso: *"Isto é de graça. Se você pagou por isto, estão te enganando"* + *"É open source
   e vai continuar sendo: leia, modifique, compile a sua versão… a única coisa que a gente pede é
   que ninguém transforme numa versão paga"*;
3. **Créditos**: DLSS-NR-on-AMD do danielblnc (com o obrigado), depois o add-on (que é onde este
   instalador também mora);
4. **Apoiar · opcional, e não compra nada**: Ko-fi e Vakinha lado a lado.

Cada linha inteira é o link, então o logo é o que se clica. As seis URLs saem do `config.json`
(`Addon`, `App`, `DiscordUrl`, `RuntimeUrl`, `KofiUrl`, `VakinhaUrl`) — convite rotacionado ou fork
não exigem build novo.

> O texto do open source está escrito como **pedido do projeto**, não como cláusula: a MIT
> tecnicamente deixa vender. Se quiser que seja proibição de verdade, é trocar a licença dos dois
> repos — e a runtime do daniel tem termos próprios.

### 2.5 O conserto de layout (o que estava escondendo informação)

Montei um renderizador headless que abre a janela sem abrir janela e despeja a árvore visual com as
medidas. Ele mostrou:

```
StackPanel#PageLanguage   w=764      <- a página
  Border.card             w=764      <- o cartão de idioma, certo
  CreditsPanel            w=819      <- 55px mais largo que o pai
```

**`Padding` num `ScrollViewer` é descontado no arrange mas não no measure.** Conteúdo que quebra
linha era medido contra a largura cheia: saía largo demais *e*, tendo quebrado em menos linhas do
que precisa, **baixo demais** — a página reportava 688px quando precisava de 773. Daí os dois
sintomas juntos: texto vazando à direita e **o scroll parando antes do fim**.

Correção: a margem vai no conteúdo, não o padding no ScrollViewer — nas três páginas que rolam.
Mais: barra de rolagem sempre visível (`AllowAutoHide=False`), janela do assistente redimensionável
e 820×900, e `WindowFit.ToScreen` nas duas janelas, que mede a área útil **da tela em que a janela
abriu**, respeita o DPI dela, encolhe só se não couber e recentraliza.

Medido depois: 698px de conteúdo contra 740 de área útil — **cabe inteiro sem rolar** a 820×900; a
700×640 rola, e agora rola até o fim.

### 2.6 Ferramenta que ficou

`tools\uishot` — o renderizador headless, agora no repositório:

```powershell
dotnet run --project tools\uishot -- <pasta de saída>
```

Gera PNG das telas e um `.tree.txt` com posição e tamanho de cada controle. Foi a árvore, não o
print, que achou o bug do §2.5.

### 2.7 Testes

139 no instalador (`dotnet test tests\AmdNr.Core.Tests\AmdNr.Core.Tests.csproj`), incluindo 5 novos
de release/versão e 3 do caso Oblivion. No add-on, protocolo + IPC + captura nas duas arquiteturas.

---

# PARTE II — O QUE FALTA

## 3. Antes de lançar (bloqueia a v0.5.1)

| # | O quê | Por quê |
|---|---|---|
| 1 | **Testar o bind no Half-Life 2** | É a razão da sessão inteira e nada disso foi visto rodando. Roteiro no §4.1 |
| 2 | **Testar o Oblivion com o formato novo** | A correção do §1.4 é medida, não observada. É o tester do relatório que fecha isso |
| 3 | **Conferir o Save** | Nunca se viu `settings saved to dlss5-neural.ini` em sessão nenhuma. Pode ser um sexto defeito |
| 4 | **`/code-review ultra` nas duas branches** | Comando seu, cobrado. `master` como base no instalador, `origin/master` no add-on (§4.2) |
| 5 | **Publicar a release v0.5.1 com os 4 assets soltos** | Sem isso o menu de versão não cresce sozinho (§5.4) |
| 6 | **Decidir a fusão dos repositórios** | Os créditos já dizem que o instalador mora no `dlss5-neural-amd`. Se for isso, `config.App` (checagem de update) e o README precisam seguir |
| 7 | **Falar com o danielblnc** | Pendência da sessão anterior: é a runtime dele, distribuída pelo nosso Hugging Face, e o README do projeto dele ainda diz que esses arquivos "nunca estarão num repo" |

## 4. Receitas

### 4.1 O teste do bind (HL2, 10 minutos)

O par novo já está instalado. Abrir o jogo, abrir o overlay (Home), clicar no botão do atalho,
**soltar tudo**, apertar F9. Esperado em `bin\dlss5-neural-x86.log`:

```
x86bridge: removed a UTF-8 byte-order mark from dlss5-neural.ini; ...
x86bridge native x86 ... ToggleKey=120 ToggleMods=1      <- 120, não 35
x86bridge: rebind armed (overlay stamp NN ms old)
x86bridge: toggle bound to key NNN mods N
```

Depois fechar o overlay e apertar a combinação nova: `x86bridge enabled=1`, e agora **a imagem tem
de mudar** (era aqui que o formato do §1.4 parava tudo). Se voltar ao normal: `%LOCALAPPDATA%\Temp\
hl2-bin-backup` tem o par oficial da v0.5.0, e cada arquivo tem um `.bak` ao lado.

**Não testar com F10**: no Windows ela gera `WM_SYSKEYDOWN`. Use F9 (120) ou Insert (45).

### 4.2 O review

O `/ultrareview` olha o repositório da pasta onde a sessão abriu, e não acha base sozinho nestes
dois. Abrir uma sessão dentro de cada um:

```
cd /d D:\AMD-NR-ReShade-Installer && claude     ->  /code-review ultra master
cd /d D:\dlss5-hotkey && claude                 ->  /code-review ultra origin/master
```

No add-on tem de ser `origin/master`: o `master` **local** está em `0a1fd9a`, 48 commits atrás, e
está checado no outro worktree (`D:\dlss5`). Base errada = review de código velho.

### 4.3 Builds

```powershell
# add-on: x86 + x64, protocolo, IPC, captura, PE, imports, addon64
D:\dlss5-hotkey\build-x86bridge.ps1

# instalador
dotnet test tests\AmdNr.Core.Tests\AmdNr.Core.Tests.csproj
dotnet publish src\AmdNr.App\AmdNr.App.csproj -c Release -r win-x64 -o dist
Copy-Item payload\api-db.json dist\api-db.json -Force     # o publish não leva este

# ver a UI sem abrir janela
dotnet run --project tools\uishot -- <pasta>
```

O exe fica **travado enquanto o app estiver aberto** — o publish falha com
`UnauthorizedAccessException` e não é outra coisa.

### 4.4 Rodar o app do zero, sem as suas configurações

```powershell
$env:AMDNR_HOME = "$env:TEMP\amdnr-limpo"
& "D:\AMD-NR-ReShade-Installer\dist\AMD-NR-ReShade-Installer.exe"
```

Pasta vazia = assistente de primeira abertura, nenhum jogo, cache vazio. `%AppData%\AmdNrInstaller`
não é tocado.

## 5. Roadmap

### 5.1 Agora (v0.5.1)

Os sete itens do §3. O beta zip já está pronto pra circular enquanto isso.

### 5.2 Logo depois

- **Espera por soltura e `is_key_down` no add-on 64-bit — conferir em jogo.** O código está lá, mas
  só a rota 32-bit foi exercitada nesta sessão.
- **Rota compartilhada do D3D9.** O §1.4 conserta a rota CPU. Se um jogo D3D9Ex cair na rota
  compartilhada com back buffer X8, o host vai parar pelo mesmo motivo e o remédio é outro: forçar a
  rota CPU quando o formato compartilhado não tiver UAV tipado. Não medido, não implementado.
- **ReShade em rota Vulkan** continua manual: lá ele é camada global registrada em
  `HKCU\SOFTWARE\Khronos\Vulkan\ImplicitLayers` + `ReShadeApps.ini`, não um DLL na pasta.
- **Os outros 12 idiomas.** Existem inglês e pt-BR completos; o mecanismo já está pronto.
- **Cobertura do api-db**: rodar o gerador com `--steam-top 3000`. Os 6 títulos ambíguos que sobraram
  respondem por appid (Steam) e por exe (resto) — uma cópia GOG de Mafia hoje não recebe resposta do
  banco, e isso é de propósito.

### 5.3 Ideias, não compromissos

- `StartOn=1` opcional na interface — hoje o add-on começa desligado de propósito.
- O menu de versão guarda a escolha **por sessão do app**, não por jogo. Se isso incomodar, guardar
  no `games.json`.
- Um segundo zip "offline" com os payloads já no cache, pra tester com internet ruim
  (`tools\seed-cache.ps1` já faz a parte difícil).

### 5.4 A receita da release v0.5.1

O menu de versão só oferece uma release que publique **tudo solto, ao lado do zip**:

```powershell
gh release upload v0.5.1 `
  build\dlss5-neural.addon64 `
  build-x86bridge\dlss5-neural.addon32 `
  build-x86bridge\dlss5-neural-host64.exe `
  release\payload.sha256 `
  SHA256SUMS.txt
```

Sem `SHA256SUMS.txt` a versão não aparece — nada a pina, e o app não instala o que não pode
conferir. Está documentado em `payload/README.md`, seção "The version menu".

---

## 6. Armadilhas pagas nesta sessão

- **Nunca leia tecla com `GetAsyncKeyState` dentro de um add-on de ReShade** quando o overlay pode
  estar aberto. Use `effect_runtime::is_key_down`. Vale pra qualquer add-on.
- **BOM em ini apaga o arquivo inteiro** para a API de perfil do Windows. `Set-Content -Encoding
  utf8` e `Out-File -Encoding utf8` do PowerShell 5.1 põem BOM.
- **`B8G8R8X8_UNORM` não é formato de UAV.** Se algo precisa escrever no back buffer, o par X8 tem
  de virar A8.
- **`Padding` em `ScrollViewer` não vale no measure.** Conteúdo que quebra linha sai largo demais e
  baixo demais, e a rolagem para antes do fim. Ponha a margem no conteúdo.
- **O gerador de nomes do Avalonia cria um campo por `x:Name`**: um método com o mesmo nome de um
  controle é `error CS0102`. (`VersionNote` virou `VersionNoteText`.)
- **Projeto self-contained só pode ser referenciado por outro self-contained** — foi o que travou o
  `tools\uishot` até fixar `win-x64` nele.
- **Comentário XML não pode conter `--`.** O XAML recusa o arquivo inteiro.
- **`Start-Process -Environment` não existe no PowerShell 5.1.** Setar `$env:X` na mesma chamada
  funciona: o filho herda.
- **`vswhere` não achou o Visual Studio Insiders** desta máquina; o `build-x86bridge.ps1` tem
  fallback próprio, script novo precisa do mesmo.
- **`\x` em string Python não-raw** vira outro caractere (de novo).
- **Os arquivos dos dois repositórios são CRLF.** Patch com padrão em LF não casa e falha calado.

---

## 7. Onde está cada coisa

```
D:\dlss5-hotkey                      add-on, branch v0.5.1 (worktree; NÃO é o D:\dlss5)
D:\AMD-NR-ReShade-Installer          instalador, branch v0.5.1
  dist\                              build publicado (fora do git)
  tools\uishot\                      renderizador headless da UI
  payload\README.md                  manifesto, publicação e o contrato da release
Desktop\AMD-NR-ReShade-Installer-beta-20260915.zip    o que mandar pro tester
Desktop\HANDOFF-x86-hotkey-2026-09-15.md              o detalhe fino das cinco causas
%AppData%\AmdNrInstaller\            cache, logs, games.json, settings.json do app
%LOCALAPPDATA%\Temp\hl2-bin-backup\  o par oficial v0.5.0, pra voltar atrás no HL2
```
