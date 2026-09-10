using System.Text.Json.Serialization;

namespace ClipBridge.Models;

public enum ClipKind { Text = 0, Image = 1, Files = 2 }

public sealed class ClipFile
{
    public string Name { get; set; } = "";
    public string LocalPath { get; set; } = "";
    public long Size { get; set; }
    /// <summary>Only populated while the file travels over sync.</summary>
    public byte[]? Data { get; set; }
}

public sealed class ClipItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public ClipKind Kind { get; set; }
    /// <summary>Text content; OCR text for images; file names for file items.</summary>
    public string Text { get; set; } = "";
    public string? Html { get; set; }
    public string? Rtf { get; set; }
    /// <summary>PNG bytes. Not kept in memory for the cached list; loaded on demand and sent over sync.</summary>
    public byte[]? ImagePng { get; set; }
    public byte[]? Thumb { get; set; }
    public int ImageWidth { get; set; }
    public int ImageHeight { get; set; }
    public List<ClipFile> Files { get; set; } = new();
    public string Hash { get; set; } = "";
    public long CreatedUtc { get; set; }
    public long ModifiedUtc { get; set; }
    public int Version { get; set; } = 1;
    public string Origin { get; set; } = "";
    public bool Pinned { get; set; }
    public int PinOrder { get; set; }
    public bool Deleted { get; set; }
    public bool OcrDone { get; set; }
    public long SizeBytes { get; set; }

    [JsonIgnore]
    public bool FilesTransferred => Kind == ClipKind.Files && Files.Count > 0 && Files.All(f => !string.IsNullOrEmpty(f.LocalPath) && File.Exists(f.LocalPath));

    public ClipItem CloneMeta() => new()
    {
        Id = Id, Kind = Kind, Text = Text, Hash = Hash, CreatedUtc = CreatedUtc, ModifiedUtc = ModifiedUtc,
        Version = Version, Origin = Origin, Pinned = Pinned, PinOrder = PinOrder, Deleted = Deleted,
        OcrDone = OcrDone, SizeBytes = SizeBytes, ImageWidth = ImageWidth, ImageHeight = ImageHeight,
    };

    public static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
