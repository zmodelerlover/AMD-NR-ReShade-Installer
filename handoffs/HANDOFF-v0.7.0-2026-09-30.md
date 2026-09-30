# Handoff v0.7.0: resultado da sessão, novidades, biblioteca, configurações e 12 idiomas

30/09/2026. Leia depois de [HANDOFF-v0.6.9-2026-09-29.md](HANDOFF-v0.6.9-2026-09-29.md). Junto com esta versão saiu o
add-on [v0.7.3](https://github.com/zmodelerlover/dlss5-neural-amd/releases/tag/v0.7.3), que corrige o crash do optical flow
em jogos D3D11.

## Payload

| Componente | Antes | Agora |
|---|---|---|
| `addon` | 0.7.2 (`8b025a26…`) | 0.7.3 (`4b5397c0…`, 860.160 bytes), `addon/0.7.3/amd-nr.addon64` |
| `bridge` | 0.7.2 | continua 0.7.2: o release v0.7.3 do add-on publica os mesmos bytes da ponte, então jogos 32-bit não recebem uma atualização vazia |

O `api-db.json` foi regerado (29/09) e sobe junto.

## O que entrou no app

**Página do jogo**
- **Última sessão** (`SessionLog` no Core, `SessionBox` + `GameSheet.Session.cs` no app): lê `amd-nr.log`,
  `amd-nr-x86.log`, `dlssnr_on_amd.log`, `mochizuki_nr.log` e `ReShade.log` da pasta do jogo. Rodou (quadros, ms da rede,
  versão do runtime), crash (`CRASH:`/`FAULT:`/device removido, com a linha do log), não iniciou (motivo do runtime ou do
  add-on), ReShade sem o add-on, ou nenhum quadro. Relido quando a janela volta ao foco. O lmxxf não é lido: não sei qual log
  ele grava.
- **O que há de novo** (`ReleaseNotes` no Core, `NotesSheet` no app): notas do release do GitHub, com cache em
  `cache\notes\`, num painel no estilo do popup do jogo. Abre pelo menu de versão (add-on ou OptiScaler, cujo repositório e
  tag saem da URL do zip), pelas Configurações, e uma vez depois que o app se atualiza (`Settings.SeenVersion`). Fecha ao
  trocar de aba.
- **Configurações do NR** (`SettingsTransfer`, `GameSheet.Transfer.cs`): exporta/importa um `.amdnr.zip`. ReShade:
  `amd-nr.ini` e `dlssnr_on_amd.ini` inteiros. OptiScaler: só `[DlssNr]`, `[AmdRtgi]`, `[AmdLook]` e `[FSR-RR]` do
  `OptiScaler.ini`, como `OptiScaler.nr.ini`, aplicados chave a chave. Recusa a outra rota; backup em
  `backups\settings\<jogo>-<data>\`.
- A coluna da esquerda virou `SheetSide.axaml` (limite de linhas); os cliques continuam nos handlers do `GameSheet`.
- Antes desta sessão de hoje, e também nesta versão: nome do proxy do OptiScaler pela wiki do OptiScaler, renomear jogo,
  capa e banner próprios, ReShade instalado pelo próprio app no Vulkan e em emuladores (camada em HKCU), ⓘ no lugar dos
  parágrafos, veredito como pílula, atualização automática por jogo, regras genéricas para API detectada errada.

**Biblioteca**
- Botão de filtro/ordem ao lado da busca: Todos, Instalados, Com atualização, Não instalados, Emuladores; Nome, Jogados
  recentemente (Play do app ou data dos logs), Adicionados recentemente (`GameEntry.Added`, vazio para jogos antigos).
- Visão em lista estilo Steam com banner, jogos ignorados nas Configurações, barra de título própria.

**Idiomas**
- 12 idiomas + pirata: en, pt-BR, es, fr, de, it, ru, tr, zh-CN, ja, ko, ar (RTL via `App.Flow`), x-pirate. Não há fallback
  para o inglês: toda chave precisa existir em todos. Fonte CJK por idioma (`UiFont` no `App.ChangeLanguage`).
- Os arquivos de idioma além de en/pt-BR foram gerados de módulos Python com checagem de chaves e placeholders; para mudar
  uma frase, edite os dois `.axaml` à mão e mantenha a mesma chave em todos.

## Testes

310 testes, `tools/gate.ps1 -Ui` sem falhas. O uishot tem `langs` (todos os idiomas no menor tamanho) e o flow novo
`SessionFlow` (sessão, filtro, painel de novidades fechando ao trocar de aba). O add-on v0.7.3 rodou 2.520 quadros num host
D3D11 que antes caía no primeiro quadro; não foi testado num jogo de verdade.

## Publicação

1. add-on v0.7.3 (commit `fd37c51`), assets conferidos baixando de volta.
2. Release v0.7.0 do instalador (exe, `SHA256SUMS.txt`, 7z com os json), `check-release.ps1`.
3. `publish-payload.ps1 -From <pasta com amd-nr.addon64>` (reverter a reescrita do `config.json`), espelho no AMD-NR-Extras.
