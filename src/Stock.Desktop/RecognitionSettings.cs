using System.IO;
using System.Net.Http;
using System.Text.Json;
using Stock.Core;
using Shared = Stock.Recognition;

namespace Stock.Desktop;

public static class RecognitionSettings
{
    private static readonly HttpClient Client=new(new SocketsHttpHandler{UseProxy=false,AllowAutoRedirect=false}){Timeout=Timeout.InfiniteTimeSpan};
    private static string DirectoryFor(StockService service)=>Path.Combine(Path.GetDirectoryName(service.DataDirectory)!,"Settings");
    public static Shared.RecognitionConfiguration Load(StockService service,Shared.RecognitionProviderKind? provider=null)
        =>LoadFromDirectory(DirectoryFor(service),provider);
    public static Shared.RecognitionConfiguration LoadFromDirectory(string directory,Shared.RecognitionProviderKind? provider=null)
    {
        var selection=Path.Combine(directory,"recognition-provider.txt");
        if(provider==Shared.RecognitionProviderKind.DeepSeek||provider is null&&File.Exists(selection)&&File.ReadAllText(selection).Trim()=="DeepSeek")
        {
            var path=Path.Combine(directory,"deepseek.dpapi");
            try{return File.Exists(path)?JsonSerializer.Deserialize<Shared.RecognitionConfiguration>(QwenSettings.Protect(File.ReadAllBytes(path),false))??throw new BusinessException("DeepSeek 设置无效。"):
                new(Shared.RecognitionProviderKind.DeepSeek,"",Shared.RecognitionConfiguration.DeepSeekEndpoint);}
            catch(Exception ex) when(ex is not BusinessException){throw new BusinessException("DeepSeek 设置无法由当前 Windows 用户解密，请重新填写。");}
        }
        var qwenPath=Path.Combine(directory,"qwen.dpapi");
        if(!File.Exists(qwenPath))return new(Shared.RecognitionProviderKind.Qwen,"",QwenConfiguration.Beijing);
        try
        {
            var qwen=JsonSerializer.Deserialize<QwenConfiguration>(QwenSettings.Protect(File.ReadAllBytes(qwenPath),false))??throw new BusinessException("百炼设置无效。");
            return new(Shared.RecognitionProviderKind.Qwen,qwen.ApiKey,qwen.Endpoint,qwen.Workspace);
        }
        catch(Exception ex) when(ex is not BusinessException){throw new BusinessException("识别设置无法由当前 Windows 用户解密，请重新填写。");}
    }
    public static void Save(StockService service,Shared.RecognitionConfiguration configuration)
    {
        try{configuration.Validate();}catch(Shared.RecognitionException ex){throw new BusinessException(ex.Message);}
        var directory=DirectoryFor(service);Directory.CreateDirectory(directory);
        if(configuration.Provider==Shared.RecognitionProviderKind.Qwen)QwenSettings.Save(service,new(configuration.ApiKey,configuration.Endpoint,configuration.Workspace));
        else
        {
            var path=Path.Combine(directory,"deepseek.dpapi");var temp=path+".tmp";
            try{File.WriteAllBytes(temp,QwenSettings.Protect(JsonSerializer.SerializeToUtf8Bytes(configuration),true));File.Move(temp,path,true);}
            finally{if(File.Exists(temp))File.Delete(temp);}
        }
        File.WriteAllText(Path.Combine(directory,"recognition-provider.txt"),configuration.Provider.ToString());
    }
    public static IRecognitionService Service(StockService service,Shared.RecognitionContext? context=null)
    {
        var configuration=Load(service);var qwen=new QwenConfiguration(configuration.ApiKey,configuration.Endpoint,configuration.Workspace);
        IRecognitionService original=new ProviderRecognitionService(Client,configuration,context);
        return QwenSettings.UsesEmbeddedRecognition?new EmbeddedRecognitionService(qwen,original){ProviderConfiguration=configuration,Context=context}:original;
    }
    public static async Task<RecognitionResult> RecognizeAsync(StockService stock,string image,Shared.RecognitionContext context,CancellationToken token)
    {
        using var budget=CancellationTokenSource.CreateLinkedTokenSource(token);budget.CancelAfter(TimeSpan.FromSeconds(180));
        RecognitionResult first;
        try{first=await Service(stock,context).RecognizeAsync(image,budget.Token);}
        catch(OperationCanceledException) when(!token.IsCancellationRequested){throw new BusinessException("识别已超过等待时间，填写内容已保留。");}
        var examples=stock.RelevantCorrectionExamples(first.Rows.Select(r=>r.Name),StockService.InvoiceStyle(first));
        if(examples.Count==0||first.Rows.All(r=>r.Name.Length>0&&r.Spec.Length>0&&r.Issues.Count==0))return first;
        // A second call is only for unresolved fields with relevant verified examples; no network failure retries.
        try
        {
            var second=await Service(stock,context with{Examples=examples}).RecognizeAsync(image,budget.Token);
            if(first.Rows.Count!=second.Rows.Count||!first.Rows.Select(r=>r.OriginalOrder).SequenceEqual(second.Rows.Select(r=>r.OriginalOrder)))
                return first with{Warnings=first.Warnings.Append("补充识别的行数或顺序不同，已保留首次结果，请对照照片检查。").ToArray()};
            var rows=first.Rows.Select((r,i)=>
            {
                var other=second.Rows[i];
                if(r.RawName!=other.RawName||r.RawQuantity!=other.RawQuantity)return r;
                return r with{Name=r.Name.Length==0?other.Name:r.Name,Spec=r.Spec.Length==0?other.Spec:r.Spec,Color=r.Color.Length==0?other.Color:r.Color,
                    MaterialCode=r.MaterialCode.Length==0?other.MaterialCode:r.MaterialCode};
            }).ToArray();
            return first with{Rows=rows,Elapsed=first.Elapsed+second.Elapsed,InputTokens=(first.InputTokens??0)+(second.InputTokens??0),OutputTokens=(first.OutputTokens??0)+(second.OutputTokens??0),
                Warnings=first.Warnings.Append("已参考以前的核对记录补充缺失资料，请检查提示的字段。").ToArray()};
        }
        catch(OperationCanceledException) when(token.IsCancellationRequested){throw;}
        catch(Exception ex) when(ex is BusinessException or OperationCanceledException)
        {return first with{Warnings=first.Warnings.Append("补充识别未完成，首次结果已保留，可以继续填写。").ToArray()};}
    }
}
