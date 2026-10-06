namespace Stock.Recognition;

public sealed record QwenConfiguration(string ApiKey, string Endpoint, string Workspace = "")
{
    public const string Beijing = "https://dashscope.aliyuncs.com/api/v1/services/aigc/multimodal-generation/generation";
    public Uri Validate()
    {
        if (string.IsNullOrWhiteSpace(ApiKey)) throw new RecognitionException("请填写百炼 API Key 和所属地域服务地址。");
        if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Port != 443 ||
            uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0 ||
            !(uri.Host == "dashscope.aliyuncs.com" || uri.Host == "dashscope-intl.aliyuncs.com" || uri.Host == "dashscope-us.aliyuncs.com" ||
              uri.Host.EndsWith(".maas.aliyuncs.com", StringComparison.OrdinalIgnoreCase)) ||
            uri.AbsolutePath != "/api/v1/services/aigc/multimodal-generation/generation")
            throw new RecognitionException("服务地址必须是官方 DashScope 原生 HTTPS 地址，并与 API Key 地域一致。");
        if (Workspace.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_'))
            throw new RecognitionException("业务空间标识无效。");
        return uri;
    }
}
