using System;

namespace Airi.Services
{
    public sealed record FileSnapshot(
        string LibraryPath,
        string AbsolutePath,
        long SizeBytes,
        DateTime LastWriteUtc,
        DateTime CreatedUtc);
}
