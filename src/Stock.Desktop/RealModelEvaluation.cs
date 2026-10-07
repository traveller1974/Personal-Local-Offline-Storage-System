using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Stock.Core;
using Shared=Stock.Recognition;

namespace Stock.Desktop;

/// <summary>Explicit developer entry point. Reads encrypted API settings only; never opens a stock database.</summary>
internal static class RealModelEvaluation
{
    internal static async Task RunAsync(string manifestPath,string output)
    {
        Directory.CreateDirectory(output);
        using var manifest=JsonDocument.Parse(File.ReadAllText(manifestPath));var root=manifest.RootElement;
        var configuration=RecognitionSettings.LoadFromDirectory(root.GetProperty("settingsDirectory").GetString()!);
        configuration.Validate();var baseline=Shared.SampleBaseline.Load(root.GetProperty("baseline").GetString()!);
        using var client=new HttpClient(new SocketsHttpHandler{UseProxy=false,AllowAutoRedirect=false}){Timeout=Timeout.InfiniteTimeSpan};
        var summaries=new List<object>();
        foreach(var sample in root.GetProperty("samples").EnumerateArray())
        {
            var id=sample.GetProperty("id").GetString()!;var image=sample.GetProperty("confirmedImage").GetString()!;
            if(sample.GetProperty("uploadConfirmed").GetBoolean()!=true)throw new BusinessException("样单截图必须事先确认上传范围。");
            var provider=Shared.RecognitionProviderFactory.Create(client,configuration);
            var improved=Path.Combine(output,id+"-improved.json");var failed=Path.Combine(output,id+"-failure.json");
            if(File.Exists(improved)) { using var stored=JsonDocument.Parse(File.ReadAllText(improved)); summaries.Add(new{sample=id,provider=configuration.Provider.ToString(),variant="improved",report=stored.RootElement.GetProperty("report").Clone()}); }
            else if(File.Exists(failed))summaries.Add(new{sample=id,provider=configuration.Provider.ToString(),variant="improved",success=false});
            else
            try
            {
                var result=await provider.RecognizeAsync(image);var diagnostic=provider.LastDiagnostic!;
                var report=Shared.SampleEvaluator.Evaluate(baseline.Single(s=>s.Id==id),result,diagnostic.ModelText);
                File.WriteAllText(Path.Combine(output,id+"-improved.json"),JsonSerializer.Serialize(new{diagnostic,result,report},Shared.RecognitionJson.Options));
                summaries.Add(new{sample=id,provider=configuration.Provider.ToString(),variant="improved",report});
            }
            catch(Shared.RecognitionException ex)
            {
                File.WriteAllText(Path.Combine(output,id+"-failure.json"),JsonSerializer.Serialize(provider.LastDiagnostic,Shared.RecognitionJson.Options));
                summaries.Add(new{sample=id,provider=configuration.Provider.ToString(),variant="improved",success=false,error=ex.Message});
                if(provider.LastDiagnostic?.Stage is "HttpError" or "Timeout" or "Transport")break;
            }
            if(configuration.Provider==Shared.RecognitionProviderKind.Qwen&&root.TryGetProperty("acceptedWorker",out var old))
            {
                var original=Path.Combine(output,id+"-original.json");
                if(File.Exists(original)){using var stored=JsonDocument.Parse(File.ReadAllText(original));summaries.Add(new{sample=id,provider="Qwen",variant="original",report=stored.RootElement.GetProperty("report").Clone()});continue;}
                var start=new ProcessStartInfo(old.GetString()!){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,
                    RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardInputEncoding=new UTF8Encoding(false),StandardOutputEncoding=new UTF8Encoding(false)};
                start.ArgumentList.Add("--recognition-worker");using var process=Process.Start(start)??throw new BusinessException("旧识图程序无法启动。");
                using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(120));
                try
                {
                    var stderr=process.StandardError.ReadToEndAsync(timeout.Token);
                    var request=new Shared.RecognitionWorkerRequest(Guid.NewGuid().ToString(),Shared.RecognitionWorkerProtocol.Recognize,image,
                        new(configuration.ApiKey,configuration.Endpoint,configuration.Workspace),ProtocolVersion:1);
                    await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request,Shared.RecognitionWorkerProtocol.Json));process.StandardInput.Close();
                    var json=await process.StandardOutput.ReadLineAsync(timeout.Token);await process.WaitForExitAsync(timeout.Token);await stderr;
                    var response=JsonSerializer.Deserialize<Shared.RecognitionWorkerResponse>(json!,Shared.RecognitionWorkerProtocol.Json)!;
                    if(!response.Success||response.Result is null)throw new BusinessException("旧识图程序本次未能返回结果。");
                    var result=response.Result;var report=Shared.SampleEvaluator.Evaluate(baseline.Single(s=>s.Id==id),result,JsonSerializer.Serialize(result,Shared.RecognitionJson.Options));
                    File.WriteAllText(Path.Combine(output,id+"-original.json"),JsonSerializer.Serialize(new{result,report,realCall=true,originalWorkerProtocol=1},Shared.RecognitionJson.Options));
                    summaries.Add(new{sample=id,provider="Qwen",variant="original",report});
                }
                finally{if(!process.HasExited){process.Kill(true);await process.WaitForExitAsync();}}
            }
        }
        File.WriteAllText(Path.Combine(output,"summary.json"),JsonSerializer.Serialize(summaries,Shared.RecognitionJson.Options));
    }
}
