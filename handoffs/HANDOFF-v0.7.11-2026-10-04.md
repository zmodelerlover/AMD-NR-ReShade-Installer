# Handoff v0.7.11: versão do danielblnc por jogo, política RX 9000, OptiScaler 0.4.10, mochizuki 0.4.10 e add-on v0.7.10

04/10/2026. Leia depois de [HANDOFF-v0.7.10-2026-10-03.md](HANDOFF-v0.7.10-2026-10-03.md). O detalhe de tudo desta
rodada (add-on, fork e a investigação do mochizuki no driver 32015) está em
`neural-amd-opti/handoff/HANDOFF-fixes-2026-10-03.md`.

## Versão da runtime do danielblnc por jogo

- `payload.json` ganhou `releases.runtime`: 0.6.0, 0.5.1, 0.5.0 e 0.4.3, cada uma com o `runtime` (pass1 patcheado +
  pesos, para o add-on) e o `opti-runtime` (cru, para o OptiScaler). Os arquivos já estavam no HF.
- `PayloadManifest.WithRuntime(version, async)`, `RuntimeVersions()`, `RuntimeChoice` (sobrevive a um `With()` de
  versão do add-on/OptiScaler aplicado depois), `PayloadPins.RuntimeChosen` / `RuntimeAsync`.
- `Work.RuntimeRunsOn` só oferece o que o add-on/OptiScaler escolhido roda.
- Ficha: linha "Versão do danielblnc" (`DanielVersionRow`, `RuntimeOption.cs`), salva em `GameStore.DanielRuntime`.
- Política (`Work.Runtimes.cs`): RX 9000 vê todas, **0.4.3 padrão** com selo verde Recomendado, as outras com selo
  laranja Instável e instaladas em async (`Work.GoesInAsync`: `Inline=0` no `amd-nr.ini`, `AmdAsync=true` no
  `OptiScaler.ini`, só essa chave). RX 6000/7000: só a 0.6.0, sem async.

## RX 9000: mochizuki no add-on, lmxxf no OptiScaler

Sem a caixa experimental: em RX 9000 o mochizuki entra sempre (`WantsMochizuki`). O add-on v0.7.10 roda o mochizuki
quando não há `NrBackend`; um `OptiScaler.ini` novo/nosso recebe `NrBackend=lmxxf`. Só foi liberado agora porque o
mochizuki 0.4.10 voltou a montar a rede no driver 32.0.32015 (o 0.4.9 fechava o jogo).

## Outras

- Arquivo preso nomeia o processo (`Engine.Holders.cs`, Restart Manager), na instalação, no pré-check e na desinstalação.
- Aviso de `d3d11.dll` de outro mod nas rotas D3D10/11 (`Work.CheckForeignD3d11`).
- OptiScaler `0.4.10-amd-nr` (`pin-optiscaler.ps1`). O script não copia o `opti-runtime`: foi posto à mão, o da 0.4.9
  (0.6.0, `195c4a89…`). Sem ele a release cairia na 0.3.1 do topo. `lmxxf-gfx1200` 0.4.10 (mesmos kernels, zip novo).
- mochizuki `0.4.10-amd-nr` (`pin-mochizuki.ps1`, 68 arquivos): shaders corrigidos para o driver 32015, prewarm feito no
  Cyberpunk com esses shaders. `mochizuki-model` continua o 1.
- Add-on e ponte v0.7.10 (protocolo v6).
- Testes: `RuntimeChoiceTests`, `HeldFilesTests`; `OptiScalerVersionTests` e `UserRuntimeTests` esperam a 0.4.10.

## Verificado

`gate.ps1 -Ui`: build, testes, line-limit, ui e flows, 0 falhas.

## Publicação (feita nesta ordem)

1. opti `v0.4.10-amd-nr` (zip) e add-on `v0.7.10` (seis arquivos).
2. `hf upload` do `mochizuki-0.4.10-amd-nr.zip` e do `lmxxf-gfx1200-0.4.10-amd-nr.zip`.
3. Release v0.7.11 do instalador (exe, `SHA256SUMS.txt`, 7z com os json e o LEIA-ME/README), `check-release.ps1`.
4. `publish-payload.ps1 -From <pasta com os arquivos da v0.7.10>`; reverter a reescrita do `config.json`; espelho no
   AMD-NR-Extras.
