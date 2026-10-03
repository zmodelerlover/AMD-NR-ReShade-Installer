# Handoff v0.7.9: danielblnc 0.6.0 pública como runtime das duas rotas

03/10/2026. Leia depois de [HANDOFF-v0.7.8-2026-10-02.md](HANDOFF-v0.7.8-2026-10-02.md).

O danielblnc publicou a [Alpha 0.6.0](https://github.com/danielblnc/DLSS-NR-on-AMD/releases/tag/v0.6.0) em 03/10. O setup
público (`20636c95…`, 59,841,536 bytes) é byte a byte o de apoiador, com o mesmo `version.dll` `195c4a89…` que o add-on
(v0.7.6+) e o OptiScaler (0.4.7-amd-nr+) já rodavam.

## Payload

- Rota ReShade (64-bit e ponte 32-bit): `runtime` 0.6.0, `dlssnr_amd_pass1.dll` com os patches de `runtime-patches.json`
  (`430be589…`, 56,677,888 bytes), em `runtime/0.6.0/` no HF. Pesos iguais (`6bf8dc93…`).
- Rota OptiScaler: `opti-runtime` 0.6.0 (`dlssnr_amd_runtime-0.6.0.dll`, o `version.dll` cru) nas releases 0.4.7, 0.4.8 e
  0.4.9-amd-nr, as que aceitam a 0.6.0. A 0.4.6 continua com a 0.5.0.
- `user_runtimes` vazio: o bloco "Runtime do danielblnc" some da ficha (`RuntimeSection.IsVisible = listed.Count > 0`).
  Quem já tinha a 0.6.0 de apoiador instalada fica com o mesmo arquivo que o payload agora entrega.
- Apps v0.7.6 a v0.7.8 já funcionam com esse payload: os pins vêm do manifesto e o add-on pinado (v0.7.8) aceita `430be589…`.

## App

- `Engine.RuntimeSha` = `430be589…`; o `af67f066…` (0.5.1 com patch) foi para `EarlierRuntimeShas`; `RuntimeSize` 56,677,888.
- `Str.HipMissing` e `Str.NotRadeon` nos 21 idiomas falam de RDNA2 e do HIP SDK 7.2 para as RX 6000. O `FindHip7` não
  mudou: procura `amdhip64_7.dll` na pasta do sistema e no PATH.
- README: requisitos com RDNA2, e a seção de builds de apoiador diz que a lista está vazia.

## Testes

`gate.ps1 -Ui`: 0 falhas, 329 testes. `PayloadTests`, `OptiScalerVersionTests` (0.6.0 na 0.4.7 a 0.4.9, 0.5.0 na 0.4.6),
`SupporterRoutesTests` (0.5.1 agora fica desatualizada) e `UserRuntimeTests` (lista vazia; o teste com setup real usa a
entrada da 0.6.0 como estava na lista) acompanharam. Com `AMDNR_TEST_RUNTIME_SETUP` apontando para o setup público, o
patch dá exatamente `430be589…`. No opti, a rota final-image do D3D11 rodou com a 0.6.0 numa RX 9070 XT
(`AMD runtime 0.6.0`, smoke sem falhas).

## Publicação

1. Release v0.7.9 do instalador (exe, `SHA256SUMS.txt`, 7z com os json e LEIA-ME/README), `check-release.ps1`.
2. `publish-payload.ps1 -From <pasta com dlssnr_amd_pass1.dll (430be589) e dlssnr_amd_runtime-0.6.0.dll (195c4a89)>`.
   Reverter a reescrita do `config.json`; espelho no AMD-NR-Extras.
