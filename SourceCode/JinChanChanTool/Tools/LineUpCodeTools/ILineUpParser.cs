namespace JinChanChanTool.Tools.LineUpCodeTools
{
    public interface ILineUpParser
    {
        bool IsAvailableForSeason(string season);
        List<string> ParseCode(string lineupCode, string season);
        string GenerateCode(List<string> heroNames, string season);
    }
}
