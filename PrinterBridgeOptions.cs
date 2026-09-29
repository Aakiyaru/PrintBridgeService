namespace ZplPrintBridge;

public class PrintBridgeOptions
{
    public const string SectionName = "PrintBridge";

    /// <summary>
    /// Маски имён принтеров (регистронезависимо, поддерживаются * и ?).
    /// Первый совпавший принтер используется для печати.
    /// Пример: ["*Zebra*", "*BSmart*"]
    /// </summary>
    public string[] PrinterNamePatterns { get; set; } = new[] { "*Zebra*", "*BSmart*", "*4BARCODE*" };

    /// <summary>
    /// Если задано — используется точное имя, маски игнорируются.
    /// Оставьте null/пусто, чтобы искать по маске.
    /// </summary>
    public string? PrinterName { get; set; }

    public int Port { get; set; } = 9100;
    public string ListenAddress { get; set; } = "0.0.0.0";
    public bool SendAck { get; set; } = true;

    /// <summary>
    /// Переискивать принтер при каждой печати (true) или кэшировать (false).
    /// При false — при ошибке 1801 всё равно будет один повторный поиск.
    /// </summary>
    public bool ResolveOnEveryPrint { get; set; } = false;
}