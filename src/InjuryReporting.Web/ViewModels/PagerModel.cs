namespace InjuryReporting.Web.ViewModels;

public class PagerModel
{
    public int Page { get; init; }
    public int PageSize { get; init; }
    public int Total { get; init; }
    public required Func<int, string?> UrlForPage { get; init; }
    public int TotalPages => Math.Max(1, (int)Math.Ceiling(Total / (double)PageSize));
}
