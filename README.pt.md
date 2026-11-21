# FAL ME7.3.1/H4/ME3.1 Flasher

[English](README.md) · **Português**

Flasher para as centralinas **Bosch ME7.3.1 / H4 / ME3.1** (Fiat, Alfa Romeo, Lancia). Uma aplicação para
Windows (.NET 10, WPF) que reprograma a flash **por OBD** (K-line, KWP2000), como faz a ferramenta de fábrica da
Fiat, e lê e grava a flash e a EEPROM em **modo boot (BSL)** em bancada.

> **Usa por tua conta e risco.** O flash apaga a ECU. Uma interrupção depois de apagar deixa a ECU sem
> arrancar até ser reprogramada. A recuperação faz-se em modo boot (BSL), que esta ferramenta também faz:
> nesta família entra-se por um pino do conector — sem abrir a ECU. Lê o
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

### Modo boot (BSL)

Com o pino de boot à massa ao ligar a alimentação e um cabo KKL (FTDI ou série) na K-line, a 125000 baud:

- **Identificação** da flash (hardware, software, versão, desenho Fiat, família) em menos de um segundo.
- **Backup completo**: flash externa (512 KB), ROM interna (32 KB) e EEPROM 95080 (1 KB) em cerca de 54 s,
  gravados com o software no nome dos ficheiros e um ficheiro de informação com CRC-32.
- **Gravação da flash** (só Am29F400BB): comparar com uma leitura que já tens, ler a ECU e comparar, ou gravar
  tudo; só se apagam os sectores que mudam, um arranque em branco no ficheiro nunca se grava e os sectores
  gravados são verificados (opcional).
- **Gravação da EEPROM 95080** (só os bytes que mudam, ou todos) com verificação opcional.
- **Repor um backup completo** (flash + EEPROM) de uma vez. A ROM interna é OTP e não se grava.
- Os originais do Examiner também servem em modo boot: procura pela identificação lida por BSL e grava o
  original diretamente (é desencriptado em memória).
- Ao contrário do OBD, o modo boot não depende do imobilizador.

## ECUs suportadas

| ECU | Carros | Testado numa ECU real | Tempo de flash (FTDI / Scanmatik 2) |
|---|---|---|---|
| Bosch **ME7.3.1** | Lancia Thesis, Alfa 147 2.0 / GTA 3.2, versões 2.4 com software ME7.3.1 | Lancia Thesis 2.0 | ~75 s / ~84 s |
| Bosch **ME7.3H4** (ME7.3.1 sem MAF) | Fiat / Lancia 1.2 e 1.4 16V | — (conferida no firmware: mesmo programador) | como a ME7.3.1 |
| Bosch **ME3.1** | Lancia Thesis 2.4 5C, Fiat Stilo 2.4 | Fiat Stilo 2.4 | ~87 s / ~127 s |

Os modelos ME7.3.1 e H4 da lista foram conferidos no firmware (mesmo programador, cifra e mapa de memória). Ver o
[docs/FLASHING.pt.md](docs/FLASHING.pt.md) para como funciona, as diferenças entre as duas famílias e o que foi
testado.

## Novidades da 1.3.0

- **Modo boot (BSL)**: identificação, backup completo, gravação da flash e da EEPROM, reposição de backups —
  testado numa ME3.1 (Fiat Stilo 2.4) e numa ME7.3.1 (Lancia Thesis 2.0); as leituras são iguais byte a byte às
  de uma ferramenta comercial.
- **Identificação por OBD na ME3.1** corrigida: com os pedidos logo atrás de cada resposta a ECU reenviava às
  vezes a resposta anterior (o software aparecia com o nº de hardware). As respostas atrasadas são descartadas.
- Mensagem clara quando a ECU recusa apagar porque o imobilizador não está a autorizar.
- Nome novo: FAL ME7.3.1/H4/ME3.1 Flasher. A barra de progresso fica vermelha enquanto se grava.

1.2.0: ME3.1 testada numa ECU real (desbloqueio `27 55`, pedido de baud próprio a 124800), seletor de ECU,
correções J2534 para ECUs com pausas grandes entre bytes.

## Download

Descarrega a última versão em [Releases](https://github.com/frago1a/FAL-ME7.3.XX-Flasher/releases): descomprime e
corre o `FAL-ME7.3.XX-Flasher.exe`. É autónomo — não é preciso instalar o .NET.

## Requisitos

- Windows (build x86, porque os drivers J2534 são de 32 bits). Compilar a partir do código precisa do .NET 10 SDK.
- Ignição ligada, **motor parado**, bateria estável (recomenda-se um carregador).
- OBD: imobilizador a autorizar (a chave do carro na ignição) **ou** uma ECU virgem. BSL: o pino de boot e um
  cabo KKL (o J2534 não faz modo boot).
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
para interoperabilidade. O modo boot usa o loader e o kernel do Minimon (Infineon) e os drivers da flash e da
EEPROM do projeto C167BootTool (GPL-3.0); ver `src/ME7Tool.Core/Bsl/Resources/NOTICE.txt`.
