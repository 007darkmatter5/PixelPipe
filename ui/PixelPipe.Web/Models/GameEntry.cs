namespace PixelPipe.Web.Models;

public sealed class GameEntry
{
    public required string RelativePath { get; init; }
    public required string Title { get; init; }
    public required string System { get; init; }
    public required string SystemId { get; init; }
    public required string Crc32 { get; init; }
    public string? RomCrc32 { get; init; }
    public string ContentHash { get; init; } = "";
    public string? BoxArtFile { get; init; }
    public int? Year { get; init; }
    public string? Developer { get; init; }
    public string? Description { get; init; }
    public long Bytes { get; init; }

    public string BoxArtUrl => "/media/boxart/" + string.Join('/', RelativePath.Split('/').Select(Uri.EscapeDataString));
    public string PlayHref => "/play/" + string.Join('/', RelativePath.Split('/').Select(Uri.EscapeDataString));
    public string DetailHref => "/game/" + string.Join('/', RelativePath.Split('/').Select(Uri.EscapeDataString));
}

public sealed class LibrarySnapshot
{
    public required string RomsPath { get; init; }
    public required IReadOnlyList<GameEntry> Games { get; init; }
    public required DateTimeOffset ScannedAt { get; init; }
}
