using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Xml;
using SapGateway.Buffers;

namespace SapGateway.Services;

/// <summary>
/// Streaming, forward-only XML → JSON transformer.
///
/// Design notes:
///  - <see cref="XmlReader"/> validates XML 1.0 well-formedness (including invalid
///    characters such as 0x0B) and throws <see cref="XmlException"/> with line/position.
///  - Output is written with <see cref="Utf8JsonWriter"/> directly into pooled buffers —
///    no string/byte[] copies, no XDocument DOM.
///  - Sibling runs (repeated element names) become JSON arrays. To stay streaming we
///    buffer at most ONE sibling subtree at a time (pooled), never the whole document.
///
/// JSON mapping conventions:
///  - Every element is an object; attributes under "@attributes".
///  - Text content under "#text".
///  - Consecutive same-named siblings are grouped into a JSON array.
///  - Element names keep their XML prefix (e.g. "soap:Envelope").
/// </summary>
public static class XmlToJsonTransformer
{
    private static readonly XmlReaderSettings Settings = new()
    {
        Async = true,
        // enosix Link responses carry a DOCTYPE and reference entities declared in it
        // (e.g. &copy;). Parse honors the internal DTD; XmlResolver = null guarantees
        // no external DTD/entity is ever fetched — XXE stays blocked.
        DtdProcessing = DtdProcessing.Parse,
        XmlResolver = null,
        MaxCharactersFromEntities = 1_000_000,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        IgnoreWhitespace = true,
        CloseInput = false,
    };

    public static async Task TransformAsync(Stream xml, IBufferWriter<byte> output, CancellationToken cancellationToken)
    {
        using var reader = XmlReader.Create(xml, Settings);
        using var writer = new Utf8JsonWriter(output);

        bool foundRoot = false;
        while (await reader.ReadAsync())
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                foundRoot = true;
                break;
            }
        }
        if (!foundRoot)
            throw new XmlException("The payload does not contain an XML document.");

        writer.WriteStartObject();
        writer.WritePropertyName(reader.Name);
        await WriteElementValueAsync(reader, writer, cancellationToken);
        writer.WriteEndObject();
        await writer.FlushAsync(cancellationToken);
    }

    /// <summary>
    /// Writes the JSON value of the element the reader is positioned on.
    /// On return the reader is on that element's EndElement node
    /// (or still on the node itself for empty elements).
    /// </summary>
    private static async Task WriteElementValueAsync(XmlReader reader, Utf8JsonWriter writer, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        writer.WriteStartObject();

        if (reader.HasAttributes)
        {
            writer.WriteStartObject("@attributes");
            while (reader.MoveToNextAttribute())
                writer.WriteString(reader.Name, reader.Value);
            writer.WriteEndObject();
            reader.MoveToElement();
        }

        if (reader.IsEmptyElement)
        {
            writer.WriteEndObject();
            return;
        }

        var text = new StringBuilder();
        string? pendingName = null;
        PooledBufferWriter? pendingValue = null;
        bool pendingRunIsArray = false;

        while (await reader.ReadAsync())
        {
            switch (reader.NodeType)
            {
                case XmlNodeType.Element:
                {
                    string childName = reader.Name;
                    using var childValue = new PooledBufferWriter();
                    using (var childWriter = new Utf8JsonWriter(childValue))
                    {
                        await WriteElementValueAsync(reader, childWriter, cancellationToken);
                        await childWriter.FlushAsync(cancellationToken);
                    }

                    if (pendingName is null)
                    {
                        pendingName = childName;
                        pendingValue = new PooledBufferWriter();
                        pendingValue.Write(childValue.WrittenSpan);
                        pendingRunIsArray = false;
                    }
                    else if (childName == pendingName)
                    {
                        // Sibling run continues → open the array once, then keep
                        // exactly one pending value buffered for lookahead.
                        if (!pendingRunIsArray)
                        {
                            writer.WritePropertyName(pendingName);
                            writer.WriteStartArray();
                            pendingRunIsArray = true;
                        }
                        writer.WriteRawValue(pendingValue!.WrittenSpan, skipInputValidation: true);
                        pendingValue.Clear();
                        pendingValue.Write(childValue.WrittenSpan);
                    }
                    else
                    {
                        FlushPending();
                        pendingName = childName;
                        pendingValue = new PooledBufferWriter();
                        pendingValue.Write(childValue.WrittenSpan);
                        pendingRunIsArray = false;
                    }
                    break;
                }
                case XmlNodeType.Text:
                case XmlNodeType.CDATA:
                    text.Append(reader.Value);
                    break;
                case XmlNodeType.EndElement:
                    goto done;
            }
        }

    done:
        FlushPending();

        if (text.Length > 0)
        {
            string content = text.ToString();
            if (!string.IsNullOrWhiteSpace(content))
                writer.WriteString("#text", content);
        }

        writer.WriteEndObject();

        void FlushPending()
        {
            if (pendingName is null) return;

            if (pendingRunIsArray)
            {
                writer.WriteRawValue(pendingValue!.WrittenSpan, skipInputValidation: true);
                writer.WriteEndArray();
            }
            else
            {
                writer.WritePropertyName(pendingName);
                writer.WriteRawValue(pendingValue!.WrittenSpan, skipInputValidation: true);
            }

            pendingValue!.Dispose();
            pendingName = null;
            pendingValue = null;
            pendingRunIsArray = false;
        }
    }
}
