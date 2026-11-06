namespace Kid.Mcp;

internal static class BoundedTreeSearch
{
    internal static TreeSearchResult<T> Search<T>(
        T root,
        Func<T, int, BoundedChildren<T>> getChildren,
        Func<T, TreeMatchState> getMatchState,
        int maximumElements,
        int maximumDepth)
    {
        ArgumentNullException.ThrowIfNull(getChildren);
        ArgumentNullException.ThrowIfNull(getMatchState);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumElements);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumDepth);

        var pending = new Stack<(T Element, int Depth)>();
        var matches = new List<T>(2);
        var wasTruncated = false;
        var skippedUnavailableElements = 0;
        var visited = 0;

        AddChildren(root, 1, maximumElements);

        while (pending.Count > 0 && visited < maximumElements)
        {
            var (element, depth) = pending.Pop();
            visited++;

            switch (getMatchState(element))
            {
                case TreeMatchState.Match:
                    matches.Add(element);
                    if (matches.Count == 2)
                    {
                        return new TreeSearchResult<T>(matches, wasTruncated, skippedUnavailableElements);
                    }

                    break;
                case TreeMatchState.Unavailable:
                    wasTruncated = true;
                    skippedUnavailableElements++;
                    continue;
            }

            var childCapacity = depth >= maximumDepth
                ? 0
                : maximumElements - visited - pending.Count;
            AddChildren(element, depth + 1, childCapacity);
        }

        if (pending.Count > 0)
        {
            wasTruncated = true;
        }

        return new TreeSearchResult<T>(matches, wasTruncated, skippedUnavailableElements);

        void AddChildren(T parent, int depth, int childCapacity)
        {
            var children = getChildren(parent, Math.Max(0, childCapacity));
            wasTruncated |= children.WasTruncated;
            skippedUnavailableElements += children.SkippedUnavailableElements;

            for (var index = children.Elements.Count - 1; index >= 0; index--)
            {
                pending.Push((children.Elements[index], depth));
            }
        }
    }

    internal enum TreeMatchState
    {
        NoMatch,
        Match,
        Unavailable
    }

    internal sealed record BoundedChildren<T>(
        IReadOnlyList<T> Elements,
        bool WasTruncated,
        int SkippedUnavailableElements = 0);

    internal sealed record TreeSearchResult<T>(
        IReadOnlyList<T> Matches,
        bool WasTruncated,
        int SkippedUnavailableElements);
}
