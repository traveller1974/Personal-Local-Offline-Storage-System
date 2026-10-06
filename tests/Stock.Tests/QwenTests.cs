using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Stock.Core;
using Xunit;

namespace Stock.Tests;

public sealed class QwenTests : IDisposable
{
    private readonly string image=Path.Combine(Path.GetTempPath(),"qwen-test-"+Guid.NewGuid().ToString("N")+".png");
    public QwenTests()=>File.WriteAllBytes(image,[1,2,3]);
    internal static JsonObject Sample()
    {
        return new JsonObject
        {
            ["actualQuantityColumn"]=true,["rows"]=new JsonArray
            {
                Row("1","车(123)","车","VQS-RC20/0800/12DGK0","白","Vehicle","成车","2","PC","X1"),
                Row("2","车(123)","车","VQS-RC20/0800/12DGK0","白","Vehicle","成车","3","PC","A"),
                Row("1","充电器(456)","充电器","60V3A/68Ω20-22Ah\n2+2/千五","","Charger","充电器","0","PC","A")
            },["sectionTotals"]=new JsonObject{["Vehicle"]=5,["Battery"]=null,["Charger"]=0,["Accessory"]=null},["warnings"]=new JsonArray()
        };
    }
    internal static JsonObject Row(string order,string rawName,string name,string spec,string color,string type,string evidence,string quantity,string unit,string marker)=>new()
    { ["originalOrder"]=order,["rawName"]=rawName,["name"]=name,["materialCode"]="",["spec"]=spec,["color"]=color,["type"]=type,["sectionEvidence"]=evidence,["rawQuantity"]=quantity,["rawUnit"]=unit,["marker"]=marker,["issues"]=new JsonArray() };
    private static string Envelope(string text,string finish="stop")=>JsonSerializer.Serialize(new{output=new{choices=new[]{new{finish_reason=finish,message=new{content=new[]{new{text}}}}}},usage=new{input_tokens=123,output_tokens=77}});
    private sealed class Handler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> call):HttpMessageHandler
    {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)=>call(request,cancellationToken);}
    [Fact] public void StrictParserKeepsDuplicatesZeroQuantitiesAndFullSpecifications()
    {
        var source=Sample().ToJsonString();var result=RecognitionParser.Parse("```json\n"+source+"\n```");Assert.Equal(3,result.Rows.Count);Assert.Equal("123",result.Rows[0].MaterialCode);Assert.Equal("车",result.Rows[0].Name);Assert.Equal("0",result.Rows[2].RawQuantity);Assert.Contains("\n",result.Rows[2].Spec);Assert.Empty(result.Rows[2].Color);Assert.Equal(ProductType.Charger,result.Rows[2].Type);
    }
    [Theory] [InlineData("1.5")] [InlineData("-1")] [InlineData("1e3")] [InlineData("2147483648")] public void InvalidQuantityIsRetainedForManualCorrection(string quantity)
    {var sample=Sample();sample["rows"]![0]!["rawQuantity"]=quantity;var result=RecognitionParser.Parse(sample.ToJsonString());Assert.Equal(quantity,result.Rows[0].RawQuantity);Assert.False(RecognitionParser.Quantity(result.Rows[0].RawQuantity,out _));Assert.Contains(result.Rows[0].Issues,i=>i.Contains("实发数量"));Assert.Equal(3,result.Rows.Count);}
    [Theory] [InlineData("{}")] [InlineData("{\"rows\":[]}")] [InlineData("```json\n{}")] [InlineData("{\"actualQuantityColumn\":true,")] [InlineData("note {}")]
    public void MalformedOrTruncatedJsonIsRejected(string text)=>Assert.Throws<BusinessException>(()=>RecognitionParser.Parse(text));
    [Fact] public void MissingActualColumnUnknownTypeAndColorConflictAreVisible()
    {
        var sample=Sample();sample["actualQuantityColumn"]=false;sample["rows"]![2]!["color"]="白";sample["rows"]![0]!["type"]="Unknown";var result=RecognitionParser.Parse(sample.ToJsonString());Assert.False(result.ActualQuantityColumn);Assert.NotEmpty(result.Warnings);Assert.NotEmpty(result.Rows[0].Issues);Assert.NotEmpty(result.Rows[2].Issues);
        sample["rows"]![0]!["type"]="Charger";Assert.Contains(RecognitionParser.Parse(sample.ToJsonString()).Rows[0].Issues,i=>i.Contains("分区依据"));
        sample["rows"]![0]!["type"]="Anything";Assert.Equal(ProductType.Unknown,RecognitionParser.Parse(sample.ToJsonString()).Rows[0].Type);
    }
    [Fact] public async Task NativeRequestContainsOnlyConfirmedImageAndPromptAndRecordsUsage()
    {
        var count=0;using var client=new HttpClient(new Handler(async(request,token)=>
        {
            count++;Assert.Equal("Bearer",request.Headers.Authorization!.Scheme);Assert.Equal(QwenConfiguration.Beijing,request.RequestUri!.ToString());using var body=JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Assert.Equal("qwen3.5-ocr",body.RootElement.GetProperty("model").GetString());var content=body.RootElement.GetProperty("input").GetProperty("messages")[0].GetProperty("content");Assert.StartsWith("data:image/png;base64,",content[0].GetProperty("image").GetString());Assert.Contains("实发",content[1].GetProperty("text").GetString());Assert.False(body.RootElement.GetProperty("parameters").TryGetProperty("ocr_options",out _));
            return new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(Envelope(Sample().ToJsonString()),Encoding.UTF8,"application/json")};
        }));
        var service=new QwenRecognitionService(client,new("test-only",QwenConfiguration.Beijing));var result=await service.RecognizeAsync(image);Assert.Equal(1,count);Assert.Equal(123,result.InputTokens);Assert.Equal(77,result.OutputTokens);Assert.Equal(3,result.Rows.Count);
    }
    [Theory]
    [InlineData("\"true\"",true,"兼容转换")] [InlineData("\" FALSE \"",false,"兼容转换")]
    [InlineData("1",false,"格式异常")] [InlineData("\"是\"",false,"格式异常")]
    [InlineData("[]",false,"格式异常")] [InlineData("{}",false,"格式异常")]
    [InlineData("null",false,"缺失")]
    public async Task ActualColumnCompatibilityReachesTheClientWithoutRetries(string json,bool expected,string warning)
    {
        var sample=Sample();sample["actualQuantityColumn"]=JsonNode.Parse(json);var count=0;
        using var client=new HttpClient(new Handler((_,_)=>
        {count++;return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(Envelope(sample.ToJsonString()),Encoding.UTF8,"application/json")});}));
        var result=await new QwenRecognitionService(client,new("test-only",QwenConfiguration.Beijing)).RecognizeAsync(image);
        Assert.Equal(1,count);Assert.Equal(expected,result.ActualQuantityColumn);Assert.Equal(3,result.Rows.Count);
        Assert.Contains(result.Warnings,w=>w.Contains(warning));Assert.Equal(123,result.InputTokens);Assert.Equal(77,result.OutputTokens);
    }
    [Theory] [InlineData(401,"InvalidApiKey","鉴权")] [InlineData(402,"Arrearage","余额")] [InlineData(403,"Arrearage","余额")] [InlineData(403,"AccessDenied","权限")] [InlineData(429,"Throttling","限流")] [InlineData(500,"InternalError","失败")]
    public async Task FailuresAreDistinctAndNeverAutomaticallyRetried(int status,string code,string label)
    {
        var count=0;using var client=new HttpClient(new Handler((_,_)=>{count++;return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status){Content=new StringContent(JsonSerializer.Serialize(new{code,message="test-only"}))});}));
        var service=new QwenRecognitionService(client,new("test-only",QwenConfiguration.Beijing));var error=await Assert.ThrowsAsync<BusinessException>(()=>service.RecognizeAsync(image));Assert.Contains(label,error.Message);Assert.Equal(1,count);Assert.True(File.Exists(image));
    }
    [Fact] public async Task TruncationAndMalformedEnvelopeNeverYieldRows()
    {
        foreach(var response in new[]{Envelope(Sample().ToJsonString(),"length"),"{}","not json"})
        {using var client=new HttpClient(new Handler((_,_)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(response)})));var service=new QwenRecognitionService(client,new("test-only",QwenConfiguration.Beijing));await Assert.ThrowsAsync<BusinessException>(()=>service.RecognizeAsync(image));}
    }
    [Fact] public async Task CancellationDiscardsEvenAHandlerThatReturnsLate()
    {
        using var cts=new CancellationTokenSource();using var client=new HttpClient(new Handler(async(_,_)=>{cts.Cancel();await Task.Delay(5);return new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(Envelope(Sample().ToJsonString()))};}));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>new QwenRecognitionService(client,new("test-only",QwenConfiguration.Beijing)).RecognizeAsync(image,cts.Token));
    }
    [Fact] public async Task TimeoutIsReportedWithoutRetry()
    {
        using var client=new HttpClient(new Handler(async(_,token)=>{await Task.Delay(10000,token);return new(HttpStatusCode.OK);}));
        var service=new QwenRecognitionService(client,new("test-only",QwenConfiguration.Beijing)){Timeout=TimeSpan.FromMilliseconds(10)};
        var error=await Assert.ThrowsAsync<BusinessException>(()=>service.RecognizeAsync(image));Assert.Contains("超过",error.Message);
    }
    [Fact] public async Task AdvancedRecognitionReadsOfficialCoordinates()
    {
        using var client=new HttpClient(new Handler(async(request,token)=>
        {
            using var body=JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));Assert.Equal("advanced_recognition",body.RootElement.GetProperty("parameters").GetProperty("ocr_options").GetProperty("task").GetString());
            return new(HttpStatusCode.OK){Content=new StringContent("""{"output":{"choices":[{"finish_reason":"stop","message":{"content":[{"ocr_result":{"words_info":[{"text":"车","rotate_rect":[100,200,50,20,0]}]}}]}}]}}""")};
        }));var regions=await new QwenRecognitionService(client,new("test-only",QwenConfiguration.Beijing)).LocateAsync(image);Assert.Single(regions);Assert.Equal(100,regions[0].CenterX);
    }
    [Theory] [InlineData("http://dashscope.aliyuncs.com/api/v1/services/aigc/multimodal-generation/generation")] [InlineData("https://attacker.example/api/v1/services/aigc/multimodal-generation/generation")] [InlineData("https://dashscope.aliyuncs.com/compatible-mode/v1")]
    public void KeyCannotBeSentToNonNativeOrUnofficialEndpoint(string endpoint)=>Assert.Throws<BusinessException>(()=>new QwenConfiguration("test-only",endpoint).Validate());
    public void Dispose()=>File.Delete(image);
}
