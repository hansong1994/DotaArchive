using System.Collections.Generic;

namespace DotaArchive {
 // A prepared expectation, not proof that the client has been downloaded or run.
 public sealed class ManifestPreparationResult {
  public List<DepotEntry> Depots {get;set;}
  public List<FileEntry> Files {get;set;}
  public List<string> Directories {get;set;}
  public long TotalBytes {get;set;}
  public List<PreparedManifestRecord> Manifests {get;set;}
  public string Assurance {get;set;}
 }
 public sealed class PreparedManifestRecord {
  public string DepotId {get;set;}
  public string ManifestId {get;set;}
  public string TextSha256 {get;set;}
  public string BinarySha1 {get;set;}
  public string ManifestCreationTimeText {get;set;}
  public int EntryCount {get;set;}
  public long UniqueChunkCount {get;set;}
  public long Bytes {get;set;}
  public long CompressedBytes {get;set;}
 }
}
