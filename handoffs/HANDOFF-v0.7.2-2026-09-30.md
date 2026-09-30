# Handoff v0.7.2: tailandês, auto-update na abertura e o nome da wiki no Automático

30/09/2026. Leia depois de [HANDOFF-v0.7.1-2026-09-30.md](HANDOFF-v0.7.1-2026-09-30.md). Payload sem mudança.

## Auto-update quebrava a abertura (desde a v0.7.0)

O auto-update abre a página do jogo antes dela ter sido desenhada. O switch de auto-update, na coluna esquerda
(`SheetSide`), achava o `GameSheet` subindo a árvore visual, que ainda não existe nessa hora: `NullReferenceException`
dentro do handler, `TargetInvocationException` no `Run("startup")`, o jogo não atualizava e o resto da abertura parava.
Agora o `GameSheet` se apresenta ao `SheetSide` no construtor (`Side.Sheet = this`). `AutoUpdateFlow` no uishot abre uma
janela nova com um jogo desatualizado e o switch ligado; sem a correção ele falha com o mesmo erro do log do jogador.

## Automático: o primeiro nome da wiki do OptiScaler vence

`Work.OptiProxyFor`: escolhido à mão, depois o primeiro nome da wiki, depois o nome já instalado, depois `dxgi.dll`. Antes o
instalado vinha antes da wiki, então uma instalação feita antes do app ler a wiki ficava em `dxgi.dll` para sempre
(Neverness to Everness, que a wiki manda por `version.dll`). A troca de nome já existia: o nome antigo volta ao que era.
`ApiDatabase.OptiScalerNames` procura também pelo nome dado no app e pelo nome da pasta. O ⓘ do nome mudou nos 14 idiomas.
Testado por um amigo do usuário em Neverness to Everness.

## Tailandês

`th`, 415 chaves, fonte Leelawadee UI. O app só quebra linha em espaço, e tailandês não separa palavras: as frases têm
espaço entre trechos.
