# Handoff v0.7.3: sete idiomas, RDR1 pelo OptiScaler, logs do OptiScaler no report e add-on v0.7.4

30/09/2026. Leia depois de [HANDOFF-v0.7.2-2026-09-30.md](HANDOFF-v0.7.2-2026-09-30.md).

## Red Dead Redemption pelo OptiScaler

O `PlayRDR.exe` (o redirecionador do Rockstar Games Launcher) fica na pasta do jogo, então carrega a DLL proxy antes do
`RDR.exe`, com qualquer nome. O OptiScaler se instalava nele e quebrava o SDK da Rockstar (erro 25D11006, "not signed
in"). Com `[ProcessFilter] TargetProcessName=rdr.exe` o OptiScaler fica em pass-through em todo processo que não seja o
jogo (`dllmain.cpp` do fork, antes de abrir o log). O jogador confirmou que o jogo passou a funcionar com isso; o
overlay do Social Club continua desligado, e isso fica para outro dia.

`Work.OptiScaler.cs`: `Launchers` lista lançadores que ficam ao lado do jogo (hoje só `PlayRDR.exe` -> `rdr.exe`). Quando
os dois estão na pasta, o `OptiScaler.ini` do pacote entra com o `TargetProcessName`; um ini da pessoa não é tocado, e o
log da instalação diz o que pôr nele. O ini é configuração, então não entra na conta de "atualização disponível".
Teste: `RedDeadRedemptionsLauncherBesideTheGameLeavesOptiScalerPassiveInIt`.

## Instalação apagada pela metade

`Scanning.Presence`: o manifesto é lido antes de `IsInstalled`, então uma pasta com o manifesto e parte dos arquivos
(um runtime apagado à mão, por exemplo) continua contando como instalada, como `Transaction.Apply` já tratava. Antes
ela aparecia como não instalada e a instalação seguinte recusava o manifesto que estava lá. Teste:
`AnInstallMissingItsRuntimeIsStillOneWhileItsOtherFilesAreThere`.

## Report

`SupportReport.RuntimeEvidence` inclui `OptiScaler.log`, `OptiScaler.ini`, `amd_bridge.log` e `amd_presr.log`. Os reports da
rota OptiScaler vinham sem nenhum log de execução (RDR1, Uncharted).

## Idiomas

Polonês, romeno, húngaro, croata, lituano, ucraniano e hindi, 415 chaves cada, gerados como os outros
(`gen.py lang_xx`). Contagens nas línguas de três plurais no formato "Rótulo: {0}"; o nome do jogo entra com a palavra
"jogo" declinada ao lado, ou depois de dois-pontos no húngaro. Hindi usa Nirmala UI (`UiFont`). `uishot langs` passa
em todos; a 980 px o cabeçalho da biblioteca aperta em húngaro e lituano como já apertava em alemão e espanhol.

## Add-on v0.7.4

Commit `3e8688c`: o add-on passa para a swapchain que um jogo cria depois de um resize recusado (Where Winds Meet: "no
frames yet" e crash ao reabrir), e o log diz o HRESULT e o motivo de remoção quando a view de profundidade do D3D11
falha. Reproduzido e verificado num host D3D11 que faz o mesmo resize; o jogador ainda não confirmou.

## Publicação

1. Add-on v0.7.4, assets da `release/upload.txt`.
2. `publish-payload.ps1 -From <pasta com amd-nr.addon64> -SetVersion addon=0.7.4`. **Antes, trocar a URL do addon no
   `payload.json` para `addon/<versão nova>/`**: o script sobe no caminho que a URL já tem. Desta vez ele subiu a 0.7.4
   em `addon/0.7.3/`; a 0.7.3 foi devolvida ao caminho dela (baixada da release do GitHub) e a 0.7.4 republicada em
   `addon/0.7.4/`. Reverter a reescrita do `config.json`.
3. Release v0.7.3 do instalador (exe, `SHA256SUMS.txt`, 7z com os json), `check-release.ps1`; espelho no AMD-NR-Extras.

## Em aberto

- One Piece Odyssey: mochizuki cai ao montar a rede em 4K no driver 32.0.32015 do jogador; aqui (32.0.31041, mesmo
  runtime, mesmas configurações) monta em 15 s e roda. danielblnc funciona lá.
- Uncharted: Legacy of Thieves: sem sintoma nem `OptiScaler.log` ainda. O `u4.exe` abre e fecha antes do `tll.exe`,
  o mesmo padrão do `PlayRDR.exe`.
