namespace VehicleDiagnostics.Contracts;

/// <summary>
/// W3C Trace Context <c>traceparent</c> format: <c>00-&lt;32 hex&gt;-&lt;16 hex&gt;-&lt;2 hex&gt;</c>
/// with non-zero trace-id and parent-id. A malformed value never rejects a message —
/// the backend starts a new trace and counts the continuity loss instead.
/// </summary>
public static class TraceParent
{
    public static bool IsValid(string? value)
    {
        if (value is null || value.Length != 55)
        {
            return false;
        }

        ReadOnlySpan<char> span = value;
        if (span[2] != '-' || span[35] != '-' || span[52] != '-')
        {
            return false;
        }

        ReadOnlySpan<char> version = span[..2];
        ReadOnlySpan<char> traceId = span[3..35];
        ReadOnlySpan<char> parentId = span[36..52];
        ReadOnlySpan<char> flags = span[53..];

        return IsLowerHex(version)
            && IsLowerHex(traceId)
            && IsLowerHex(parentId)
            && IsLowerHex(flags)
            && !version.SequenceEqual("ff")
            && traceId.ContainsAnyExcept('0')
            && parentId.ContainsAnyExcept('0');
    }

    private static bool IsLowerHex(ReadOnlySpan<char> span)
    {
        foreach (char c in span)
        {
            if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
            {
                return false;
            }
        }

        return true;
    }
}
