using Stock.Core;

namespace Stock.Desktop;

public sealed class OcrReviewRow : Observable
{
    private string name,spec,quantity;
    private Product? product;
    private bool reviewed;
    public InvoiceRow Source { get; }
    public IReadOnlyList<Product> Products { get; private set; }
    public string Name { get=>name;set{if(Set(ref name,value))Rematch();} }
    public string Spec { get=>spec;set{if(Set(ref spec,value))Rematch();} }
    public string Quantity { get=>quantity;set{if(Set(ref quantity,value))Invalidate();} }
    public Product? Product { get=>product;set{if(Set(ref product,value))Invalidate();} }
    public bool Reviewed { get=>reviewed;set=>Set(ref reviewed,value&&CanReview); }
    public bool CanReview => Product?.Active==true&&IntegerInput.TryParse(Quantity,1,out _);
    public string Warning => string.Join("；",new[]{Source.Confidence<.90?"识别置信度低，请对照照片":"",Product is null?"请关联已有货品或新建货品":"",!IntegerInput.TryParse(Quantity,1,out _)?"请输入有效整数数量":""}.Where(x=>x.Length>0));
    public OcrReviewRow(InvoiceRow source,IReadOnlyList<Product> products)
    {Source=source;Products=products;name=source.Name;spec=source.Spec;quantity=source.RawQuantity;Rematch();}
    private void Invalidate() {Reviewed=false;Changed(nameof(CanReview));Changed(nameof(Warning));}
    private void Rematch() {Product=Products.FirstOrDefault(p=>Rules.Identity(p.Name)==Rules.Identity(Name)&&Rules.Identity(p.Spec)==Rules.Identity(Spec));Invalidate();}
    public void RefreshProducts(IReadOnlyList<Product> products,Product? chosen=null) {Products=products;Changed(nameof(Products));Product=chosen??products.FirstOrDefault(p=>p.Id==Product?.Id);Invalidate();}
}
