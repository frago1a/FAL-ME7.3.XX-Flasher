# FAL ME7.3.XX Flasher

**English** · [Português](README.pt.md)

OBD flasher for the **Bosch ME7.3.1 and ME3.1** engine ECUs (Fiat, Alfa Romeo, Lancia), over the K-line
(KWP2000). A Windows desktop application (.NET 10, WPF) that reprograms the flash through the diagnostic
port, the same way the Fiat factory tool does.

> **Use at your own risk.** Flashing erases the ECU. An interruption after the erase leaves the ECU unable to
> start until it is reprogrammed. Recovery needs the C167 bootstrap loader (BSL), which on this family is
> entered through a pin on the connector — no need to open the ECU. Read
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

The flasher does one thing: flash the ECU (and decrypt Examiner files). Image patches, such as enabling RAM
writes (0x3D), are no longer part of it.

## Supported ECUs

| ECU | Cars | Tested on a real ECU | Flash time (FTDI / Scanmatik 2) |
|---|---|---|---|
| Bosch **ME7.3.1** | Lancia Thesis, Fiat Stilo 1.2/1.4, Alfa 147 2.0 / GTA 3.2, 2.4 versions with ME7.3.1 software | Lancia Thesis 2.0 | ~75 s / ~84 s |
| Bosch **ME3.1** | Lancia Thesis 2.4 5C, Fiat Stilo 2.4 | Fiat Stilo 2.4 | ~87 s / ~127 s |

The ME7.3.1 models listed were checked in firmware (same programmer, cipher and memory layout). See
[docs/FLASHING.md](docs/FLASHING.md) for how it works, the differences between the two families and what was
tested.

## What's new in 1.2.0

- **ME3.1 tested on a real ECU** and no longer experimental: its own security access level (`27 55`), its own
  baud request (124800 instead of 10400 → ~6× faster), and it waits for the ECU while it writes the tester code
  to EEPROM.
- **ECU selector** (ME7.3.xx / ME3.1), no more failed attempts.
- **J2534 (Scanmatik)**: ECUs that leave long gaps between bytes (ME3.1) no longer lose their answers; after a
  baud change the link is checked and falls back to 10400 if the ECU did not switch.
- The tool disconnects cleanly after a successful flash.
- The RAM-writing (0x3D) image patch was removed from the flasher.

## Download

Get the latest build from [Releases](https://github.com/frago1a/FAL-ME7.3.XX-Flasher/releases): unzip and run
`FAL-ME7.3.XX-Flasher.exe`. It is self-contained — no .NET installation needed.

## Requirements

- Windows (x86 build, because the J2534 drivers are 32-bit). Building from source needs the .NET 10 SDK.
- Ignition on, **engine stopped**, stable battery (a charger is recommended).
- The immobiliser authorising (car key in the ignition) **or** a virgin ECU.
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
engineering for interoperability.
