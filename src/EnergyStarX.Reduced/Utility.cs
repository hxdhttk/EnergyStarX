namespace EnergyStarX.Reduced;

internal enum ListKind
{
    WhiteList,
    BlackList
}

internal static class Utility
{
    public static string GetListContent(ListKind kind) => File.ReadAllText(GetListPath(kind));

    public static void SaveListContent(ListKind kind, string content) =>
        File.WriteAllText(GetListPath(kind), content);

    private static string GetListPath(ListKind kind) =>
        kind switch
        {
            ListKind.WhiteList => Path.Combine(AppPath, "whiteList.txt"),
            ListKind.BlackList => Path.Combine(AppPath, "blackList.txt"),
            _ => throw new NotImplementedException("Unknown list kind")
        };

    private static string AppPath => AppContext.BaseDirectory;
}
