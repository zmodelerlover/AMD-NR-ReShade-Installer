# Handoff v0.6.0: o runtime mochizuki na rota OptiScaler

25/09/2026. Leia depois de [HANDOFF-v0.5.0-2026-09-23.md](HANDOFF-v0.5.0-2026-09-23.md), que cobre o
menu de versões do OptiScaler e o lmxxf. Este arquivo cobre só o que a v0.6.0 acrescenta.

---

## 1. O que entrou

- **mochizuki, terceiro runtime de NR da rota OptiScaler** (experimental). É o port Vulkan da rede do
  DLSS 5 feito por mochizuki0323 (DLSSNR-AMD), compilado pelo neural-amd-opti como
  `MochizukiNrRuntime.dll`, que lê uma pasta `dlssnr-amd\` ao lado de si: `shaders\` (54 arquivos, com
  `runtime\` e `temporal\`), `prewarm\manifest.txt` e o modelo `dlssnr.bin` (147.756.560 bytes). Só
  roda em RDNA4 (`VK_KHR_cooperative_matrix` + `VK_EXT_shader_float8`, driver AMD 25.10+).
- **OptiScaler 0.4.0-amd-nr** na lista de versões, com os pinos ainda como placeholder (§3): o app
  não oferece a versão até o estágio de publicação preenchê-los.
- **Na ficha**, abaixo do menu de versão, a seção **Runtime de NR** aparece quando a versão escolhida
  traz o mochizuki: uma caixa, desmarcada por padrão e lembrada por jogo (`GameEntry.Mochizuki`,
  `bool?`). Marcada, a instalação baixa o `mochizuki` e o `mochizuki-model`, põe os arquivos no jogo e
  grava `[DlssNr] NrBackend=mochizuki` no `OptiScaler.ini`. **Desmarcada, a próxima instalação tira** o
  que uma instalação anterior pôs do mochizuki (§4). Sem escolha registrada (`null`: jogo adicionado de
  novo, `games.json` posto de lado, outro PC) a caixa segue a pasta (`Work.HasMochizuki`), para um
  update não tirar o mochizuki de quem o tinha.
- **RDNA4:** `GpuService` passou a ler o device id (`SystemState.Rdna4`: Navi 48/44 por id, ou
  "RX 9xxx" / "AI PRO R9xxx" no nome). Placa conhecida e não RDNA4: a caixa fica desabilitada e a nota
  diz por quê. Placa desconhecida: a caixa fica disponível e a nota avisa. Os logs de instalação e o
  relatório de problema registram `rdna4`, e o relatório passa a levar o `mochizuki_nr.log`.

## 2. Como o payload guarda o mochizuki

Dois componentes dentro de `releases.optiscaler[0.4.0-amd-nr]`, nunca em `components` (o v0.4.0 e o
assistente de primeiro uso baixariam tudo que está lá):

| componente | versão | o quê |
|---|---|---|
| `mochizuki` | `0.4.0-amd-nr` | `mochizuki-0.4.0-amd-nr.zip` com `extract` pinando os 57 arquivos (runtime, prewarm, 54 shaders e a licença MIT do DLSSNR-AMD em `dlssnr-amd/LICENSE-DLSSNR-AMD.txt`) |
| `mochizuki-model` | `1` | `dlssnr.bin` solto, como o `dlssnr_on_amd_weights.bin` |

- **Caminhos com ponto.** O runtime lê arquivos três níveis abaixo da pasta do jogo, e todo app já
  publicado (v0.5.0 e v0.5.1 validam `releases` também) recusa o manifesto inteiro se um caminho tiver
  mais de dois níveis. Então o payload escreve as pastas debaixo de `dlssnr-amd` com pontos:
  `dlssnr-amd.shaders.runtime/cascade_blur.spv` no zip e no cache, `dlssnr-amd\shaders\runtime\...` no
  jogo (`Work.MochizukiDestination`). O modelo não precisa: `dlssnr-amd/dlssnr.bin`. O zip usa o mesmo
  layout do cache, então a extração acha cada entrada pelo caminho exato (há dois
  `shader-constants.txt`).
- **Sob demanda.** Os dois estão em `PayloadManifest.OnDemand`; o v0.5.x nunca os baixa (o
  `ComponentsFor` dele não conhece os nomes) e instala a 0.4.0 com daniel e lmxxf.
- **Pinos:** `PayloadPins.MochizukiFiles`, só quando os dois componentes existem (runtime sem modelo
  não serve). "Desatualizado" compara os arquivos do mochizuki só em pasta que os tem.
- O modelo já está pinado de verdade (`2b41c888…`, é o mesmo em todo snapshot desde 24/09); o resto
  do 0.4.0 é placeholder.

## 3. Placeholders

Pino de 64 zeros (`PayloadManifest.PlaceholderSha`) = bytes que ainda não existem. Uma release com
qualquer placeholder **não é oferecida** (`PayloadManifest.IsPlaceholder`, em `Offered`), então o
`payload.json` do repositório pode levar a 0.4.0 com o layout escrito e o app continua instalável.
Os apps antigos não conhecem a regra, por isso:

- `tools/publish-payload.ps1` recusa publicar manifesto com placeholder (antes de qualquer upload);
- `tools/check-release.ps1` recusa um build cujo `payload.json` (embutido e ao lado do exe) tenha um.

Os pinos saem dos arquivos, por script:

- `tools/pin-optiscaler.ps1 -Zip <OptiScaler-X-amd-nr.zip baixado do release>`: zip + `extract`
  (tudo menos `Licenses/`, `README.md`, `Setup*`, `Uninstall*`, `SHA256SUMS.txt` na raiz; `D3D12Core`
  vai para `D3D12_OptiScaler/`). Confere o `SHA256SUMS.txt` do próprio zip e recusa nome que o v0.5.x
  recusaria instalar. Testado: regenerou do zip publicado exatamente os 55 pinos da 0.3.0.
- `tools/pin-mochizuki.ps1 -From <pasta final> -License <LICENSE do DLSSNR-AMD>`: recusa
  `pipeline.cache`, `*.tmp`, logs, pasta funda ou com ponto; confere que o `prewarm\manifest.txt` foi
  feito para esses shaders (contagem da linha `shaders`); exige a licença MIT do DLSSNR-AMD (o zip
  leva código dele) e a põe em `dlssnr-amd/LICENSE-DLSSNR-AMD.txt`, fora de `shaders\` (o prewarm
  confere o hash da lista de arquivos de `shaders\`); monta o zip em ordem ordinal, de forma
  determinística em qualquer máquina; reescreve os dois componentes; imprime os comandos de upload.
  Modelo com outros bytes exige `-ModelVersion` novo. Rodado no `release\candidate\runtime`: 57
  arquivos, prewarm para 54 shaders, `.lib`/`.exp`/`obj` ignorados.
- `publish-payload.ps1` agora reenvia um arquivo para o caminho que o `url` dele já tem, em vez de
  sempre para a raiz pelo nome.

## 4. O que mudou no motor

- `Engine.IsAllowed`: `MochizukiNrRuntime.dll` na lista, e qualquer arquivo simples em `dlssnr-amd\`
  até dois níveis abaixo (`IsMochizukiData`). O nome da pasta é do runtime.
- **O runtime reescreve o `prewarm\manifest.txt`** quando o driver da máquina não é o do manifesto
  enviado (UUID do cache) ou quando surgem pipelines novos. Sem tratamento, toda reinstalação daria
  "File changed since install" e o uninstall o manteria como "modificado". `Engine.IsRuntimeMaintained`
  marca esse arquivo: `Transaction.Apply` mantém o que o runtime fez quando o pino não mudou e troca
  (sem backup) quando mudou; o uninstall o leva mesmo mudado (`Ours`). Vale também para a entrada que
  não é nossa: quem copiou o mochizuki à mão antes (como as notas do OptiScaler 0.4.0 ensinam) tem a
  cópia idêntica registrada como `Owned=false`; o runtime reescreve a lista e a reinstalação não pode
  falhar por isso. Mesmo pino: fica como está. Pino novo: a lista passa a ser nossa (os bytes
  registrados eram os que o app pina, não há nada de ninguém para guardar). Nenhum nome antigo é
  afetado.
- **Desmarcar tira.** `Transaction.Apply` ganhou `retire` (`Transaction.Retire.cs`, `PlanRetire`):
  nomes registrados que a rota não quer mais na pasta saem na mesma transação, planejados antes do
  journal e revertidos junto. Como no uninstall: o que o app escreveu sai, o que ele deslocou volta do
  backup, arquivo mudado depois fica (com aviso), arquivo que o jogo segura aborta a instalação antes de
  escrever. Entrada `Owned=false` (cópia idêntica que já estava lá) só sai do registro; o arquivo é da
  pessoa. A rota OptiScaler passa todo nome do mochizuki registrado (`MochizukiRecorded`): desmarcado,
  sai tudo, com o `pipeline.cache`, os `.tmp` e as pastas vazias (`AfterMochizukiRetired`); marcado,
  sai só o que um build antigo tinha e o novo não. Sem isso a pasta ficava "desatualizada" para
  sempre: `PayloadMovedOn` comparava os arquivos que ficaram com os pinos novos e nenhum update
  resolvia. Um `OptiScaler.ini` da pessoa que ainda diz `NrBackend=mochizuki` depois disso gera um
  aviso.
- **Uninstall:** `mochizuki_nr.log` entrou nas `Droppings`; `AfterMochizukiUninstall` apaga
  `dlssnr-amd\pipeline.cache` e os `.tmp` de escrita interrompida só depois que o runtime saiu, e poda
  as pastas vazias de baixo para cima. Arquivo alheio dentro de `dlssnr-amd` fica.
- **INI:** `PickMochizukiInIni` usa o `Engine.SetIni` (a mesma edição no lugar do `ReShade.ini`) sobre
  o `OptiScaler.ini` do pacote, quando é a instalação que o escreve. Um ini que é da pessoa (já estava
  lá, ou o OptiScaler salvou configurações nele depois) não é escrito pela instalação, como antes, e o
  relatório diz para escolher mochizuki na aba Neural.
- `Work.Preflight`/`Install` ganharam `mochizuki: bool` (as rotas ReShade ignoram). Sem o mochizuki
  na versão escolhida, pedir por ele é erro antes de escrever qualquer coisa.

## 5. Provas

- 254 testes verdes (eram 240). Os 14 de `tests/AmdNr.Core.Tests/MochizukiTests.cs`: oferta e pinos
  (só com o modelo), placeholder escondido, destinos e `IsAllowed`, caminho profundo recusado pelo
  `Parse`, instalação com a linha única do ini trocada, sem mochizuki nada muda, versão sem mochizuki
  recusada, ini da pessoa preservado (os dois casos), prewarm reescrito mantido/trocado, uninstall que
  devolve a pasta byte a byte, runtime preso pelo jogo; e, desta rodada: desmarcado contra pinos novos
  (runtime preso pelo jogo recusa sem escrever nada; depois sai tudo, `PayloadMovedOn` falso, pasta
  final idêntica), ini da pessoa que ainda nomeia mochizuki (aviso), cópia à mão com a lista
  reescrita (reinstala, atualiza, desinstala devolvendo a cópia) e cópia à mão desmarcada (a cópia
  fica). `TheShippedManifest…` confere a 0.4.0 do repositório.
- `tools/gate.ps1 -Ui`: build, testes, limite de linhas, render e flows OK. O flow
  (`tools/uishot/Flows.Mochizuki.cs`) abre a ficha, escolhe a versão que traz mochizuki, marca a caixa,
  instala, confere arquivos e ini; esquece a escolha e vê a caixa seguir a pasta; desmarca, instala e
  confere que saiu e que a pasta não fica desatualizada; marca e instala de novo; o flow de uninstall
  confere que tudo saiu.
- Ponta a ponta com o **candidato de release** (`opti/exports/mochizuki-work/installer-e2e`, lido sem
  alterar `release\candidate`): o `OptiScaler-0.4.0-amd-nr.zip` de `candidate\dist` pinado por
  `pin-optiscaler.ps1` (55 arquivos, somas conferem), o runtime, os shaders e o prewarm de
  `candidate\runtime` com o modelo copiado da onda 2 (`stage-rc`), pinados por `pin-mochizuki.ps1`
  com a licença; arquivos servidos do disco pelo caminho "já está nesta máquina" do app. Pasta falsa:
  instalação com mochizuki (57 arquivos byte a byte), reinstalação com a lista reescrita, instalação
  desmarcada (sai tudo, ini volta a ser o do pacote), marcada de novo, uninstall, pasta idêntica à de
  antes. Segunda pasta com cópia à mão: instala, reinstala com a lista reescrita, desmarca, desinstala,
  e a cópia da pessoa fica. Log: `installer-e2e\e2e-run.log`.
- Compatibilidade: o motor da v0.5.1, tirado da tag (`installer-e2e\compat-v051`), lê o payload novo,
  oferece a 0.4.0, instala e desinstala a 0.4.0 do candidato sem mochizuki. Lendo o payload com
  placeholders ele também oferece a 0.4.0: é por isso que nenhum placeholder pode ser publicado.

## 6. Publicar

O roteiro completo está em `opti/exports/mochizuki-work/release/INSTALLER_PUBLISH_RUNBOOK.md`. A ordem
de sempre continua: release do OptiScaler no GitHub → pinos → testes → build → release do instalador
→ arquivos do mochizuki nos endereços pinados → `publish-payload.ps1` → conferência baixando de volta.

## 7. O que ficou de fora

- Um `OptiScaler.ini` em que o OptiScaler já salvou configurações não recebe o `NrBackend`; a pessoa
  escolhe no menu (o combo lista os runtimes instalados). Desmarcado o mochizuki, um ini desses que
  ainda o nomeia só gera aviso.
- A detecção de RDNA4 é por device id conhecido ou pelo nome. Uma placa RDNA4 futura com outro nome
  cai em "não é RDNA4" só se o id também for desconhecido.
- Placa conhecida e não RDNA4 com o mochizuki instalado (placa trocada): a caixa fica desabilitada e a
  próxima instalação tira o mochizuki, que nessa placa não roda.

## 8. Publicado

Em 25/09, nesta ordem:

- **OptiScaler 0.4.0-amd-nr**: release `v0.4.0-amd-nr` de `MatheusFerreiraS/neural-amd-opti`, commit
  `171475e1`. `OptiScaler-0.4.0-amd-nr.zip`: 141.518.665 bytes, sha256
  `9c47e4991ce1fd9924d63834136345616a0b9662460fb9dd9bd022b4827a40a8`, 55 arquivos pinados dentro dele.
- **mochizuki 0.4.0-amd-nr**: `mochizuki-0.4.0-amd-nr.zip`, 6.263.288 bytes, sha256
  `26170b9b80e0aae1f26d50e558bd07c6561f6ce8daca632ea7b4daf391166f95` (runtime, 54 shaders, lista de prewarm e
  licença). Modelo 1: `dlssnr.bin`, 147.756.560 bytes, sha256
  `2b41c888cf4155b8958c665ba64018ab0bd25c85fc71a2b6db86d0d04d1f7fbd`. Os dois baixados de volta e conferidos
  contra os pinos antes de o payload apontar para eles.
- **Instalador v0.6.0**: commits `87a13c3`, `dd43578`, `67324d4`, tag `v0.6.0`. Release com
  `AMD-NR-ReShade-Installer.exe` (sha256 `848f7c8cf494d77594acefe4cf3230f8bcb67e1d6f3077411c2d8840949a41a6`),
  `SHA256SUMS.txt` e `AMD-NR-ReShade-Installer-v0.6.0.7z` (16.113.123 bytes). `check-release.ps1`:
  "Versions agree."
- **payload.json** publicado por `publish-payload.ps1`, que baixou de volta todos os arquivos publicados
  ("every published file matches"). Espelho em `zmodelerlover/AMD-NR-Extras`, commit `8ca3d96`.

Conferências depois de publicar:

- O `payload.json` no ar é byte a byte o do repositório, e igual ao do espelho. Ele oferece
  `0.4.0-amd-nr` primeiro.
- End-to-end com a lista publicada e um cache vazio, tudo baixado dos endereços pinados: instala com
  mochizuki, reinstala, instala desmarcado, marca de novo, desinstala, e o caso da cópia feita à mão.
  23 PASS, 0 FAIL ("END-TO-END: every check passed"; o log fica em `installer-e2e\e2e-live.log`).
- Antes de publicar: `gate.ps1 -Ui` sem falhas, o end-to-end servido do disco e o motor da v0.5.1
  (oferece a 0.4.0 e instala sem mochizuki).
