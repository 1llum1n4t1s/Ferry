using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace __NAMESPACE__ {
// 既存PowerShellホストの.NET APIだけを使用する。SDK・Node・外部依存は不要。
public sealed class Publisher : IDisposable {
    static readonly StringComparer CI = StringComparer.OrdinalIgnoreCase;
    readonly Dictionary<string,object> config;
    readonly string root, moduleRoot, manifestPath, account, token, apiBase;
    readonly bool dryRun;
    readonly Dictionary<string,Module> modules = new Dictionary<string,Module>(CI);
    readonly Dictionary<string,string> maps = new Dictionary<string,string>(CI);
    readonly Dictionary<string,object> metadata = Map();
    readonly Dictionary<string,object> assets = Map();
    readonly Dictionary<string,string> assetFiles = new Dictionary<string,string>(CI);
    readonly Dictionary<string,object> secretValues = Map();
    readonly Dictionary<string,object> subdomain = Map();
    readonly List<object> bindings = new List<object>();
    HttpClient client;
    string entry;
    sealed class Module { public byte[] Bytes; public string Mime; }
    sealed class RoutePlan { public Dictionary<string,object> Route; public string Zone; public bool Exists; }
    static Dictionary<string,object> Map() { return new Dictionary<string,object>(CI); }
    static Dictionary<string,object> D(object value) { return value as Dictionary<string,object> ?? Map(); }
    static object G(object value,string key) { object found; return D(value).TryGetValue(key,out found) ? found : null; }
    static string S(object value) { return value == null ? "" : Convert.ToString(value,System.Globalization.CultureInfo.InvariantCulture); }
    static bool B(object value) { return value != null && (!(value is bool) || (bool)value) && S(value).Length != 0; }
    static IEnumerable<object> A(object value) { return value is IList<object> ? (IList<object>)value : value == null ? Array.Empty<object>() : new []{value}; }
    static string Json(object value) { return JsonSerializer.Serialize(value,new JsonSerializerOptions{MaxDepth=100}); }
    static object Parse(string value) { try{using(var doc=JsonDocument.Parse(value,new JsonDocumentOptions { MaxDepth=100,AllowTrailingCommas=true,CommentHandling=JsonCommentHandling.Skip })) return FromJson(doc.RootElement);}catch(JsonException){throw new FormatException("Invalid JSON configuration or input.");} }
    static object FromJson(JsonElement value) {
        switch(value.ValueKind) {
            case JsonValueKind.Object: var result=Map();foreach(var pair in value.EnumerateObject())result[pair.Name]=FromJson(pair.Value);return result;
            case JsonValueKind.Array:return value.EnumerateArray().Select(FromJson).ToList();
            case JsonValueKind.String:return value.GetString();
            case JsonValueKind.Number:long number;return value.TryGetInt64(out number)?(object)number:value.GetDouble();
            case JsonValueKind.True:return true;case JsonValueKind.False:return false;default:return null;
        }
    }
    static string Full(string path,string basePath) { return Path.GetFullPath(Path.IsPathRooted(path)?path:Path.Combine(basePath,path)); }
    static void AssertPath(string path) {
        for(var current=Path.GetFullPath(path);!String.IsNullOrEmpty(current);current=Path.GetDirectoryName(current))
            if((File.Exists(current)||Directory.Exists(current))&&(File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)throw new InvalidOperationException("配信パスにリンクがあります: "+current);
    }
    void WithinModule(string path) {
        AssertPath(path);
        if(!path.StartsWith(moduleRoot.TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("ModuleRoot の範囲外: "+path);
    }
    static string Relative(string dir,string file) { return Path.GetRelativePath(dir,file).Replace('\\','/'); }
    static string Hash(byte[] bytes) { using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-","").ToLowerInvariant(); }
    // 旧入口が解釈していたTOML subset（table/array table/inline table/複数行配列）を維持する。
    static List<string> Split(string text,char separator=',') {
        var parts=new List<string>();int depth=0,start=0;char quote='\0';bool escaped=false;
        for(int i=0;i<text.Length;i++) {
            char ch=text[i];
            if(quote!='\0'){if(escaped){escaped=false;continue;}if(ch=='\\'&&quote=='"'){escaped=true;continue;}if(ch==quote)quote='\0';continue;}
            if(ch=='"'||ch=='\''){quote=ch;continue;}if(ch=='['||ch=='{')depth++;if(ch==']'||ch=='}')depth--;
            if(ch==separator&&depth==0){parts.Add(text.Substring(start,i-start));start=i+1;}
        }
        if(quote!='\0'||depth!=0)throw new FormatException("TOML value is incomplete.");
        parts.Add(text.Substring(start));return parts;
    }
    static object TomlValue(string text) {
        text=text.Trim();
        if(text.StartsWith("["))return Split(text.Substring(1,text.Length-2)).Where(v=>!String.IsNullOrWhiteSpace(v)).Select(TomlValue).ToList();
        if(text.StartsWith("{")){var map=Map();foreach(var part in Split(text.Substring(1,text.Length-2))){if(String.IsNullOrWhiteSpace(part))continue;var match=Regex.Match(part,@"^\s*([\w-]+)\s*=\s*(.*)$");if(!match.Success)throw new FormatException("未対応のTOML inline key");map[match.Groups[1].Value]=TomlValue(match.Groups[2].Value);}return map;}
        if(text.StartsWith("'"))return text.Substring(1,text.Length-2);return Parse(text);
    }
    static Dictionary<string,object> Toml(string text) {
        var result=Map();var table=result;string pending="";
        foreach(var raw in Regex.Split(text,"\\r?\\n")) {
            char quote='\0';bool escaped=false;string line=raw;
            for(int i=0;i<raw.Length;i++){char ch=raw[i];if(quote!='\0'){if(escaped){escaped=false;continue;}if(ch=='\\'&&quote=='"'){escaped=true;continue;}if(ch==quote)quote='\0';continue;}if(ch=='"'||ch=='\''){quote=ch;continue;}if(ch=='#'){line=raw.Substring(0,i);break;}}
            line=line.Trim();if(line.Length==0)continue;
            var header=Regex.Match(line,@"^\[\[?([\w.-]+)\]\]?$");
            if(pending.Length==0&&header.Success){bool array=line.StartsWith("[[");var keys=header.Groups[1].Value.Split('.');table=result;
                for(int i=0;i<keys.Length;i++){string key=keys[i];if(i==keys.Length-1&&array){if(!table.ContainsKey(key))table[key]=new List<object>();var next=Map();((List<object>)table[key]).Add(next);table=next;}else{if(!table.ContainsKey(key))table[key]=Map();object next=table[key];if(next is List<object>)next=((List<object>)next).Last();table=D(next);}}continue;}
            pending+=" "+line;var assignment=Regex.Match(pending,@"^\s*([\w-]+)\s*=\s*(.*)$");if(!assignment.Success)throw new FormatException("未対応のTOML構文");
            try{Split(assignment.Groups[2].Value);}catch(FormatException){continue;}
            table[assignment.Groups[1].Value]=TomlValue(assignment.Groups[2].Value);pending="";
        }
        if(pending.Length!=0)throw new FormatException("TOML value is incomplete.");return result;
    }
    Publisher(Dictionary<string,object> options) {
        string configPath=Path.GetFullPath(S(G(options,"ConfigPath")));AssertPath(configPath);root=Path.GetDirectoryName(configPath);
        config=Path.GetExtension(configPath).Equals(".toml",StringComparison.OrdinalIgnoreCase)?Toml(File.ReadAllText(configPath)):D(Parse(File.ReadAllText(configPath)));
        if(config.ContainsKey("route"))throw new InvalidOperationException("route 単数形には未対応です。zone_name/zone_id 付きの routes 配列を指定してください。");
        if(Equals(G(config,"keep_vars"),true))throw new InvalidOperationException("keep_vars=true には未対応です。vars の正本を config に揃えてから公開してください。");
        if(!B(G(config,"name"))||!B(G(config,"main"))||!B(G(config,"compatibility_date")))throw new InvalidOperationException("name/main/compatibility_date が必要です。");
        string main=B(G(options,"Main"))?Path.GetFullPath(S(G(options,"Main"))):Full(S(G(config,"main")),root);
        if(!File.Exists(main))throw new FileNotFoundException("Worker main がありません。",main);
        moduleRoot=B(G(options,"ModuleRoot"))?Path.GetFullPath(S(G(options,"ModuleRoot"))):Path.GetDirectoryName(main);AssertPath(moduleRoot);
        manifestPath=S(G(options,"ModuleManifestPath"));account=B(G(options,"AccountId"))?S(G(options,"AccountId")):S(G(config,"account_id"));token=S(G(options,"ApiToken"));
        apiBase=S(G(options,"ApiBase"));dryRun=Equals(G(options,"DryRun"),true);
        entry=AddModule(main);
        if(manifestPath.Length!=0){AssertPath(manifestPath);foreach(var item in A(Parse(File.ReadAllText(manifestPath))))AddModule(Full(S(item),moduleRoot));}
        ReadMaps();
        metadata["main_module"]=entry;metadata["compatibility_date"]=G(config,"compatibility_date");metadata["bindings"]=bindings;metadata["keep_bindings"]=new[]{"secret_text","secret_key"};
        string secrets=S(G(options,"SecretsFile"));
        if(secrets.Length!=0){AssertPath(secrets);foreach(var pair in D(Parse(File.ReadAllText(secrets)))){if(!(pair.Value is string))throw new InvalidOperationException("Secret は文字列で指定してください: "+pair.Key);secretValues[pair.Key]=pair.Value;bindings.Add(new Dictionary<string,object>{{"name",pair.Key},{"type","secret_text"},{"text",dryRun?"[redacted]":pair.Value}});}}
        foreach(var key in new[]{"compatibility_flags","observability","limits","placement","tail_consumers"})if(config.ContainsKey(key))metadata[key]=config[key];
        Bindings();ReadAssets();
        subdomain["enabled"]=config.ContainsKey("workers_dev")?B(config["workers_dev"]):!A(G(config,"routes")).Any();if(config.ContainsKey("preview_urls"))subdomain["previews_enabled"]=B(config["preview_urls"]);
    }
    string AddModule(string path) {
        path=Path.GetFullPath(path);WithinModule(path);string name=Relative(moduleRoot,path);if(modules.ContainsKey(name))return name;
        string extension=Path.GetExtension(path).ToLowerInvariant(),mime="application/javascript+module";
        if(extension==".wasm"||extension==".bin"){modules[name]=new Module{Bytes=File.ReadAllBytes(path),Mime=extension==".wasm"?"application/wasm":"application/octet-stream"};return name;}
        string body=File.ReadAllText(path);
        if(extension==".json")body="export default "+body+";";else if(extension==".sql"||extension==".txt")mime="text/plain";else if(extension!=".js"&&extension!=".mjs")throw new InvalidOperationException("先に製品ビルドが必要です: "+path);
        var module=new Module{Bytes=Encoding.UTF8.GetBytes(body),Mime=mime};modules[name]=module;
        if(manifestPath.Length==0&&mime=="application/javascript+module"&&extension!=".json")foreach(Match match in Regex.Matches(body,"(?m)(?:\\bfrom\\s*|\\bimport\\s*\\(\\s*|\\bimport\\s*)[\"'](?<path>\\.{1,2}/[^\"']+)[\"']"))AddModule(Full(match.Groups["path"].Value,Path.GetDirectoryName(path)));
        if(mime=="application/javascript+module"&&extension!=".json")module.Bytes=Encoding.UTF8.GetBytes(Regex.Replace(body,"(?<=[\"'])\\s+(?:with|assert)\\s*\\{\\s*type\\s*:\\s*[\"']json[\"']\\s*\\}",""));
        return name;
    }
    void ReadMaps() {
        if(Equals(G(config,"upload_source_maps"),false))return;
        foreach(var pair in modules){if(pair.Value.Mime!="application/javascript+module")continue;var matches=Regex.Matches(Encoding.UTF8.GetString(pair.Value.Bytes),@"(?m)^//# sourceMappingURL=(.+)\r?$");if(matches.Count==0)continue;
            string reference=matches[matches.Count-1].Groups[1].Value.Trim();bool required=Equals(G(config,"upload_source_maps"),true);
            if(reference.StartsWith("data:")){if(required)throw new InvalidOperationException("外部 source map が必要です。");continue;}
            string path=Full(reference,Path.GetDirectoryName(Path.Combine(moduleRoot,pair.Key)));WithinModule(path);if(!File.Exists(path)){if(required)throw new FileNotFoundException("Source map がありません。",path);continue;}maps[Relative(moduleRoot,path)]=File.ReadAllText(path);
        }
    }
    void Bindings() {
        foreach(var pair in D(G(config,"vars")))bindings.Add(new Dictionary<string,object>{{"name",pair.Key},{"type",pair.Value is string?"plain_text":"json"},{pair.Value is string?"text":"json",pair.Value}});
        var types=new Dictionary<string,string>{{"services","service"},{"r2_buckets","r2_bucket"},{"d1_databases","d1"},{"kv_namespaces","kv_namespace"},{"send_email","send_email"}};
        foreach(var type in types)foreach(var binding in A(G(config,type.Key))){var record=Map();record["name"]=G(binding,"binding");record["type"]=type.Value;
            switch(type.Key){case "services":record["service"]=G(binding,"service");CopyOptional(record,binding,"environment","entrypoint");break;case "r2_buckets":record["bucket_name"]=G(binding,"bucket_name");CopyOptional(record,binding,"jurisdiction");break;case "d1_databases":record["id"]=G(binding,"database_id");break;case "kv_namespaces":record["namespace_id"]=G(binding,"id");break;case "send_email":record["name"]=G(binding,"name");CopyPresent(record,binding,"destination_address","allowed_destination_addresses","allowed_sender_addresses");break;}bindings.Add(record);}
        foreach(var binding in A(G(G(config,"durable_objects"),"bindings"))){var record=new Dictionary<string,object>{{"name",G(binding,"name")},{"type","durable_object_namespace"},{"class_name",G(binding,"class_name")}};CopyOptional(record,binding,"script_name");bindings.Add(record);}
        bindings.AddRange(A(G(G(config,"unsafe"),"bindings")));
        foreach(var workflow in A(G(config,"workflows"))){var record=new Dictionary<string,object>{{"type","workflow"},{"name",G(workflow,"binding")},{"workflow_name",G(workflow,"name")},{"class_name",G(workflow,"class_name")}};CopyOptional(record,workflow,"script_name");bindings.Add(record);}
    }
    static void CopyOptional(Dictionary<string,object> to,object from,params string[] keys){foreach(var key in keys)if(B(G(from,key)))to[key]=G(from,key);}
    static void CopyPresent(Dictionary<string,object> to,object from,params string[] keys){foreach(var key in keys)if(D(from).ContainsKey(key))to[key]=G(from,key);}
    void ReadAssets() {
        object asset=G(config,"assets");if(asset==null)return;string dir=Full(S(G(asset,"directory")),root);AssertPath(dir);
        if(File.Exists(Path.Combine(dir,".assetsignore")))throw new InvalidOperationException(".assetsignore は製品ビルドで適用済みのディレクトリを指定してください。");
        var pending=new Stack<string>();pending.Push(dir);
        while(pending.Count>0)foreach(var path in Directory.EnumerateFileSystemEntries(pending.Pop())){AssertPath(path);if(Directory.Exists(path)){pending.Push(path);continue;}string rel=Relative(dir,path);if(rel=="_headers"||rel=="_redirects")continue;var bytes=File.ReadAllBytes(path);string hash=Hash(Encoding.UTF8.GetBytes(Convert.ToBase64String(bytes)+Path.GetExtension(path).TrimStart('.'))).Substring(0,32);assets["/"+rel]=new Dictionary<string,object>{{"hash",hash},{"size",bytes.LongLength}};assetFiles[hash]=path;}
        if(B(G(asset,"binding")))bindings.Add(new Dictionary<string,object>{{"name",G(asset,"binding")},{"type","assets"}});
        var settings=Map();CopyPresent(settings,asset,"html_handling","not_found_handling","run_worker_first");foreach(var special in new[]{"_headers","_redirects"}){string file=Path.Combine(dir,special);if(File.Exists(file))settings[special]=File.ReadAllText(file);}metadata["assets"]=new Dictionary<string,object>{{"config",settings}};
    }
    object Api(string method,string path,object body=null,string bearer=null,bool missing=false) {
        using(var request=new HttpRequestMessage(new HttpMethod(method),apiBase.TrimEnd('/')+"/"+path)){
            request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",bearer??token);
            if(body!=null)request.Content=body as HttpContent??new StringContent(Json(body),Encoding.UTF8,"application/json");
            HttpResponseMessage response;
            try{response=client.SendAsync(request).GetAwaiter().GetResult();}catch{throw new InvalidOperationException("Cloudflare API "+method+" request failed; response unknown. Inspect state before retrying.");}
            using(response){if(missing&&response.StatusCode==HttpStatusCode.NotFound)return null;if(!response.IsSuccessStatusCode)throw new InvalidOperationException("Cloudflare API "+method+" "+path+" failed: HTTP "+(int)response.StatusCode);
                object result;try{result=Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());}catch{throw new InvalidOperationException("Cloudflare API returned invalid JSON.");}
                if(Equals(G(result,"success"),false))throw new InvalidOperationException("Cloudflare API "+method+" "+path+" rejected the operation.");return G(result,"result");}
        }
    }
    static ByteArrayContent Part(byte[] bytes,string mime){var content=new ByteArrayContent(bytes);content.Headers.ContentType=new MediaTypeHeaderValue(mime);return content;}
    static string Mime(string path){switch(Path.GetExtension(path).TrimStart('.').ToLowerInvariant()){case "html":return "text/html";case "css":return "text/css";case "js":case "mjs":return "application/javascript";case "json":return "application/json";case "svg":return "image/svg+xml";case "png":return "image/png";case "jpg":case "jpeg":return "image/jpeg";case "webp":return "image/webp";case "ico":return "image/x-icon";case "woff":return "font/woff";case "woff2":return "font/woff2";case "txt":return "text/plain";case "xml":return "application/xml";case "wasm":return "application/wasm";case "pdf":return "application/pdf";default:return "application/octet-stream";}}
    object Execute() {
        string name=S(G(config,"name"));if(dryRun)return new Dictionary<string,object>{{"Worker",name},{"Modules",modules.Keys.ToArray()},{"SourceMaps",maps.Keys.ToArray()},{"AssetCount",assets.Count},{"Metadata",metadata},{"Manifest",assets},{"Subdomain",subdomain}};
        if(token.Length==0||account.Length==0)throw new InvalidOperationException("CLOUDFLARE_API_TOKEN と CLOUDFLARE_ACCOUNT_ID が必要です。");
        var uri=new Uri(apiBase);if(apiBase!="https://api.cloudflare.com/client/v4"&&!uri.IsLoopback)throw new InvalidOperationException("API override は loopback の検証サーバーに限定します。");
        client=new HttpClient(new HttpClientHandler{AllowAutoRedirect=false});client.Timeout=uri.IsLoopback?TimeSpan.FromSeconds(10):TimeSpan.FromMinutes(5);
        string script="accounts/"+account+"/workers/scripts/"+name;var routes=new List<RoutePlan>();
        // 既知の競合・secret不足・未知migrationを、Worker更新前に検査する。
        foreach(var route in A(G(config,"routes"))){if(route is string)throw new InvalidOperationException("routes は zone_name/zone_id 付き object を使ってください。");string zone=S(G(route,"zone_id")),pattern=S(G(route,"pattern"));
            if(zone.Length==0){if(B(G(route,"zone_name")))zone=S(G(A(Api("GET","zones?name="+Uri.EscapeDataString(S(G(route,"zone_name"))))).FirstOrDefault(),"id"));else{string host=pattern.Split('/')[0];var selected=A(Api("GET","zones?per_page=50")).Where(z=>host==S(G(z,"name"))||host.EndsWith("."+S(G(z,"name")))).OrderByDescending(z=>S(G(z,"name")).Length).FirstOrDefault();zone=S(G(selected,"id"));}}
            if(zone.Length==0)throw new InvalidOperationException("Route のzoneが見つかりません: "+pattern);bool exists=false;
            if(B(G(route,"custom_domain"))){foreach(var domain in A(Api("GET","accounts/"+account+"/workers/domains?hostname="+Uri.EscapeDataString(pattern))))if(S(G(domain,"service"))!=name||(B(G(domain,"environment"))&&S(G(domain,"environment"))!="production"))throw new InvalidOperationException("Custom Domain は別Workerまたは環境が使用中です: "+pattern);}
            else{var existing=A(Api("GET","zones/"+zone+"/workers/routes")).Where(r=>S(G(r,"pattern"))==pattern).ToList();if(existing.Count>0&&S(G(existing[0],"script"))!=name)throw new InvalidOperationException("Route は別Workerが使用中です: "+pattern);exists=existing.Count>0;}
            routes.Add(new RoutePlan{Route=D(route),Zone=zone,Exists=exists});
        }
        foreach(var workflow in A(G(config,"workflows"))){if(B(G(workflow,"script_name"))&&S(G(workflow,"script_name"))!=name)continue;var existing=Api("GET","accounts/"+account+"/workflows/"+S(G(workflow,"name")),missing:true);if(existing!=null&&S(G(existing,"script_name"))!=name)throw new InvalidOperationException("Workflow は別Workerが使用中です: "+S(G(workflow,"name")));}
        var required=A(G(G(config,"secrets"),"required")).Select(S).Where(s=>!secretValues.ContainsKey(s)).ToList();
        if(required.Count>0){var present=A(Api("GET",script+"/secrets",missing:true)).Select(s=>S(G(s,"name"))).ToList();foreach(var secret in required)if(!present.Contains(secret))throw new InvalidOperationException("Worker secret が不足しています: "+secret);}
        var migrations=A(G(config,"migrations")).ToList();
        if(migrations.Count>0){var service=Api("GET","accounts/"+account+"/workers/services/"+name,missing:true);string old=S(G(G(G(service,"default_environment"),"script"),"migration_tag"));var tags=migrations.Select(m=>S(G(m,"tag"))).ToList();if(old.Length>0&&!tags.Contains(old))throw new InvalidOperationException("未知のDO migration tag: "+old);int start=old.Length>0?tags.IndexOf(old)+1:0;if(start<tags.Count){var steps=new List<object>();foreach(var migration in migrations.Skip(start)){var step=Map();foreach(var pair in D(migration))if(pair.Key!="tag")step[pair.Key]=pair.Value;steps.Add(step);}var changes=new Dictionary<string,object>{{"new_tag",tags.Last()},{"steps",steps}};if(old.Length>0)changes["old_tag"]=old;metadata["migrations"]=changes;}}
        if(G(config,"assets")!=null){var session=Api("POST",script+"/assets-upload-session",new Dictionary<string,object>{{"manifest",assets}});string jwt=S(G(session,"jwt")),completion=jwt;
            foreach(var bucket in A(G(session,"buckets"))){var form=new MultipartFormDataContent();foreach(var hashValue in A(bucket)){string hash=S(hashValue),file;if(!assetFiles.TryGetValue(hash,out file))throw new InvalidOperationException("不明なasset hash: "+hash);form.Add(Part(Encoding.UTF8.GetBytes(Convert.ToBase64String(File.ReadAllBytes(file))),Mime(file)),hash,hash);}var uploaded=Api("POST","accounts/"+account+"/workers/assets/upload?base64=true",form,jwt);if(B(G(uploaded,"jwt")))completion=S(G(uploaded,"jwt"));}
            if(completion.Length==0)throw new InvalidOperationException("Asset completion JWT がありません。");D(metadata["assets"])["jwt"]=completion;
        }
        var upload=new MultipartFormDataContent();upload.Add(Part(Encoding.UTF8.GetBytes(Json(metadata)),"application/json"),"metadata");foreach(var pair in modules)upload.Add(Part(pair.Value.Bytes,pair.Value.Mime),pair.Key,pair.Key);foreach(var pair in maps)upload.Add(Part(Encoding.UTF8.GetBytes(pair.Value),"application/source-map"),pair.Key,pair.Key);
        var result=Api("PUT",script,upload);
        foreach(var workflow in A(G(config,"workflows"))){if(B(G(workflow,"script_name"))&&S(G(workflow,"script_name"))!=name)continue;var definition=new Dictionary<string,object>{{"script_name",name},{"class_name",G(workflow,"class_name")}};CopyPresent(definition,workflow,"limits","concurrency","default_retention","jurisdiction");if(B(G(workflow,"schedules")))definition["schedules"]=A(G(workflow,"schedules")).Select(c=>(object)new Dictionary<string,object>{{"cron",c}}).ToList();Api("PUT","accounts/"+account+"/workflows/"+S(G(workflow,"name")),definition);}
        Api("POST",script+"/subdomain",subdomain);var triggers=D(G(config,"triggers"));if(triggers.ContainsKey("crons"))Api("PUT",script+"/schedules",A(triggers["crons"]).Select(c=>(object)new Dictionary<string,object>{{"cron",c}}).ToList());
        foreach(var plan in routes){if(B(G(plan.Route,"custom_domain")))Api("PUT","accounts/"+account+"/workers/domains",new Dictionary<string,object>{{"hostname",G(plan.Route,"pattern")},{"service",name},{"environment","production"},{"zone_id",plan.Zone}});else if(!plan.Exists)Api("POST","zones/"+plan.Zone+"/workers/routes",new Dictionary<string,object>{{"pattern",G(plan.Route,"pattern")},{"script",name}});}
        return new Dictionary<string,object>{{"Worker",name},{"Success",true},{"Result",result}};
    }
    public static string Run(string optionsJson){using(var publisher=new Publisher(D(Parse(optionsJson))))return Json(publisher.Execute());}
    public void Dispose(){if(client!=null)client.Dispose();}
}
}
