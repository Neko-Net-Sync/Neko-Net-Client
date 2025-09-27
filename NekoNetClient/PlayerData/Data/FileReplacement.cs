/*
   Neko-Net Client — PlayerData.Data.FileReplacement
   -------------------------------------------------
   Purpose
   - Represents a resolved file replacement mapping from in-game paths to a local or swap path, with
     helper conversion to API DTO.
*/
using NekoNet.API.Data;
using System.Text.RegularExpressions;

namespace NekoNetClient.PlayerData.Data;

/// <summary>
/// Represents a resolved file replacement: a set of game paths mapping to a resolved path (local or swap).
/// </summary>
public partial class FileReplacement
{
    /// <summary>
    /// Creates a new file replacement with normalized, lower-cased game paths and a normalized resolved path.
    /// </summary>
    public FileReplacement(string[] gamePaths, string filePath)
    {
        GamePaths = gamePaths.Select(g => g.Replace('\\', '/').ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        ResolvedPath = filePath.Replace('\\', '/');
    }

    /// <summary>Set of affected game paths (lower-cased).</summary>
    public HashSet<string> GamePaths { get; init; }

    /// <summary>Indicates whether this replacement refers to a different path than the resolved one.</summary>
    public bool HasFileReplacement => GamePaths.Count >= 1 && GamePaths.Any(p => !string.Equals(p, ResolvedPath, StringComparison.Ordinal));

    /// <summary>Content hash for non-swap files (empty for unresolved).</summary>
    public string Hash { get; set; } = string.Empty;
    /// <summary>True if this is a file swap (no local paths involved).</summary>
    public bool IsFileSwap => !LocalPathRegex().IsMatch(ResolvedPath) && GamePaths.All(p => !LocalPathRegex().IsMatch(p));
    /// <summary>Resolved local or swap path.</summary>
    public string ResolvedPath { get; init; }

    /// <summary>
    /// Converts this replacement into API DTO form, including swap path when applicable.
    /// </summary>
    public FileReplacementData ToFileReplacementDto()
    {
        return new FileReplacementData
        {
            GamePaths = [.. GamePaths],
            Hash = Hash,
            FileSwapPath = IsFileSwap ? ResolvedPath : string.Empty,
        };
    }

    /// <inheritdoc />
    public override string ToString()
    {
        return $"HasReplacement:{HasFileReplacement},IsFileSwap:{IsFileSwap} - {string.Join(",", GamePaths)} => {ResolvedPath}";
    }

    [GeneratedRegex(@"^[a-zA-Z]:(/|\\)", RegexOptions.ECMAScript)]
    private static partial Regex LocalPathRegex();
}