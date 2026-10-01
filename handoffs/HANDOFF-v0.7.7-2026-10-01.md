# Handoff v0.7.7: OptiScaler 0.4.8, mochizuki 0.4.8, add-on v0.7.7 e a ponte 32-bit em dia

01/10/2026. Leia depois de [HANDOFF-v0.7.6-2026-10-01.md](HANDOFF-v0.7.6-2026-10-01.md).

## A ponte 32-bit estava parada na 0.7.2

Um player viu a ponte 32-bit marcada como .7.2 e o jogo 32-bit recebendo o add-on 0.7.2. Os releases do add-on de
0.7.3 a 0.7.6 publicaram o par 32-bit (`amd-nr.addon32`, `amd-nr-host64.exe`, `payload.sha256`), mas só o componente
`addon` do `payload.json` foi atualizado: o `bridge` continuou em 0.7.2. Por isso a 0.6.0 de apoiador também não
aparecia em jogos 32-bit (`addon_since` é comparado com a versão da ponte nessa rota).

- `bridge` agora é 0.7.7, com o par do mesmo release do add-on.
- `PayloadTests.TheShippedManifestAgreesWithTheAddOnsOwnConstants` falha se `bridge` e `addon` tiverem versões
  diferentes. Ao fixar um add-on novo, fixe a ponte do mesmo release.

## Add-on v0.7.7

- O Style (Default, Natural, Cinematic) vai para a entrada de estilo da própria rede, como no overlay do danielblnc;
  o colour grade que imitava o NVIDIA saiu.
- Jogos OpenGL (Firestorm) quebravam ao criar os contextos: a thread do hook de `vkCreateDevice` segurava o add-on
  depois do `FreeLibrary` do ReShade e o descarregava ao sair (`amd-nr.addon64_unloaded`, `__vcrt_freefls`). Ela só
  existe agora quando o ReShade roda como layer Vulkan.

## OptiScaler 0.4.8-amd-nr e mochizuki 0.4.8-amd-nr

- Cor do danielblnc como a dele: encoding Linear por padrão e o Style na rede. Um `OptiScaler.ini` de antes da 0.4.8
  (tem `AmdColourGrade`, não tem `AmdStyle`) lê o `AmdEncoding=2` que o pacote antigo gravava como Linear até a pessoa
  salvar as configurações. O app não precisou mudar: um ini mantido como configuração da pessoa migra sozinho.
- Watch Dogs: Legion: tela verde (exposição só para entrada HDR) e NR abaixo de 100% (vetores TYPELESS).
- mochizuki: exposição do jogo, `dlssnr-amd\` ao lado do exe (RE Engine e o `_storage_`), peso do histórico,
  formatos de cor e o Preprocess do upstream. Componente `mochizuki` 0.4.8-amd-nr (`pin-mochizuki.ps1`, 62 arquivos,
  `runtime_prep.spv` novo, lista de prewarm feita para estes 59 shaders). `mochizuki-model` continua o 1.
- `opti-runtime` é o 0.5.1 da 0.4.7, copiado à mão.

Testes: os do app (`dotnet test`) e o gate. Nada disso foi jogado depois de empacotado; os jogos citados são onde cada
correção foi vista ou de onde veio o log.

## Publicação

1. opti `v0.4.8-amd-nr` e add-on `v0.7.7` já publicados.
2. Release v0.7.7 do instalador (exe, `SHA256SUMS.txt`, 7z com os json e LEIA-ME/README), `check-release.ps1`.
3. `hf upload` do zip do mochizuki (`mochizuki/0.4.8-amd-nr/`), como o `pin-mochizuki.ps1` imprime.
4. `publish-payload.ps1 -From <pasta com amd-nr.addon64, amd-nr.addon32, amd-nr-host64.exe e payload.sha256 da v0.7.7>`:
   as URLs já apontam para `addon/0.7.7/` e `bridge/0.7.7/`. Reverter a reescrita do `config.json`; espelho no
   AMD-NR-Extras.
