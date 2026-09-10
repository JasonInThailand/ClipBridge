using ClipBridge.Models;

namespace ClipBridge.Sync;

public sealed class ManifestEntry
{
    public string Id { get; set; } = "";
    public int V { get; set; }
    public bool D { get; set; }
}

/// <summary>Envelope for every sync frame. T = hello | manifest | item | meta | need | ping.</summary>
public sealed class Msg
{
    public string T { get; set; } = "";
    public int Ver { get; set; } = 1;
    public string? Machine { get; set; }
    public ClipItem? Item { get; set; }
    public List<ManifestEntry>? Manifest { get; set; }
    public List<string>? Ids { get; set; }
}
