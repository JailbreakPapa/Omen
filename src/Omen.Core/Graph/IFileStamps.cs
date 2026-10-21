// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

namespace Omen.Core.Graph;

/// <summary>A file's last write time (UTC), or null when it does not exist.</summary>
public interface IFileStamps
{
    DateTime? Get(string path);
}
