using ME7Tool.Core.Kwp2000;

namespace ME7Tool.Core.Flashing;

/// <summary>Fase atual do processo de flash (para a UI/progresso).</summary>
public enum FlashPhase
{
    Connecting,
    Unlocking,
    Erasing,
    Writing,
    Verifying,
    Finalizing,
    Done,
}

/// <summary>Evento de progresso do flash.</summary>
public readonly record struct FlashProgress(FlashPhase Phase, long Current, long Total, string Message)
{
    public double Percent => Total > 0 ? 100.0 * Current / Total : 0;
}

/// <summary>
/// Opções de flash. Sequência por zona contígua (spec Fiat 07223 §10–11):
/// 31 02 (apagar) → 33 02 até concluir → 34 (requestDownload) → 36… (transferData) → 37 →
/// [31 01 checksum → 33 01] → no fim 3B 99/98 (data/tester code).
/// </summary>
public sealed class FlashOptions
{
    /// <summary>Máximo de bytes de dados por TransferData (0x36). Limitado também pelo
    /// maxNumberOfBlockLength que a ECU devolve no 0x34.</summary>
    public int TransferBlockSize { get; set; } = 0xFE; // o programador da Fiat diz 74 FD → 252 bytes de dados

    /// <summary>dataFormatIdentifier no RequestDownload: 0x00 = sem compressão, sem cifra (spec §11.1.2).</summary>
    public byte DataFormat { get; set; } = 0x00;

    /// <summary>Contador de sequência no TransferData (estilo UDS). Fiat KWP2000: não usa.</summary>
    public bool UseBlockSequenceCounter { get; set; } = false;

    // --- Apagar (31 02) ---

    /// <summary>Apagar cada zona (rotina 0x02) antes de a escrever.</summary>
    public bool EraseBeforeWrite { get; set; } = true;

    /// <summary>Endereço de fim no 31 02/31 01 é inclusivo (último byte) — convenção KWP. false = exclusivo.</summary>
    public bool StopAddressInclusive { get; set; } = true;

    /// <summary>Tempo máximo à espera do fim de uma rotina (apagar/checksum).</summary>
    public int RoutineTimeoutMs { get; set; } = 60_000;

    /// <summary>Intervalo entre pedidos 33 enquanto a ECU responde routineNotComplete/busy (e espera antes do 1.º).</summary>
    public int RoutinePollMs { get; set; } = 250;

    /// <summary>
    /// Timeout da resposta ao 31 (start routine). O primeiro 31 responde 7F 31 78 e a ECU passa da
    /// aplicação para o programador da IROM antes do 71 — precisa de mais tempo que um pedido normal.
    /// </summary>
    public int RoutineStartTimeoutMs { get; set; } = 5000;

    /// <summary>Pausa depois do 74 antes do primeiro 36 (a IROM arma a escrita no ciclo seguinte).</summary>
    public int DownloadSettleMs { get; set; } = 20;

    /// <summary>Tentativas por bloco 36 quando a ECU responde ocupada (NRC 0x23/0x21).</summary>
    public int TransferRetries { get; set; } = 5;

    /// <summary>
    /// Se a ECU recusar a passagem ao programador (31 → 7F 31 22), corre logo o diagnóstico só de leitura
    /// (<see cref="HandoverDiagnostic"/>) enquanto a sessão ainda está viva — passados ~10 s a ECU deixa de
    /// responder. O relatório chega por <see cref="Flasher.OnHandoverDiagnostic"/>.
    /// </summary>
    public bool DiagnoseOnHandoverFailure { get; set; } = true;

    /// <summary>
    /// Espera entre a identificação (3B 99) e o primeiro apagar (31 02), com TesterPresent a cada segundo.
    /// 0 = sem pausa. Dá tempo à ECU para acabar de gravar a EEPROM antes da passagem ao programador.
    /// </summary>
    public int PauseBeforeEraseMs { get; set; }

    /// <summary>
    /// Procedimento Fiat de passagem ao programador com a IGNIÇÃO DESLIGADA. Pelo firmware (Alfa anotada):
    /// o 31 mantém o relé principal (b_temp_um.1 → P3.7) e a passagem limpa (interrupções desligadas, teste
    /// da RAM, reinício no programador) corre no pós-funcionamento, que só existe com a KL15 desligada.
    /// Com esta opção o flasher pede ao utilizador para desligar a ignição antes do 1.º 31 (mantendo a
    /// ECU acordada com TesterPresent) e, se a ECU deixar de responder (reiniciou no programador), pede para
    /// voltar a ligar a ignição, volta a ligar-se e continua. Requer <see cref="Flasher.Instruct"/>.
    /// </summary>
    public bool IgnitionOffHandover { get; set; }

    /// <summary>Timeout da resposta ao 1.º 31 com a ignição desligada (a ECU pode reiniciar no programador).</summary>
    public int HandoverTimeoutMs { get; set; } = 15000;

    /// <summary>
    /// Procedimento Fiat em 2 passos, como no fluxo do Examiner (Flash.sm: PROGRSTEP1 → KeyOFF-ON →
    /// PROGRSTEP2, "Pwr-ltch a 60sec."): passo 1 com a chave ON (0x85, seed/key, 3B 98/99) e fim da
    /// comunicação; chave OFF SEM comunicação durante <see cref="KeyOffWaitSeconds"/> (power latch da ECU);
    /// chave ON, ligação nova (key bytes DF 8F = só BootROM; EF 8F = aplicação ou programador; a leitura de DTC distingue-os) e passo 2. Ver
    /// <see cref="Flasher.Instruct"/>.
    /// </summary>
    public bool TwoStepKeyCycle { get; set; }

    /// <summary>
    /// Reescreve o tester code e a data de programação que já estão na ECU (lidos antes da sessão 0x85),
    /// em vez dos indicados. A ECU obriga a escrevê-los antes de apagar; assim ficam iguais aos anteriores.
    /// </summary>
    public bool KeepEcuIdentification { get; set; }

    /// <summary>Tempo com a chave OFF entre os dois passos (power latch). Examiner: até 60 s.</summary>
    public int KeyOffWaitSeconds { get; set; } = 60;

    // --- Verificar (31 01) ---

    /// <summary>
    /// Pede à ECU a rotina de verificação (0x01) sobre cada zona escrita, mandando o CRC-32 esperado.
    /// O algoritmo (CRC-32 poly 0xEDB88320) foi confirmado na desmontagem da IROM (sub_5BE6), por isso
    /// deixou de ser experimental. É o único meio de confirmar a escrita (não há leitura/upload de
    /// flash nestas ECUs). Por omissão fica desligado; uma falha aqui é sinal real de escrita má.
    /// </summary>
    public bool VerifyChecksum { get; set; } = false;

    /// <summary>Se a verificação por CRC-32 falhar: true = erro (pára), false = só aviso.</summary>
    public bool ChecksumFailureIsError { get; set; } = false;

    // --- Segurança da operação ---

    /// <summary>
    /// Nunca escrever o sector de boot (offset 0x0000–0x3FFF). Num flash completo é saltado; num
    /// parcial com diferenças lá, o flash é recusado (desligar esta opção para o escrever).
    /// </summary>
    public bool ProtectBootSector { get; set; } = true;

    // --- SecurityAccess ---
    public byte SeedSubFunction { get; set; } = 0x01;
    public byte SeedParameter { get; set; } = 0x00;
    public uint PerEcuPoly { get; set; } = SecurityAccess.PerEcuPolyThesis;

    public Kwp2000SessionType ProgrammingSession { get; set; } = Kwp2000SessionType.Programming;

    /// <summary>Baud de trabalho pedido no StartDiagnosticSession 0x85 (0 = fica a 10400).</summary>
    public int WorkingBaud { get; set; }

    /// <summary>Negociar timings rápidos (0x83) depois de entrar na sessão (ignora falhas).</summary>
    public bool NegotiateTiming { get; set; } = true;
}
