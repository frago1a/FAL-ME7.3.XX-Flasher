# FAL ME7.3.1/H4/ME3.1 Flasher

**English** · [Português](README.pt.md)

Flasher for the **Bosch ME7.3.1 / H4 / ME3.1** engine ECUs (Fiat, Alfa Romeo, Lancia). A Windows desktop
application (.NET 10, WPF) that reprograms the flash **over OBD** (K-line, KWP2000), the same way the Fiat
factory tool does, and reads and writes the flash and EEPROM in **boot mode (BSL)** on the bench.

> **Use at your own risk.** Flashing erases the ECU. An interruption after the erase leaves the ECU unable to
> start until it is reprogrammed. Recovery is done in boot mode (BSL), which this tool also does: on this
> family it is entered through a pin on the connector — no need to open the ECU. Read
> [docs/FLASHING.md](docs/FLASHING.md) before using it, use a stable power supply, and start with an ECU you
> can recover.

## What it does

- **Full OBD flash** of the ME7.3.1 (512 KB) and the ME3.1 (496 KB, the boot/programmer is kept), with the
  encryption and checksum the ECU requires, at 124800 baud (about 75–90 s with an FTDI cable).
- **ECU selector** (ME7.3.xx / ME3.1): each family has its own security access and baud request, so the
  flasher goes straight to the right one. For a flash it follows the file.
- **Access test** (session + security access) that writes nothing, to check the ECU answers before erasing.
- **Keeps the ECU identification** (tester code and programming date) unless you choose to change it.
- **Decrypts/encrypts** Fiat workshop-tool images to and from a plain binary.
- **Finds the original software** in a Fiat workshop-tool (Examiner) folder by the ECU identification: tells
  whether the software is current or an update exists, and can use the original for the flash or extract it
  decrypted. Originals can also be searched by Bosch number, software or Fiat drawing, without an ECU.
- Works with an **FTDI KKL cable** or a **J2534 interface** with ISO 14230 support (e.g. Scanmatik 2). Shows
  the battery voltage when the interface can measure it.

### Boot mode (BSL)

With the boot pin grounded at power-up and a KKL cable (FTDI or serial) on the K-line, at 125000 baud:

- **Identification** of the flash (hardware, software, version, Fiat drawing, family) in under a second.
- **Full backup**: external flash (512 KB), internal ROM (32 KB) and EEPROM 95080 (1 KB) in about 54 s, saved
  with the software in the file names and an info file with CRC-32.
- **Flash write** (Am29F400BB only): compare with a read you already have, read the ECU and compare, or write
  everything; only the sectors that change are erased, a blank boot sector in the file is never written, and the
  written sectors are verified (optional).
- **EEPROM 95080 write** (only the bytes that change, or all) with optional verification.
- **Restore a full backup** (flash + EEPROM) in one go. The internal ROM is OTP and is not written.
- The Examiner originals also work in boot mode: look up by the identification read in BSL and write the
  original directly (it is decrypted in memory).
- Unlike OBD, boot mode does not depend on the immobiliser.

## Supported ECUs

| ECU | Cars | Tested on a real ECU | Flash time (FTDI / Scanmatik 2) |
|---|---|---|---|
| Bosch **ME7.3.1** | Lancia Thesis, Alfa 147 2.0 / GTA 3.2, 2.4 versions with ME7.3.1 software | Lancia Thesis 2.0 | ~75 s / ~84 s |
| Bosch **ME7.3H4** (ME7.3.1 without MAF) | Fiat / Lancia 1.2 and 1.4 16V | — (firmware checked: same programmer) | as ME7.3.1 |
| Bosch **ME3.1** | Lancia Thesis 2.4 5C, Fiat Stilo 2.4 | Fiat Stilo 2.4 | ~87 s / ~127 s |

The ME7.3.1 and H4 models listed were checked in firmware (same programmer, cipher and memory layout). See
[docs/FLASHING.md](docs/FLASHING.md) for how it works, the differences between the two families and what was
tested.

## What's new in 1.3.0

- **Boot mode (BSL)**: identification, full backup, flash and EEPROM write, backup restore — tested on an ME3.1
  (Fiat Stilo 2.4) and an ME7.3.1 (Lancia Thesis 2.0); reads identical byte for byte to a commercial tool.
- **OBD identification on the ME3.1** fixed: with requests sent right after each answer the ECU sometimes sent
  the previous answer again (the software showed the hardware number). Late answers are now discarded.
- A clear message when the ECU refuses to erase because the immobiliser is not authorising.
- New name: FAL ME7.3.1/H4/ME3.1 Flasher. The progress bar is red while writing.

1.2.0: ME3.1 tested on a real ECU (security access `27 55`, its own 124800 baud request), ECU selector, J2534
fixes for ECUs with long gaps between bytes.

## Download

Get the latest build from [Releases](https://github.com/frago1a/FAL-ME7.3.XX-Flasher/releases): unzip and run
`FAL-ME7.3.XX-Flasher.exe`. It is self-contained — no .NET installation needed.

## Requirements

- Windows (x86 build, because the J2534 drivers are 32-bit). Building from source needs the .NET 10 SDK.
- Ignition on, **engine stopped**, stable battery (a charger is recommended).
- OBD: the immobiliser authorising (car key in the ignition) **or** a virgin ECU. BSL: the boot pin and a KKL
  cable (the J2534 cannot do boot mode).
- A 512 KB image of the same ECU family (plain, BSL read or Examiner file; ME3.1 Examiner files are 496 KB).

## Build

```
dotnet build FAL-ME73-Flasher.sln -c Release
```

Run the tests:

```
dotnet test FAL-ME73-Flasher.sln
```

## Documentation

- [docs/FLASHING.md](docs/FLASHING.md) — how OBD flashing works on this ECU: procedure, security access,
  encryption, verification, constraints.

## License

[GPL-3.0](LICENSE). The procedure, the security algorithm and the encryption were obtained by reverse
engineering for interoperability. Boot mode uses the Minimon (Infineon) loader and kernel and the flash and
EEPROM drivers from the C167BootTool project (GPL-3.0); see `src/ME7Tool.Core/Bsl/Resources/NOTICE.txt`.
