# Handoff v0.7.1: o idioma pirata derrubava o app

30/09/2026. Leia depois de [HANDOFF-v0.7.0-2026-09-30.md](HANDOFF-v0.7.0-2026-09-30.md).

Com o idioma pirata, a "Última sessão" formatava números com `CultureInfo.GetCultureInfo("x-pirate", predefinedOnly: true)`.
O .NET aceita a tag privada sem lançar, mas a cultura não tem dados, e `NumberFormat` dá `NullReferenceException`. No modo
lista a página de um jogo fica sempre aberta, então o app caía a cada abertura, antes do atualizador rodar.

- `GameSheet.Culture()`: tags `x-` usam `en-US`.
- `ShowSession` (async void) passa por `ShowSessionAsync` dentro de um try: um erro ali esconde o bloco e vai para o log.
- `SessionFlow` renderiza a sessão também em `x-pirate`; sem a correção o flow reproduz o crash.

Quem travou na v0.7.0: baixar o exe da v0.7.1 à mão, ou apagar `"Language"` de `%APPDATA%\AmdNrInstaller\settings.json`.
Payload sem mudança.
