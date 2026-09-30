# Handoff v0.7.4: FiveM, shadPS4 e Kyty, e o cartão de apoio

30/09/2026. Leia depois de [HANDOFF-v0.7.3-2026-09-30.md](HANDOFF-v0.7.3-2026-09-30.md). Só o app muda; o payload, o HF
e o AMD-NR-Extras continuam os da v0.7.3.

## FiveM

`Preset.FiveM` (último no enum, para o `games.json` antigo ler igual), `Work.FiveM.cs`. O FiveM carrega ReShade e add-ons
de `FiveM.app\plugins`, mas roda o GTA V como `data\cache\subprocess\FiveM_b####_GTAProcess.exe`, e o add-on procura o
runtime e os pesos ao lado do executável em execução (`ExeDirectory()` no `neural.cpp`). Uma transação e um manifesto,
com raiz em `FiveM.app`, gravam nos dois lugares: `plugins/amd-nr.addon64`, `plugins/<proxy>`, `plugins/ReShade.ini`,
o efeito em `plugins/reshade-shaders/Shaders/`, e `data/cache/subprocess/` com o runtime e os pesos. Os nomes estão no
`Engine.Allowed`; o `Manifest.Decode` aceita o preset `FiveM` (sem isso o uninstall caía para "por nome" e o backup do
ReShade da pessoa não voltava).

O que está em `plugins` é da pessoa. O ReShade que já estiver lá fica quando o add-on carrega nele (`FitOf`): o build com
suporte completo a add-ons, que não é assinado (o normal é assinado com CN=ReShade e desliga add-ons em jogo online), na
6.8.0 ou mais nova. O add-on é compilado contra a ReShade API 20; a 6.7.3 é API 18 e a 6.8.0 é a primeira com 20 (lido do
`reshade.hpp` de cada tag). Fora disso o ReShade é trocado pelo 6.8.0 no mesmo nome, com backup, e o uninstall devolve.
Um `dxgi.dll` que não seja ReShade nunca é trocado. O `ReShade.ini` só ganha o painel do add-on e perde o add-on do
`DisabledAddons`. `Engine.IsConfig` compara pelo nome do arquivo, para `plugins/ReShade.ini` ser configuração também.

`FiveM` entra na tabela `Emulators.Known` por ter a mesma forma (um host com rota própria, achado pelo executável).
`FiveM_Diag.exe` é só marcador (`EmulatorInfo.Markers`): o executável é o `FiveM.exe`, uma pasta acima. Com o Diag como
executável, o Play abria o CfxDiag. O Scan acha `%LOCALAPPDATA%\FiveM\FiveM.app` (`GameScanner.FiveM`,
`GamePlatform.FiveM`). Marcadores de instalação aninhados em `Work.FiveMMarkers`, separados de `InstalledMarkers`.

Testado no FiveM do usuário (ReShade 6.7.3 com add-on completo, ENB como `d3d11.dll`, ShaderToggler, MartysMods): o
ReShade foi trocado pelo 6.8.0 com backup idêntico ao original, "Registered add-on AMD Neural Rendering using ReShade API
version 20", profundidade D3D11 1920x1080, runtime 0.5.0 do danielblnc, "engine ready", dois passes. Antes, o install e o
uninstall rodaram numa cópia do `plugins` real: tudo voltou byte a byte.

## shadPS4 e Kyty

Kyty (`kyty_emulator.exe`, `fc_script.exe`) entrou na tabela. O shadPS4 ganhou `Launchers`: a pasta do Qt launcher é
reconhecida, e o pre-flight recusa instalar ao lado dele, nomeando as pastas das builds (`Emulators.BuildsUnder`). Os dois
aparecem pelo nome em Adicionar > Emulador. Nenhum importa `vulkan-1.dll` (lido do PE das builds de 30/09); o add-on
engancha loader dinâmico desde a v0.5.3, então a frase "o jogo precisa importar vkCreateDevice estaticamente" saiu da
nota do Vulkan nos 21 idiomas. `NoteVulkanLayer` avisa quando o layer de outra pessoa não serve (antigo ou assinado).

## Apoio

Café cinza acima da versão no rail; abre `SupportSheet`, no quadro do "What's new" (mesmos nomes, mesmos estilos), com o
texto e os cartões do Ko-fi (`ko-fi.com/proceduralnilo`) e da Vakinha. Textos nos 21 idiomas.

## Publicação

Release v0.7.4 do instalador (exe, `SHA256SUMS.txt`, 7z com os json e o LEIA-ME/README), `check-release.ps1`. Sem
`publish-payload.ps1` e sem espelho: o payload não mudou.

## Em aberto

- shadPS4 e Kyty não foram testados dentro do emulador.
- Pasta `FiveM` adicionada à mão (em vez de `FiveM.app`) instala, mas o selo de instalado do card não aparece.
