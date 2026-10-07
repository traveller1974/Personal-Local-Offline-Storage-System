using System.Net;
using System.Text.Json;
using Stock.Recognition;
using Xunit;

namespace Stock.Recognition.Tests;

public sealed class DeepSeekTests : IDisposable
{
    private readonly string image=Path.Combine(Path.GetTempPath(),"deepseek-test-"+Guid.NewGuid().ToString("N")+".png");
    private readonly RecognitionConfiguration configuration=new(RecognitionProviderKind.DeepSeek,"test-secret",RecognitionConfiguration.DeepSeekEndpoint);
    public DeepSeekTests()=>File.WriteAllBytes(image,[1,2,3]);
    private sealed class Handler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> action):HttpMessageHandler
    {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>action(request,token);}
    private static string Envelope(string content,string finish="stop")=>JsonSerializer.Serialize(new{choices=new[]{new{finish_reason=finish,message=new{content}}},usage=new{prompt_tokens=99,completion_tokens=22}});
    [Fact] public async Task OfficialVisionRequestUsesJsonAndKeepsDuplicateRowsAndUsage()
    {
        var calls=0;using var client=new HttpClient(new Handler(async(request,token)=>
        {
            calls++;Assert.Equal(RecognitionConfiguration.DeepSeekEndpoint,request.RequestUri!.ToString());Assert.Equal("test-secret",request.Headers.Authorization!.Parameter);
            using var body=JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Assert.Equal("deepseek-flash",body.RootElement.GetProperty("model").GetString());Assert.Equal("json_object",body.RootElement.GetProperty("response_format").GetProperty("type").GetString());
            var content=body.RootElement.GetProperty("messages")[0].GetProperty("content");Assert.StartsWith("data:image/png;base64,",content[0].GetProperty("image_url").GetProperty("url").GetString());
            return new(HttpStatusCode.OK){Content=new StringContent(Envelope(QwenTests.Sample().ToJsonString()))};
        }));
        var service=new DeepSeekRecognitionService(client,configuration);var result=await service.RecognizeAsync(image);
        Assert.Equal(1,calls);Assert.Equal(3,result.Rows.Count);Assert.Equal("0",result.Rows[2].RawQuantity);Assert.Equal(99,result.InputTokens);Assert.Equal("DeepSeek",result.Provider);
        Assert.Equal(3,result.Rows.Select(r=>r.RowId).Distinct().Count());Assert.DoesNotContain("test-secret",JsonSerializer.Serialize(service.LastDiagnostic));
    }
    [Theory] [InlineData(401,"密钥")] [InlineData(402,"余额")] [InlineData(429,"请求")] [InlineData(500,"无法识别")]
    public async Task HttpErrorsAreBoundedAndNeverSwitchOrRetry(int status,string message)
    {
        var calls=0;using var client=new HttpClient(new Handler((_,_)=>{calls++;return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status){Content=new StringContent("test-secret")});}));
        var service=new DeepSeekRecognitionService(client,configuration);Assert.Contains(message,(await Assert.ThrowsAsync<RecognitionException>(()=>service.RecognizeAsync(image))).Message);Assert.Equal(1,calls);Assert.DoesNotContain("test-secret",service.LastDiagnostic!.RawResponse);
    }
    [Fact] public async Task OverviewAndDetailViewsUseOneBoundedRequestAndAreRecordedSeparately()
    {
        var calls=0;using var client=new HttpClient(new Handler(async(request,token)=>
        {
            calls++;using var body=JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var content=body.RootElement.GetProperty("messages")[0].GetProperty("content");
            Assert.Equal(4,content.GetArrayLength());Assert.All(content.EnumerateArray().Take(3),entry=>Assert.Equal("image_url",entry.GetProperty("type").GetString()));
            Assert.Contains("自上向下",content[3].GetProperty("text").GetString());
            return new(HttpStatusCode.OK){Content=new StringContent(Envelope(QwenTests.Sample().ToJsonString()))};
        }));
        var service=new DeepSeekRecognitionService(client,configuration){Context=new(DetailImages:[image,image])};
        await service.RecognizeAsync(image);Assert.Equal(1,calls);Assert.Equal(2,service.LastDiagnostic!.DetailImageCount);
    }
    [Theory] [InlineData("")] [InlineData("{}", "length")] [InlineData("broken")]
    public async Task EmptyTruncatedAndInvalidResponsesRemainFailures(string text,string finish="stop")
    {
        using var client=new HttpClient(new Handler((_,_)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(Envelope(text,finish))})));
        await Assert.ThrowsAsync<RecognitionException>(()=>new DeepSeekRecognitionService(client,configuration).RecognizeAsync(image));
    }
    [Fact] public async Task LateResponseAfterCancellationDoesNotReturnResults()
    {
        using var cts=new CancellationTokenSource();using var client=new HttpClient(new Handler((_,_)=>{cts.Cancel();return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(Envelope(QwenTests.Sample().ToJsonString()))});}));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>new DeepSeekRecognitionService(client,configuration).RecognizeAsync(image,cts.Token));
    }
    [Fact] public async Task TimeoutStopsTheOnlyRequest()
    {
        using var client=new HttpClient(new Handler(async(_,token)=>{await Task.Delay(10000,token);return new(HttpStatusCode.OK);}));
        var service=new DeepSeekRecognitionService(client,configuration){Timeout=TimeSpan.FromMilliseconds(10)};
        Assert.Contains("超过",(await Assert.ThrowsAsync<RecognitionException>(()=>service.RecognizeAsync(image))).Message);
    }
    [Fact] public void ExternalEndpointsAreRejectedBeforeKeyTransmission()
    {Assert.Throws<RecognitionException>(()=>(configuration with{Endpoint="https://attacker.example/chat/completions"}).Validate());}
    public void Dispose()=>File.Delete(image);
}
