# Handoff v0.7.10: RX 6000 com o HIP SDK, Assetto Corsa e add-on v0.7.9

03/10/2026. Leia depois de [HANDOFF-v0.7.9-2026-10-03.md](HANDOFF-v0.7.9-2026-10-03.md).

## RX 6000 (RDNA2) com o HIP SDK 7.2

Dois jogadores com RX 6600 instalaram o HIP SDK 7.2; o setup do danielblnc funcionava e o nosso não. O SDK instala em
`C:\Program Files\AMD\ROCm\7.2\bin`, define `HIP_PATH` (e `HIP_PATH_72`) e não mexe no `PATH`.

- App: `GpuService.FindHip7` procura também em `HIP_PATH\bin`. Antes a tela dizia "hip 7: not found" e "ready: False".
- Add-on v0.7.9 (`zmodelerlover/dlss5-neural-amd`, commit `551eea0`): `InitHip` carrega `amdhip64_7.dll` de
  `HIP_PATH\bin` quando o sistema não tem um; a runtime depois encontra o mesmo módulo pelo nome (conferido com um
  programa de teste: `devices=1`, mesmo módulo). O par 32-bit usa o mesmo código. O OptiScaler já procurava no
  `HIP_PATH` (`HipRuntimeLoad.h`).
- Nenhum jogador com RX 6000 testou ainda.

## Assetto Corsa

`AssettoCorsa.exe`, na raiz, é o launcher WPF de 32 bits; o jogo é `acs.exe`, 64 bits, na mesma pasta. A detecção
escolhia o launcher. `GraphicsDetector.LauncherOf` troca um launcher conhecido pelo executável que ele inicia, quando
esse arquivo existe ao lado. Teste `AssettoCorsaIsTheGameBesideItsLauncher`.

The Witcher 3 (remaster) aponta para o launcher também; fica para a próxima, falta ver as pastas do jogo.

## Fora desta versão

- Modo assíncrono da runtime para placas mais fracas: o add-on só funciona no mesmo quadro (com `Async` ele se
  desliga: "the engine could not run same-frame"), e o OptiScaler força o inline. Precisa de trabalho no add-on
  e no fork; outro projeto disse que o assíncrono ajuda pouco nas travadas.
- NR/FG sem upscaler no OptiScaler: em testes (`wt\int`, branch `fi-int`).

## Publicação

1. Add-on `v0.7.9` (seis arquivos, `release/upload.txt`).
2. Release v0.7.10 do instalador (exe, `SHA256SUMS.txt`, 7z com os json e o LEIA-ME/README), `check-release.ps1`.
3. `publish-payload.ps1 -From <pasta com amd-nr.addon64, amd-nr.addon32, amd-nr-host64.exe e payload.sha256 da v0.7.9>`;
   reverter a reescrita do `config.json`; espelho no AMD-NR-Extras.

`gate.ps1 -Ui`: 0 falhas, 330 testes.
