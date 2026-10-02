# Handoff v0.7.8: add-on v0.7.8 (D3D10, OpenGL 32-bit), OptiScaler 0.4.9, mochizuki 0.4.9 e favoritos

02/10/2026. Leia depois de [HANDOFF-dx10-gl32-2026-10-01.md](HANDOFF-dx10-gl32-2026-10-01.md), que tem o detalhe das
rotas D3D10 e OpenGL 32-bit, das regras por jogo (`GameQuirks`) e dos favoritos.

## O que sai

- **Add-on v0.7.8** (`addon` e `bridge` 0.7.8 juntos): D3D10 64/32-bit, OpenGL 32-bit, a correção do
  `D3D10CompileShader` do ReShade 6.8.0 (Just Cause 2) e a parada por travamento da GPU (`StallStandDownMs`).
  `host_check --cross` nos binários da release: todas as rotas iguais dentro do ruído, menos o x86-d3d9, que difere
  só pela ordem BGRA da captura (já documentado).
- **OptiScaler 0.4.9-amd-nr**: lmxxf 0.39 (Style, reuso, faixa 1080 compacta, RX 9060), mochizuki 82560c4, menu novo,
  NR no FPS overlay, proteção contra travamento, logs de DLSS. `opti-runtime` 0.5.1 copiado da 0.4.8.
- **mochizuki 0.4.9-amd-nr** (`pin-mochizuki.ps1`, 68 arquivos, prewarm de 38 linhas para 65 shaders). Modelo
  continua o 1.
- **Componente novo `lmxxf-gfx1200`**: os 34 arquivos de `lmxxf-modules-gfx1200/`. Os apps publicados recusam esse
  nome, então o `pin-optiscaler.ps1` tira essas entradas do componente `optiscaler`, monta
  `lmxxf-gfx1200-<versão>.zip` (ordem e data fixas) e o fixa como componente próprio, sob demanda; só o app v0.7.8
  o pede (`ComponentsFor`, `Pins`, `PinnedForOptiScaler`). Publicado em `lmxxf-gfx1200/0.4.9-amd-nr/` no HF.
- **App**: rotas D3D10 e OpenGL 32-bit, `GameQuirks`, favoritos e ordem da biblioteca, mensagem do lmxxf citando a
  RX 9060 quando a release traz os kernels.

## Verificado

- `gate.ps1 -Ui`: 0 falhas, 329 testes. `OptiScalerVersionTests` e `UserRuntimeTests` agora esperam a 0.4.9; o
  primeiro confere que os kernels da RX 9060 estão nos pins da 0.4.9 e fora do componente `optiscaler`.
- Instalação real pela internet (harness no scratchpad, `AMDNR_HOME` temporário): o payload novo baixou a 0.4.9 do
  GitHub e o resto do HF, instalou com mochizuki numa pasta de teste (35 + 34 módulos, todos verificados) e a
  desinstalação deixou só o `OptiScaler.ini`.
- O `ApiDbBuilder` foi recusado de novo pelo PCGamingWiki ao refazer os 168 misses; `api-db.json` e
  `api-db.misses.txt` ficaram como estavam. Jogos D3D10 seguem detectados pelo import table e pelas regras por jogo.

## Publicação (feita nesta ordem)

1. Add-on `v0.7.8` (GitHub, seis arquivos) e opti `v0.4.9-amd-nr` (zip).
2. `hf upload` do `mochizuki-0.4.9-amd-nr.zip` e do `lmxxf-gfx1200-0.4.9-amd-nr.zip`.
3. Release v0.7.8 do instalador (exe, `SHA256SUMS.txt`, 7z com os json e o LEIA-ME/README), `check-release.ps1`.
4. `publish-payload.ps1 -From <pasta com os seis arquivos da v0.7.8>`; reverter a reescrita do `config.json`;
   espelho no AMD-NR-Extras.
