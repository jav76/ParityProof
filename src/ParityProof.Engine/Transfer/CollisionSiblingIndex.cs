using System;
using System.Collections.Generic;
using System.IO;

namespace ParityProof.Engine.Transfer;

// Finds the stem_N.ext siblings MediaCopier writes when a destination name is taken. Every sibling is returned,
// not just an unbroken run from _1, so a gap left by a deleted sibling cannot hide an identical copy further along.
// Each folder is listed once per copy batch, so a card whose names all collide does not rescan it for every file.
// Kept out of the [LogMethod]-woven MediaCopier because it runs once per folder entry, and the aspect would wrap
// and log every one of those calls.
internal sealed class CollisionSiblingIndex
{
    private readonly Dictionary<string, Dictionary<string, List<string>>> _siblingsByDirectory =
        new(StringComparer.Ordinal);

    // The listing can be older than the files it names, so each candidate's length is read again here.
    public List<string> FindSiblingsWithLength(string requestedPath, long length)
    {
        List<string> matches = new();
        string? directory = Path.GetDirectoryName(requestedPath);
        if (string.IsNullOrEmpty(directory))
        {
            return matches;
        }

        string requestedKey = string.Concat(
            Path.GetFileNameWithoutExtension(requestedPath.AsSpan()),
            Path.GetExtension(requestedPath.AsSpan()));
        if (!GetOrListDirectory(directory).TryGetValue(requestedKey, out List<string>? siblingPaths))
        {
            return matches;
        }

        foreach (string siblingPath in siblingPaths)
        {
            FileInfo siblingInfo = new(siblingPath);
            if (siblingInfo.Exists && siblingInfo.Length == length)
            {
                matches.Add(siblingPath);
            }
        }

        return matches;
    }

    public void RecordWrittenFile(string path)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)
            && _siblingsByDirectory.TryGetValue(directory, out Dictionary<string, List<string>>? siblingsByKey))
        {
            AddIfSibling(siblingsByKey, path);
        }
    }

    private Dictionary<string, List<string>> GetOrListDirectory(string directory)
    {
        if (_siblingsByDirectory.TryGetValue(directory, out Dictionary<string, List<string>>? cached))
        {
            return cached;
        }

        Dictionary<string, List<string>> siblingsByKey = new(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(directory))
        {
            EnumerationOptions options = new()
            {
                AttributesToSkip = 0,
            };

            foreach (string filePath in Directory.EnumerateFiles(directory, "*", options))
            {
                AddIfSibling(siblingsByKey, filePath);
            }
        }

        _siblingsByDirectory[directory] = siblingsByKey;
        return siblingsByKey;
    }

    // Indexes a stem_N.ext name (N all digits) under the stem.ext name it was derived from. Splitting at the last
    // underscore gives each name exactly one key, because N cannot itself contain an underscore.
    private static void AddIfSibling(Dictionary<string, List<string>> siblingsByKey, string filePath)
    {
        ReadOnlySpan<char> nameWithoutExtension = Path.GetFileNameWithoutExtension(filePath.AsSpan());
        int separatorIndex = nameWithoutExtension.LastIndexOf('_');
        if (separatorIndex < 0)
        {
            return;
        }

        ReadOnlySpan<char> suffix = nameWithoutExtension[(separatorIndex + 1)..];
        if (suffix.IsEmpty || suffix.ContainsAnyExceptInRange('0', '9'))
        {
            return;
        }

        string key = string.Concat(nameWithoutExtension[..separatorIndex], Path.GetExtension(filePath.AsSpan()));
        if (!siblingsByKey.TryGetValue(key, out List<string>? siblingPaths))
        {
            siblingPaths = new List<string>();
            siblingsByKey[key] = siblingPaths;
        }

        siblingPaths.Add(filePath);
    }
}
