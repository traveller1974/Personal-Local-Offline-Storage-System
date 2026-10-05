using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using Stock.Core;

namespace Stock.Desktop;

public sealed class OcrClient
{
    public TimeSpan Timeout { get; init; }=TimeSpan.FromSeconds(120);
    public string? ExecutableOverride { get; init; }
    public async Task<OcrResponse> RecognizeAsync(string imagePath,CancellationToken cancellation=default)
    {
        var executable=ExecutableOverride??Path.Combine(AppContext.BaseDirectory,"Ocr","StockOcr.exe");
        if(!File.Exists(executable))throw new BusinessException("离线识别组件缺失，请重新安装完整软件。仍可手动进货。");
        var start=new ProcessStartInfo(executable){CreateNoWindow=true,UseShellExecute=false,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=Path.GetDirectoryName(executable)!,StandardInputEncoding=new UTF8Encoding(false),StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8};
        using var process=new Process{StartInfo=start};using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellation);timeout.CancelAfter(Timeout);
        try
        {
            if(!process.Start())throw new BusinessException("无法启动识别组件，仍可手动进货。");
            var errors=process.StandardError.ReadToEndAsync();var id=Guid.NewGuid().ToString("N");
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new{protocolVersion=1,requestId=id,imagePath=Path.GetFullPath(imagePath)}));process.StandardInput.Close();
            var line=await process.StandardOutput.ReadLineAsync(timeout.Token);
            if(line is null)throw new BusinessException("识别进程意外退出。请重试或手动输入，库存尚未改变。");
            var result=JsonSerializer.Deserialize<OcrResponse>(line,new JsonSerializerOptions{PropertyNameCaseInsensitive=true})??throw new BusinessException("识别返回内容无效。");
            if(result.RequestId!=id)throw new BusinessException("识别请求编号不匹配，请重新识别。");
            await process.WaitForExitAsync(timeout.Token);await errors;
            if(!result.Success)throw new BusinessException($"{result.Message}（{result.ErrorCode}）");
            if(result.ImageWidth<=0||result.ImageHeight<=0||result.Blocks is null||result.Blocks.Any(b=>b.Box.Length!=4||b.Box.Any(p=>p.Length!=2||p.Any(v=>!double.IsFinite(v)))||!double.IsFinite(b.Confidence)||b.Confidence<0||b.Confidence>1))throw new BusinessException("识别组件返回的坐标无效，请重试。");
            return result;
        }
        catch(OperationCanceledException) when(!cancellation.IsCancellationRequested){throw new BusinessException("照片识别超过120秒，已停止。请裁剪照片后重试，或手动录入。");}
        catch(JsonException){throw new BusinessException("识别组件返回内容无效，仍可手动进货。");}
        finally { try{if(!process.HasExited)process.Kill(true);}catch(InvalidOperationException){}catch(System.ComponentModel.Win32Exception){} }
    }
}
