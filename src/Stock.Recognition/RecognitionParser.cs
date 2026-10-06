using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace Stock.Recognition;
public static class RecognitionParser
{
    public static RecognitionResult Parse(string text)
    {
        text=text.Trim();
        if(text.StartsWith("```",StringComparison.Ordinal))
        {
            var newline=text.IndexOf('\n');
            if(newline<0||!text.EndsWith("```",StringComparison.Ordinal))throw Error("$","JSON 代码围栏未闭合");
            var language=text[3..newline].Trim();
            if(language.Length>0&&!language.Equals("json",StringComparison.OrdinalIgnoreCase))throw Error("$","代码围栏必须为 JSON");
            text=text[(newline+1)..^3].Trim();
        }
        try
        {
            using var doc=JsonDocument.Parse(text,new JsonDocumentOptions{MaxDepth=16});var root=doc.RootElement;
            CheckDuplicates(root,"$");
            var warnings=new List<string>();
            Object(root,"$",warnings,"actualQuantityColumn","rows","sectionTotals","warnings");
            var actual=ActualColumn(root,warnings);
            if(!root.TryGetProperty("rows",out var rowArray))throw Error("rows","字段缺失");
            if(rowArray.ValueKind!=JsonValueKind.Array)throw Error("rows","必须为明细数组");
            if(rowArray.GetArrayLength() is 0 or >1000)throw Error("rows","明细数量必须为1至1000行");
            var rows=new List<RecognizedRow>();var rowIndex=0;
            foreach(var row in rowArray.EnumerateArray())
            {
                var path=$"rows[{rowIndex++}]";var issues=new List<string>();
                Object(row,path,issues,"originalOrder","rawName","name","materialCode","spec","color","type","sectionEvidence","rawQuantity","rawUnit","marker","issues");
                issues.AddRange(Strings(row,"issues",path+".issues"));
                string S(string key,bool scalar=false)
                {
                    if(!row.TryGetProperty(key,out var e)||e.ValueKind==JsonValueKind.Null){issues.Add($"{key} 缺失，请人工核对");return "";}
                    var value=e.ValueKind==JsonValueKind.String?e.GetString()!:
                        scalar&&e.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False?e.GetRawText():
                        throw Error(path+"."+key,"字段类型无效，必须为文字");
                    if(value.Length>2000)throw Error(path+"."+key,"字段超过2000字符");
                    return value;
                }
                var typeText=S("type");
                if(!Enum.TryParse<RecognitionProductType>(typeText,false,out var type)||!Enum.IsDefined(type)||typeText!=type.ToString())
                {type=RecognitionProductType.Unknown;issues.Add("货物类型不明，请人工确认");}
                var raw=S("rawQuantity",true).Trim();
                if(!Quantity(raw,out _))issues.Add("实发数量缺失或不是有效的非负整数，请人工修正");
                var name=S("name");var original=S("rawName");var code=S("materialCode");
                var suffix=Regex.Match(original,@"^(?<name>.+)[（(](?<code>[0-9]+)[）)]\s*$",RegexOptions.Singleline);
                if(suffix.Success)
                {if(code.Length>0&&code!=suffix.Groups["code"].Value)issues.Add("物料编码与名称括号不一致");code=suffix.Groups["code"].Value;name=suffix.Groups["name"].Value.Trim();}
                var color=S("color");var unit=S("rawUnit").Trim();var evidence=S("sectionEvidence");
                if(type is RecognitionProductType.Battery or RecognitionProductType.Charger && color.Length>0)issues.Add("无颜色类型出现颜色文字，请核对列错位并人工修正");
                var expected=type switch{RecognitionProductType.Vehicle or RecognitionProductType.Charger=>"PC",RecognitionProductType.Battery=>"PAA",RecognitionProductType.Accessory=>"Z1",_=>""};
                if(unit!=expected||type==RecognitionProductType.Unknown)issues.Add("类型与原单位冲突或未知，必须人工确认");
                if(type==RecognitionProductType.Vehicle&&name.Contains("充电")||type==RecognitionProductType.Charger&&name.Contains("电池")||type==RecognitionProductType.Battery&&name.Contains("充电"))issues.Add("名称与类型冲突");
                var sectionTypes=new[]{(Label:"成车",Type:RecognitionProductType.Vehicle),(Label:"电池",Type:RecognitionProductType.Battery),(Label:"充电器",Type:RecognitionProductType.Charger),(Label:"附件",Type:RecognitionProductType.Accessory)}.Where(s=>evidence.Contains(s.Label,StringComparison.Ordinal)).Select(s=>s.Type).Distinct().ToList();
                if(sectionTypes.Count>1||sectionTypes.Count==1&&sectionTypes[0]!=type)issues.Add("分区依据与货物类型冲突，必须人工确认");
                if(evidence.Length==0)issues.Add("分区依据缺失，必须核对类型");
                if(name.Length==0||raw.Length==0)issues.Add("必填名称或实发数量不清楚");
                var order=S("originalOrder",true);
                if(row.TryGetProperty("originalOrder",out var orderValue)&&orderValue.ValueKind is not(JsonValueKind.String or JsonValueKind.Null)&&
                   (orderValue.ValueKind!=JsonValueKind.Number||!long.TryParse(order,NumberStyles.None,CultureInfo.InvariantCulture,out _)))
                    throw Error(path+".originalOrder","必须为文字或非负整数序号");
                rows.Add(new(order,original,name,code,S("spec"),color,type,evidence,raw,unit,S("marker"),issues.Distinct().ToList()));
            }
            var totals=new Dictionary<RecognitionProductType,long?>();
            var hasTotals=root.TryGetProperty("sectionTotals",out var t)&&t.ValueKind!=JsonValueKind.Null;
            if(hasTotals)Object(t,"sectionTotals",warnings,"Vehicle","Battery","Charger","Accessory");
            foreach(var type in new[]{RecognitionProductType.Vehicle,RecognitionProductType.Battery,RecognitionProductType.Charger,RecognitionProductType.Accessory})
            {
                totals[type]=null;
                if(!hasTotals||!t.TryGetProperty(type.ToString(),out var value)||value.ValueKind==JsonValueKind.Null)continue;
                var raw=value.ValueKind==JsonValueKind.String?value.GetString()!:value.ValueKind==JsonValueKind.Number?value.GetRawText():"";
                if(Quantity(raw,out var n))totals[type]=n;
                else warnings.Add($"sectionTotals.{type} 无效，已标记为未知，请人工核对");
            }
            if(!hasTotals)warnings.Add("分区合计未返回，请对照原图人工核对。");
            warnings.AddRange(Strings(root,"warnings","warnings"));
            if(!actual)warnings.Add("实发列尚未确认，不能加入进货清单。请包含列标题重新识别，或返回进货清单手动录入。");
            return new(rows,totals,warnings.Distinct().ToList(),actual);
        }
        catch(JsonException){throw Error("$","JSON 无效、嵌套过深或输出截断");}
    }
    public static bool Quantity(string text,out long value)=>long.TryParse(text,NumberStyles.None,CultureInfo.InvariantCulture,out value)&&value is >=0 and <=int.MaxValue;
    private static bool ActualColumn(JsonElement root,List<string> warnings)
    {
        if(!root.TryGetProperty("actualQuantityColumn",out var column)||column.ValueKind==JsonValueKind.Null)
        {warnings.Add("actualQuantityColumn 缺失，实发列尚未确认。");return false;}
        if(column.ValueKind is JsonValueKind.True or JsonValueKind.False)return column.GetBoolean();
        if(column.ValueKind==JsonValueKind.String)
        {
            var value=column.GetString()!.Trim();
            if(value.Equals("true",StringComparison.OrdinalIgnoreCase)||value.Equals("false",StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add("实发列标志以文字形式返回，已兼容转换，请对照原图核对。");
                return value.Equals("true",StringComparison.OrdinalIgnoreCase);
            }
        }
        var kind=column.ValueKind switch{JsonValueKind.String=>"文字",JsonValueKind.Number=>"数字",JsonValueKind.Array=>"数组",JsonValueKind.Object=>"对象",_=>"未知"};
        warnings.Add($"actualQuantityColumn 格式异常（返回类型：{kind}），实发列已标记为未确认。");
        return false;
    }
    private static RecognitionException Error(string path,string reason)=>new($"识别 JSON {path}：{reason}。未加入进货清单。");
    private static IReadOnlyList<string> Strings(JsonElement obj,string key,string path)
    {
        if(!obj.TryGetProperty(key,out var array)||array.ValueKind==JsonValueKind.Null)return [];
        if(array.ValueKind!=JsonValueKind.Array)throw Error(path,"必须为文字数组");
        var strings=new List<string>();var index=0;
        foreach(var item in array.EnumerateArray())
        {if(item.ValueKind!=JsonValueKind.String)throw Error($"{path}[{index}]","核对说明必须为文字");var value=item.GetString()!;if(value.Length>2000)throw Error($"{path}[{index}]","核对说明超过2000字符");strings.Add(value);index++;}
        return strings;
    }
    private static void Object(JsonElement obj,string path,List<string> notes,params string[] keys)
    {
        if(obj.ValueKind!=JsonValueKind.Object)throw Error(path,"必须为对象");
        if(obj.EnumerateObject().Any(p=>!keys.Contains(p.Name,StringComparer.Ordinal)))notes.Add($"{path} 含额外字段，已忽略，请人工核对");
    }
    private static void CheckDuplicates(JsonElement element,string path)
    {
        if(element.ValueKind==JsonValueKind.Object)
        {
            var names=new HashSet<string>(StringComparer.Ordinal);
            foreach(var property in element.EnumerateObject())
            {
                if(!names.Add(property.Name))throw Error(path,"存在重复字段");
                // Only fixed schema names enter diagnostics; arbitrary returned text stays private.
                var known=new[]{"rows","actualQuantityColumn","sectionTotals","warnings","originalOrder","rawName","name","materialCode","spec","color","type","sectionEvidence","rawQuantity","rawUnit","marker","issues","Vehicle","Battery","Charger","Accessory"};
                CheckDuplicates(property.Value,path+"."+(known.Contains(property.Name,StringComparer.Ordinal)?property.Name:"额外字段"));
            }
        }
        else if(element.ValueKind==JsonValueKind.Array){var i=0;foreach(var item in element.EnumerateArray())CheckDuplicates(item,$"{path}[{i++}]");}
    }
}
