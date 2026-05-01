using System.Text.Json.Serialization;

namespace NLink.Core.FileTransfer;

public abstract record FileTransferDataFrame
{
    public string Kind { get; init; } = FileTransferProtocol.Kind;

    public string Type { get; init; } = string.Empty;

    public string SessionId { get; init; } = string.Empty;

    public string TransferId { get; init; } = string.Empty;
}

public abstract record FileTransferChunkBatchFrame : FileTransferDataFrame
{
    private IReadOnlyList<byte[]> dataSegments = Array.Empty<byte[]>();

    public int StartChunkIndex { get; init; }

    public int ChunkCount { get; init; }

    public IReadOnlyList<byte[]> DataSegments
    {
        get => dataSegments;
        init => dataSegments = value ?? Array.Empty<byte[]>();
    }

    [JsonIgnore]
    public IReadOnlyList<string> DataBase64Segments
    {
        get => dataSegments.Select(static segment => Convert.ToBase64String(segment)).ToArray();
        init => dataSegments = value?.Select(static segment => string.IsNullOrWhiteSpace(segment)
                ? Array.Empty<byte>()
                : Convert.FromBase64String(segment))
            .ToArray() ?? Array.Empty<byte[]>();
    }
}
