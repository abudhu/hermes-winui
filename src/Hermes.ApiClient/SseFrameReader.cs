using System.IO;
using System.Runtime.CompilerServices;
using System.Text;

namespace Hermes.ApiClient;

/// <summary>
/// Minimal Server-Sent Events frame reader (W3C SSE — `event:`, `data:`, `id:`,
/// blank-line frame separators, lines starting with `:` are comments).
///
/// Why hand-rolled instead of a library: SSE is ~70 lines and our needs are
/// modest (single connection, one frame at a time, cancellation must be clean).
/// </summary>
internal static class SseFrameReader
{
    public readonly struct Frame
    {
        public string? Event { get; init; }
        public string Data { get; init; }
        public string? Id { get; init; }
    }

    public static async IAsyncEnumerable<Frame> ReadAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(stream);
        var data = new StringBuilder();
        string? evt = null;
        string? id = null;

        while (!ct.IsCancellationRequested)
        {
            // ReadLineAsync(CancellationToken) is required so cancellation
            // actually unblocks a hung read on a stalled socket.
            string? line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            if (line.Length == 0)
            {
                if (data.Length > 0 || evt is not null || id is not null)
                {
                    // SSE spec: strip the single trailing \n that the parser appended.
                    if (data.Length > 0 && data[^1] == '\n')
                    {
                        data.Length--;
                    }
                    yield return new Frame { Event = evt, Data = data.ToString(), Id = id };
                }
                data.Clear();
                evt = null;
                id = null;
                continue;
            }

            // Comment line — ignore but useful for keep-alive heartbeats.
            if (line[0] == ':')
            {
                continue;
            }

            var colon = line.IndexOf(':');
            string field;
            string value;
            if (colon < 0)
            {
                field = line;
                value = "";
            }
            else
            {
                field = line[..colon];
                // SSE: a single leading space after the colon is stripped.
                var start = colon + 1;
                if (start < line.Length && line[start] == ' ') start++;
                value = line[start..];
            }

            switch (field)
            {
                case "event":
                    evt = value;
                    break;
                case "data":
                    data.Append(value).Append('\n');
                    break;
                case "id":
                    id = value;
                    break;
                case "retry":
                    // ignored — we don't auto-reconnect
                    break;
                default:
                    // Unknown field — ignore per spec
                    break;
            }
        }
    }
}
