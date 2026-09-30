# Handoff v0.7.5: o launcher do shadPS4, onde a instalação grava, e o report do FiveM

01/10/2026. Leia depois de [HANDOFF-v0.7.4-2026-09-30.md](HANDOFF-v0.7.4-2026-09-30.md). Só o app muda; o payload, o HF
e o AMD-NR-Extras continuam os da v0.7.3.

## shadPS4 pelo Qt launcher

Um jogador tinha o `shadPS4QtLauncher.exe` solto em Downloads e recebeu "shadPS4.exe is neither in it nor under it". O
launcher não guarda as builds perto dele: a pasta de dados é `launcher\` ao lado dele quando existe (portátil) e
`%APPDATA%\shadPS4QtLauncher` senão (`path_util.cpp` do shadps4-qtlauncher). Lá, `versions.json` lista o `path` do
executável de cada build e `qt_ui.ini` `[version_manager] versionSelected` diz qual ele abre.

`EmulatorInfo.LauncherData` e `Emulators.BuildsUnder` leem essa lista, a selecionada primeiro, antes da busca em
subpastas. `GraphicsDetector.ForEmulator` usa o executável escolhido pela pessoa, depois o da pasta, depois essa build;
o install vai para a pasta dele. O executável escolhido agora vale também para emulador (antes a detecção de emulador
ignorava). Na ficha, "Escolher…" aceita o executável do mesmo emulador fora da pasta do card. Teste:
`ALaunchersOwnListSaysWhichBuildItStarts`.

## Onde a instalação grava

A ficha mostra "Instala em: <pasta>" abaixo do executável (as duas pastas no FiveM), e um aviso quando a build de um
emulador está fora da pasta adicionada, com como escolher outra. 21 idiomas.

## Report do FiveM

O report olhava a pasta do `FiveM.exe`. Agora junta listagem e logs de `plugins` e `data\cache\subprocess` e o manifesto
do `FiveM.app`. O report que motivou isso (NordPC) era de uma sessão anterior ao install: o add-on do guia manual em
`plugins` sem o `dlssnr_amd_pass1.dll` ao lado do processo do jogo, que é o "Not installed in this game's folder" do
add-on; o install das 01:11 criou o arquivo, e o report saiu 8 s depois, sem o jogo ter aberto de novo.

## Publicação

Release v0.7.5 do instalador (exe, `SHA256SUMS.txt`, 7z com os json e o LEIA-ME/README), `check-release.ps1`. Sem
`publish-payload.ps1` e sem espelho.
