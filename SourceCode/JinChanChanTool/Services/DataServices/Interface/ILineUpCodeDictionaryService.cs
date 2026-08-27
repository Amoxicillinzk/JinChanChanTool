namespace JinChanChanTool.Services.DataServices.Interface
{
    /// <summary>
    /// 主赛季阵容码字典服务。
    /// </summary>
    public interface ILineUpCodeDictionaryService
    {
        string LoadedSeason { get; }
        bool IsReady { get; }
        IReadOnlyDictionary<string, string> CodeToName { get; }
        IReadOnlyDictionary<string, string> NameToCode { get; }

        bool LoadMainSeasonDictionary(string mainSeason);
        bool NeedsUpdate(string mainSeason);
        bool UpdateDataFromCrawling(string mainSeason, IReadOnlyDictionary<string, string> codeToName);
    }
}
