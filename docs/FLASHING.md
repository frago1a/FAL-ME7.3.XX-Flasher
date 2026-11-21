# How OBD flashing works on the Bosch ME7.3.x (Fiat / Alfa Romeo / Lancia)

**English** · [Português](FLASHING.pt.md)

The keys and algorithms below were recovered by reverse engineering for interoperability.

This document explains what the **FAL ME7.3.1/H4/ME3.1 Flasher** does when it reprograms an ECU over the OBD
port, why it was hard to get working, and what can go wrong. It is based on the disassembly of the ECU
firmware (internal ROM and application) and on tests on a real ECU.

> **Warning.** Flashing erases the ECU. If the process is interrupted after the erase, the ECU has no
> application and will not start until it is reprogrammed — over OBD again if the programmer still runs,
> otherwise through the C167 **bootstrap loader (BSL)**, entered via a pin on the connector on this family
> (no need to open the ECU). Use a stable power
> supply, do not touch the cable or the PC during the flash, and keep a BSL setup at hand for the first
> ECU of each model.

## The ECU

- **Bosch ME7.3.1** family used by Fiat, Alfa Romeo and Lancia (e.g. Lancia Thesis, Fiat Stilo, Alfa 147).
- **Infineon C167** microcontroller, **512 KB external flash** (AM29F400, bottom boot), mapped at
  `0x800000–0x87FFFF`.
- Two layers of software:
  - the **internal ROM (IROM, 32 KB)** — boot code burned into the chip, never changes;
  - the **application** in the flash — the engine software that is being replaced.
- Diagnostics over **K-line** with **KWP2000** (ISO 14230), in the variant defined by Fiat standard 07223.

## The sequence

| # | Step | Request | Notes |
|---|------|---------|-------|
| 1 | Connect | fast init + `81` | Always at **10400 baud**. The ECU answers with key bytes `EF 8F`. |
| 2 | Programming session | `10 85 <baud>` | Engine must be stopped. The ECU confirms at 10400, then both sides switch to the working baud (124800). |
| 3 | Security access | `27 01 PP` / `27 02 <key>` | Seed/key (see below). Requires the immobiliser to authorise, or a **virgin** ECU. |
| 4 | Tester code, programming date | `3B 98 …`, `3B 99 …` | Written to EEPROM. **Must happen before step 5** — the programmer has no `3B`. |
| 5 | Hand over to the programmer | first `31` | The application copies Fiat's programmer from the flash into RAM and jumps to it. From here on, the programmer answers, not the engine software. |
| 6 | Erase | `31 02 800000 87FFFF` | Erases boot, data and code sections. ~6 s. |
| 7 | Download | `34 …`, `36 …` × N, `37` | One 512 KB download, **encrypted data**, 252-byte blocks. Each word is programmed, read back and compared. ~53 s (FTDI) / ~63 s (J2534). |
| 8 | Verify | `31 01 800000 87FFFF <cs>` | `cs` = upper half of the image CRC-32. The ECU answers "in progress" for ~15 s, then confirms. |
| 9 | Ignition off/on | | The ECU boots the new software. |

Total: about **75 s** with an FTDI KKL cable, **84 s** with a J2534 interface (Scanmatik 2). The ME3.1
differs in some steps — see below.

### Security access (seed/key)

The ECU sends a 32-bit seed; the key is computed with a **32-bit Galois LFSR** (number of iterations taken
from the request parameter, polynomial chosen by security level). The per-ECU polynomial of this family is
`0x564A4F25` — the same in every software that was analysed (`SecurityAccess.cs`). The algorithm was recovered from the IROM and cross-checked with
the open-source NefMoto tools.

The hidden condition is the **immobiliser**: the request is only accepted if a status bit is set, and that
bit is set either when the key in the ignition was recognised during the current ignition cycle, or when
the ECU is **not paired** (virgin) — an unpaired ECU stores a special value in EEPROM. This is why a virgin
ECU can be flashed on the bench, while a paired one needs the car's key.

### The encryption

The programmer does not accept plain data (`dataFormat = 0x01`). Fiat's workshop tool (Examiner) sends the
image **encrypted** and the ECU decrypts every byte before programming it:

- a **24-byte key** stored in the programmer:
  `03 A2 C3 4C 45 FE 07 82 31 E2 23 D4 25 76 E7 08 B1 C2 F3 84 A5 D6 97 18` (`FiatCrypto.cs`);
- a running state that starts from the first key byte; each plain byte is
  `rotate-right(cipher XOR key[state mod 24], state mod 8)`, and the state advances by the cipher byte;
- the state restarts every **16 KB** (`0x4000` bytes).

This was confirmed by decrypting an original Examiner file, which produced exactly the software of the test
ECU, and by the checksum in the Examiner's parameter file (CRC-32 >> 16). The flasher encrypts any plain
image the same way, and can also **decrypt Examiner files** to a normal binary.

## Why it was hard

**1. "OBD flashing is impossible."** The ECU accepted the whole sequence up to the erase request, answered
"wait" (`7F 31 78`) and, 100 ms later, refused it (`7F 31 22`). The first reading of the firmware suggested
the copy of the programmer into RAM was failing. That was wrong: a RAM dump after the refusal showed the
programmer there, byte for byte. The refusal came **from the programmer itself**, which only accepts a
**full flash**: erase starting exactly at `0x800000`, and a single 512 KB encrypted download. Partial
flashing over OBD does not exist on these ECUs (only through BSL).

**2. The encryption.** The "special format" turned out to be the cipher above. It had to be located in the
programmer, understood, and validated against original Examiner files before a single byte could be
written.

**3. A strict order.** An internal state counter only allows session → security access → tester code →
date → programmer. Any step out of order is refused with codes that do not say why; only the disassembly
showed the rules.

**4. K-line timing.**
- The fast-init pulse must be accurate to the millisecond; Windows' `Thread.Sleep` jitter breaks it, so the
  tool busy-waits.
- The baud change happens only after the ECU's confirmation, still at 10400.
- The ECU closes the session after ~5 s of silence, so TesterPresent is sent every 2 s (some ECUs only
  accept the bare `3E` defined by Fiat).
- On J2534 interfaces the adapter waits a "line idle" time (P1max) after every response; with the default
  value the flash took 22 s longer, so the flasher uses 5 ms. Some ECUs (the ME3.1) leave more than 5 ms
  between bytes in long replies and the adapter then drops the whole reply; the flasher detects a reply lost
  this way, raises P1max to 20 ms for that connection and repeats the request if it only reads.
- After a baud change the flasher sends a TesterPresent at the new baud; if the ECU does not answer, it goes
  back to 10400 (some ECUs accept the session but keep their baud).
- When the ECU answers "wait" (`7F xx 78`, e.g. while writing the tester code to EEPROM) the flasher waits up
  to 5 s (P2*) for the final answer.

**5. The flash cannot be read over diagnostics.** `ReadMemoryByAddress` always denies the flash ranges and
`RequestUpload` does not exist, so the original software must come from elsewhere (BSL read or the
Examiner file).

**6. No application during the write.** Between the erase and the end of the download the ECU cannot run
the engine software. The flasher therefore runs an **access test** (session + security access, nothing
erased), validates the file size and checksum **before** erasing, and keeps the session alive throughout.

## ME3.1 variant (same hardware, other software)

Some 2.4 engines (e.g. Lancia Thesis 2.4 5C, Fiat Stilo 2.4) run **Bosch ME3.1** software on the **same
hardware** (C167, same flash). The cipher, the key and the seed/key algorithm are the same, but the
programmer and the diagnostic software are different, so the procedure changes. It was recovered from the
firmware (Stilo dump), checked against the Examiner files and then confirmed on a real ECU:

| | ME7.3.1 | ME3.1 |
|---|---|---|
| Security access | `27 01 PP` / `27 02 <key>` | `27 55 00` / `27 56 <key>` (another access level; `27 01` → `7F 27 12`) — same polynomial and iterations |
| Working baud in `10 85` | formula byte (`0x87` = 124800) | its own codes; 124800 is requested as exact 125000 with code 6 and 3 bytes: `10 85 06 01 E8 48` (`0x87` → `7F 10 31`) |
| Flash seen by the software at | `0x800000` | `0x000000` |
| Programmer | copied from flash into RAM | lives in the boot sector `0x0000–0x3FFF` |
| Erase | `31 02 800000 87FFFF` (everything, boot included) | `31 02 004000 07FFFF` (**boot/programmer kept**) |
| Download | `34 800000 01 080000` (512 KB) | `34 004000 01 07C000` (496 KB) |
| Verify | `31 01 … <cs>`, cs = CRC-32 **>> 16** | `31 01 … <cs>`, cs = CRC-32 **& 0xFFFF** |
| Identification block | near `0x10190` | near `0x80C6` |

Choose **ME3.1** in the flasher's ECU selector (for a flash, the selector follows the file, which tells the
variant by the position of the identification block). A 512 KB ME3.1 image read by BSL is accepted: only
`0x4000–0x7FFFF` is sent, the boot in the file is ignored. The encrypted stream the flasher produces from a BSL
read is byte-identical to the Examiner's `BO010302.BIN`, with the same checksum (`0xCDB2`).

Other differences seen on the ME3.1: it checks the exact length of every request (a wrong length is
`7F xx 12`), it answers `7F 3B 78` while writing the tester code to EEPROM (~2 s), and it leaves more than
5 ms between bytes in long replies (see the J2534 note above).

**Status: tested on a real ECU** (Stilo 2.4, HW 0261206655), with an FTDI KKL cable (**87 s**) and a
Scanmatik 2 (**127 s**, slower because of the longer P1max), including a flash of the Examiner original.
Converting an ME3.1 to ME7.3.1 software over OBD is not possible (the ME3.1 programmer never writes the
boot); that conversion is done by BSL.

## Examiner originals

Point the flasher at a complete Examiner folder (with `Global.ndb`). After connecting it reads the ECU
identification (`1A 92/94/95/91`: Bosch number, software, version, Fiat drawing) and looks it up in the
Examiner database, telling whether the software is current, whether an update exists, or whether the
software is not in the list (e.g. modified). A file is only offered if the flasher can write it: right size,
`.Prm` for the right flash area, and the `.Prm` checksum equal to the decrypted image's checksum (this proves
both the key and the file). Originals can also be searched by reference (Bosch number, software, Fiat
drawing, file name) and extracted decrypted, without an ECU.

## Validation status

- Tested on a real ECU: **Lancia Thesis** (ME7.3.1) and **Fiat Stilo 2.4** (ME3.1), each with an FTDI KKL
  cable and with a Scanmatik 2 (J2534).
- Analysed in firmware and identical (same programmer, cipher, polynomial and memory layout): Thesis 3.2,
  Stilo 1.2 / 1.4 (ME7.3H4), Alfa 147 2.0 / GTA 3.2, and the 2.4 versions that run ME7.3.1 software.
- The first ECU of each model should be flashed with care, with BSL recovery available.

## Requirements

- Ignition on, **engine stopped**, stable battery voltage (a charger is recommended).
- Immobiliser authorising (car key in the ignition) **or** a virgin ECU.
- A 512 KB image of the same ECU family (plain binary, BSL read or Examiner file; ME3.1 Examiner files are
  496 KB).
- K-line interface: FTDI-based KKL cable, or a J2534 interface with ISO 14230 support (32-bit driver).
