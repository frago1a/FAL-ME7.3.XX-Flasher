# FAL ME7.3.XX Flasher

[English](README.md) · **Português**

Flasher por OBD para as centralinas **Bosch ME7.3.1 e ME3.1** (Fiat, Alfa Romeo, Lancia), pela K-line
(KWP2000). Uma aplicação para Windows (.NET 10, WPF) que reprograma a flash pela ficha de diagnóstico, como faz
a ferramenta de fábrica da Fiat.

> **Usa por tua conta e risco.** O flash apaga a ECU. Uma interrupção depois de apagar deixa a ECU sem
> arrancar até ser reprogramada. A recuperação precisa do bootstrap loader do C167 (BSL), que nesta família
> se entra por um pino do conector — sem abrir a ECU. Lê o
> [docs/FLASHING.pt.md](docs/FLASHING.pt.md) antes de usar, usa uma alimentação estável e começa com uma ECU
> que consigas recuperar.

## O que faz

- **Flash completo por OBD** da ME7.3.1 (512 KB) e da ME3.1 (496 KB, o arranque/programador fica intacto), com
  a cifra e a checksum que a ECU exige, a 124800 baud (cerca de 75–90 s com um cabo FTDI).
- **Seletor de ECU** (ME7.3.xx / ME3.1): cada família tem o seu desbloqueio e o seu pedido de baud, por isso o
  flasher vai direto ao certo. Num flash acompanha o ficheiro.
- **Teste de acesso** (sessão + seed/key) que não escreve nada, para confirmar que a ECU responde antes de
  apagar.
- **Mantém a identificação da ECU** (tester code e data de programação), a não ser que escolhas mudá-la.
- **Decifra/cifra** imagens da ferramenta de oficina da Fiat de e para um binário normal.
- **Encontra o software original** numa pasta da ferramenta de oficina da Fiat (Examiner) pela identificação da
  ECU: diz se o software está atual ou se há atualização, e pode usar o original no flash ou extraí-lo
  desencriptado. Os originais também se procuram pelo nº Bosch, software ou desenho Fiat, sem ECU.
- Funciona com um **cabo KKL FTDI** ou um **interface J2534** com suporte ISO 14230 (ex.: Scanmatik 2). Mostra a
  tensão da bateria quando o interface a sabe medir.

## ECUs suportadas

| ECU | Carros | Testado numa ECU real | Tempo de flash (FTDI / Scanmatik 2) |
|---|---|---|---|
| Bosch **ME7.3.1** | Lancia Thesis, Fiat Stilo 1.2/1.4, Alfa 147 2.0 / GTA 3.2, versões 2.4 com software ME7.3.1 | Lancia Thesis 2.0 | ~75 s / ~84 s |
| Bosch **ME3.1** | Lancia Thesis 2.4 5C, Fiat Stilo 2.4 | Fiat Stilo 2.4 | ~87 s / ~127 s |

Os modelos ME7.3.1 da lista foram conferidos no firmware (mesmo programador, cifra e mapa de memória). Ver o
[docs/FLASHING.pt.md](docs/FLASHING.pt.md) para como funciona, as diferenças entre as duas famílias e o que foi
testado.

## Novidades da 1.2.0

- **ME3.1 testada numa ECU real** e deixa de ser experimental: tem o seu nível de desbloqueio (`27 55`), o seu
  pedido de baud (124800 em vez de 10400 → ~6× mais rápido), e espera pela ECU enquanto ela grava o tester code
  na EEPROM.
- **Seletor de ECU** (ME7.3.xx / ME3.1), sem tentativas falhadas.
- **J2534 (Scanmatik)**: as ECUs que deixam pausas grandes entre bytes (ME3.1) já não perdem as respostas; depois
  de trocar de baud a ligação é confirmada e volta a 10400 se a ECU não trocou.
- Desliga de forma limpa depois de um flash bem-sucedido.

## Download

Descarrega a última versão em [Releases](https://github.com/frago1a/FAL-ME7.3.XX-Flasher/releases): descomprime e
corre o `FAL-ME7.3.XX-Flasher.exe`. É autónomo — não é preciso instalar o .NET.

## Requisitos

- Windows (build x86, porque os drivers J2534 são de 32 bits). Compilar a partir do código precisa do .NET 10 SDK.
- Ignição ligada, **motor parado**, bateria estável (recomenda-se um carregador).
- Imobilizador a autorizar (a chave do carro na ignição) **ou** uma ECU virgem.
- Uma imagem de 512 KB da mesma família de ECU (em claro, leitura BSL ou ficheiro do Examiner; os ficheiros ME3.1
  do Examiner têm 496 KB).

## Compilar

```
dotnet build FAL-ME73-Flasher.sln -c Release
```

Correr os testes:

```
dotnet test FAL-ME73-Flasher.sln
```

## Documentação

- [docs/FLASHING.pt.md](docs/FLASHING.pt.md) — como funciona o flash por OBD nesta ECU: procedimento, seed/key,
  cifra, verificação, restrições.

## Licença

[GPL-3.0](LICENSE). O procedimento, o algoritmo de segurança e a cifra foram obtidos por engenharia inversa,
para interoperabilidade.
