namespace Kid.Mcp;

internal static class UniqueElementSearchPolicy
{
    internal static T Resolve<T>(
        IReadOnlyList<T> matches,
        bool wasTruncated,
        int skippedUnavailableElements)
    {
        ArgumentNullException.ThrowIfNull(matches);

        if (matches.Count > 1)
        {
            throw new InvalidOperationException(
                "More than one element matched. Provide a more specific name or automationId.");
        }

        if (wasTruncated || skippedUnavailableElements > 0)
        {
            var skippedDetail = skippedUnavailableElements > 0
                ? $" {skippedUnavailableElements} UI element(s) became unavailable during the search."
                : string.Empty;
            throw new InvalidOperationException(
                "Could not verify that the matching element is unique because the accessibility tree was truncated or changed." +
                skippedDetail +
                " Use a more specific automationId or retry after the window settles.");
        }

        if (matches.Count == 0)
        {
            throw new InvalidOperationException("No accessible element matched in the foreground window.");
        }

        return matches[0];
    }
}
