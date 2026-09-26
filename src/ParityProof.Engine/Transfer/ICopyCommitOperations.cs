using System.IO;

namespace ParityProof.Engine.Transfer;

// The steps that turn a written temp file into a durable backup. For each destination MediaCopier calls them in
// this order, and a file counts as copied only after all four succeed: FlushToDisk, ReadBackHeadTail, Rename,
// FlushDirectory. FlushDirectory also runs on the parent of each folder the copier creates, before anything is
// written below it. Tests substitute this to check that order and the failure paths.
internal interface ICopyCommitOperations
{
    void FlushToDisk(FileStream tempStream);

    (ulong HeadHash, ulong TailHash) ReadBackHeadTail(string tempPath);

    // Must never replace an existing file.
    void Rename(string tempPath, string finalPath);

    void FlushDirectory(string directoryPath);
}
