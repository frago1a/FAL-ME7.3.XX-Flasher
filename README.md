# FAL ME7.3.XX Flasher

**English** · [Português](README.pt.md)

OBD flasher for the **Bosch ME7.3.1** engine ECU family (Fiat, Alfa Romeo, Lancia), over the K-line
(KWP2000). A Windows desktop application (.NET 10, WPF) that reprograms the full 512 KB flash through the
diagnostic port, the same way the Fiat factory tool does.

> **Use at your own risk.** Flashing erases the ECU. An interruption after the erase leaves the ECU unable to
> start until it is reprogrammed; recovery may require the C167 bootstrap loader (opening the ECU). Read
> [docs/FLASHING.md](docs/FLASHING.md) before using it, use a stable power supply, and start with an ECU you
> can recover.

## What it does

- **Full OBD flash** of the ME7.3.1 family (512 KB), with the encryption and checksum the ECU requires.
- **Access test** (session + security access) that writes nothing, to check the ECU answers before erasing.
- **Keeps the ECU identification** (tester code and programming date) unless you choose to change it.
- **Decrypts/encrypts** Fiat workshop-tool images to and from a plain binary.
- Works with an **FTDI KKL cable** or a **J2534 interface** with ISO 14230 support (e.g. Scanmatik 2).

## Supported ECUs

Bosch ME7.3.1 as used by Fiat, Alfa Romeo and Lancia — validated on a Lancia Thesis and identical in firmware
across the family (Thesis 2.4/3.2, Stilo 1.2/1.4, Alfa 147 2.0/GTA 3.2). See
[docs/FLASHING.md](docs/FLASHING.md) for how it works and what was tested.

## Requirements

- Windows, **.NET 10 Desktop Runtime (x86)** — the app is x86 because the J2534 drivers are 32-bit.
- Ignition on, **engine stopped**, stable battery (a charger is recommended).
- The immobiliser authorising (car key in the ignition) **or** a virgin ECU.
- A 512 KB image of the same ECU family.

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
