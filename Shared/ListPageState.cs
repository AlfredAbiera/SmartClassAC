namespace SmartClassAC.Shared;

public sealed class ListPageState
{
    public const int DefaultPageSize = 10;

    public ListPageState(int pageSize = DefaultPageSize) =>
        PageSize = Math.Max(1, pageSize);

    public int Page { get; private set; } = 1;
    public int PageSize { get; }

    public void Reset() => Page = 1;

    public void EnsureValid(int totalCount)
    {
        var pages = TotalPages(totalCount);
        if (Page > pages)
        {
            Page = pages;
        }

        if (Page < 1)
        {
            Page = 1;
        }
    }

    public int TotalPages(int totalCount) =>
        totalCount <= 0 ? 1 : (int)Math.Ceiling(totalCount / (double)PageSize);

    public IReadOnlyList<T> Slice<T>(IReadOnlyList<T> items)
    {
        EnsureValid(items.Count);
        return items.Skip((Page - 1) * PageSize).Take(PageSize).ToList();
    }

    public string Summary(int totalCount)
    {
        if (totalCount <= 0)
        {
            return "No items";
        }

        EnsureValid(totalCount);
        var start = (Page - 1) * PageSize + 1;
        var end = Math.Min(Page * PageSize, totalCount);
        return $"Showing {start}–{end} of {totalCount}";
    }

    public bool CanPrev => Page > 1;

    public bool CanNext(int totalCount) => Page < TotalPages(totalCount);

    public void Prev()
    {
        if (CanPrev)
        {
            Page--;
        }
    }

    public void Next(int totalCount)
    {
        if (CanNext(totalCount))
        {
            Page++;
        }
    }
}
