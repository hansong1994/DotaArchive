using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace DotaArchive {
 public static class RuntimeLauncher {
  const string LocalArguments = "-steam -insecure -console -condebug -novid -windowed -w 1280 -h 720 -language schinese +sv_lan 1";
  public static int PrepareAndLaunch(VersionEntry version, string directory) {
   return PrepareAndLaunch(version,directory,CancellationToken.None);
  }
  public static int PrepareAndLaunch(VersionEntry version, string directory,CancellationToken token) {
   token.ThrowIfCancellationRequested();
   RequireNoGame();
   if (Process.GetProcessesByName("steam").Length == 0)
    throw new InvalidOperationException("请先打开 Steam 客户端并登录，再启动历史版本。");
   if (version.LaunchArguments != LocalArguments)
    throw new InvalidDataException("启动参数尚未经过验证。");
   string exe = VersionCatalog.SafePath(directory, version.Executable);
   RequireOriginal(version, directory, version.Executable);
   RequireOriginal(version, directory, "game/dota/bin/win64/client.dll");
   RequireOriginal(version, directory, "game/dota/bin/win64/server.dll");
   PrepareCompatibility(version, directory);
   string hint = VersionCatalog.SafePath(directory, "game/bin/win64/steam_appid.txt");
   if (File.Exists(hint)) {
    if (File.ReadAllText(hint).Trim('\0','\t','\r','\n',' ') != "570")
     throw new InvalidDataException("已有 steam_appid.txt 不是 Dota 2 的编号 570。");
   } else File.WriteAllText(hint, "570", Encoding.ASCII);
   RequireNoGame();
   token.ThrowIfCancellationRequested();
   using (Process p = Process.Start(new ProcessStartInfo(exe, LocalArguments) {
    WorkingDirectory = Path.GetDirectoryName(exe), UseShellExecute = false
   })) {
    if (p == null) throw new IOException("未能启动历史客户端。");
    return p.Id;
   }
  }
  public static void RequireNoGame() {
   Process[] games = Process.GetProcessesByName("dota2");
   bool running = games.Length != 0;
   foreach (Process game in games) game.Dispose();
   if (running) throw new InvalidOperationException("已有 Dota 2 正在运行，请先退出游戏再操作。");
  }
  static void RequireOriginal(VersionEntry v, string root, string relative) {
   FileEntry expected = v.Files.FirstOrDefault(f => String.Equals(f.Path, relative, StringComparison.OrdinalIgnoreCase));
   if (expected == null) throw new InvalidDataException("版本索引缺少关键文件：" + relative);
   string path = VersionCatalog.SafePath(root, relative);
   if (!File.Exists(path) || new FileInfo(path).Length != expected.Size || !EqualsHash(Hash(path),expected.Sha1))
    throw new InvalidDataException("关键文件未通过原版校验：" + relative);
  }
  public static void PrepareCompatibility(VersionEntry v, string root) {
   if (v.Compatibility == null) return;
   RequireNoGame();
   var replacements = new List<Replacement>();
   var additions = new List<string>();
   // Inspect every input before changing any file. An unknown file is never replaced.
   foreach (PatchSpec spec in v.Compatibility.Patches) {
    string target = VersionCatalog.SafePath(root, spec.Path);
    byte[] current = File.ReadAllBytes(target);
    string currentHash = Hash(current);
    string backup = BackupPath(root, spec);
    byte[] original;
    if (File.Exists(backup)) {
     original = File.ReadAllBytes(backup);
     if (!EqualsHash(Hash(original),spec.OriginalSha1)) throw new InvalidDataException("原版备份校验失败：" + spec.Path);
    } else {
     if (!EqualsHash(currentHash,spec.OriginalSha1)) throw new InvalidDataException("缺少原版备份：" + spec.Path);
     original = current;
    }
    if (!EqualsHash(currentHash,spec.OriginalSha1) && !EqualsHash(currentHash,spec.PatchedSha1))
     throw new InvalidDataException("文件不是已验证的原版或兼容版：" + spec.Path);
    byte[] candidate = (byte[])original.Clone();
    if (spec.Offsets.Length != spec.BeforeBytes.Length || spec.Offsets.Length != spec.AfterBytes.Length)
     throw new InvalidDataException("兼容配置长度错误。");
    for (int i = 0; i < spec.Offsets.Length; i++) {
     int offset = spec.Offsets[i];
     if (offset < 0 || offset >= candidate.Length || candidate[offset] != spec.BeforeBytes[i])
      throw new InvalidDataException("兼容补丁位置未通过核对：" + spec.Path);
     candidate[offset] = checked((byte)spec.AfterBytes[i]);
    }
    if (!EqualsHash(Hash(candidate),spec.PatchedSha1)) throw new InvalidDataException("兼容文件校验失败：" + spec.Path);
    replacements.Add(new Replacement { Target=target, Backup=backup, Original=original, Before=current, After=candidate });
   }
   var copies = new List<Replacement>();
   foreach (CopySpec spec in v.Compatibility.Copies) {
    string source=VersionCatalog.SafePath(root,spec.Source), target=VersionCatalog.SafePath(root,spec.Target);
    byte[] bytes=File.ReadAllBytes(source);
    if (!EqualsHash(Hash(bytes),spec.Sha1)) throw new InvalidDataException("原版 ICU 库校验失败。");
    if (File.Exists(target)) {
     if (!EqualsHash(Hash(target),spec.Sha1)) throw new InvalidDataException("已有私有 ICU 库内容未知，已停止。");
    } else copies.Add(new Replacement {Target=target, After=bytes});
   }
   var changed = new List<Replacement>();
   try {
    foreach (Replacement item in replacements) {
     Directory.CreateDirectory(Path.GetDirectoryName(item.Backup));
     if (!File.Exists(item.Backup)) WriteNew(item.Backup,item.Original);
    }
    foreach (Replacement item in copies) { WriteNew(item.Target,item.After); additions.Add(item.Target); }
    foreach (Replacement item in replacements) {
     if (!EqualsHash(Hash(item.Before),Hash(item.After))) {
      AtomicReplace(item.Target,item.After); changed.Add(item);
     }
    }
    foreach (Replacement item in replacements)
     if (!EqualsHash(Hash(item.Target),Hash(item.After))) throw new IOException("写入后的兼容文件校验失败。");
    string receipt=VersionCatalog.SafePath(root,"_compatibility/icu52/gui-installation.json");
    Directory.CreateDirectory(Path.GetDirectoryName(receipt));
    string json = new JavaScriptSerializer().Serialize(new {
     VersionId=v.Id, AppliedAtUtc=DateTime.UtcNow.ToString("o"), ActiveInstallAllOriginal=false,
     Description=v.Compatibility.Description, GameplayFilesModified=false, SystemFilesModified=false,
     Patches=v.Compatibility.Patches, Copies=v.Compatibility.Copies
    });
    if (File.Exists(receipt)) AtomicReplace(receipt,Encoding.UTF8.GetBytes(json));
    else WriteNew(receipt,Encoding.UTF8.GetBytes(json));
   } catch {
    foreach (Replacement item in changed) AtomicReplace(item.Target,item.Before);
    foreach (string added in additions) File.Delete(added);
    throw;
   }
  }
  public static void RestoreOriginals(VersionEntry v,string root) {
   RequireNoGame();
   if(v.Compatibility==null) return;
   var restore=new List<Replacement>();
   foreach(PatchSpec spec in v.Compatibility.Patches) {
    string target=VersionCatalog.SafePath(root,spec.Path);
    string current=Hash(target);
    if(EqualsHash(current,spec.OriginalSha1)) continue;
    if(!EqualsHash(current,spec.PatchedSha1)) throw new InvalidDataException("目标文件已被其他程序修改，不能自动还原："+spec.Path);
    byte[] original=File.ReadAllBytes(BackupPath(root,spec));
    if(!EqualsHash(Hash(original),spec.OriginalSha1)) throw new InvalidDataException("原版备份校验失败。");
    restore.Add(new Replacement{Target=target,Before=File.ReadAllBytes(target),After=original});
   }
   foreach(CopySpec spec in v.Compatibility.Copies) {
    string target=VersionCatalog.SafePath(root,spec.Target);
    if(File.Exists(target) && !EqualsHash(Hash(target),spec.Sha1)) throw new InvalidDataException("私有 ICU 库已改变，不能自动还原。");
   }
   var changed=new List<Replacement>();
   try {
    foreach(Replacement item in restore) {AtomicReplace(item.Target,item.After);changed.Add(item);}
   } catch { foreach(Replacement item in changed) AtomicReplace(item.Target,item.Before); throw; }
   // The extra private copy is harmless when no original consumer references it.
   // Keep it and the original backups for repeatable, non-destructive restoration.
  }
  static string BackupPath(string root,PatchSpec spec) {
   return VersionCatalog.SafePath(root,"_compatibility/icu52/originals/"+Path.GetFileName(spec.Path));
  }
  static void WriteNew(string path,byte[] bytes) {
   string staging=Path.Combine(Path.GetDirectoryName(path),".da-write-"+Guid.NewGuid().ToString("N")+".tmp");
   try {
    using(var stream=new FileStream(staging,FileMode.CreateNew,FileAccess.Write,FileShare.None)) {
     stream.Write(bytes,0,bytes.Length); stream.Flush(true);
    }
    File.Move(staging,path);
   } finally { if(File.Exists(staging)) File.Delete(staging); }
  }
  static void AtomicReplace(string target,byte[] bytes) {
   string temp=Path.Combine(Path.GetDirectoryName(target),".da-replace-"+Guid.NewGuid().ToString("N")+".tmp");
   try { WriteNew(temp,bytes); File.Replace(temp,target,null); }
   finally { if(File.Exists(temp)) File.Delete(temp); }
  }
  static string Hash(string path) { using(var f=File.OpenRead(path)) using(var h=SHA1.Create()) return Hex(h.ComputeHash(f)); }
  static string Hash(byte[] data) {using(var h=SHA1.Create()) return Hex(h.ComputeHash(data));}
  static string Hex(byte[] bytes) {return BitConverter.ToString(bytes).Replace("-","").ToLowerInvariant();}
  static bool EqualsHash(string a,string b) {return String.Equals(a,b,StringComparison.OrdinalIgnoreCase);}
  sealed class Replacement {public string Target,Backup;public byte[] Original,Before,After;}
 }
}
