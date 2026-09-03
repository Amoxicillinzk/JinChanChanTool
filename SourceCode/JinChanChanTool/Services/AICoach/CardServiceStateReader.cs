using System.Reflection;
using JinChanChanTool.Services;

namespace JinChanChanTool.Services.AICoach;

public sealed class CardServiceStateReader
{
    private readonly CardService _cardService;
    private readonly FieldInfo? _correctedResultsField;

    public CardServiceStateReader(CardService cardService)
    {
        _cardService = cardService;
        _correctedResultsField = typeof(CardService).GetField("纠正结果数组", BindingFlags.Instance | BindingFlags.NonPublic);
    }

    public string[] GetShopHeroes()
    {
        try
        {
            if (_correctedResultsField?.GetValue(_cardService) is string[] values)
            {
                return values.Where(v => !string.IsNullOrWhiteSpace(v)).ToArray();
            }
        }
        catch
        {
        }
        return [];
    }
}
