using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using XStoreManager.Core;

if (args.Contains("--wait-child")) { Console.WriteLine("READY"); Console.ReadLine(); return; }
if (args.Contains("--live-catalog"))
{
    var game = new GameProfile { StoreId = "9NNX1VVR3KNQ" };
    var result = await new CatalogService().RefreshAsync(game);
    if (!result.Available || game.Dlcs.Count == 0 || game.Dlcs.Any(d => d.Selected || d.Blocked || !IniService.IsStoreId(d.StoreId))) throw new Exception(result.Detail);
    Console.WriteLine($"PASS live catalogue: {game.Dlcs.Count} verified unselected add-ons. {result.Detail}"); return;
}
int passed = 0;
var tests = new (string, Action)[]
{
    ("local original and adopted INI restored byte-for-byte", () => WithFixture(f =>
    {
        byte[] config = Encoding.Unicode.GetBytes("; original\r\n[Settings]\r\nunlock_all=0\r\n");
        File.WriteAllBytes(f.PathOf(InstallerService.ProxyName), f.Original);
        File.WriteAllBytes(f.PathOf(InstallerService.ConfigName), config);
        f.Service(adopt: true).Install(f.Game); Equal("Installed", f.Service().Status(f.Game).State);
        f.Service().Uninstall(f.Game); Bytes(f.Original, File.ReadAllBytes(f.PathOf(InstallerService.ProxyName))); Bytes(config, File.ReadAllBytes(f.PathOf(InstallerService.ConfigName)));
        False(File.Exists(f.PathOf(InstallerService.OriginalName))); False(File.Exists(f.PathOf(InstallerService.ManifestName)));
    })),
    ("system fallback leaves no game-local files on removal", () => WithFixture(f =>
    { f.Service().Install(f.Game); Bytes(f.Original, File.ReadAllBytes(f.PathOf(InstallerService.OriginalName))); f.Service().Uninstall(f.Game); False(File.Exists(f.PathOf(InstallerService.ProxyName))); False(File.Exists(f.PathOf(InstallerService.ConfigName))); Bytes(f.Original, File.ReadAllBytes(f.SystemOriginal)); })),
    ("corrupted original snapshots preserve all installation evidence", () =>
    {
        foreach (string name in new[] { InstallerService.ProxyName, InstallerService.ConfigName }) WithFixture(f =>
        {
            File.WriteAllBytes(f.PathOf(InstallerService.ProxyName),f.Original);
            File.WriteAllText(f.PathOf(InstallerService.ConfigName),"original INI bytes");
            var service=f.Service(adopt:true);service.Install(f.Game);
            string manifestPath=f.PathOf(InstallerService.ManifestName);
            var manifest=JsonNode.Parse(File.ReadAllText(manifestPath))!;
            byte[] original=Convert.FromBase64String(manifest["Originals"]![name]!.GetValue<string>());original[0]^=1;
            manifest["Originals"]![name]=Convert.ToBase64String(original);File.WriteAllText(manifestPath,manifest.ToJsonString());
            string before=f.Snapshot();Equal("Conflict",service.Status(f.Game).State);
            Throws<InvalidDataException>(()=>service.Install(f.Game));Equal(before,f.Snapshot());
            Throws<InvalidDataException>(()=>service.Uninstall(f.Game));Equal(before,f.Snapshot());
            Bytes(f.Original,File.ReadAllBytes(f.PathOf(InstallerService.OriginalName)));True(File.Exists(manifestPath));
        });
    }),
    ("original runtime cross-check rejects altered snapshot and snapshot hash", () => WithFixture(f =>
    {
        File.WriteAllBytes(f.PathOf(InstallerService.ProxyName),f.Original);var service=f.Service();service.Install(f.Game);
        string path=f.PathOf(InstallerService.ManifestName);var manifest=JsonNode.Parse(File.ReadAllText(path))!;
        byte[] original=f.Original.ToArray();original[0]^=1;manifest["Originals"]![InstallerService.ProxyName]=Convert.ToBase64String(original);manifest["OriginalHashes"]![InstallerService.ProxyName]=Convert.ToHexString(SHA256.HashData(original));File.WriteAllText(path,manifest.ToJsonString());
        string before=f.Snapshot();Equal("Conflict",service.Status(f.Game).State);Throws<InvalidDataException>(()=>service.Install(f.Game));Throws<InvalidDataException>(()=>service.Uninstall(f.Game));Equal(before,f.Snapshot());
    })),
    ("missing integrity metadata and old schema fail closed", () =>
    {
        foreach(bool oldSchema in new[]{false,true})WithFixture(f=>
        {
            var service=f.Service();service.Install(f.Game);string path=f.PathOf(InstallerService.ManifestName);var manifest=JsonNode.Parse(File.ReadAllText(path))!;
            if(oldSchema)manifest["Version"]=1;else manifest.AsObject().Remove("OriginalHashes");File.WriteAllText(path,manifest.ToJsonString());
            string before=f.Snapshot();Equal("Conflict",service.Status(f.Game).State);Throws<InvalidDataException>(()=>service.Install(f.Game));Throws<InvalidDataException>(()=>service.Uninstall(f.Game));Equal(before,f.Snapshot());
        });
    }),
    ("second install updates settings and repairs missing payload", () => WithFixture(f =>
    { var s = f.Service(); s.Install(f.Game); f.Game.Enabled = true; s.Install(f.Game); True(File.ReadAllText(f.PathOf(InstallerService.ConfigName)).Contains("enabled=1")); File.Delete(f.PathOf(InstallerService.ProxyName)); s.Install(f.Game); True(s.Status(f.Game).IsInstalled); s.Uninstall(f.Game); })),
    ("unknown INI, proxy, and backup are preserved", () => WithFixture(f =>
    {
        File.WriteAllText(f.PathOf(InstallerService.ConfigName), "unknown"); Throws<IOException>(() => f.Service().Install(f.Game)); Equal("unknown", File.ReadAllText(f.PathOf(InstallerService.ConfigName))); File.Delete(f.PathOf(InstallerService.ConfigName));
        File.WriteAllText(f.PathOf(InstallerService.OriginalName), "unknown backup"); Throws<IOException>(() => f.Service().Install(f.Game)); Equal("unknown backup", File.ReadAllText(f.PathOf(InstallerService.OriginalName))); File.Delete(f.PathOf(InstallerService.OriginalName));
        File.WriteAllBytes(f.PathOf(InstallerService.ProxyName), f.Payload); Throws<InvalidDataException>(() => f.Service().Install(f.Game)); Bytes(f.Payload, File.ReadAllBytes(f.PathOf(InstallerService.ProxyName)));
    })),
    ("external changes block install and removal before mutations", () => WithFixture(f =>
    { var s = f.Service(); s.Install(f.Game); File.AppendAllText(f.PathOf(InstallerService.ConfigName), "; changed"); var before = f.Snapshot(); Throws<IOException>(() => s.Install(f.Game)); Throws<IOException>(() => s.Uninstall(f.Game)); Equal(before, f.Snapshot()); Equal("Conflict", s.Status(f.Game).State); })),
    ("every interrupted install mutation rolls back exact state", () =>
    {
        for (int step = 1; step <= 4; step++) WithFixture(f =>
        { File.WriteAllBytes(f.PathOf(InstallerService.ProxyName), f.Original); File.WriteAllText(f.PathOf(InstallerService.ConfigName), "prior ini"); var before = f.Snapshot(); Throws<InjectedException>(() => f.Service(true, i => { if (i == step) throw new InjectedException(); }).Install(f.Game)); Equal(before, f.Snapshot()); });
    }),
    ("every interrupted uninstall mutation rolls back installed state", () =>
    {
        for (int step = 1; step <= 4; step++) WithFixture(f =>
        { f.Service().Install(f.Game); var before = f.Snapshot(); Throws<InjectedException>(() => f.Service(false, i => { if (i == step) throw new InjectedException(); }).Uninstall(f.Game)); Equal(before, f.Snapshot()); });
    }),
    ("durable interrupted journal recovers before retry", () => WithFixture(f =>
    {
        var before = new Dictionary<string,string?> { [InstallerService.ProxyName]=null,[InstallerService.OriginalName]=null,[InstallerService.ConfigName]=null,[InstallerService.ManifestName]=null };
        var after = new Dictionary<string,string?>(before) { [InstallerService.ProxyName]=Convert.ToBase64String(f.Payload) };
        File.WriteAllBytes(f.PathOf(InstallerService.ProxyName),f.Payload);
        File.WriteAllText(f.PathOf(InstallerService.JournalName),JsonSerializer.Serialize(new {Version=1,Directory=f.Root,Before=before,After=after}));
        Equal("Interrupted", f.Service().Status(f.Game).State); f.Service().Recover(f.Game); False(File.Exists(f.PathOf(InstallerService.ProxyName))); False(File.Exists(f.PathOf(InstallerService.JournalName)));
    })),
    ("journal conflict preserves all evidence", () => WithFixture(f =>
    {
        var state = new Dictionary<string,string?> { [InstallerService.ProxyName]=null,[InstallerService.OriginalName]=null,[InstallerService.ConfigName]=null,[InstallerService.ManifestName]=null };
        File.WriteAllText(f.PathOf(InstallerService.JournalName),JsonSerializer.Serialize(new {Version=1,Directory=f.Root,Before=state,After=state}));
        File.WriteAllText(f.PathOf(InstallerService.ConfigName),"external"); string before = f.Snapshot(); Throws<IOException>(()=>f.Service().Recover(f.Game)); Equal(before,f.Snapshot());
    })),
    ("locked files fail preflight without transaction", () => WithFixture(f =>
    { f.Service().Install(f.Game); using var locked = File.Open(f.PathOf(InstallerService.ConfigName), FileMode.Open, FileAccess.Read, FileShare.None); Throws<IOException>(()=>f.Service().Uninstall(f.Game)); False(File.Exists(f.PathOf(InstallerService.JournalName))); })),
    ("running game blocks install", () => WithFixture(f =>
    { Throws<IOException>(()=>f.Service(running:true).Install(f.Game)); False(File.Exists(f.PathOf(InstallerService.ProxyName))); })),
    ("real running process is detected", () => WithFixture(f =>
    {
        foreach (string file in Directory.EnumerateFiles(AppContext.BaseDirectory)) File.Copy(file, f.PathOf(Path.GetFileName(file)));
        string executable = f.PathOf("XStoreManager.Tests.exe"); f.Game.ExecutablePath = executable;
        using var child = Process.Start(new ProcessStartInfo(executable, "--wait-child") { UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true })!;
        try { Equal("READY",child.StandardOutput.ReadLine()!); Throws<IOException>(()=>f.Service(realProcessCheck:true).Install(f.Game)); False(File.Exists(f.PathOf(InstallerService.ProxyName))); }
        finally { child.StandardInput.WriteLine(); if (!child.WaitForExit(5000)) child.Kill(); }
    })),
    ("excluded profiles and invalid paths are rejected", () => WithFixture(f =>
    { f.Game.Excluded = true; Throws<InvalidOperationException>(()=>f.Service().Install(f.Game)); f.Game.Excluded=false; f.Game.Directory="relative"; Throws<InvalidDataException>(()=>f.Service().Install(f.Game)); f.Game.Directory=f.Root; f.Game.ExecutablePath=f.SystemOriginal; Throws<InvalidDataException>(()=>f.Service().Install(f.Game)); })),
    ("junction roots and files are rejected", () => WithFixture(f =>
    {
        string link=Path.Combine(f.Parent,"link");
        using var command=Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{f.Root}\"") {UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true})!; command.WaitForExit();
        Equal(0,command.ExitCode); f.Game.Directory=link; Throws<IOException>(()=>f.Service().Install(f.Game)); Directory.Delete(link);
    })),
    ("x86 payload and checksum mismatch are rejected", () => WithFixture(f =>
    {
        byte[] x86 = f.Payload.ToArray(); x86[0x84]=0x4c; x86[0x85]=0x01; File.WriteAllBytes(f.PayloadPath,x86); Throws<InvalidDataException>(()=>f.Service().Install(f.Game));
        File.WriteAllBytes(f.PayloadPath,f.Payload); var production = new InstallerService(f.PayloadPath,new() {SystemRuntimePath=f.SystemOriginal,VerifyOriginal=_=>{},IsGameRunning=_=>false}); File.WriteAllText(f.PayloadPath+".sha256","0000"); Throws<InvalidDataException>(()=>production.Install(f.Game)); False(File.Exists(f.PathOf(InstallerService.ManifestName)));
    })),
    ("genuine Microsoft runtime passes production trust verification", () => WithFixture(f =>
    {
        string runtime=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),InstallerService.ProxyName);
        byte[] before=File.ReadAllBytes(runtime);
        File.WriteAllText(f.PayloadPath+".sha256",Convert.ToHexString(SHA256.HashData(f.Payload)));
        var service=new InstallerService(f.PayloadPath,new(){IsGameRunning=_=>false}); service.Install(f.Game);
        Bytes(before,File.ReadAllBytes(f.PathOf(InstallerService.OriginalName))); service.Uninstall(f.Game); Bytes(before,File.ReadAllBytes(runtime));
    })),
    ("Windows and bundled engine destinations are rejected", () => WithFixture(f =>
    {
        f.Game.Directory=Environment.GetFolderPath(Environment.SpecialFolder.System); Throws<InvalidDataException>(()=>f.Service().Install(f.Game));
        f.Game.Directory=f.Parent; Throws<InvalidDataException>(()=>f.Service().Install(f.Game));
    })),
    ("invalid mount paths fail before installation", () => WithFixture(f =>
    { f.Game.Dlcs.Add(new(){StoreId="9NNX1VVR3KNQ",MountPath="relative",Selected=true}); Throws<InvalidDataException>(()=>f.Service().Install(f.Game)); False(File.Exists(f.PathOf(InstallerService.ManifestName))); })),
    ("INI zero values, duplicate keys, blacklist and mount paths", () =>
    {
        var g=new GameProfile(); IniService.Import("[Settings]\nunlock_all=1\nunlock_all=0\nenabled=0\nlog_enabled=0\n[DLCs]\n9N6X17XQBNW0=0\n9NNX1VVR3KNQ=1\n[Blacklist]\n9N6X17XQBNW0=0\n9NNX1VVR3KNQ=1\n[MountPaths]\n9NNX1VVR3KNQ=C:\\DLC",g);
        False(g.UnlockAll); False(g.Enabled); False(g.EnableLogging); False(g.Dlcs[0].Selected); False(g.Dlcs[0].Blocked); True(g.Dlcs[1].Selected); True(g.Dlcs[1].Blocked);
        string text=IniService.Serialize(g); False(text.Split("[Blacklist]")[0].Contains("9NNX1VVR3KNQ=1")); False(text.Contains("C:\\DLC")); True(text.Contains("[Blacklist]"));
    }),
    ("profile persistence retains exclusions and selections", () => WithFixture(f =>
    {
        var store = new ProfileStore(Path.Combine(f.Parent,"profiles")); var state = store.Load(); Equal(0,state.Games.Count); f.Game.Excluded=true; f.Game.Dlcs.Add(new(){StoreId="9NNX1VVR3KNQ",Selected=true,Blocked=true,MountPath="D:\\DLC"}); state.Games.Add(f.Game); state.LibraryRoots.Add(f.Root); store.Save(state); store.Save(state); var loaded=store.Load(); True(loaded.Games[0].Excluded); True(loaded.Games[0].Dlcs[0].Blocked); Equal(f.Root,loaded.LibraryRoots[0]); False(loaded.Games[0].Enabled);
    })),
    ("duplicate Store IDs normalize with blacklist precedence", () =>
    {
        var game=new GameProfile();game.Dlcs.Add(new(){StoreId=" 9nnx1vvr3knq ",Selected=true});game.Dlcs.Add(new(){StoreId="9NNX1VVR3KNQ",Blocked=true});
        string ini=IniService.Serialize(game); False(ini.Split("[Blacklist]")[0].Contains("9NNX1VVR3KNQ")); True(ini.Contains("9NNX1VVR3KNQ=1")); False(ini.Contains("9nnx1vvr3knq"));
    }),
    ("discovery manifest, manual folder, XML escapes and path traversal", () => WithFixture(f =>
    {
        File.WriteAllText(f.PathOf("game.exe"),"test"); var service=new DiscoveryService(); Equal("game",service.InspectFolder(f.Root).Name);
        File.WriteAllText(f.PathOf("MicrosoftGame.config"),"<Game><Identity Name=\"Game.Id\"/><StoreId>9NNX1VVR3KNQ</StoreId><ShellVisuals DefaultDisplayName=\"Game &amp; Name\"/><ExecutableList><Executable Name=\"game.exe\"/></ExecutableList></Game>"); var game=service.InspectFolder(f.Root); Equal("Game & Name",game.Name); Equal("9NNX1VVR3KNQ",game.StoreId); Equal(1,new DiscoveryService(()=>[f.Root]).ScanAsync([]).GetAwaiter().GetResult().Count); Equal(1,new DiscoveryService(()=>[f.Root]).ScanAsync([f.Root]).GetAwaiter().GetResult().Count);
        File.WriteAllText(f.PathOf("MicrosoftGame.config"),"<Game><Executable Name=\"../outside.exe\"/></Game>"); Throws<InvalidDataException>(()=>service.InspectFolder(f.Root));
    })),
    ("catalogue accepts only related verified IDs and preserves choices", () =>
    {
        var game=new GameProfile {StoreId="9NNX1VVR3KNQ"}; game.Dlcs.Add(new(){StoreId="9N6X17XQBNW0",Name="Old",Selected=true,Blocked=true,MountPath="D:\\DLC"});
        using var http=new HttpClient(new FakeHandler("{\"Cards\":[{\"ProductId\":\"9N6X17XQBNW0\",\"ParentId\":\"9NNX1VVR3KNQ\",\"Title\":\"Known\"},{\"ProductId\":\"9N6X17XQBNW1\",\"ParentId\":\"9NNX1VVR3KNQ\",\"Title\":\"New\"},{\"ProductId\":\"9N6X17XQBNW2\",\"ParentId\":\"Other\",\"Title\":\"Wrong\"}],\"TotalItems\":4}"));
        var result=new CatalogService(http).RefreshAsync(game).GetAwaiter().GetResult(); True(result.Available); Equal(2,game.Dlcs.Count); True(game.Dlcs[0].Blocked); True(game.Dlcs[0].Selected); Equal("Known",game.Dlcs[0].Name); False(game.Dlcs[1].Selected); True(result.Detail.Contains("omitted"));
        using var envelope=new HttpClient(new FakeHandler("{\"Payload\":{\"Cards\":[{\"ProductId\":\"9N6X17XQBNW3\",\"ParentId\":\"9NNX1VVR3KNQ\",\"Title\":\"Envelope\"}]}}"));True(new CatalogService(envelope).RefreshAsync(game).GetAwaiter().GetResult().Available);Equal(3,game.Dlcs.Count);
        using var failed=new HttpClient(new FakeHandler("invalid")); False(new CatalogService(failed).RefreshAsync(game).GetAwaiter().GetResult().Available); Equal(3,game.Dlcs.Count);
    })
};
foreach (var (name,test) in tests) { try { test(); Console.WriteLine("PASS " + name); passed++; } catch(Exception e) { Console.Error.WriteLine("FAIL " + name + "\n" + e); Environment.ExitCode=1; } }
Console.WriteLine($"{passed}/{tests.Length} tests passed.");
static void WithFixture(Action<Fixture> test) { using var fixture = new Fixture(); test(fixture); }
static void True(bool value) { if (!value) throw new Exception("Expected true."); }
static void False(bool value) => True(!value);
static void Equal<T>(T expected,T actual) { if (!EqualityComparer<T>.Default.Equals(expected,actual)) throw new Exception($"Expected {expected}, got {actual}."); }
static void Bytes(byte[] expected,byte[] actual) => True(expected.SequenceEqual(actual));
static void Throws<T>(Action action) where T:Exception { try { action(); } catch(T) { return; } throw new Exception("Expected " + typeof(T).Name); }
sealed class InjectedException:Exception { }
sealed class FakeHandler(string response):HttpMessageHandler { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(response)}); }
sealed class Fixture:IDisposable
{
    public string Parent {get;}=Path.Combine(Path.GetTempPath(),"XStoreManagerTests",Guid.NewGuid().ToString("N"));
    public string Root {get;} public string PayloadPath {get;} public string SystemOriginal {get;}
    public byte[] Original {get;}=Pe(3); public byte[] Payload {get;}=Pe(7);
    public GameProfile Game {get;}
    public Fixture() { Root=Path.Combine(Parent,"game");Directory.CreateDirectory(Root);PayloadPath=Path.Combine(Parent,"payload.dll");SystemOriginal=Path.Combine(Parent,"XGameRuntime.dll");File.WriteAllBytes(PayloadPath,Payload);File.WriteAllBytes(SystemOriginal,Original);Game=new(){Name="Fixture",Directory=Root}; }
    public string PathOf(string file)=>Path.Combine(Root,file);
    public InstallerService Service(bool adopt=false,Action<int>? after=null,bool running=false,bool realProcessCheck=false)=>new(PayloadPath,new(){SystemRuntimePath=SystemOriginal,AdoptExistingConfiguration=adopt,VerifyPayload=p=>{if(!File.ReadAllBytes(p).SequenceEqual(Payload)) throw new InvalidDataException("Unknown payload.");},VerifyOriginal=p=>{if(!File.ReadAllBytes(p).SequenceEqual(Original)) throw new InvalidDataException("Unknown original.");},IsGameRunning=realProcessCheck?null:_=>running,AfterMutation=after});
    public string Snapshot()=>string.Join("|",Directory.EnumerateFiles(Root).Order().Select(p=>Path.GetFileName(p)+":"+Convert.ToBase64String(File.ReadAllBytes(p))));
    public void Dispose() { if(Directory.Exists(Parent)) Directory.Delete(Parent,true); }
    static byte[] Pe(byte marker) { byte[] data=new byte[512];data[0]=(byte)'M';data[1]=(byte)'Z';BitConverter.GetBytes(0x80).CopyTo(data,0x3c);data[0x80]=(byte)'P';data[0x81]=(byte)'E';BitConverter.GetBytes((ushort)0x8664).CopyTo(data,0x84);BitConverter.GetBytes((ushort)240).CopyTo(data,0x94);BitConverter.GetBytes((ushort)0x2022).CopyTo(data,0x96);BitConverter.GetBytes((ushort)0x20b).CopyTo(data,0x98);data[500]=marker;return data; }
}
