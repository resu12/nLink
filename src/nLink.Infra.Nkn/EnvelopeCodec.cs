using System.Text.Json;

namespace NLink.Infra.Nkn;

internal static class EnvelopeCodec
{
    private const int CurrentVersion = 1;

    public static byte[] Serialize(Envelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        var dto = new EnvelopeDto
        {
            v = envelope.Version,
            c = envelope.Code ?? string.Empty,
            id = envelope.MessageId ?? string.Empty,
            t = envelope.Type.ToString(),
            p = Convert.ToBase64String(envelope.Payload ?? Array.Empty<byte>()),
            ts = envelope.UnixTimeMs,
            r = envelope.ReplyTo,
        };

        return JsonSerializer.SerializeToUtf8Bytes(dto);
    }

    public static bool TryDeserialize(byte[] data, out Envelope env)
    {
        env = default!;

        if (data is null || data.Length == 0)
        {
            return false;
        }

        try
        {
            var dto = JsonSerializer.Deserialize<EnvelopeDto>(data);
            if (dto is null)
            {
                return false;
            }

            if (dto.v <= 0 || dto.v > CurrentVersion)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(dto.c) || string.IsNullOrWhiteSpace(dto.id) || string.IsNullOrWhiteSpace(dto.t))
            {
                return false;
            }

            if (!Enum.TryParse<MsgType>(dto.t, ignoreCase: true, out var type))
            {
                return false;
            }

            byte[] payload;
            try
            {
                payload = string.IsNullOrEmpty(dto.p) ? Array.Empty<byte>() : Convert.FromBase64String(dto.p);
            }
            catch (FormatException)
            {
                return false;
            }

            env = new Envelope(
                Version: dto.v,
                Code: dto.c.Trim(),
                MessageId: dto.id.Trim(),
                Type: type,
                Payload: payload,
                UnixTimeMs: dto.ts,
                ReplyTo: string.IsNullOrWhiteSpace(dto.r) ? null : dto.r.Trim());

            return true;
        }
        catch
        {
            return false;
        }
    }

    private sealed class EnvelopeDto
    {
        public int v { get; set; }
        public string? c { get; set; }
        public string? id { get; set; }
        public string? t { get; set; }
        public string? p { get; set; }
        public long ts { get; set; }
        public string? r { get; set; }
    }
}
