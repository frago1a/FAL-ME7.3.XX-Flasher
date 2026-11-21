# Como funciona o flash por OBD na Bosch ME7.3.x (Fiat / Alfa Romeo / Lancia)

[English](FLASHING.md) · **Português**

As chaves e os algoritmos abaixo foram obtidos por engenharia inversa, para interoperabilidade.

Este documento explica o que o **FAL ME7.3.1/H4/ME3.1 Flasher** faz quando reprograma uma ECU pela ficha OBD, porque é
que foi difícil pô-lo a funcionar e o que pode correr mal. Baseia-se na desmontagem do firmware da ECU (ROM
interna e aplicação) e em testes numa ECU real.

> **Atenção.** O flash apaga a ECU. Se o processo for interrompido depois de apagar, a ECU fica sem aplicação e
> não arranca até ser reprogramada — outra vez por OBD, se o programador ainda estiver a correr, ou então pelo
> **bootstrap loader (BSL)** do C167, que nesta família se entra por um pino do conector (sem abrir a ECU). Usa
> uma alimentação estável, não mexas no
> cabo nem no PC durante o flash, e tem o BSL à mão na primeira ECU de cada modelo.

## A ECU

- Família **Bosch ME7.3.1**, usada pela Fiat, Alfa Romeo e Lancia (ex.: Lancia Thesis, Fiat Stilo, Alfa 147).
- Microcontrolador **Infineon C167** e **flash externa de 512 KB** (AM29F400, bottom boot), em
  `0x800000–0x87FFFF`.
- Duas camadas de software:
  - a **ROM interna (IROM, 32 KB)** — o programa de arranque gravado no chip, que nunca muda;
  - a **aplicação** na flash — o software do motor que é substituído.
- Diagnóstico pela **K-line** com **KWP2000** (ISO 14230), na variante definida pela norma Fiat 07223.

## A sequência

| # | Passo | Pedido | Notas |
|---|-------|--------|-------|
| 1 | Ligar | fast init + `81` | Sempre a **10400 baud**. A ECU responde com os key bytes `EF 8F`. |
| 2 | Sessão de programação | `10 85 <baud>` | Motor parado. A ECU confirma a 10400 e só depois os dois lados passam ao baud de trabalho (124800). |
| 3 | Desbloqueio | `27 01 PP` / `27 02 <key>` | Seed/key (ver abaixo). Exige o imobilizador a autorizar, ou uma ECU **virgem**. |
| 4 | Tester code e data de programação | `3B 98 …`, `3B 99 …` | Gravados na EEPROM. **Têm de ir antes do passo 5** — o programador não tem o `3B`. |
| 5 | Passar ao programador | 1.º `31` | A aplicação copia para a RAM o programador da Fiat, que está na própria flash, e salta para ele. Daqui em diante quem responde é o programador, não o software do motor. |
| 6 | Apagar | `31 02 800000 87FFFF` | Apaga as zonas de arranque, dados e código. ~6 s. |
| 7 | Escrever | `34 …`, `36 …` × N, `37` | Um único download de 512 KB, com os **dados cifrados**, em blocos de 252 bytes. Cada palavra é gravada, lida de volta e comparada. ~53 s (FTDI) / ~63 s (J2534). |
| 8 | Verificar | `31 01 800000 87FFFF <cs>` | `cs` = metade de cima do CRC-32 da imagem. A ECU responde "em curso" durante ~15 s e depois confirma. |
| 9 | Desligar e ligar a ignição | | A ECU arranca com o software novo. |

Total: cerca de **75 s** com um cabo KKL FTDI, **84 s** com um interface J2534 (Scanmatik 2). A ME3.1 é
diferente em alguns passos — ver mais abaixo.

### Desbloqueio (seed/key)

A ECU envia um seed de 32 bits; a key calcula-se com um **LFSR de Galois de 32 bits** (o número de iterações
vem do parâmetro do pedido e o polinómio depende do nível de segurança). O polinómio próprio desta família é
`0x564A4F25` — o mesmo em todos os softwares analisados (`SecurityAccess.cs`). O algoritmo foi tirado da IROM e
confirmado com as ferramentas open source do NefMoto.

A condição escondida é o **imobilizador**: o pedido só é aceite com um bit de estado ligado, e esse bit liga-se
quando a chave na ignição foi reconhecida neste ciclo de ignição, ou quando a ECU **não está emparelhada**
(virgem) — uma ECU por emparelhar guarda um valor especial na EEPROM. É por isso que uma ECU virgem se pode
flashar na bancada, e uma emparelhada precisa da chave do carro.

### A cifra

O programador não aceita dados em claro (`dataFormat = 0x01`). A ferramenta de oficina da Fiat (Examiner) envia
a imagem **cifrada** e a ECU decifra cada byte antes de o gravar:

- uma **chave de 24 bytes** guardada no programador:
  `03 A2 C3 4C 45 FE 07 82 31 E2 23 D4 25 76 E7 08 B1 C2 F3 84 A5 D6 97 18` (`FiatCrypto.cs`);
- um estado que começa no primeiro byte da chave; cada byte em claro é
  `rodar-à-direita(cifrado XOR chave[estado mod 24], estado mod 8)`, e o estado avança o valor do byte cifrado;
- o estado recomeça a cada **16 KB** (`0x4000` bytes).

Foi confirmado decifrando um ficheiro original do Examiner, que deu exatamente o software da ECU de teste, e
pela checksum no ficheiro de parâmetros do Examiner (CRC-32 >> 16). O flasher cifra assim qualquer imagem em
claro, e também consegue **decifrar ficheiros do Examiner** para um binário normal.

## Porque foi difícil

**1. "O flash por OBD é impossível."** A ECU aceitava a sequência toda até ao pedido de apagar, respondia
"espera" (`7F 31 78`) e, 100 ms depois, recusava (`7F 31 22`). A primeira leitura do firmware dava a entender
que a cópia do programador para a RAM estava a falhar. Estava errado: um dump da RAM depois da recusa mostrou o
programador lá, byte a byte. A recusa vinha **do próprio programador**, que só aceita o **flash completo**:
apagar a começar exatamente em `0x800000` e um único download de 512 KB cifrado. O flash parcial por OBD não
existe nestas ECUs (só por BSL).

**2. A cifra.** O "formato especial" era a cifra acima. Teve de ser encontrada no programador, percebida e
validada com ficheiros originais do Examiner antes de se poder escrever um único byte.

**3. Uma ordem rígida.** Um contador de estado interno só deixa passar sessão → desbloqueio → tester code →
data → programador. Qualquer passo fora de ordem é recusado com códigos que não dizem porquê; só a desmontagem
mostrou as regras.

**4. Os tempos da K-line.**
- O pulso do fast-init tem de ser preciso ao milissegundo; os atrasos do `Thread.Sleep` do Windows estragam-no,
  por isso a ferramenta mede o tempo com espera ativa.
- A troca de baud só acontece depois da confirmação da ECU, ainda a 10400.
- A ECU fecha a sessão ao fim de ~5 s sem mensagens, por isso o TesterPresent vai de 2 em 2 s (há ECUs que só
  aceitam o `3E` simples, como a Fiat define).
- Nos interfaces J2534 o adaptador espera um tempo de "linha calada" (P1max) depois de cada resposta; com o
  valor por omissão, o flash demorava mais 22 s, por isso o flasher usa 5 ms. Há ECUs (a ME3.1) que deixam mais
  de 5 ms entre bytes nas respostas longas, e o adaptador deita a resposta inteira fora; o flasher deteta uma
  resposta perdida assim, sobe o P1max para 20 ms nessa ligação e repete o pedido se for só de leitura.
- Depois de trocar de baud, o flasher manda um TesterPresent ao baud novo; se a ECU não responder, volta a
  10400 (há ECUs que aceitam a sessão mas ficam no baud em que estavam).
- Quando a ECU responde "espera" (`7F xx 78`, por exemplo a gravar o tester code na EEPROM), o flasher espera
  até 5 s (P2*) pela resposta final.

**5. A flash não se lê por diagnóstico.** O `ReadMemoryByAddress` nega sempre as zonas da flash e o
`RequestUpload` não existe, por isso o software original tem de vir de outro lado (leitura por BSL ou o ficheiro
do Examiner).

**6. Sem aplicação durante a escrita.** Entre apagar e acabar o download a ECU não consegue correr o software do
motor. Por isso o flasher faz um **teste de acesso** (sessão + desbloqueio, sem apagar nada), valida o tamanho e
a checksum do ficheiro **antes** de apagar, e mantém a sessão viva durante todo o processo.

## Variante ME3.1 (mesmo hardware, outro software)

Alguns motores 2.4 (por exemplo Lancia Thesis 2.4 5C, Fiat Stilo 2.4) usam software **Bosch ME3.1** no **mesmo
hardware** (C167, a mesma flash). A cifra, a chave e o algoritmo do seed/key são iguais, mas o programador e o
software de diagnóstico são outros e o procedimento muda. Foi tirado do firmware (dump da Stilo), conferido
contra os ficheiros do Examiner e depois confirmado numa ECU real:

| | ME7.3.1 | ME3.1 |
|---|---|---|
| Desbloqueio | `27 01 PP` / `27 02 <key>` | `27 55 00` / `27 56 <key>` (outro nível de acesso; `27 01` → `7F 27 12`) — mesmo polinómio e iterações |
| Baud de trabalho no `10 85` | byte pela fórmula (`0x87` = 124800) | códigos próprios; 124800 pede-se como 125000 exato com o código 6 e 3 bytes: `10 85 06 01 E8 48` (`0x87` → `7F 10 31`) |
| Flash vista pelo software em | `0x800000` | `0x000000` |
| Programador | copiado da flash para a RAM | vive no sector de arranque `0x0000–0x3FFF` |
| Apagar | `31 02 800000 87FFFF` (tudo, incluindo o arranque) | `31 02 004000 07FFFF` (**arranque/programador intactos**) |
| Download | `34 800000 01 080000` (512 KB) | `34 004000 01 07C000` (496 KB) |
| Verificação | `31 01 … <cs>`, cs = CRC-32 **>> 16** | `31 01 … <cs>`, cs = CRC-32 **& 0xFFFF** |
| Bloco de identificação | perto de `0x10190` | perto de `0x80C6` |

Escolhe-se **ME3.1** no seletor de ECU do flasher (num flash, o seletor acompanha o ficheiro, que diz a
variante pela posição do bloco de identificação). Uma imagem ME3.1 de 512 KB lida por BSL é aceite: só se envia
`0x4000–0x7FFFF`, o arranque do ficheiro é ignorado. O fluxo cifrado que o flasher gera a partir de uma leitura
BSL é igual byte a byte ao `BO010302.BIN` do Examiner, com a mesma checksum (`0xCDB2`).

Outras diferenças vistas na ME3.1: confere o comprimento exato de cada pedido (comprimento errado dá
`7F xx 12`), responde `7F 3B 78` enquanto grava o tester code na EEPROM (~2 s) e deixa mais de 5 ms entre bytes
nas respostas longas (ver a nota do J2534 acima).

**Estado: testado numa ECU real** (Stilo 2.4, HW 0261206655), com um cabo KKL FTDI (**87 s**) e com um
Scanmatik 2 (**127 s**, mais lento por causa do P1max maior), incluindo um flash do original do Examiner.
Converter uma ME3.1 para software ME7.3.1 por OBD não é possível (o programador da ME3.1 nunca grava o
arranque); essa conversão faz-se por BSL.

## Originais do Examiner

Indica-se ao flasher a pasta completa do Examiner (com o `Global.ndb`). Depois de ligar, lê a identificação
da ECU (`1A 92/94/95/91`: nº Bosch, software, versão, desenho Fiat) e procura-a na base de dados do Examiner,
dizendo se o software está atual, se há atualização ou se o software não está na lista (por exemplo,
modificado). Um ficheiro só é oferecido se o flasher o souber gravar: tamanho certo, `.Prm` da zona de flash
certa e a checksum do `.Prm` igual à da imagem desencriptada (prova a chave e o ficheiro). Os originais também
se procuram por referência (nº Bosch, software, desenho Fiat, nome do ficheiro) e se extraem desencriptados,
sem ECU.

## Estado da validação

- Testado numa ECU real: **Lancia Thesis** (ME7.3.1) e **Fiat Stilo 2.4** (ME3.1), cada uma com um cabo KKL
  FTDI e com um Scanmatik 2 (J2534).
- Analisado no firmware e igual (mesmo programador, cifra, polinómio e mapa de memória): Thesis 3.2,
  Stilo 1.2 / 1.4 (ME7.3H4), Alfa 147 2.0 / GTA 3.2, e as versões 2.4 que usam software ME7.3.1.
- A primeira ECU de cada modelo deve ser flashada com cuidado, com a recuperação por BSL disponível.

## Requisitos

- Ignição ligada, **motor parado**, tensão de bateria estável (recomenda-se um carregador).
- Imobilizador a autorizar (a chave do carro na ignição) **ou** uma ECU virgem.
- Uma imagem de 512 KB da mesma família de ECU (binário em claro, leitura BSL ou ficheiro do Examiner; os
  ficheiros ME3.1 do Examiner têm 496 KB).
- Interface K-line: cabo KKL com chip FTDI, ou um interface J2534 com suporte ISO 14230 (driver de 32 bits).
