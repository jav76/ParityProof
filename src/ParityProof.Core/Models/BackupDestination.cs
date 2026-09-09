namespace ParityProof.Core.Models;

public sealed record BackupDestination(
    string Id,
    string Name,
    string RootPath,
    bool IsEnabled = true,
    bool IsRequired = true);
