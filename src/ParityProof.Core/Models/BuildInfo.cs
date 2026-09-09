using System;
using System.Reflection;

namespace ParityProof.Core.Models;

public sealed record BuildInfo
{
    private const string DEFAULT_VERSION = "1.0.0-dev";
    private const string UNKNOWN_SHA = "unknown";
    private const int SHA_PREFIX_LENGTH = 7;

    private static readonly Lazy<BuildInfo> _current = new(ResolveBuildInfo);

    public static BuildInfo Current => _current.Value;

    public string SemVer { get; init; } = DEFAULT_VERSION;

    public string CommitSha { get; init; } = UNKNOWN_SHA;

    public string DisplayVersion { get; init; } = $"ParityProof v{DEFAULT_VERSION}";

    public bool IsDebug { get; init; }

    public static BuildInfo ResolveBuildInfo()
    {
        Assembly assembly = typeof(BuildInfo).Assembly;
        AssemblyInformationalVersionAttribute? infoVersionAttribute =
            assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();

        bool isDebug = false;
#if DEBUG
        isDebug = true;
#endif

        return Parse(infoVersionAttribute?.InformationalVersion, isDebug);
    }

    public static BuildInfo Parse(string? rawVersion, bool isDebug = false)
    {
        string normalized = rawVersion?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalized))
        {
            normalized = DEFAULT_VERSION;
        }

        string semVer = DEFAULT_VERSION;
        string commitSha = UNKNOWN_SHA;

        int plusIndex = normalized.IndexOf('+');
        if (plusIndex >= 0)
        {
            semVer = normalized[..plusIndex];
            string rawSha = normalized[(plusIndex + 1)..];
            commitSha = rawSha.Length > SHA_PREFIX_LENGTH
                ? rawSha[..SHA_PREFIX_LENGTH]
                : rawSha;
        }
        else
        {
            semVer = normalized;
        }

        if (string.IsNullOrWhiteSpace(semVer))
        {
            semVer = DEFAULT_VERSION;
        }

        if (string.IsNullOrWhiteSpace(commitSha))
        {
            commitSha = UNKNOWN_SHA;
        }

        string displayVersion = commitSha != UNKNOWN_SHA
            ? $"ParityProof v{semVer} ({commitSha})"
            : $"ParityProof v{semVer}";

        return new()
        {
            SemVer = semVer,
            CommitSha = commitSha,
            DisplayVersion = displayVersion,
            IsDebug = isDebug
        };
    }
}
