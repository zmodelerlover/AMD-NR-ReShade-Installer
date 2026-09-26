# Handoff v0.6.1: runtime 0.4.0 do danielblnc nas duas rotas

Publicado em 26/09/2026. O único assunto é o runtime: DLSS-NR-on-AMD v0.4.0, 42% mais rápido que a
0.3.3 segundo o autor. Os pesos não mudaram (`6bf8dc93…`), então a atualização só baixa um runtime novo.

## O que mudou no payload

| Componente | Antes | Agora | Onde no dataset |
|---|---|---|---|
| `runtime` (rota ReShade) | 0.3.0 com patches, `70af3fb7…`, 7.290.880 bytes | 0.4.0 com patches, `ff6feffa…`, 10.027.008 bytes | `runtime/0.4.0/dlssnr_amd_pass1.dll` |
| `addon` | 0.6.5 | 0.6.7, `48eb86e6…` | `addon/0.6.7/amd-nr.addon64` |
| `bridge` | 0.6.6 | 0.6.7: `bcaf1d5a…`, `c83dd632…`, `payload.sha256` `c494ad1a…` | `bridge/0.6.7/…` |
| `opti-runtime` global | 0.3.1 | **continua 0.3.1** | `dlssnr_amd_runtime-0.3.1.dll` |
| release `0.4.1-amd-nr` | não existia | OptiScaler 0.4.1 (`409d5b00…`) com `opti-runtime` 0.4.0 **próprio** (`d62be3d8…`), mais os pinos do mochizuki e dos pesos do lmxxf copiados do 0.4.0 | `opti-runtime/dlssnr_amd_runtime-0.4.0.dll` |

- **Por que o `opti-runtime` 0.4.0 fica dentro do release.** O OptiScaler 0.4.0 e os anteriores
  recusam o runtime 0.4.0 pelo SHA, porque não têm a tabela `kAmd040`. `PayloadManifest.With`
  sobrepõe os componentes de um release aos globais: só quem escolhe o 0.4.1 recebe o runtime 0.4.0.
- **Caminhos com versão.** Os arquivos novos subiram em caminhos que nenhum manifesto publicado
  apontava. A troca aconteceu inteira quando o `payload.json` novo subiu, sem janela em que um
  instalador antigo baixasse bytes novos contra pinos velhos.
- **Espelhos.** O add-on e a ponte têm os assets do release v0.6.7 de `zmodelerlover/dlss5-neural-amd`
  como espelho, conferidos pelo mesmo hash.

## Código

- `Engine.RuntimeSha` agora é o runtime 0.4.0 com patches (`ff6feffa…`). É o par que o add-on 0.6.7 aceita.
- `Work.OptiScaler.KnownRuntimePrefixes` reconhece as versões 0.3.3 (`907b30a6…`), 0.4.0 (`d62be3d8…`)
  e 0.4.0 com patches (`ff6feffa…`) deixadas na pasta do jogo como `version.dll`.
- Testes: o manifesto enviado oferece o 0.4.1 primeiro, com `opti-runtime` 0.4.0; o 0.4.0 continua
  com 0.3.1; o mochizuki do 0.4.1 é o mesmo do 0.4.0.

## Publicação

1. Os seis arquivos novos foram enviados aos caminhos com versão e baixados de volta. Os hashes
   bateram, inclusive nos espelhos do GitHub.
2. Release `v0.6.1` com o exe (`7d07e8a8…`), o `SHA256SUMS.txt` e o `AMD-NR-ReShade-Installer-v0.6.1.7z`.
   O `check-release.ps1` respondeu "Versions agree".
3. `publish-payload.ps1` sem `-From`, que baixou de volta e conferiu todos os arquivos publicados.
4. Espelho no AMD-NR-Extras.

## Testado antes

- A/B em jogo contra o runtime 0.3.x: OptiScaler no Cyberpunk 2077 e add-on 32-bit no GTA IV.
  Os dois foram aprovados.
- O gate (`tools\gate.ps1 -Ui`) passou sem falhas.

## Limitação conhecida

O menu de versões do add-on lê os releases do GitHub e não sabe de runtime. Quem escolher o add-on
v0.6.6 ou anterior recebe o runtime 0.4.0 global, que esses add-ons recusam pelo tamanho: o efeito
fica desligado, e o painel diz "a different build". Use a versão mais nova do add-on.
