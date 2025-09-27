//
// Neko-Net Client — MareCharaFileDataFactory
// Purpose: Small factory to materialize MareCharaFileData instances with access to the
//          FileCacheManager for resolving cached file sizes and paths when serializing MCDF.
//
using NekoNet.API.Data;
using NekoNetClient.FileCache;
using NekoNetClient.Services.CharaData.Models;

namespace NekoNetClient.Services.CharaData;

/// <summary>
/// Constructs <see cref="MareCharaFileData"/> with access to the cache manager so file
/// metadata and sizes can be embedded when writing MCDF files.
/// </summary>
public sealed class MareCharaFileDataFactory
{
    private readonly FileCacheManager _fileCacheManager;

    /// <summary>
    /// Creates a new factory using the provided cache manager.
    /// </summary>
    public MareCharaFileDataFactory(FileCacheManager fileCacheManager)
    {
        _fileCacheManager = fileCacheManager;
    }

    /// <summary>
    /// Builds a <see cref="MareCharaFileData"/> from the given description and captured
    /// <see cref="CharacterData"/> snapshot.
    /// </summary>
    public MareCharaFileData Create(string description, CharacterData characterCacheDto)
    {
        return new MareCharaFileData(_fileCacheManager, description, characterCacheDto);
    }
}