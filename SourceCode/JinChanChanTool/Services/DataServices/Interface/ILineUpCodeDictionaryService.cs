namespace JinChanChanTool.Services.DataServices.Interface
{
    /// <summary>
    /// 阵容码字典服务。
    /// </summary>
    public interface ILineUpCodeDictionaryService
    {
        string LoadedSeason { get; }
        bool IsReady { get; }
        IReadOnlyDictionary<string, string> CodeToName { get; }
        IReadOnlyDictionary<string, string> NameToCode { get; }

        bool LoadSeasonDictionary(string season);
        bool NeedsUpdate(string season);
        bool UpdateDataFromCrawling(string season, IReadOnlyDictionary<string, string> codeToName);
    }
}
