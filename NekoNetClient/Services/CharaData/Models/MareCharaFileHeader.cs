//
// Neko-Net Client — MareCharaFileHeader
// Purpose: Binary container header for Mare Chara Data Files (MCDF). Encapsulates versioning and
//          JSON data length for <see cref="MareCharaFileData"/>. Provides read/write helpers.
//
namespace NekoNetClient.Services.CharaData.Models;

/// <summary>
/// Represents the MCDF header. The binary format is:
/// 'M''C''D''F' [byte version] [int32 jsonLength] [jsonPayload]
/// followed by the raw file payload bytes written separately by the caller.
/// </summary>
public record MareCharaFileHeader(byte Version, MareCharaFileData CharaFileData)
{
    public static readonly byte CurrentVersion = 1;

    public byte Version { get; set; } = Version;
    public MareCharaFileData CharaFileData { get; set; } = CharaFileData;
    public string FilePath { get; private set; } = string.Empty;

    /// <summary>Writes the MCDF header and embedded JSON metadata to the provided writer.</summary>
    public void WriteToStream(BinaryWriter writer)
    {
        writer.Write('M');
        writer.Write('C');
        writer.Write('D');
        writer.Write('F');
        writer.Write(Version);
        var charaFileDataArray = CharaFileData.ToByteArray();
        writer.Write(charaFileDataArray.Length);
        writer.Write(charaFileDataArray);
    }

    /// <summary>
    /// Reads and decodes the MCDF header and JSON metadata. On success, returns a header with
    /// the original file path recorded for later extraction.
    /// </summary>
    public static MareCharaFileHeader? FromBinaryReader(string path, BinaryReader reader)
    {
        var chars = new string(reader.ReadChars(4));
        if (!string.Equals(chars, "MCDF", StringComparison.Ordinal)) throw new InvalidDataException("Not a Mare Chara File");

        MareCharaFileHeader? decoded = null;

        var version = reader.ReadByte();
        if (version == 1)
        {
            var dataLength = reader.ReadInt32();

            decoded = new(version, MareCharaFileData.FromByteArray(reader.ReadBytes(dataLength)))
            {
                FilePath = path,
            };
        }

        return decoded;
    }

    /// <summary>
    /// Advances a reader past the header and JSON metadata to the start of the raw payload data.
    /// </summary>
    public static void AdvanceReaderToData(BinaryReader reader)
    {
        reader.ReadChars(4);
        var version = reader.ReadByte();
        if (version == 1)
        {
            var length = reader.ReadInt32();
            _ = reader.ReadBytes(length);
        }
    }
}