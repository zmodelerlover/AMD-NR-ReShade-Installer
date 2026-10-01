# Handoff v0.7.6: danielblnc 0.5.1 pública como padrão e a 0.6.0 de apoiador (RDNA2)

01/10/2026. Leia depois de [HANDOFF-v0.7.5-2026-10-01.md](HANDOFF-v0.7.5-2026-10-01.md).

## 0.5.1 pública vira o runtime das duas rotas

O danielblnc publicou a [Alpha 0.5.1](https://github.com/danielblnc/DLSS-NR-on-AMD/releases/tag/v0.5.1) em 01/10. O setup
público é byte a byte o de apoiador (`b00818c7…`), com o mesmo `version.dll` `493b4a3b…` que o add-on (v0.7.2+) e o
OptiScaler (0.4.5-amd-nr+) já rodavam.

- Rota ReShade: `runtime` 0.5.1, `dlssnr_amd_pass1.dll` com patch `af67f066…` (38,569,472 bytes), em `runtime/0.5.1/` no HF.
  `Engine.RuntimeSha` aponta para ele e o 0.5.0 com patch (`c808cdb0…`) foi para `EarlierRuntimeShas`.
- Rota OptiScaler: release `0.4.7-amd-nr` com `opti-runtime` 0.5.1 (`dlssnr_amd_runtime-0.5.1.dll`, o `version.dll` cru).

## 0.6.0 de apoiador

Não é distribuída. Adiciona RX 6000 (RDNA2), que precisa do runtime HIP SDK 7.2 da AMD (o driver delas só traz HIP 6.4),
e corrige tremor em áreas escuras e luzes pequenas antes do upscale. `version.dll` `195c4a89…`, 56,677,888 bytes, no
`.rdata` do setup em `0x262017`.

- `user_runtimes` só com a 0.6.0, `addon_since` 0.7.6, patches de `runtime-patches.json` (`0x64ed`, `0xa8c2`, saída `430be589…`).
- `AcceptedRuntimes`: 0.6.0 a partir do OptiScaler 0.4.7. `KnownRuntimePrefixes` ganhou `195c4a891b6eac4c`.
- A janela de tamanho do `version.dll` do autor era 7 a 40 MB; a 0.6.0 tem 56,7 MB e passaria despercebida. Agora vai até 80 MB.
- `Str.SupporterPitch.0.6.0` nos 21 idiomas (RDNA2, HIP SDK 7.2, o tremor).
- Os textos de GPU (`Str.NotRadeon`, `Str.HipMissing`) ainda dizem RDNA3 e RDNA4 e "o Adrenalin traz o HIP 7": seguem
  certos para a versão pública. Quando a 0.6.0 ficar pública, eles precisam falar do RDNA2 e do HIP SDK 7.2.

## Mapeamento

- opti `2f39f190` (`v0.4.7-amd-nr`): `kAmd060`, bootstrap, ANCHORS (`derive_060.py`), binary check em 8 runtimes, contratos,
  testes do Setup, e a 0.6.0 rodando sem jogo numa RX 9070 XT (24/24 quadros por modo de espera, 9,41 ms).
- add-on `0e255e9` (`v0.7.6`): build 0.6.0 em `runtime_offsets.h` (a tabela foi para `runtime_builds.inc` pelo limite de
  linhas), `runtime_offsets_check.py` passando no arquivo com patch, e 4.937 quadros num host D3D11 em 1080p com a rede
  mudando a imagem.
- Nenhum dos dois foi jogado num jogo com a 0.6.0, nem testado numa placa RDNA2.

Testes: 323 (`UserRuntimeTests`, `SupporterRoutesTests`, `PayloadTests`, `OptiScalerVersionTests` acompanharam a troca); o
teste com o setup real (`AMDNR_TEST_RUNTIME_SETUP` apontando para o setup 0.6.0) passa.

## Publicação

1. opti `v0.4.7-amd-nr` e add-on `v0.7.6` (já publicados).
2. Release v0.7.6 do instalador (exe, `SHA256SUMS.txt`, 7z com os json e LEIA-ME/README), `check-release.ps1`.
3. `publish-payload.ps1 -From <pasta com amd-nr.addon64, dlssnr_amd_pass1.dll e dlssnr_amd_runtime-0.5.1.dll>`. As URLs já
   apontam para caminhos novos (`addon/0.7.6/`, `runtime/0.5.1/`, `opti-runtime/dlssnr_amd_runtime-0.5.1.dll`): o script sobe
   no caminho que a URL tem. Reverter a reescrita do `config.json`; espelho no AMD-NR-Extras.
