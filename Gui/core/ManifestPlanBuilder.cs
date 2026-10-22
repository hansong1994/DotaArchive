using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace DotaArchive {
 public static class ManifestPlanBuilder {
  private const string EmptySha1="da39a3ee5e6b4b0d3255bfef95601890afd80709";
  private static readonly Regex Entry=new Regex(@"^\s*(?<Size>[0-9]+)\s+(?<Chunks>[0-9]+)\s+(?<Hash>[0-9a-fA-F]{40})?\s+(?<Flags>[0-9a-fA-F]+) (?<Name>.+)$",RegexOptions.CultureInvariant);
  // No client files are accessed. The caller must supply a complete, resolved
  // depot set; the manifests alone cannot prove historical build membership.
  public static ManifestPreparationResult Prepare(IEnumerable<DepotEntry> expectedDepots,string manifestDirectory,CancellationToken token,Action<ProgressInfo> progress) {
   if(expectedDepots==null) throw new ArgumentNullException("expectedDepots");
   token.ThrowIfCancellationRequested();
   VersionCatalog.SafePath(manifestDirectory,".manifest-plan-validation");
   var depots=new List<DepotEntry>();var ids=new HashSet<string>(StringComparer.Ordinal);
   foreach(DepotEntry depot in expectedDepots) {
    token.ThrowIfCancellationRequested();uint id;ulong manifest;
    if(depot==null || !CanonicalNumber(depot.DepotId) || !uint.TryParse(depot.DepotId,NumberStyles.None,CultureInfo.InvariantCulture,out id) || !CanonicalNumber(depot.ManifestId) || !ulong.TryParse(depot.ManifestId,NumberStyles.None,CultureInfo.InvariantCulture,out manifest) || !ids.Add(depot.DepotId)) throw new InvalidDataException("内容库或清单编号无效，或内容库重复。");
    depots.Add(new DepotEntry {DepotId=depot.DepotId,ManifestId=depot.ManifestId,Name=depot.Name});
   }
   if(depots.Count==0) throw new InvalidDataException("未指定待准备的内容库清单。");
   var files=new Dictionary<string,FileEntry>(StringComparer.OrdinalIgnoreCase);
   var directories=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
   var result=new ManifestPreparationResult {Depots=depots,Manifests=new List<PreparedManifestRecord>(),Assurance="Expected files prepared from exact requested official-engine text manifests and locally consistent binary SHA1 sidecars. Binary identity and text-to-binary equivalence are not independently decoded; no client files or launch were verified."};
   foreach(DepotEntry depot in depots) {
    token.ThrowIfCancellationRequested();
    if(progress!=null) progress(new ProgressInfo {Phase="preparing_manifests",Message="检查内容库 "+depot.DepotId+" 的清单",Percent=100.0*result.Manifests.Count/depots.Count,CompletedDepots=result.Manifests.Count,TotalDepots=depots.Count,CurrentFile=""});
    PreparedManifestRecord record=ReadManifest(depot,manifestDirectory,files,directories,token);
    depot.Bytes=record.Bytes;result.Manifests.Add(record);
   }
   foreach(string directory in directories) if(files.ContainsKey(directory)) throw new InvalidDataException("清单路径同时用作文件和目录："+directory);
   result.Files=new List<FileEntry>(files.Values);result.Files.Sort(delegate(FileEntry a,FileEntry b) {return StringComparer.OrdinalIgnoreCase.Compare(a.Path,b.Path);});
   result.Directories=new List<string>(directories);result.Directories.Sort(StringComparer.OrdinalIgnoreCase);
   try {foreach(FileEntry file in result.Files) {token.ThrowIfCancellationRequested();checked {result.TotalBytes+=file.Size;}}}
   catch(OverflowException ex) {throw new InvalidDataException("去重后的文件总大小溢出。",ex);}
   token.ThrowIfCancellationRequested();
   if(progress!=null) progress(new ProgressInfo {Phase="manifests_prepared",Message="文件计划已生成；客户端尚待下载和校验",Percent=100,CompletedDepots=depots.Count,TotalDepots=depots.Count,CurrentFile=""});
   return result;
  }
  private static PreparedManifestRecord ReadManifest(DepotEntry depot,string root,Dictionary<string,FileEntry> files,HashSet<string> directories,CancellationToken token) {
   string key=depot.DepotId+"_"+depot.ManifestId;
   string textPath=VersionCatalog.SafePath(root,"manifest_"+key+".txt");
   string binaryPath=VersionCatalog.SafePath(root,".DepotDownloader/"+key+".manifest");
   string shaPath=VersionCatalog.SafePath(root,".DepotDownloader/"+key+".manifest.sha");
   var record=new PreparedManifestRecord {DepotId=depot.DepotId,ManifestId=depot.ManifestId};
   using(FileStream shaFile=OpenRead(shaPath,20)) using(FileStream binary=OpenRead(binaryPath,512L*1024*1024)) {
    if(shaFile.Length!=20 || binary.Length==0) throw new InvalidDataException("清单缓存或 SHA1 文件长度无效："+key);
    string expected=HashBytes(shaFile,token);record.BinarySha1=ComputeHash(binary,SHA1.Create(),token);
    if(!String.Equals(expected,record.BinarySha1,StringComparison.Ordinal)) throw new InvalidDataException("清单缓存 SHA1 不匹配："+key);
   }
   using(FileStream text=OpenRead(textPath,128L*1024*1024)) {
    record.TextSha256=ComputeHash(text,SHA256.Create(),token);text.Position=0;
    using(var reader=new StreamReader(text,new UTF8Encoding(false,true),true,65536)) {
     var headers=new HashSet<string>(StringComparer.Ordinal);var ownPaths=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
     bool table=false;long declaredEntries=-1,declaredBytes=-1,declaredCompressed=-1,declaredChunks=-1,parsedBytes=0,chunkReferences=0;int entries=0;
     string line;while((line=reader.ReadLine())!=null) {
      token.ThrowIfCancellationRequested();if(line.Length>32768) throw new InvalidDataException("清单行过长："+key);
      if(String.IsNullOrWhiteSpace(line)) continue;
      if(!table) {
       Match m;
       if((m=Regex.Match(line,@"\AContent Manifest for Depot\s+([0-9]+)\s*\z")).Success) {Header(headers,"depot");if(m.Groups[1].Value!=depot.DepotId) throw new InvalidDataException("清单头内容库编号不符："+key);}
       else if((m=Regex.Match(line,@"\AManifest ID / date\s*:\s*([0-9]+)\s*/\s*(\S.*?)\s*\z")).Success) {Header(headers,"manifest");if(m.Groups[1].Value!=depot.ManifestId) throw new InvalidDataException("清单头 Manifest 编号不符："+key);record.ManifestCreationTimeText=m.Groups[2].Value;}
       else if((m=Regex.Match(line,@"\ATotal number of files\s*:\s*([0-9]+)\s*\z")).Success) {Header(headers,"files");declaredEntries=Number(m.Groups[1].Value);}
       else if((m=Regex.Match(line,@"\ATotal number of chunks\s*:\s*([0-9]+)\s*\z")).Success) {Header(headers,"chunks");declaredChunks=Number(m.Groups[1].Value);}
       else if((m=Regex.Match(line,@"\ATotal bytes on disk\s*:\s*([0-9]+)\s*\z")).Success) {Header(headers,"bytes");declaredBytes=Number(m.Groups[1].Value);}
       else if((m=Regex.Match(line,@"\ATotal bytes compressed\s*:\s*([0-9]+)\s*\z")).Success) {Header(headers,"compressed");declaredCompressed=Number(m.Groups[1].Value);}
       else if(Regex.IsMatch(line,@"\A\s*Size\s+Chunks\s+File SHA\s+Flags\s+Name\s*\z")) {if(headers.Count!=6) throw new InvalidDataException("清单头不完整："+key);table=true;}
       else throw new InvalidDataException("未识别的清单头："+key);
       continue;
      }
      Match entry=Entry.Match(line);if(!entry.Success) throw new InvalidDataException("清单条目格式无效："+key);
      long size=Number(entry.Groups["Size"].Value),chunks=Number(entry.Groups["Chunks"].Value);uint flags;
      if(!uint.TryParse(entry.Groups["Flags"].Value,NumberStyles.AllowHexSpecifier,CultureInfo.InvariantCulture,out flags)) throw new InvalidDataException("清单条目标记溢出："+key);
      if((flags&0x200)!=0) throw new InvalidDataException("不支持包含链接条目的 Windows 文件计划："+key);
      bool directory=(flags&0x40)!=0;string path=entry.Groups["Name"].Value.Replace('\\','/');
      if(directory) path=path.TrimEnd('/');path=VersionCatalog.NormalizeRelative(path);
      if(!ownPaths.Add(path)) throw new InvalidDataException("同一清单包含重复路径："+path);
      string hash=entry.Groups["Hash"].Value.ToLowerInvariant();
      if(directory) {
       if(size!=0 || chunks!=0 || (hash.Length!=0 && !EmptyHash(hash))) throw new InvalidDataException("目录条目包含文件内容："+path);
       directories.Add(path);
      } else {
       if(hash.Length!=40 || (size>0 && (VersionCatalog.IsZeroHash(hash) || chunks==0)) || (size==0 && (!EmptyHash(hash) || chunks!=0))) throw new InvalidDataException("文件大小、分块或 SHA1 无效："+path);
       FileEntry earlier;
       if(files.TryGetValue(path,out earlier)) {
        if(earlier.Size!=size || !(String.Equals(earlier.Sha1,hash,StringComparison.OrdinalIgnoreCase) || (size==0 && EmptyHash(earlier.Sha1) && EmptyHash(hash)))) throw new InvalidDataException("不同内容库的文件内容冲突："+path);
       } else files.Add(path,new FileEntry {Path=path,Size=size,Sha1=hash});
      }
      string parent=path;while(parent.LastIndexOf('/')>=0) {parent=parent.Substring(0,parent.LastIndexOf('/'));directories.Add(parent);}
      try {checked {entries++;parsedBytes+=size;chunkReferences+=chunks;}}
      catch(OverflowException ex) {throw new InvalidDataException("清单计数或大小溢出："+key,ex);}
     }
     if(!table || entries!=declaredEntries || parsedBytes!=declaredBytes || declaredChunks>chunkReferences || (declaredChunks==0 && chunkReferences!=0)) throw new InvalidDataException("清单条目数量、总大小或分块数量不符："+key);
     record.EntryCount=entries;record.Bytes=parsedBytes;record.CompressedBytes=declaredCompressed;record.UniqueChunkCount=declaredChunks;
    }
   }
   return record;
  }
  private static bool CanonicalNumber(string value) {return value!=null && Regex.IsMatch(value,@"\A[1-9][0-9]*\z",RegexOptions.CultureInvariant);}
  private static bool EmptyHash(string value) {return VersionCatalog.IsZeroHash(value) || String.Equals(value,EmptySha1,StringComparison.OrdinalIgnoreCase);}
  private static long Number(string value) {long n;if(!long.TryParse(value,NumberStyles.None,CultureInfo.InvariantCulture,out n)) throw new InvalidDataException("清单数字超出支持范围。");return n;}
  private static void Header(HashSet<string> seen,string name) {if(!seen.Add(name)) throw new InvalidDataException("清单头重复："+name);}
  private static FileStream OpenRead(string path,long maxBytes) {var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);if(file.Length>maxBytes) {file.Dispose();throw new InvalidDataException("清单文件过大："+Path.GetFileName(path));}return file;}
  private static string HashBytes(Stream stream,CancellationToken token) {var bytes=new byte[20];int read=0;while(read<bytes.Length) {token.ThrowIfCancellationRequested();int n=stream.Read(bytes,read,bytes.Length-read);if(n==0) throw new EndOfStreamException();read+=n;}return Hex(bytes);}
  private static string ComputeHash(Stream stream,HashAlgorithm hash,CancellationToken token) {
   using(hash) {var buffer=new byte[1024*1024];int read;while((read=stream.Read(buffer,0,buffer.Length))!=0) {token.ThrowIfCancellationRequested();hash.TransformBlock(buffer,0,read,buffer,0);}token.ThrowIfCancellationRequested();hash.TransformFinalBlock(buffer,0,0);return Hex(hash.Hash);}
  }
  private static string Hex(byte[] bytes) {return BitConverter.ToString(bytes).Replace("-","").ToLowerInvariant();}
 }
}
