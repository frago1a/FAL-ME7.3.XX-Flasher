# FAL ME7.3.XX Flasher

[English](README.md) · **Português**

Flasher por OBD para a família de centralinas **Bosch ME7.3.1** (Fiat, Alfa Romeo, Lancia), pela K-line
(KWP2000). Uma aplicação para Windows (.NET 10, WPF) que reprograma os 512 KB de flash pela ficha de
diagnóstico, como faz a ferramenta de fábrica da Fiat.

> **Usa por tua conta e risco.** O flash apaga a ECU. Uma interrupção depois de apagar deixa a ECU sem
> arrancar até ser reprogramada. A recuperação precisa do bootstrap loader do C167 (BSL), que nesta família
> se entra por um pino do conector — sem abrir a ECU. Lê o
> [docs/FLASHING.pt.md](docs/FLASHING.pt.md) antes de usar, usa uma alimentação estável e começa com uma ECU
> que consigas recuperar.

## O que faz

- **Flash completo por OBD** da família ME7.3.1 (512 KB), com a cifra e a checksum que a ECU exige.
- **Teste de acesso** (sessão + seed/key) que não escreve nada, para confirmar que a ECU responde antes de
  apagar.
- **Mantém a identificação da ECU** (tester code e data de programação), a não ser que escolhas mudá-la.
- **Decifra/cifra** imagens da ferramenta de oficina da Fiat de e para um binário normal.
- **Encontra o software original** numa pasta da ferramenta de oficina da Fiat (Examiner) pela identificação da
  ECU: diz se o software está atual ou se há atualização, e pode usar o original no flash ou extraí-lo
  desencriptado. Os originais também se procuram pelo nº Bosch, software ou desenho Fiat, sem ECU.
- **Suporte ME3.1** (mesmo hardware, outro software, ex.: Thesis 2.4 5C / Stilo 2.4) — *experimental*: o
  procedimento está confirmado no firmware e é igual byte a byte ao da ferramenta de fábrica, mas ainda não
  correu numa ECU.
- **Ativar a escrita de RAM (0x3D)** numa imagem ME7.3.1 (necessário para o logging rápido por rotina na RAM).
  Não corrige as checksums — faz isso no WinOLS antes de gravar.
- Funciona com um **cabo KKL FTDI** ou um **interface J2534** com suporte ISO 14230 (ex.: Scanmatik 2).

## ECUs suportadas

Bosch ME7.3.1 da Fiat, Alfa Romeo e Lancia — validado numa Lancia Thesis e igual no firmware em toda a família
(Thesis 3.2, Stilo 1.2/1.4, Alfa 147 2.0/GTA 3.2, e as versões 2.4 com software ME7.3.1). Bosch ME3.1 (Thesis
2.4 5C, Stilo 2.4): experimental, ainda não testado numa ECU. Ver o [docs/FLASHING.pt.md](docs/FLASHING.pt.md)
para como funciona e o que foi testado.

## Download

Descarrega a última versão em [Releases](https://github.com/frago1a/FAL-ME7.3.XX-Flasher/releases): descomprime e
corre o `FAL-ME7.3.XX-Flasher.exe`. É autónomo — não é preciso instalar o .NET.

## Requisitos

- Windows (build x86, porque os drivers J2534 são de 32 bits). Compilar a partir do código precisa do .NET 10 SDK.
- Ignição ligada, **motor parado**, bateria estável (recomenda-se um carregador).
- Imobilizador a autorizar (a chave do carro na ignição) **ou** uma ECU virgem.
- Uma imagem de 512 KB da mesma família de ECU (em claro, leitura BSL ou ficheiro do Examiner).

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
