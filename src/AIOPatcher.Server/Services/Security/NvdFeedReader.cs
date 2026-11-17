using System.Text.Json;
using System.Text.Json.Nodes;

namespace AIOPatcher.Server.Services.Security;

/// <summary>
/// Liest einen NVD-Datenfeed ({"vulnerabilities":[{"cve":{…}}, …]}) Eintrag für Eintrag, ohne die ganze Datei
/// (bis 300 MB je Jahr) in den Speicher zu laden. Gepuffert wird höchstens ein Eintrag plus Lesepuffer.
/// </summary>
public static class NvdFeedReader
{
    public static int Read(Stream stream, Action<JsonObject> onCve, int bufferSize = 1 << 20)
    {
        var buffer = new byte[Math.Max(bufferSize, 16)];
        var length = 0;
        var endOfStream = false;
        var state = new JsonReaderState();
        var expectArray = false;
        var inArray = false;
        var count = 0;

        while (true)
        {
            var consumed = ReadBlock(buffer.AsSpan(0, length), endOfStream, ref state, ref expectArray, ref inArray, onCve, ref count);
            if (endOfStream)
            {
                if (inArray)
                {
                    throw new InvalidDataException("NVD-Feed endet mitten in der Liste.");
                }

                return count;
            }

            // Verarbeitetes nach vorn wegschieben, bei einem Eintrag größer als der Puffer den Puffer vergrößern
            Buffer.BlockCopy(buffer, consumed, buffer, 0, length - consumed);
            length -= consumed;
            if (length == buffer.Length)
            {
                Array.Resize(ref buffer, buffer.Length * 2);
            }

            var read = stream.Read(buffer, length, buffer.Length - length);
            if (read == 0)
            {
                endOfStream = true;
            }

            length += read;
        }
    }

    private static int ReadBlock(ReadOnlySpan<byte> data, bool final, ref JsonReaderState state, ref bool expectArray, ref bool inArray, Action<JsonObject> onCve, ref int count)
    {
        var reader = new Utf8JsonReader(data, final, state);
        while (true)
        {
            if (inArray)
            {
                var probe = reader; // Kopie: bei unvollständigem Eintrag wird ab hier neu gelesen
                if (!probe.Read())
                {
                    break;
                }

                if (probe.TokenType == JsonTokenType.StartObject)
                {
                    var start = (int)probe.TokenStartIndex;
                    if (!probe.TrySkip())
                    {
                        break;
                    }

                    var element = JsonNode.Parse(data[start..(int)probe.BytesConsumed]);
                    if (element?["cve"] is JsonObject cve)
                    {
                        onCve(cve);
                        count++;
                    }
                }
                else if (probe.TokenType == JsonTokenType.EndArray)
                {
                    inArray = false;
                }

                reader = probe;
                continue;
            }

            if (!reader.Read())
            {
                break;
            }

            if (reader.TokenType == JsonTokenType.PropertyName && reader.CurrentDepth == 1)
            {
                expectArray = reader.ValueTextEquals("vulnerabilities"u8);
            }
            else if (expectArray && reader.TokenType == JsonTokenType.StartArray)
            {
                expectArray = false;
                inArray = true;
            }
        }

        state = reader.CurrentState;
        return (int)reader.BytesConsumed;
    }
}
