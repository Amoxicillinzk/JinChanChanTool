namespace JinChanChanTool.Services.DataServices.Interface
{
    /// <summary>
    /// 自动更新服务接口
    /// </summary>
    public interface IAutoUpdateService
    {
        /// <summary>
        /// 检查并在后台更新数据（如果需要）
        /// </summary>
        Task CheckAndUpdateAsync();

        /// <summary>
        /// 确保指定赛季的阵容码字典已加载；本地缓存缺失时下载并保存一次。
        /// </summary>
        Task<bool> EnsureLineUpCodeDictionaryAsync(string season);
    }
}
