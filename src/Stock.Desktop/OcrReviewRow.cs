using Stock.Core;

namespace Stock.Desktop;

public sealed class OcrReviewRow : Observable
{
    private string name,spec,quantity,color="",code="",marker="",rawUnit="";
    private ProductType type;
    private Product? product;
    private bool reviewed;
    public InvoiceRow Source { get; }
    public RecognizedRow? CloudSource { get; }
    public bool Cloud => CloudSource is not null;
    public IReadOnlyList<ProductType> Types { get; }=[ProductType.Unknown,ProductType.Vehicle,ProductType.Battery,ProductType.Charger,ProductType.Accessory];
    public IReadOnlyList<Choice<ProductType>> TypeChoices { get; }=Enum.GetValues<ProductType>().Select(t=>new Choice<ProductType>(Rules.TypeName(t),t)).ToList();
    public ProductType Type { get=>type;set{if(Set(ref type,value)){Rematch();Changed(nameof(Unit));}} }
    public string Color { get=>color;set{if(Set(ref color,value))Rematch();} }
    public string MaterialCode { get=>code;set{if(Set(ref code,value))Rematch();} }
    public string Marker { get=>marker;set{if(Set(ref marker,value))Invalidate();} }
    public string RawUnit { get=>rawUnit;set{if(Set(ref rawUnit,value))Invalidate();} }
    public string Unit => Type==ProductType.Unknown?Product?.Unit??"待确认":Rules.Unit(Type);
    public IReadOnlyList<Product> Products { get; private set; }
    public string Name { get=>name;set{if(Set(ref name,value))Rematch();} }
    public string Spec { get=>spec;set{if(Set(ref spec,value))Rematch();} }
    public string Quantity { get=>quantity;set{if(Set(ref quantity,value))Invalidate();} }
    public Product? Product { get=>product;set{if(Set(ref product,value)){Invalidate();Changed(nameof(Unit));}} }
    public bool Reviewed { get=>reviewed;set=>Set(ref reviewed,value&&CanReview); }
    public bool CanReview => Product?.Active==true&&IntegerInput.TryParse(Quantity,0,out _)&&(!Cloud||(Product.Complete&&Type!=ProductType.Unknown&&IdentityMatches(Product)&&
        !(Type is ProductType.Battery or ProductType.Charger && Color.Length>0)&&RawUnit==(Type switch{ProductType.Vehicle or ProductType.Charger=>"PC",ProductType.Battery=>"PAA",ProductType.Accessory=>"Z1",_=>""})));
    public string Warning => string.Join("；",new[]{Cloud?string.Join("；",CloudSource!.Issues):Source.Confidence<.90?"识别置信度低，请对照照片":"",Product is null?"请关联完整身份货品或新建货品":"",!CanReview?"请核对类型、颜色、编码、原单位及非负整数数量，货品身份必须一致":""}.Where(x=>x.Length>0));
    public OcrReviewRow(InvoiceRow source,IReadOnlyList<Product> products)
    {Source=source;Products=products;name=source.Name;spec=source.Spec;quantity=source.RawQuantity;Rematch();}
    public OcrReviewRow(RecognizedRow source,IReadOnlyList<Product> products)
    {CloudSource=source;Source=new(source.Name,source.Spec,source.RawQuantity,0,[],"");Products=products.Where(p=>p.Complete).ToList();name=source.Name;spec=source.Spec;quantity=source.RawQuantity;type=source.Type;color=source.Color;code=source.MaterialCode;marker=source.Marker;rawUnit=source.RawUnit;Rematch();}
    private void Invalidate() {Reviewed=false;Changed(nameof(CanReview));Changed(nameof(Warning));}
    private bool IdentityMatches(Product p)=>Cloud?p.Type==Type&&Rules.ExactIdentity(p.Name)==Rules.ExactIdentity(Name)&&Rules.ExactIdentity(p.Spec)==Rules.ExactIdentity(Spec)&&Rules.ExactIdentity(p.Color??"")==Rules.ExactIdentity(Color)&&Rules.ExactIdentity(p.MaterialCode??"")==Rules.ExactIdentity(MaterialCode):Rules.Identity(p.Name)==Rules.Identity(Name)&&Rules.Identity(p.Spec)==Rules.Identity(Spec);
    private void Rematch() {Product=Products.FirstOrDefault(IdentityMatches);Invalidate();}
    public void RefreshProducts(IReadOnlyList<Product> products,Product? chosen=null) {Products=Cloud?products.Where(p=>p.Complete).ToList():products;Changed(nameof(Products));Product=chosen??Products.FirstOrDefault(p=>p.Id==Product?.Id)??Products.FirstOrDefault(IdentityMatches);Invalidate();}
}
