using RDM.Core.Interfaces;
using RDM.Core.Models;
using TagFile = TagLib.File;

namespace RDM.Infrastructure.FileSystem;

public sealed class Id3TagReader : IMetadataReader
{
    public MetadataReaderKind Kind => MetadataReaderKind.Id3;

    public Task<AssetMetadata?> TryReadAsync(string filePath, CancellationToken ct = default)
    {
        try
        {
            using var file = TagFile.Create(filePath, TagLib.ReadStyle.Average);

            uint durationMs = file.Properties is not null
                ? (uint)file.Properties.Duration.TotalMilliseconds
                : 0;

            // Prefer TPE1 (lead performer), fall back to TPE2 (album artist)
            var artist = NullIfEmpty(file.Tag.FirstPerformer)
                      ?? NullIfEmpty(file.Tag.FirstAlbumArtist);

            var pic = file.Tag.Pictures.FirstOrDefault();

            return Task.FromResult<AssetMetadata?>(new AssetMetadata
            {
                Title           = NullIfEmpty(file.Tag.Title),
                Artist          = artist,
                Album           = NullIfEmpty(file.Tag.Album),
                Bpm             = file.Tag.BeatsPerMinute > 0
                                  ? (decimal)file.Tag.BeatsPerMinute
                                  : null,
                Year            = file.Tag.Year > 0 ? (int)file.Tag.Year : null,
                Genre           = NullIfEmpty(file.Tag.FirstGenre),
                DurationMs      = durationMs > 0 ? durationMs : null,
                PictureBytes    = pic?.Data?.Data,
                PictureMimeType = NullIfEmpty(pic?.MimeType)
            });
        }
        catch
        {
            return Task.FromResult<AssetMetadata?>(null);
        }
    }

    private static string? NullIfEmpty(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s;
}
