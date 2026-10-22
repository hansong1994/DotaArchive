using System;
using System.IO;
using System.Text;
using System.Security.Cryptography;
using System.Collections.Generic;
namespace DotaArchiveIcu {
 public sealed class ExportEntry {
  public string Name, Forwarder; public uint Ordinal;
 }
 public sealed class ImportEntry {
  public string Library; public int NameOffset; public bool DelayLoad;
  public List<string> Symbols = new List<string>();
 }
 public sealed class PeImage {
  public string Path, Sha1; public long Size; public ushort Machine;
  public uint Timestamp; public bool Is64Bit;
  public List<ImportEntry> Imports = new List<ImportEntry>();
  public List<ExportEntry> Exports = new List<ExportEntry>();
  public List<int> IcuucLiteralOffsets = new List<int>();
  byte[] bytes; int sectionTable, sectionCount, optionalHeader, optionalSize;
  uint headerSize, directoryCount; ulong imageBase;
  static string Hash(byte[] data) {
   using(var sha=SHA1.Create()) return BitConverter.ToString(sha.ComputeHash(data)).Replace("-","").ToLowerInvariant();
  }
  static byte[] ReadBytes(string path) {
   FileAttributes attributes=File.GetAttributes(path);
   if((attributes & (FileAttributes.ReparsePoint|FileAttributes.Directory))!=0)
    throw new InvalidDataException("PE source must be an ordinary file.");
   using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read)) {
    if(stream.Length>Int32.MaxValue) throw new InvalidDataException("PE source exceeds supported in-memory size.");
    byte[] data=new byte[(int)stream.Length]; int offset=0;
    while(offset<data.Length) { int count=stream.Read(data,offset,data.Length-offset); if(count==0) throw new EndOfStreamException(); offset+=count; }
    return data;
   }
  }
  public static PeImage Read(string path) {
   return ParseBytes(path,ReadBytes(path));
  }
  public static PeImage ParseBytes(string path,byte[] data) {
   var result=new PeImage { Path=path,Size=data.LongLength,Sha1=Hash(data),bytes=data };
   result.Parse(); result.bytes=null; return result;
  }
  void Need(long at,long length) {
   if(at<0 || length<0 || at>bytes.LongLength-length) throw new InvalidDataException("PE data exceeds file bounds.");
  }
  ushort U16(int at) { Need(at,2); return BitConverter.ToUInt16(bytes,at); }
  uint U32(int at) { Need(at,4); return BitConverter.ToUInt32(bytes,at); }
  ulong U64(int at) { Need(at,8); return BitConverter.ToUInt64(bytes,at); }
  int R(uint rva,int length) {
   ulong end=(ulong)rva+(ulong)length;
   if(length<0) throw new InvalidDataException("Negative RVA length.");
   if(rva<headerSize && end<=headerSize) { Need(rva,length); return checked((int)rva); }
   for(int index=0;index<sectionCount;index++) {
    int at=sectionTable+index*40;
    uint virtualAddress=U32(at+12),rawSize=U32(at+16),rawOffset=U32(at+20);
    if(rva>=virtualAddress && end<=(ulong)virtualAddress+rawSize) {
     long offset=(long)rawOffset+(long)rva-virtualAddress; Need(offset,length); return checked((int)offset);
    }
   }
   throw new InvalidDataException("RVA is not backed by complete file bytes: 0x"+rva.ToString("x"));
  }
  static uint Add(uint value,ulong amount) {
   return checked((uint)((ulong)value+amount));
  }
  string CString(uint rva) {
   var text=new StringBuilder();
   for(uint i=0;;i++) {
    byte value=bytes[R(Add(rva,i),1)];
    if(value==0) return text.ToString();
    if(value>127) throw new InvalidDataException("Non-ASCII PE import/export name.");
    text.Append((char)value);
   }
  }
  void Directory(int index,out uint rva,out uint size) {
   rva=0;size=0;
   if((uint)index>=directoryCount) return;
   int relative=(Is64Bit?112:96)+index*8;
   if(relative+8>optionalSize) throw new InvalidDataException("Data directory exceeds optional header.");
   rva=U32(optionalHeader+relative);size=U32(optionalHeader+relative+4);
   if((rva==0)!=(size==0)) throw new InvalidDataException("Inconsistent PE directory location/size.");
  }
  void Parse() {
   Need(0,64); if(U16(0)!=0x5a4d) throw new InvalidDataException("Missing DOS MZ signature.");
   int pe=checked((int)U32(0x3c)); Need(pe,24);
   if(U32(pe)!=0x4550) throw new InvalidDataException("Missing PE signature.");
   Machine=U16(pe+4);sectionCount=U16(pe+6);Timestamp=U32(pe+8);optionalSize=U16(pe+20);
   optionalHeader=checked(pe+24);Need(optionalHeader,optionalSize);
   ushort magic=U16(optionalHeader);
   if(magic!=0x10b && magic!=0x20b) throw new InvalidDataException("Unsupported PE optional header.");
   Is64Bit=magic==0x20b;
   if(optionalSize<(Is64Bit?112:96)) throw new InvalidDataException("Truncated PE optional header.");
   headerSize=U32(optionalHeader+60);imageBase=Is64Bit?U64(optionalHeader+24):U32(optionalHeader+28);
   directoryCount=U32(optionalHeader+(Is64Bit?108:92));
   if(directoryCount>(uint)((optionalSize-(Is64Bit?112:96))/8)) throw new InvalidDataException("Invalid data-directory count.");
   sectionTable=checked(optionalHeader+optionalSize);Need(sectionTable,(long)sectionCount*40);
   ParseExports();ParseImports(false);ParseImports(true);
   byte[] literal=Encoding.ASCII.GetBytes("icuuc.dll\0");
   for(int i=0;i<=bytes.Length-literal.Length;i++) {
    bool match=true;
    for(int j=0;j<literal.Length;j++) { byte value=bytes[i+j];if(value>=65 && value<=90)value=(byte)(value+32);if(value!=literal[j]){match=false;break;} }
    if(match) IcuucLiteralOffsets.Add(i);
   }
  }
  void ParseExports() {
   uint directory,size; Directory(0,out directory,out size); if(directory==0)return;
   if(size<40) throw new InvalidDataException("Truncated export directory.");
   int at=R(directory,40);uint ordinalBase=U32(at+16),functionCount=U32(at+20),nameCount=U32(at+24);
   uint functions=U32(at+28),names=U32(at+32),ordinals=U32(at+36);
   var values=new Dictionary<uint,ExportEntry>();
   for(uint i=0;i<functionCount;i++) {
    uint function=U32(R(Add(functions,(ulong)i*4),4));if(function==0)continue;
    var entry=new ExportEntry {Ordinal=checked(ordinalBase+i)};
    if(function>=directory && (ulong)function<(ulong)directory+size)entry.Forwarder=CString(function);
    values.Add(i,entry);Exports.Add(entry);
   }
   for(uint i=0;i<nameCount;i++) {
    uint name=U32(R(Add(names,(ulong)i*4),4));ushort function=U16(R(Add(ordinals,(ulong)i*2),2));
    ExportEntry entry;
    if(function>=functionCount || !values.TryGetValue(function,out entry))throw new InvalidDataException("Invalid named-export ordinal.");
    string symbol=CString(name);
    if(entry.Name==null)entry.Name=symbol;
    else Exports.Add(new ExportEntry {Name=symbol,Ordinal=entry.Ordinal,Forwarder=entry.Forwarder});
   }
  }
  void ParseImports(bool delay) {
   uint directory,size;Directory(delay?13:1,out directory,out size);if(directory==0)return;
   int stride=delay?32:20;bool terminated=false;
   for(ulong used=0;used+(uint)stride<=size;used+=(uint)stride) {
    int at=R(Add(directory,used),stride);bool empty=true;
    for(int i=0;i<stride;i+=4)if(U32(at+i)!=0){empty=false;break;}
    if(empty){terminated=true;break;}
    uint name,table;
    if(delay) {
     uint attributes=U32(at);if((attributes & ~1U)!=0)throw new InvalidDataException("Unsupported delay-import attributes.");
     name=U32(at+4);table=U32(at+16);
     if((attributes & 1)==0) {
      if((ulong)name<imageBase || (ulong)table<imageBase)throw new InvalidDataException("Invalid delay-import virtual address.");
      name=checked((uint)((ulong)name-imageBase));table=checked((uint)((ulong)table-imageBase));
     }
    } else { name=U32(at+12);table=U32(at);if(table==0)table=U32(at+16); }
    if(name==0 || table==0)throw new InvalidDataException("Missing import name or thunk table.");
    var entry=new ImportEntry {Library=CString(name),NameOffset=R(name,1),DelayLoad=delay};
    int width=Is64Bit?8:4;ulong flag=Is64Bit?0x8000000000000000UL:0x80000000UL;
    for(ulong i=0;;i++) {
     int thunk=R(Add(table,i*(uint)width),width);ulong value=Is64Bit?U64(thunk):U32(thunk);
     if(value==0)break;
     if((value & flag)!=0) {
      if((value & ~(flag|0xffffUL))!=0)throw new InvalidDataException("Invalid ordinal import bits.");
      entry.Symbols.Add("#"+(value & 0xffffUL));
     } else {
      if(value>UInt32.MaxValue)throw new InvalidDataException("Import thunk is not an RVA; bound imports are unsupported.");
      R((uint)value,2);entry.Symbols.Add(CString(Add((uint)value,2)));
     }
    }
    Imports.Add(entry);
   }
   if(!terminated)throw new InvalidDataException("Import descriptor array has no terminator within its directory.");
  }
  public static string CandidateSha1(string path,string expectedSha1,int nameOffset) {
   byte[] data=ReadBytes(path);
   if(!String.Equals(Hash(data),expectedSha1,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("PE changed after analysis.");
   byte[] expected=Encoding.ASCII.GetBytes("icuuc.dll\0");
   if(nameOffset<0 || nameOffset>data.Length-expected.Length)throw new InvalidDataException("Candidate name is out of range.");
   for(int i=0;i<expected.Length;i++)if(data[nameOffset+i]!=expected[i])throw new InvalidDataException("Candidate import name is not exact lowercase icuuc.dll.");
   data[nameOffset+3]=53;data[nameOffset+4]=50;
   PeImage candidate=ParseBytes(path,data);int privateCount=0;
   foreach(ImportEntry entry in candidate.Imports) {
    if(String.Equals(entry.Library,"icuuc.dll",StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Candidate retains an ICUUC import.");
    if(entry.Library=="icu52.dll" && entry.NameOffset==nameOffset && !entry.DelayLoad)privateCount++;
   }
   if(privateCount!=1 || candidate.IcuucLiteralOffsets.Count!=0)throw new InvalidDataException("Candidate import isolation is incomplete.");
   return candidate.Sha1;
  }
 }
}

namespace DotaArchive
{
    // Compatibility is derived from this client's Steam manifest and original
    // import table. No patch-specific hashes, Build IDs or replacement DLLs
    // are embedded in this implementation.
    public static class HistoricalIcuCompatibility
    {
        private const string Provider = "game/bin/win64/icuuc.dll";
        private const string PrivateProvider = "game/bin/win64/icu52.dll";
        private static readonly string[] Consumers = {
            "game/bin/win64/icui18n.dll", "game/bin/win64/v8.dll"
        };

        public static CompatibilitySpec Derive(VersionEntry version, string root)
        {
            if(version == null || version.Files == null) throw new ArgumentNullException("version");
            if(!String.Equals(version.Executable, "game/bin/win64/dota2.exe", StringComparison.OrdinalIgnoreCase)) return null;
            var files = new System.Collections.Generic.Dictionary<string,FileEntry>(StringComparer.OrdinalIgnoreCase);
            foreach(FileEntry file in version.Files) {
                string relative = VersionCatalog.NormalizeRelative(file.Path);
                if(files.ContainsKey(relative)) throw new InvalidDataException("原始清单含重复文件：" + relative);
                files.Add(relative,file);
            }
            FileEntry providerFile;
            if(!files.TryGetValue(Provider,out providerFile)) return null;
            string providerPath = VersionCatalog.SafePath(root,Provider);
            DotaArchiveIcu.PeImage provider = ReadOriginal(providerPath,providerFile);
            bool oldIcu = false;
            foreach(DotaArchiveIcu.ExportEntry export in provider.Exports)
                if(export.Name != null && export.Name.IndexOf("icu_52",StringComparison.Ordinal) >= 0) oldIcu = true;
            if(!oldIcu) return null;
            if(provider.Machine != 0x8664 || !provider.Is64Bit) throw Unsupported("ICU 提供库并非 Win64");

            var exports = new System.Collections.Generic.Dictionary<string,DotaArchiveIcu.ExportEntry>(StringComparer.Ordinal);
            foreach(DotaArchiveIcu.ExportEntry export in provider.Exports) {
                exports["#" + export.Ordinal] = export;
                if(export.Name != null) exports[export.Name] = export;
            }
            var patches = new System.Collections.Generic.List<PatchSpec>();
            var seen = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(var pair in files) {
                string relative = pair.Key;
                if(relative.IndexOf("/win64/",StringComparison.OrdinalIgnoreCase) < 0 ||
                   (!relative.EndsWith(".dll",StringComparison.OrdinalIgnoreCase) && !relative.EndsWith(".exe",StringComparison.OrdinalIgnoreCase))) continue;
                string target = VersionCatalog.SafePath(root,relative);
                bool expectedConsumer = IsConsumer(relative);
                string backup = expectedConsumer ? VersionCatalog.SafePath(root,"_compatibility/icu52/originals/" + System.IO.Path.GetFileName(relative)) : null;
                bool hasBackup = backup != null && System.IO.File.Exists(backup);
                // The backup is always checked if it exists, even when the
                // installed file is currently original after a redownload.
                string originalPath = hasBackup ? backup : target;
                DotaArchiveIcu.PeImage pe = ReadOriginal(originalPath,pair.Value);
                foreach(DotaArchiveIcu.ImportEntry import in pe.Imports) {
                    if(!String.Equals(import.Library,"icuuc.dll",StringComparison.OrdinalIgnoreCase)) continue;
                    if(!expectedConsumer || !seen.Add(relative)) throw Unsupported("存在额外或重复 ICUUC 使用者：" + relative);
                    if(import.Library != "icuuc.dll" || import.DelayLoad || pe.Machine != 0x8664 || !pe.Is64Bit)
                        throw Unsupported("不受支持的 ICU 导入形式：" + relative);
                    if(pe.IcuucLiteralOffsets.Count != 1 || pe.IcuucLiteralOffsets[0] != import.NameOffset)
                        throw Unsupported("ICU 库名位置无法唯一确认：" + relative);
                    foreach(string symbol in import.Symbols) {
                        DotaArchiveIcu.ExportEntry export;
                        if(!exports.TryGetValue(symbol,out export) || !String.IsNullOrEmpty(export.Forwarder))
                            throw Unsupported("原版 ICU 缺少直接导出：" + symbol);
                    }
                    string candidate = DotaArchiveIcu.PeImage.CandidateSha1(originalPath,pe.Sha1,import.NameOffset);
                    string currentHash = FileHash(target,pair.Value.Size);
                    if(!EqualHash(currentHash,pe.Sha1) && !EqualHash(currentHash,candidate))
                        throw new InvalidDataException("兼容文件不是已验证的原版或本构建兼容版：" + relative);
                    patches.Add(new PatchSpec {
                        Path=relative, OriginalSha1=pe.Sha1, PatchedSha1=candidate,
                        Offsets=new[] { import.NameOffset+3, import.NameOffset+4 },
                        BeforeBytes=new[] {117,99}, AfterBytes=new[] {53,50}
                    });
                }
            }
            // Later clients still ship ICU52 but already use a distinct DLL
            // name (for example icuucx64.dll). Those need no name isolation.
            if(seen.Count == 0) return null;
            if(seen.Count != Consumers.Length) throw Unsupported("缺少完整 ICU52 使用者组合");
            if(files.ContainsKey(PrivateProvider)) throw Unsupported("私有兼容文件名已被原版清单使用");
            string privatePath = VersionCatalog.SafePath(root,PrivateProvider);
            if(System.IO.Directory.Exists(privatePath)) throw Unsupported("私有 ICU 目标是目录");
            if(System.IO.File.Exists(privatePath) && !EqualHash(FileHash(privatePath,providerFile.Size),provider.Sha1))
                throw Unsupported("已有私有 ICU 文件内容未知");
            patches.Sort(delegate(PatchSpec a,PatchSpec b) { return StringComparer.Ordinal.Compare(a.Path,b.Path); });
            return new CompatibilitySpec {
                Description="隔离此构建原版 ICU52 与 Windows 同名库：每个已核实的导入库名仅改变两个字节，原版 DLL 保留备份；不改变游戏逻辑。",
                Patches=patches,
                Copies=new System.Collections.Generic.List<CopySpec> { new CopySpec {Source=Provider,Target=PrivateProvider,Sha1=provider.Sha1} }
            };
        }

        private static bool IsConsumer(string value) {
            foreach(string consumer in Consumers) if(String.Equals(value,consumer,StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
        private static DotaArchiveIcu.PeImage ReadOriginal(string path,FileEntry expected) {
            DotaArchiveIcu.PeImage pe = DotaArchiveIcu.PeImage.Read(path);
            if(pe.Size != expected.Size || !EqualHash(pe.Sha1,expected.Sha1))
                throw new InvalidDataException("兼容分析需要与 Steam 原始清单一致的文件或原版备份：" + expected.Path);
            return pe;
        }
        private static string FileHash(string path,long expectedSize) {
            FileAttributes attributes = System.IO.File.GetAttributes(path);
            if((attributes & (FileAttributes.ReparsePoint|FileAttributes.Directory)) != 0) throw new InvalidDataException("兼容文件必须为普通文件。");
            using(var stream = new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read)) {
                if(stream.Length != expectedSize) throw new InvalidDataException("兼容文件大小与原版不一致：" + path);
                using(var sha = SHA1.Create()) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","").ToLowerInvariant();
            }
        }
        private static bool EqualHash(string a,string b) { return String.Equals(a,b,StringComparison.OrdinalIgnoreCase); }
        private static InvalidDataException Unsupported(string reason) {
            return new InvalidDataException("检测到旧版 ICU52，但尚不能安全生成兼容修复。" + reason + "。已有文件未修改。");
        }
    }
}
