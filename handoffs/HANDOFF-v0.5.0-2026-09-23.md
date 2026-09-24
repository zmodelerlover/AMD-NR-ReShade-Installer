# Handoff v0.5.0: versões do OptiScaler e o runtime lmxxf

23/09/2026. Leia depois de [HANDOFF-2026-09-23.md](HANDOFF-2026-09-23.md), que cobre a rota OptiScaler
da v0.4.0.

---

## 1. O que entrou

- **Seletor de versão na rota OptiScaler.** A seção de versão, que antes sumia nessa rota, agora
  se chama "Versão do OptiScaler" e lista todas as versões que o `payload.json` traz, da mais nova
  para a mais antiga. A escolha fica gravada por jogo (`GameEntry.OptiScalerVersion`, separado do
  `AddonVersion`) e segue a mesma regra do add-on (`AddonReleases.Preferred`): a versão com que o
  jogo foi instalado, depois a da sessão, depois a mais nova.
- **OptiScaler 0.2.0** (neural-amd-opti `v0.2.0-amd-nr`): runtime lmxxf, effect strength, colour
  grade e multi frame generation do XeSS. É a versão padrão.
- **Tudo o que o lmxxf precisa vai junto:** `LmxxfNrRuntime.dll`, `lmxxf-modules\` e `shaders\`
  saem do zip do release; os pesos (`native-game-tiled-assets\`, 460 arquivos, 591 MB) saem de
  `native-game-tiled-assets.zip` no dataset do HF. O lmxxf só roda em RDNA4 (gfx1201); o relatório
  da instalação diz isso e diz como trocar o runtime no menu do OptiScaler.

## 2. Como o payload guarda mais de uma versão

O `payload.json` ganhou uma chave de topo, `releases`:

```json
"releases": { "optiscaler": [ { "version": "0.2.0-amd-nr", "components": {
    "optiscaler":    { "version": "0.2.0-amd-nr", "published": "2026-09-23", "files": [zip], "extract": [55] },
    "lmxxf-weights": { "version": "1", "files": [zip], "extract": [460] } } } ] }
```

- `components.optiscaler` continua na 0.1.1. É o que a **v0.4.0** lê, e ela ignora `releases`
  (chave desconhecida). Se a 0.2.0 fosse para `components`, a v0.4.0 tentaria instalá-la e o
  `Transaction.Apply` dela recusaria os nomes do lmxxf; e os 228 MB dos pesos entrariam no
  assistente de primeiro uso dela, que baixa todo componente fora de `OnDemand`.
- `PayloadManifest.Offered(componente)` junta a versão de `components` e as de `releases`, ordena
  pela parte numérica (`0.2.0-amd-nr` vira `0.2.0`). `With(release)` troca os componentes que a
  versão traz e mantém o resto (runtime 0.3.1, pesos do danielblnc). `Newest(...)` é o padrão.
- `Parse` valida os componentes de `releases` como os outros (https, SHA-256, caminho de no máximo
  dois níveis) e exige que cada versão traga o próprio componente na própria versão.
- "Desatualizado" (`PinnedForOptiScaler`) compara com a versão mais nova. Quem instalou a 0.1.1 de
  propósito vê o aviso, igual a quem fixa um add-on antigo.

## 3. O que mudou no motor

- **`Engine.IsAllowed`** substitui `Engine.Allowed.Contains` na transação e no `Manifest.Decode`.
  Além da lista fixa (que ganhou `LmxxfNrRuntime.dll`), aceita um arquivo simples direto dentro de
  `lmxxf-modules/` e `native-game-tiled-assets/` (os nomes vêm do upstream e mudam), e só os
  `shaders/native_*.hlsl` na pasta `shaders/`, que é um nome que outros programas usam. Nada mais
  fundo, nada que suba.
- O limite de entradas do manifesto deixou de ser `Allowed.Count` e virou `MaxManifestEntries`
  (4096): uma instalação 0.2.0 tem 520 entradas.
- **Extração:** `EnsureAsync` abre o zip uma vez para todas as entradas (antes, uma vez por
  entrada, o que com 460 entradas de um zip de 228 MB eram minutos de disco). Cada entrada vai para
  um `.part` com o hash calculado no caminho, sem passar pela memória (um peso tem 201 MB). A
  entrada é achada primeiro pelo caminho em que fica guardada, depois pelo nome como caminho, e só
  por último pelo nome solto: o pacote tem `README.md` na raiz e em `lmxxf-modules/`.
- **Pesos sem `HIP\`:** o zip tem `native-game-tiled-assets/HIP/gfx120x/`, módulos que o runtime
  nunca lê (os dele vêm de `lmxxf-modules`, que tem `SHA256SUMS`). Fora eles, tudo está a dois
  níveis. O runtime lê de uma vez todo `.f32/.f16/.i32` da pasta, então o resto vai inteiro.
- **Desinstalação:** leva `lmxxf-modules\`, `native-game-tiled-assets\` e `shaders\` quando ficam
  vazias, e antes apaga `shaders\shader-cache\` se só houver `.dxbc` nela (o runtime compila isso
  durante o jogo; nada registra).
- **Memória:** o motor lê cada arquivo inteiro antes de gravar, então instalar a 0.2.0 chega a
  1,1 GB de working set por uns 6 s. Não mexi na transação; se incomodar, o caminho é ela aceitar
  arquivos em disco em vez de `byte[]`.

## 4. Provas

- 195 testes verdes (eram 188). Os novos estão em `tests/AmdNr.Core.Tests/OptiScalerVersionTests.cs`:
  versões oferecidas e trocadas, `releases` validado, `IsAllowed`, instalar e desinstalar a 0.2.0
  inteira (com `shader-cache`), pasta `shaders\` de outro programa preservada, entrada de zip pelo
  caminho, e o `payload.json` real: toda versão abre e todo arquivo dela passa por `IsAllowed`.
- Ponta a ponta com os arquivos reais (harness em `opti/exports/installer-test/harness`, cache de
  teste semeado com os dois zips): instalou a 0.1.1, atualizou para a 0.2.0 por cima (o
  `OptiScaler.ini` intocado foi trocado pelo novo; o `dxgi.dll` também), não acusou
  desatualização, desinstalou tudo e devolveu o `dxgi.dll` original.
- A ficha do Cyberpunk renderizada sem janela (Avalonia headless): "OptiScaler version" com
  `v0.2.0-amd-nr - 2026-09-23` e `v0.1.1-amd-nr - 2026-09-23`, a nota dos pesos lmxxf só na 0.2.0.

## 5. Publicar um OptiScaler novo daqui para frente

1. Release do neural-amd-opti com o zip (nunca re-suba um asset numa tag publicada).
2. Um item novo em `releases.optiscaler`, com `components.optiscaler` na mesma versão, a URL do
   zip e o `extract` recalculado do zip (nome, caminho de no máximo dois níveis, tamanho, SHA-256).
   O script usado para a 0.2.0 fazia exatamente isso a partir do zip: listar o que o
   `PACKAGE_RELEASE.ps1` põe fora de `Licenses/`, `README.md`, `Setup*` e `SHA256SUMS.txt`, e
   mapear `OptiScaler/D3D12_OptiScaler/D3D12Core.dll` para `D3D12_OptiScaler/D3D12Core.dll`.
3. Se a versão precisar de pesos novos, um componente novo dentro dela (versão nova, arquivo novo
   no HF).
4. `dotnet test`: o teste do `payload.json` real pega nome que a transação recusaria.
5. `tools/publish-payload.ps1 -Repo zmodelerlover/amd-nr`, que agora percorre `releases` no upload
   e na verificação, e recusa um componente de release cujo conteúdo mudou sem versão nova.

Mover `components.optiscaler` para a 0.2.0 só faz sentido quando ninguém mais usar a v0.4.0.

## 6. Release v0.5.0

Feito na ordem de sempre: instalador (exe, `SHA256SUMS.txt`) primeiro, depois
`publish-payload.ps1` (com `-From` apontando para uma pasta só com `native-game-tiled-assets.zip`),
depois o `payload.json` novo copiado para `publish-v0.5.0\` e o `.7z` montado e anexado. O
`config.json` que o script reescreve foi revertido. O Extras recebeu o `payload.json` novo.

- O `.7z` leva uma pasta `AMD-NR-ReShade-Installer-v0.5.0\` com o exe, os três json e um
  `LEIA-ME.txt`/`README.txt` reescritos para a v0.5.0 (os da v0.4.0 ainda eram do beta de 15/09).
- `tools/check-release.ps1` tem que rodar no PowerShell 7: no 5.1 o `Get-FileHash` não carrega
  quando o `PSModulePath` aponta para os módulos do 7. Deu "Versions agree".
- Depois de publicado, o harness rodou de novo com o cache vazio e o manifesto lido do HF: baixou
  o zip da 0.2.0 do GitHub e os pesos do HF, conferiu tudo, instalou a 0.1.1, atualizou para a
  0.2.0 e desinstalou.
- Falta a prova no jogo pela janela, como a da v0.4.0 no Cyberpunk. Lá a pasta tem uma
  instalação manual de teste; `opti/exports/cyberpunk-test/remove-test.ps1` a desfaz antes.
