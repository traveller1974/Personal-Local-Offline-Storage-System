using System.Text.Json;
using Stock.Core;
using Xunit;

namespace Stock.Tests;

public sealed class RecognitionMemoryTests : IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"stock-memory-"+Guid.NewGuid().ToString("N"));
    private readonly StockService service;
    private readonly Product product;
    private readonly RecognizedRow original=new("1","飞池","飞池","","48V","薄何绿",ProductType.Vehicle,"成车","2","PC","",[]);
    public RecognitionMemoryTests()
    {
        service=new(Path.Combine(root,"Data"));product=service.GetProduct(service.CreateProduct(ProductType.Vehicle,"飞驰","48V","薄荷绿","A1",0,0,Key()));
    }
    private static string Key()=>Guid.NewGuid().ToString("N");
    private string Commit(long quantity=3,bool confirmed=true,string? key=null)
    {
        var photo=Path.Combine(root,Key()+".png");File.WriteAllBytes(photo,[1,2,3]);
        var feedback=new RecognitionFeedback("invoice-a",[new("row-1",1,original,confirmed)]);
        return service.Commit(DocumentKind.Purchase,"厂家",[new(product.Id,quantity)],key??Key(),[photo],new Dictionary<string,string>{{photo,JsonSerializer.Serialize(new{feedback})}});
    }
    [Fact] public void FirstConfirmedCorrectionSuggestsAndSecondDistinctDocumentAllowsAutomaticMatch()
    {
        Commit();var first=service.MatchRecognition(original,"invoice-a");Assert.Null(first.Selected);Assert.Equal(product.Id,Assert.Single(first.Candidates).Id);
        Commit();Assert.Equal(product.Id,service.MatchRecognition(original,"invoice-a").Selected!.Id);
        Assert.Null(service.MatchRecognition(original,"different-invoice").Selected);
        Assert.Equal("2",original.RawQuantity);Assert.All(service.CorrectionMemory(),e=>Assert.Equal(3,e.Final.Quantity));
    }
    [Fact] public void ModelGeneratedValuesDoNotBecomeHumanStandards()
    {Commit(confirmed:false);Commit(confirmed:false);Assert.Null(service.MatchRecognition(original,"invoice-a").Selected);Assert.Empty(service.RelevantCorrectionExamples(["飞池"]));}
    [Fact] public void IdempotencyAndRebuildDoNotCountOneDocumentTwice()
    {
        var key=Key();var id=Commit(key:key);Assert.Equal(id,Commit(key:key));service.RebuildRecognitionMemory();service.RebuildRecognitionMemory();
        Assert.Single(service.CorrectionMemory());Assert.Null(service.MatchRecognition(original,"invoice-a").Selected);Assert.Equal(3,service.GetProduct(product.Id).Total);
    }
    [Fact] public void StockSuccessSurvivesMemoryFailureAndRebuildUsesFinalSavedValues()
    {
        service.FaultInjector=stage=>{if(stage=="BeforeMemoryWrite")throw new IOException("test write failure");};
        var id=Commit(quantity:7);Assert.Equal(7,service.GetProduct(product.Id).Total);Assert.NotNull(service.MemoryNotice);Assert.Empty(service.CorrectionMemory());
        service.FaultInjector=null;service.RebuildRecognitionMemory();Assert.Equal(7,Assert.Single(service.CorrectionMemory()).Final.Quantity);Assert.Equal(id,service.CorrectionMemory()[0].DocumentId);
    }
    [Fact] public void VoidsDisabledEntriesAndDisabledLearningAreIgnored()
    {
        var id=Commit();Commit();Assert.NotNull(service.MatchRecognition(original,"invoice-a").Selected);
        service.Void(id,"测试",Key());Assert.Null(service.MatchRecognition(original,"invoice-a").Selected);
        foreach(var e in service.CorrectionMemory())service.SetCorrectionEnabled(e.DocumentId,e.RowId,false);
        Assert.Empty(service.RelevantCorrectionExamples(["飞池"]));service.RebuildRecognitionMemory();Assert.All(service.CorrectionMemory(),e=>Assert.False(e.Enabled));
        service.LearningEnabled=false;Commit();Assert.Equal(2,service.CorrectionMemory().Count);Assert.Empty(service.RelevantCorrectionExamples(["飞池"]));
    }
    [Fact] public void BackupRestoreIncludesMemoryAndDisablingState()
    {
        Commit();service.LearningEnabled=false;var backup=Path.Combine(root,"backup.stockbackup");service.Backup(backup);
        var target=new StockService(Path.Combine(root,"RestoredData"));target.Restore(backup);
        Assert.Single(target.CorrectionMemory());Assert.False(target.LearningEnabled);Assert.Equal(3,target.GetProduct(product.Id).Total);
    }
    [Fact] public void ExamplesAreRelevantBoundedAndNeverContainHistoricalQuantities()
    {
        Commit(quantity:17);Assert.Empty(service.RelevantCorrectionExamples(["无关产品"]));Assert.Empty(service.RelevantCorrectionExamples([]));
        var examples=service.RelevantCorrectionExamples(["飞池"]);Assert.Single(examples);Assert.DoesNotContain("17",JsonSerializer.Serialize(examples));
    }
    [Fact] public void UniqueMaterialCodeFillsMissingFieldsButConflictsNeedAChoice()
    {
        var coded=original with{Name="",Spec="",Color="",MaterialCode="A1"};Assert.Equal(product.Id,service.MatchRecognition(coded,"x").Selected!.Id);
        Assert.Null(service.MatchRecognition(coded with{Spec="60V"},"x").Selected);
        service.CreateProduct(ProductType.Vehicle,"另一款","60V","白","A1",0,0,Key());Assert.Null(service.MatchRecognition(coded,"x").Selected);
    }
    [Fact] public void ConflictingConfirmedCorrectionsDoNotPickTheFirstProduct()
    {
        Commit();Commit();var other=service.CreateProduct(ProductType.Vehicle,"飞驰二代","48V","薄荷绿","B2",0,0,Key());
        var photo=Path.Combine(root,"other.png");File.WriteAllBytes(photo,[1]);
        var feedback=new RecognitionFeedback("invoice-a",[new("row-1",1,original,true)]);
        service.Commit(DocumentKind.Purchase,"厂家",[new(other,1)],Key(),[photo],new Dictionary<string,string>{{photo,JsonSerializer.Serialize(new{feedback})}});
        var match=service.MatchRecognition(original,"invoice-a");Assert.Null(match.Selected);Assert.Equal(2,match.Candidates.Count);
    }
    public void Dispose(){if(Directory.Exists(root))Directory.Delete(root,true);}
}
