using System.Buffers;

namespace SapGateway.Streams;

/// <summary>
/// Collects the results of the invalid-character inspection.
/// Messages are capped so a pathological payload cannot exhaust memory.
/// </summary>
public sealed class XmlInspectionLog
{
    private const int MaxMessages = 64;
    private bool _suppressionNoted;

    public long BytesScanned { get; private set; }
    public long InvalidCharacterCount { get; private set; }
    public List<string> Messages { get; } = new();

    /// <summary>
    /// XML 1.0 legal character ranges are #x9, #xA, #xD, #x20-#xD7FF, #xE000-#xFFFD,
    /// #x10000-#x10FFFF. Every other C0 control byte (0x00-0x1F except 09/0A/0D) is
    /// invalid. In UTF-8 these can only appear as single-byte characters, so a raw
    /// byte scan is exact — no decoding needed.
    /// </summary>
    private static readonly SearchValues<byte> InvalidBytes = SearchValues.Create(new byte[]
    {
        0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08,
        /*  0x09 TAB ok, 0x0A LF ok */ 0x0B, 0x0C, /* 0x0D CR ok */
        0x0E, 0x0F, 0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17,
        0x18, 0x19, 0x1A, 0x1B, 0x1C, 0x1D, 0x1E, 0x1F,
    });

    internal void Scan(ReadOnlySpan<byte> data, long offset)
    {
        BytesScanned += data.Length;

        int searched = 0;
        int idx;
        // Vectorized search (SIMD) — no per-byte loop.
        while ((idx = data[searched..].IndexOfAny(InvalidBytes)) >= 0)
        {
            int pos = searched + idx;
            InvalidCharacterCount++;
            if (Messages.Count < MaxMessages)
            {
                Messages.Add($"Invalid XML character 0x{data[pos]:X2} at byte offset {offset + pos}.");
            }
            else if (!_suppressionNoted)
            {
                _suppressionNoted = true;
                Messages.Add("... further violations suppressed (count continues above).");
            }
            searched = pos + 1;
        }

        ScanForFoobar(data);
    }

    // ------------------------------------------------------------------
    // Substring scan test: find "Foobar" as 6 consecutive bytes.
    // Reads arrive in chunks, so a match can span the boundary between two
    // Scan calls. To catch those without ever buffering the payload, we carry
    // over only the last (pattern length - 1) = 5 bytes of the previous chunk.
    // ------------------------------------------------------------------
    private static ReadOnlySpan<byte> Foobar => "Foobar"u8;
    private const int FoobarCarryover = 5; // Foobar.Length - 1
    private readonly byte[] _foobarTail = new byte[FoobarCarryover];
    private int _foobarTailLength;

    private const string FoobarBlock = "**********\n**Foobar**\n**********";

    public long FoobarCount { get; private set; }

    private void ScanForFoobar(ReadOnlySpan<byte> data)
    {
        // 1. Matches fully inside this chunk (vectorized IndexOf).
        int searched = 0;
        int idx;
        while ((idx = data[searched..].IndexOf(Foobar)) >= 0)
        {
            FoobarCount++;
            Messages.Add(FoobarBlock);
            searched += idx + Foobar.Length;
        }

        // 2. Matches crossing from the previous chunk's tail into this chunk.
        //    k = number of match bytes coming from the current chunk (1..5).
        for (int k = 1; k < Foobar.Length && k <= data.Length; k++)
        {
            int fromTail = Foobar.Length - k;
            if (_foobarTailLength >= fromTail &&
                _foobarTail.AsSpan(_foobarTailLength - fromTail).SequenceEqual(Foobar[..fromTail]) &&
                data[..k].SequenceEqual(Foobar[fromTail..]))
            {
                FoobarCount++;
                Messages.Add(FoobarBlock);
            }
        }

        // 3. Save this chunk's tail for the next call (5 bytes, not the payload).
        if (data.Length >= FoobarCarryover)
        {
            data[^FoobarCarryover..].CopyTo(_foobarTail);
            _foobarTailLength = FoobarCarryover;
        }
        else
        {
            int drop = _foobarTailLength + data.Length - FoobarCarryover;
            if (drop > 0)
            {
                _foobarTail.AsSpan(drop, _foobarTailLength - drop).CopyTo(_foobarTail);
                _foobarTailLength -= drop;
            }
            data.CopyTo(_foobarTail.AsSpan(_foobarTailLength));
            _foobarTailLength += data.Length;
        }
    }
}
