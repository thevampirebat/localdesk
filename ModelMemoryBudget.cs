namespace LocalDesk;

internal sealed record ModelMemoryBudget(decimal RamGiB, decimal VramGiB)
{
    const long GiB = 1024L * 1024 * 1024;

    public bool Enabled => RamGiB > 0 || VramGiB > 0;

    public string? Rejection(ResidentMemory? memory)
    {
        if (!Enabled) return null;
        if (memory?.VramBytes is not { } gpu || gpu < 0 || gpu > memory.Bytes)
            return "Ollama could not report this model's memory allocation. Try again or clear the model memory budgets in Settings.";

        var ram = (memory.Bytes - gpu) / (decimal)GiB;
        var vram = gpu / (decimal)GiB;
        if ((RamGiB > 0 && ram > RamGiB) || (VramGiB > 0 && vram > VramGiB))
            return $"Model exceeds the memory budget ({ram:F1} GiB RAM, {vram:F1} GiB VRAM). Choose a smaller model or lower its context in Profile.";
        return null;
    }
}

internal sealed class ModelMemoryBudgetException(string message) : InvalidOperationException(message);
