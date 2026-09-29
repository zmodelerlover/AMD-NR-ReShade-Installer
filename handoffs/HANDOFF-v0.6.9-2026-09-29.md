# Handoff v0.6.9: danielblnc 0.5.0 como runtime padrão e OptiScaler 0.4.6

29/09/2026. Leia depois de [HANDOFF-v0.6.8-2026-09-28.md](HANDOFF-v0.6.8-2026-09-28.md). O danielblnc liberou a 0.5.0
para todos ([Alpha 0.5.0](https://github.com/danielblnc/DLSS-NR-on-AMD/releases/tag/v0.5.0)); o setup público traz o
mesmo `version.dll` da build de apoiador (`cddfb09e…`).

## O que mudou no payload

| Componente | Antes | Agora | Onde no dataset |
|---|---|---|---|
| `runtime` (rota ReShade) | 0.4.3 corrigida (`f3d9f2e5…`) | 0.5.0 corrigida para o add-on (`c808cdb0…`, 38.703.616 bytes) | `runtime/0.5.0/dlssnr_amd_pass1.dll` |
| release `0.4.6-amd-nr` | não existia | OptiScaler 0.4.6 (`125543e3…`, do GitHub), `opti-runtime` 0.5.0 (`cddfb09e…`), pesos do lmxxf, mochizuki e modelo da 0.4.4 | `opti-runtime/dlssnr_amd_runtime-0.5.0.dll` |
| `user_runtimes` | 0.5.0 e 0.5.1 | só 0.5.1 | — |

O add-on (v0.7.2) e a ponte não mudam: o add-on aceita a 0.5.0 corrigida desde a v0.7.0. Os pesos são os mesmos.

## Código

- `Engine.RuntimeSha` passa para a 0.5.0 corrigida; a 0.4.3 corrigida entra em `EarlierRuntimeShas`, então uma
  instalação antiga continua reconhecida. `Work.RuntimeSize` 38.703.616.
- Testes: o payload real com 0.4.6 e a 0.5.0 primeiro, e só a 0.5.1 como build de apoiador. 291 no total; o teste
  com o setup real da 0.5.1 passou.

## Publicação

1. opti `v0.4.6-amd-nr` (zip `125543e3…`, baixado de volta e conferido).
2. `pin-optiscaler.ps1` com o zip baixado; a 0.5.0 tirada do setup público com `extract_runtime.py` e corrigida com
   `patch_runtime.py` do add-on (`c808cdb0…`, o `kSha256` dele).
3. Release v0.6.9, `publish-payload.ps1`, espelho no AMD-NR-Extras.

Nada disso foi testado em jogo com a 0.5.0 pela rota do instalador.
